using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VideoControls
{
    /// <summary>
    /// Key that blanks your video by switching NVIDIA Broadcast's camera to the "Blank Camera" virtual camera,
    /// and back to your real camera on the next press. Apps keep using "Camera (NVIDIA Broadcast)" throughout.
    /// </summary>
    internal class CameraAction : PluginAction
    {
        // Key states as defined in manifest.json: 0 = live (green), 1 = blank (red).
        const int StateLive = 0;
        const int StateBlank = 1;

        const string BlankCameraName = "Blank Camera";
        const int ReadTimeoutMs = 5000;
        const int SwitchTimeoutMs = 20000;   // set_active_device restarts Broadcast's pipeline
        const int PollEverySeconds = 3;      // Program polls every second; reads stay well inside the rate limit
        static readonly TimeSpan MinSwitchGap = TimeSpan.FromMilliseconds(750);

        readonly HashSet<string> contexts = new HashSet<string>();
        readonly object contextsLock = new object();
        int lastState = -1;
        int pollTick;
        int switching;                       // 1 while a switch is in flight
        DateTime lastSwitch = DateTime.MinValue;
        bool loggedUnavailable;

        // The real camera to go back to. Saved in the key's settings so it survives restarts.
        readonly object rememberedLock = new object();
        RealCamera remembered;

        // Camera effects the key turned off when going blank, to turn back on when going live:
        // each is {"effectId": id, "params": {...}}. Saved in the key's settings with the camera.
        // Guarded by rememberedLock.
        List<Dictionary<string, object>> savedEffects = new List<Dictionary<string, object>>();
        bool settingsLoaded;

        // Image to show while blank (via background replace); null = plain black. blankImageApplied means the
        // key turned background replace on for it, so going live must turn it off again.
        const string BackgroundReplace = "background_replace";
        string blankImage;
        bool blankImageApplied;

        // Param names set_effects accepts when enabling; anything else in the state is left out.
        static readonly string[] EffectParams =
        {
            "warmth", "strength", "mode", "imagePath", "faceTracking", "outputWidth", "outputHeight",
            "multiplier", "model", "zoom", "micProfile"
        };

        class CameraInfo
        {
            public string Id, Name;
            public bool Active;
            public int Width, Height, FrameRate;
            public List<int[]> Resolutions = new List<int[]>(); // {width, height, frameRate}

            // Broadcast shows virtual cameras with a suffix: "Blank Camera (Windows Virtual Camera)".
            public bool IsBlank
            {
                get
                {
                    return Name != null &&
                        (string.Equals(Name, BlankCameraName, StringComparison.OrdinalIgnoreCase) ||
                         Name.StartsWith(BlankCameraName + " (", StringComparison.OrdinalIgnoreCase));
                }
            }

            public bool Supports(int w, int h, int fps)
            {
                foreach (int[] r in Resolutions)
                    if (r[0] == w && r[1] == h && r[2] == fps) return true;
                return false;
            }
        }

        class RealCamera
        {
            public string Id, Name;
            public int Width, Height, FrameRate;
        }

        public override string Uuid { get { return "com.local.videocontrols.camera"; } }

        public override async Task Handle(string ev, string context, Dictionary<string, object> payload)
        {
            switch (ev)
            {
                case "willAppear":
                    lock (contextsLock) contexts.Add(context);
                    RestoreRemembered(StreamDock.GetDict(payload, "settings"));
                    if (lastState >= 0) await StreamDock.SetState(context, lastState);
                    else await Refresh(); // first key: ask Broadcast right away instead of waiting for the poll
                    break;

                case "willDisappear":
                    lock (contextsLock) contexts.Remove(context);
                    break;

                case "keyDown":
                    await Toggle(context);
                    break;

                case "propertyInspectorDidAppear":
                    await SendImageToInspector(context, null);
                    break;

                case "sendToPlugin":
                    // The plugin owns the settings: the inspector only asks for changes.
                    if (payload == null) return;
                    if (payload.ContainsKey("browse"))
                    {
                        // Not awaited: the dialog stays open while Stream Dock keeps sending events.
                        var ignored = BrowseForImage(context);
                    }
                    else if (payload.ContainsKey("blankImage"))
                    {
                        await SetBlankImage(context, Broadcast.GetString(payload, "blankImage"));
                    }
                    break;
            }
        }

        // --- Blank image (shown through Broadcast's background replace while blank) ---

        /// <summary>The image to show while blank, or null for black (none set, or the file is gone).</summary>
        string BlankImage()
        {
            string path;
            lock (rememberedLock) path = blankImage;
            if (string.IsNullOrEmpty(path)) return null;
            if (File.Exists(path)) return path;
            StreamDock.Log("Blank image not found, using black: " + path);
            return null;
        }

        async Task BrowseForImage(string context)
        {
            string current;
            lock (rememberedLock) current = blankImage;
            string picked = await FileDialog.PickImage(current);
            if (picked != null) await SetBlankImage(context, picked);
        }

        async Task SetBlankImage(string context, string path)
        {
            path = (path ?? "").Trim().Trim('"');
            string error = null;
            if (path.Length > 0)
            {
                if (!Path.IsPathRooted(path)) error = "Use a full path, like C:\\Pictures\\brb.png.";
                else if (path.Length > 259) error = "Broadcast can't use paths longer than 259 characters.";
                else if (!File.Exists(path)) error = "File not found.";
            }
            if (error != null)
            {
                await SendImageToInspector(context, error);
                return;
            }

            lock (rememberedLock) blankImage = path.Length > 0 ? path : null;
            SaveRemembered();
            await SendImageToInspector(context, null);

            // Already blank: show the new image (or go back to black) straight away.
            if (lastState == StateBlank && Interlocked.CompareExchange(ref switching, 1, 0) == 0)
            {
                try { await Task.Run(() => ApplyBlankImageNow()); }
                catch (Exception ex) { StreamDock.Log("Changing the blank image failed: " + ex.Message); }
                finally { Interlocked.Exchange(ref switching, 0); }
            }
        }

        void ApplyBlankImageNow()
        {
            string image = BlankImage();
            bool applied;
            lock (rememberedLock) applied = blankImageApplied;
            Dictionary<string, object> change;
            if (image != null)
            {
                lock (rememberedLock) blankImageApplied = true;
                SaveRemembered();
                change = EnableChange(BackgroundReplace, new Dictionary<string, object> { { "imagePath", image } });
            }
            else if (applied)
            {
                change = new Dictionary<string, object> { { "effectId", BackgroundReplace }, { "enabled", false } };
            }
            else return;

            LogEffectProblems(Broadcast.Call("set_effects", new Dictionary<string, object>
                { { "effects", new List<Dictionary<string, object>> { change } } }, SwitchTimeoutMs), "changing the blank image");
            if (image == null)
            {
                lock (rememberedLock) blankImageApplied = false;
                SaveRemembered();
            }
        }

        Task SendImageToInspector(string context, string error)
        {
            string path;
            lock (rememberedLock) path = blankImage;
            var payload = new Dictionary<string, object> { { "blankImage", path ?? "" } };
            if (error != null) payload["error"] = error;
            else if (path != null && !File.Exists(path)) payload["error"] = "File not found; the blank feed will be black.";
            return StreamDock.SendToInspector(Uuid, context, payload);
        }

        public override Task Poll()
        {
            if (++pollTick % PollEverySeconds != 0) return Task.FromResult(0);
            lock (contextsLock) if (contexts.Count == 0) return Task.FromResult(0);
            return Refresh();
        }

        /// <summary>Colours the key by which camera Broadcast is using. Leaves it alone if Broadcast is unreachable.</summary>
        async Task Refresh()
        {
            if (Volatile.Read(ref switching) == 1) return;

            List<CameraInfo> cameras;
            try
            {
                cameras = await Task.Run(() => ListCameras());
                loggedUnavailable = false;
            }
            catch (BroadcastUnavailableException ex)
            {
                if (!loggedUnavailable) StreamDock.Log(ex.Message);
                loggedUnavailable = true;
                return;
            }
            catch (Exception ex)
            {
                StreamDock.Log("Reading Broadcast cameras failed: " + ex.Message);
                return;
            }

            // A press that started while we were reading wins.
            if (Volatile.Read(ref switching) == 1) return;

            CameraInfo active = cameras.Find(c => c.Active);
            if (active == null) return; // still initialising, or no camera selected
            int state = active.IsBlank ? StateBlank : StateLive;
            if (!active.IsBlank) Remember(active);
            if (state != lastState) await ApplyState(state);

            // Switched back to the real camera in Broadcast's own window: bring the effects back too.
            if (!active.IsBlank && HasPendingRestore() && Interlocked.CompareExchange(ref switching, 1, 0) == 0)
            {
                try { await Task.Run(() => RestoreEffects()); }
                catch (Exception ex) { StreamDock.Log("Restoring camera effects failed: " + ex.Message); }
                finally { Interlocked.Exchange(ref switching, 0); }
            }
        }

        async Task Toggle(string context)
        {
            if (Interlocked.Exchange(ref switching, 1) == 1) return; // a switch is already running
            int previousState = lastState;
            try
            {
                // Don't hammer set_active_device if the key is mashed.
                TimeSpan sinceLast = DateTime.UtcNow - lastSwitch;
                if (sinceLast < MinSwitchGap) await Task.Delay(MinSwitchGap - sinceLast);

                int target = await Task.Run(() => SwitchCamera(previousState));
                lastSwitch = DateTime.UtcNow;
                await ApplyState(target);
                return;
            }
            catch (BroadcastUnavailableException ex)
            {
                StreamDock.Log(ex.Message);
            }
            catch (Exception ex)
            {
                StreamDock.Log("Camera switch failed: " + ex.Message);
            }
            finally
            {
                lastSwitch = DateTime.UtcNow;
                Interlocked.Exchange(ref switching, 0);
            }

            // Failed: show the alert and put the key back to whatever Broadcast is really doing.
            await StreamDock.ShowAlert(context);
            if (previousState >= 0) await ApplyState(previousState);
            await Refresh();
        }

        /// <summary>Does the switch (blocking). Returns the key state to show afterwards.</summary>
        int SwitchCamera(int keyState)
        {
            List<CameraInfo> cameras = ListCameras();
            CameraInfo blank = cameras.Find(c => c.IsBlank);
            CameraInfo active = cameras.Find(c => c.Active);

            if (blank == null)
                throw new InvalidOperationException("\"" + BlankCameraName + "\" is not in Broadcast's camera list. " +
                    "Run setup-blankcam.bat, then restart NVIDIA Broadcast.");

            if (active == null || !active.IsBlank)
            {
                // Live -> blank. Keep the resolution if Blank Camera offers it, so Broadcast doesn't rescale.
                if (active != null) Remember(active);
                ShowPending(StateBlank);

                // Note which effects are on (and save them) before touching anything.
                List<Dictionary<string, object>> enabled = null;
                try { enabled = ReadEnabledEffects(); }
                catch (Exception ex) { StreamDock.Log("Reading camera effects failed; leaving them on: " + ex.Message); }
                if (enabled != null && enabled.Count > 0) AddSavedEffects(enabled);

                var args = SwitchArgs(blank.Id);
                if (active != null && blank.Supports(active.Width, active.Height, active.FrameRate))
                    AddResolution(args, active.Width, active.Height, active.FrameRate);
                Confirm(Broadcast.Call("set_active_device", args, SwitchTimeoutMs), blank.Name);

                // The feed is already blank; failing to set the effects isn't worth an alert.
                try { SetBlankEffects(enabled ?? new List<Dictionary<string, object>>()); }
                catch (Exception ex) { StreamDock.Log("Setting camera effects for blank failed: " + ex.Message); }
                return StateBlank;
            }

            // Blank -> live.
            RealCamera saved;
            lock (rememberedLock) saved = remembered;
            CameraInfo target = null;
            if (saved != null)
            {
                // Prefer the saved ID; fall back to the name in case the ID changed (e.g. other USB port).
                target = cameras.Find(c => !c.IsBlank && c.Id == saved.Id) ??
                         cameras.Find(c => !c.IsBlank && string.Equals(c.Name, saved.Name, StringComparison.OrdinalIgnoreCase));
            }
            if (target == null) target = cameras.Find(c => !c.IsBlank);
            if (target == null) throw new InvalidOperationException("Broadcast lists no camera other than " + BlankCameraName + ".");

            ShowPending(StateLive);
            var liveArgs = SwitchArgs(target.Id);
            if (saved != null && target.Supports(saved.Width, saved.Height, saved.FrameRate))
                AddResolution(liveArgs, saved.Width, saved.Height, saved.FrameRate);
            Confirm(Broadcast.Call("set_active_device", liveArgs, SwitchTimeoutMs), target.Name);

            // The camera is back; an effect that won't come back is logged rather than alerted.
            try { RestoreEffects(); }
            catch (Exception ex) { StreamDock.Log("Restoring camera effects failed: " + ex.Message); }
            return StateLive;
        }

        // --- Camera effects (off while blank) ---

        /// <summary>Camera effects that are on now, each as {"effectId", "params"}.</summary>
        static List<Dictionary<string, object>> ReadEnabledEffects()
        {
            var state = Broadcast.Call("get_broadcast_state",
                new Dictionary<string, object> { { "effectType", "camera" } }, ReadTimeoutMs);
            var camera = StreamDock.GetDict(state, "camera") ?? state; // flat when filtered to camera
            var enabled = new List<Dictionary<string, object>>();
            foreach (var entry in camera)
            {
                var effect = entry.Value as Dictionary<string, object>;
                if (effect == null || !Broadcast.GetBool(effect, "enabled", false)) continue;
                var parameters = new Dictionary<string, object>();
                foreach (string name in EffectParams)
                {
                    object v;
                    if (effect.TryGetValue(name, out v) && v != null) parameters[name] = v;
                }
                enabled.Add(new Dictionary<string, object> { { "effectId", entry.Key }, { "params", parameters } });
            }
            return enabled;
        }

        static Dictionary<string, object> EnableChange(object effectId, Dictionary<string, object> parameters)
        {
            var change = new Dictionary<string, object> { { "effectId", effectId }, { "enabled", true } };
            if (parameters != null && parameters.Count > 0) change["params"] = parameters;
            return change;
        }

        /// <summary>
        /// Turns off every camera effect that's on, then, if a blank image is set, turns background replace on
        /// with it. Disables come first in the batch so blur / remove don't block background replace.
        /// </summary>
        void SetBlankEffects(List<Dictionary<string, object>> enabled)
        {
            string image = BlankImage();
            var changes = new List<Dictionary<string, object>>();
            foreach (var e in enabled)
            {
                if (image != null && (string)e["effectId"] == BackgroundReplace) continue; // re-set below
                changes.Add(new Dictionary<string, object> { { "effectId", e["effectId"] }, { "enabled", false } });
            }
            if (image != null)
            {
                // Saved before the call, so a crash mid-way still turns it off on the way back.
                lock (rememberedLock) blankImageApplied = true;
                SaveRemembered();
                changes.Add(EnableChange(BackgroundReplace, new Dictionary<string, object> { { "imagePath", image } }));
            }
            if (changes.Count == 0) return;
            LogEffectProblems(Broadcast.Call("set_effects",
                new Dictionary<string, object> { { "effects", changes } }, SwitchTimeoutMs), "setting for blank");
        }

        /// <summary>
        /// Undoes SetBlankEffects: turns off the blank image's background replace (unless it was on before),
        /// turns the saved effects back on with their old params, then forgets them.
        /// </summary>
        void RestoreEffects()
        {
            List<Dictionary<string, object>> effects;
            bool applied;
            lock (rememberedLock)
            {
                if (savedEffects.Count == 0 && !blankImageApplied) return;
                effects = new List<Dictionary<string, object>>(savedEffects);
                applied = blankImageApplied;
            }
            var changes = new List<Dictionary<string, object>>();
            if (applied && !effects.Exists(e => (string)e["effectId"] == BackgroundReplace))
                changes.Add(new Dictionary<string, object> { { "effectId", BackgroundReplace }, { "enabled", false } });
            foreach (var e in effects)
                changes.Add(EnableChange(e["effectId"], e["params"] as Dictionary<string, object>));

            var result = Broadcast.Call("set_effects",
                new Dictionary<string, object> { { "effects", changes } }, SwitchTimeoutMs);

            // Forget them even if some failed, so a broken effect doesn't get retried every poll.
            lock (rememberedLock)
            {
                savedEffects.Clear();
                blankImageApplied = false;
            }
            SaveRemembered();
            LogEffectProblems(result, "turning back on");
        }

        static void LogEffectProblems(Dictionary<string, object> result, string what)
        {
            if (Broadcast.GetBool(result, "success", false)) return;
            object failed, skipped;
            result.TryGetValue("failed", out failed);
            result.TryGetValue("skipped", out skipped);
            StreamDock.Log("Some camera effects failed " + what + ": failed=" + StreamDock.Json.Serialize(failed) +
                " skipped=" + StreamDock.Json.Serialize(skipped));
        }

        /// <summary>Adds to the saved list, keeping any not yet restored (their old params win).</summary>
        void AddSavedEffects(List<Dictionary<string, object>> effects)
        {
            lock (rememberedLock)
            {
                foreach (var e in effects)
                    if (!savedEffects.Exists(s => (string)s["effectId"] == (string)e["effectId"]))
                        savedEffects.Add(e);
            }
            SaveRemembered();
        }

        bool HasPendingRestore()
        {
            lock (rememberedLock) return savedEffects.Count > 0 || blankImageApplied;
        }

        static Dictionary<string, object> SwitchArgs(string deviceId)
        {
            return new Dictionary<string, object> { { "kind", "camera" }, { "deviceId", deviceId } };
        }

        static void AddResolution(Dictionary<string, object> args, int width, int height, int frameRate)
        {
            if (width <= 0 || height <= 0 || frameRate <= 0) return;
            args["width"] = width;
            args["height"] = height;
            args["frameRate"] = frameRate;
        }

        /// <summary>Accepted but unconfirmed switches come back as success:false (not a tool error).</summary>
        static void Confirm(Dictionary<string, object> result, string name)
        {
            if (!Broadcast.GetBool(result, "success", false) || !Broadcast.GetBool(result, "deviceConfirmed", true))
                throw new InvalidOperationException("Broadcast didn't confirm the switch to " + name + ": " +
                    (Broadcast.GetString(result, "message") ?? "no message"));
            var resolution = StreamDock.GetDict(result, "resolution");
            if (resolution != null && !Broadcast.GetBool(resolution, "confirmed", true))
                StreamDock.Log("Switched to " + name + ", but not at the requested resolution: " + Broadcast.GetString(result, "message"));
        }

        /// <summary>Flip the key straight away; a switch can take a couple of seconds.</summary>
        void ShowPending(int state)
        {
            ApplyState(state).Wait();
        }

        List<CameraInfo> ListCameras()
        {
            var result = Broadcast.Call("list_devices", new Dictionary<string, object> { { "kind", "camera" } }, ReadTimeoutMs);
            var cameras = new List<CameraInfo>();
            foreach (var c in Broadcast.GetList(result, "cameras"))
            {
                var info = new CameraInfo
                {
                    Id = Broadcast.GetString(c, "id"),
                    Name = Broadcast.GetString(c, "name"),
                    Active = Broadcast.GetBool(c, "active", false)
                };
                if (info.Id == null) continue;
                var current = StreamDock.GetDict(c, "currentResolution");
                if (current != null)
                {
                    info.Width = Broadcast.GetInt(current, "width");
                    info.Height = Broadcast.GetInt(current, "height");
                    info.FrameRate = Broadcast.GetInt(current, "frameRate");
                }
                foreach (var r in Broadcast.GetList(c, "availableResolutions"))
                    info.Resolutions.Add(new[] { Broadcast.GetInt(r, "width"), Broadcast.GetInt(r, "height"), Broadcast.GetInt(r, "frameRate") });
                cameras.Add(info);
            }
            return cameras;
        }

        // --- Remembered real camera (key settings) ---

        void Remember(CameraInfo camera)
        {
            lock (rememberedLock)
            {
                if (remembered != null && remembered.Id == camera.Id && remembered.Name == camera.Name &&
                    remembered.Width == camera.Width && remembered.Height == camera.Height &&
                    remembered.FrameRate == camera.FrameRate)
                    return;
                remembered = new RealCamera
                {
                    Id = camera.Id, Name = camera.Name,
                    Width = camera.Width, Height = camera.Height, FrameRate = camera.FrameRate
                };
            }
            SaveRemembered();
        }

        void RestoreRemembered(Dictionary<string, object> settings)
        {
            lock (rememberedLock)
            {
                if (settingsLoaded || settings == null) return; // already know it from this session
                settingsLoaded = true;

                foreach (var e in Broadcast.GetList(settings, "savedEffects"))
                {
                    string effectId = Broadcast.GetString(e, "effectId");
                    if (effectId == null || savedEffects.Exists(s => (string)s["effectId"] == effectId)) continue;
                    savedEffects.Add(new Dictionary<string, object>
                    {
                        { "effectId", effectId },
                        { "params", StreamDock.GetDict(e, "params") ?? new Dictionary<string, object>() }
                    });
                }

                string image = Broadcast.GetString(settings, "blankImage");
                if (!string.IsNullOrEmpty(image)) blankImage = image;
                blankImageApplied = Broadcast.GetBool(settings, "blankImageApplied", false);

                string id = Broadcast.GetString(settings, "cameraId");
                if (id == null || remembered != null) return;
                remembered = new RealCamera
                {
                    Id = id,
                    Name = Broadcast.GetString(settings, "cameraName"),
                    Width = Broadcast.GetInt(settings, "width"),
                    Height = Broadcast.GetInt(settings, "height"),
                    FrameRate = Broadcast.GetInt(settings, "frameRate")
                };
            }
        }

        void SaveRemembered()
        {
            Dictionary<string, object> settings;
            lock (rememberedLock)
            {
                settings = new Dictionary<string, object>
                {
                    { "savedEffects", new List<Dictionary<string, object>>(savedEffects) },
                    { "blankImage", blankImage ?? "" },
                    { "blankImageApplied", blankImageApplied }
                };
                if (remembered != null)
                {
                    settings["cameraId"] = remembered.Id;
                    settings["cameraName"] = remembered.Name;
                    settings["width"] = remembered.Width;
                    settings["height"] = remembered.Height;
                    settings["frameRate"] = remembered.FrameRate;
                }
            }
            List<string> targets;
            lock (contextsLock) targets = new List<string>(contexts);
            foreach (string ctx in targets)
                StreamDock.SetSettings(ctx, settings).Wait();
        }

        /// <summary>Pushes the state to every key showing this action.</summary>
        async Task ApplyState(int state)
        {
            lastState = state;
            List<string> targets;
            lock (contextsLock) targets = new List<string>(contexts);
            foreach (string ctx in targets)
                await StreamDock.SetState(ctx, state);
        }
    }
}
