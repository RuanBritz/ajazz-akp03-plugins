using System;
using System.Collections.Generic;
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
            }
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
                var args = SwitchArgs(blank.Id);
                if (active != null && blank.Supports(active.Width, active.Height, active.FrameRate))
                    AddResolution(args, active.Width, active.Height, active.FrameRate);
                Confirm(Broadcast.Call("set_active_device", args, SwitchTimeoutMs), blank.Name);
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
            return StateLive;
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
            string id = Broadcast.GetString(settings, "cameraId");
            if (id == null) return;
            lock (rememberedLock)
            {
                if (remembered != null) return; // already know it from this session
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
                if (remembered == null) return;
                settings = new Dictionary<string, object>
                {
                    { "cameraId", remembered.Id },
                    { "cameraName", remembered.Name },
                    { "width", remembered.Width },
                    { "height", remembered.Height },
                    { "frameRate", remembered.FrameRate }
                };
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
