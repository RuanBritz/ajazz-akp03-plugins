using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AudioControls
{
    /// <summary>Knob that changes the volume of a chosen output device; pressing it mutes.</summary>
    internal class VolumeAction : PluginAction
    {
        /// <summary>One knob showing this action, with its own settings.</summary>
        class Knob
        {
            public string DeviceId = "";   // "" = follow the Windows default output device
            public int Step = 2;           // percent per knob tick
            public string LastTitle;
            public int LastState = -1;
        }

        // Knob states as defined in manifest.json: 0 = sound on, 1 = muted.
        const int StateOn = 0;
        const int StateMuted = 1;

        readonly Dictionary<string, Knob> knobs = new Dictionary<string, Knob>();
        readonly object knobsLock = new object();

        public override string Uuid { get { return "com.local.audiocontrols.volume"; } }

        public override async Task Handle(string ev, string context, Dictionary<string, object> payload)
        {
            Knob knob;
            switch (ev)
            {
                case "willAppear":
                    knob = new Knob();
                    ApplySettings(knob, StreamDock.GetDict(payload, "settings"));
                    lock (knobsLock) knobs[context] = knob;
                    await Refresh(context, knob);
                    break;

                case "willDisappear":
                    lock (knobsLock) knobs.Remove(context);
                    break;

                case "didReceiveSettings":
                    knob = Find(context);
                    if (knob == null) return;
                    ApplySettings(knob, StreamDock.GetDict(payload, "settings"));
                    await Refresh(context, knob);
                    break;

                case "dialRotate":
                    knob = Find(context);
                    if (knob == null || payload == null || !payload.ContainsKey("ticks")) return;
                    await Rotate(context, knob, Convert.ToInt32(payload["ticks"]));
                    break;

                case "dialDown":
                    knob = Find(context);
                    if (knob != null) await ToggleMute(context, knob);
                    break;

                case "propertyInspectorDidAppear":
                    await StreamDock.SendOutputDevices(Uuid, context);
                    break;

                case "sendToPlugin":
                    // The property inspector asks for the device list, and also sends its settings directly
                    // in case the app doesn't echo setSettings back as didReceiveSettings.
                    if (payload == null) return;
                    if (payload.ContainsKey("refreshDevices")) await StreamDock.SendOutputDevices(Uuid, context);
                    knob = Find(context);
                    var settings = StreamDock.GetDict(payload, "settings");
                    if (knob != null && settings != null)
                    {
                        ApplySettings(knob, settings);
                        await Refresh(context, knob);
                    }
                    break;
            }
        }

        static void ApplySettings(Knob knob, Dictionary<string, object> settings)
        {
            if (settings == null) return;
            object v;
            if (settings.TryGetValue("deviceId", out v)) knob.DeviceId = v as string ?? "";
            if (settings.TryGetValue("step", out v))
            {
                int step;
                if (v != null && int.TryParse(v.ToString(), out step) && step >= 1 && step <= 25) knob.Step = step;
            }
            // Force the next refresh to redraw.
            knob.LastTitle = null;
            knob.LastState = -1;
        }

        async Task Rotate(string context, Knob knob, int ticks)
        {
            bool ok = false;
            try
            {
                int percent = (int)Math.Round(Audio.GetVolume(knob.DeviceId) * 100);
                int target = Math.Max(0, Math.Min(100, percent + ticks * knob.Step));
                Audio.SetVolume(knob.DeviceId, target / 100f);
                // Like the Windows volume keys: turning up unmutes.
                if (ticks > 0 && Audio.IsMuted(knob.DeviceId)) Audio.SetMuted(knob.DeviceId, false);
                ok = true;
            }
            catch (Exception ex)
            {
                StreamDock.Log("Volume change failed: " + ex.Message);
            }

            // Shows the warning triangle if the device is missing.
            if (!ok) await StreamDock.ShowAlert(context);
            else await Poll();
        }

        async Task ToggleMute(string context, Knob knob)
        {
            bool ok = false;
            try
            {
                Audio.SetMuted(knob.DeviceId, !Audio.IsMuted(knob.DeviceId));
                ok = true;
            }
            catch (Exception ex)
            {
                StreamDock.Log("Mute toggle failed: " + ex.Message);
            }

            if (!ok) await StreamDock.ShowAlert(context);
            else await Poll();
        }

        /// <summary>Refreshes every knob, so changes made in the Windows mixer show up too.</summary>
        public override async Task Poll()
        {
            List<KeyValuePair<string, Knob>> targets;
            lock (knobsLock) targets = new List<KeyValuePair<string, Knob>>(knobs);
            foreach (var pair in targets)
                await Refresh(pair.Key, pair.Value);
        }

        /// <summary>Shows the device's volume as the knob title and muted/unmuted as its state.</summary>
        static async Task Refresh(string context, Knob knob)
        {
            string title;
            int state;
            try
            {
                bool muted = Audio.IsMuted(knob.DeviceId);
                title = muted ? "Muted" : (int)Math.Round(Audio.GetVolume(knob.DeviceId) * 100) + "%";
                state = muted ? StateMuted : StateOn;
            }
            catch
            {
                title = "No device"; // selected device is unplugged or disabled
                state = StateMuted;
            }

            if (title != knob.LastTitle)
            {
                knob.LastTitle = title;
                await StreamDock.SetTitle(context, title);
            }

            if (state != knob.LastState)
            {
                knob.LastState = state;
                await StreamDock.SetState(context, state);
            }
        }

        Knob Find(string context)
        {
            Knob knob;
            lock (knobsLock) return knobs.TryGetValue(context, out knob) ? knob : null;
        }
    }
}
