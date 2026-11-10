using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AudioControls
{
    /// <summary>
    /// Key that switches the Windows default output device to the next one in its list.
    /// With one device in the list it becomes a "switch to X" key; with none it cycles through all active outputs.
    /// The key shows the current default output's name, coloured by its position in the key's list.
    /// </summary>
    internal class OutputSwitchAction : PluginAction
    {
        class SwitchKey
        {
            public List<string> DeviceIds = new List<string>();  // empty = all active outputs
            public bool IncludeComms = true;                      // also switch the default communications device
            public string LastDrawn;                              // name + colour last sent, to skip redundant redraws
        }

        readonly Dictionary<string, SwitchKey> keys = new Dictionary<string, SwitchKey>();
        readonly object keysLock = new object();

        public override string Uuid { get { return "com.local.audiocontrols.outputswitch"; } }

        public override async Task Handle(string ev, string context, Dictionary<string, object> payload)
        {
            SwitchKey key;
            switch (ev)
            {
                case "willAppear":
                    key = new SwitchKey();
                    ApplySettings(key, StreamDock.GetDict(payload, "settings"));
                    lock (keysLock) keys[context] = key;
                    await Poll();
                    break;

                case "willDisappear":
                    lock (keysLock) keys.Remove(context);
                    break;

                case "didReceiveSettings":
                    key = Find(context);
                    if (key == null) return;
                    ApplySettings(key, StreamDock.GetDict(payload, "settings"));
                    await Poll();
                    break;

                case "keyDown":
                    key = Find(context);
                    if (key != null) await SwitchNext(context, key);
                    break;

                case "propertyInspectorDidAppear":
                    await StreamDock.SendOutputDevices(Uuid, context);
                    break;

                case "sendToPlugin":
                    // Same protocol as the volume knob's settings panel.
                    if (payload == null) return;
                    if (payload.ContainsKey("refreshDevices")) await StreamDock.SendOutputDevices(Uuid, context);
                    key = Find(context);
                    var settings = StreamDock.GetDict(payload, "settings");
                    if (key != null && settings != null)
                    {
                        ApplySettings(key, settings);
                        await Poll();
                    }
                    break;
            }
        }

        static void ApplySettings(SwitchKey key, Dictionary<string, object> settings)
        {
            if (settings == null) return;
            object v;
            if (settings.TryGetValue("devices", out v))
            {
                key.DeviceIds = new List<string>();
                var list = v as IEnumerable;
                if (list != null && !(v is string))
                {
                    foreach (object id in list)
                    {
                        var s = id as string;
                        if (!string.IsNullOrEmpty(s)) key.DeviceIds.Add(s);
                    }
                }
            }
            if (settings.TryGetValue("comms", out v) && v is bool) key.IncludeComms = (bool)v;
            key.LastDrawn = null; // force the next refresh to redraw
        }

        async Task SwitchNext(string context, SwitchKey key)
        {
            bool ok = false;
            try
            {
                var active = new List<string>();
                foreach (var d in Audio.ListOutputs()) active.Add(d.Key);

                // Saved devices that are unplugged right now are skipped.
                List<string> cycle = key.DeviceIds.Count == 0 ? active : key.DeviceIds.FindAll(active.Contains);
                if (cycle.Count > 0)
                {
                    int current = cycle.IndexOf(Audio.DefaultOutputId());
                    Audio.SetDefaultOutput(cycle[(current + 1) % cycle.Count], key.IncludeComms);
                    ok = true;
                }
                else
                {
                    StreamDock.Log("Output switch: none of the chosen devices are connected.");
                }
            }
            catch (Exception ex)
            {
                StreamDock.Log("Output switch failed: " + ex.Message);
            }

            if (!ok) await StreamDock.ShowAlert(context);
            else await Poll();
        }

        /// <summary>Redraws every key for the current default output, which may also change in Windows settings.</summary>
        public override async Task Poll()
        {
            List<KeyValuePair<string, SwitchKey>> targets;
            lock (keysLock) targets = new List<KeyValuePair<string, SwitchKey>>(keys);
            if (targets.Count == 0) return;

            string defaultId = "";
            var outputs = new List<KeyValuePair<string, string>>();
            try
            {
                outputs = Audio.ListOutputs();
                defaultId = Audio.DefaultOutputId();
            }
            catch
            {
                // no output devices right now; draws "No device"
            }
            string name = ShortName(defaultId, outputs);

            foreach (var pair in targets)
            {
                int colour = ColourIndex(pair.Value, defaultId, outputs);
                string drawn = name + "|" + colour;
                if (drawn == pair.Value.LastDrawn) continue;
                pair.Value.LastDrawn = drawn;
                await StreamDock.SetImage(pair.Key, KeyImage.Render(name, colour));
            }
        }

        /// <summary>
        /// Position of the current default in the key's list, so each device gets its own colour.
        /// -1 (grey) if it isn't in the list, e.g. a "switch to X" key while X isn't the default.
        /// Saved order is used even for unplugged devices, so colours don't shift when one is disconnected.
        /// </summary>
        static int ColourIndex(SwitchKey key, string defaultId, List<KeyValuePair<string, string>> outputs)
        {
            if (key.DeviceIds.Count > 0) return key.DeviceIds.IndexOf(defaultId);
            for (int i = 0; i < outputs.Count; i++)
                if (outputs[i].Key == defaultId) return i;
            return -1;
        }

        /// <summary>
        /// "Speakers (Realtek(R) Audio)" becomes "Speakers". If another active output has the same short name
        /// (two "Speakers" are common), the part in brackets is used instead so the keys stay distinguishable.
        /// </summary>
        static string ShortName(string defaultId, List<KeyValuePair<string, string>> outputs)
        {
            string full = null;
            foreach (var d in outputs)
                if (d.Key == defaultId) full = d.Value;
            if (full == null) return "No device";

            string shortName = BeforeBracket(full);
            foreach (var d in outputs)
            {
                if (d.Key != defaultId && BeforeBracket(d.Value) == shortName)
                {
                    int open = full.IndexOf(" (", StringComparison.Ordinal);
                    return open >= 0 ? full.Substring(open + 2).TrimEnd(')') : full;
                }
            }
            return shortName;
        }

        static string BeforeBracket(string name)
        {
            int open = name.IndexOf(" (", StringComparison.Ordinal);
            return open > 0 ? name.Substring(0, open) : name;
        }

        SwitchKey Find(string context)
        {
            SwitchKey key;
            lock (keysLock) return keys.TryGetValue(context, out key) ? key : null;
        }
    }
}
