// Output Volume plugin for AJAZZ / MiraBox Stream Dock (Elgato-style SDK v2).
// Turn a knob to change the volume of a chosen Windows output device; press it to mute.
// Written in C# 5 so it compiles with the csc.exe that ships with Windows (.NET Framework 4.x).

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace OutputVolume
{
    /// <summary>One knob showing this action, with its own settings.</summary>
    internal class Knob
    {
        public string Action;
        public string DeviceId = "";   // "" = follow the Windows default output device
        public int Step = 2;           // percent per knob tick
        public string LastTitle;
        public int LastState = -1;
    }

    internal static class Program
    {
        // Knob states as defined in manifest.json: 0 = sound on, 1 = muted.
        const int StateOn = 0;
        const int StateMuted = 1;

        static ClientWebSocket socket;
        static readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        static readonly JavaScriptSerializer json = new JavaScriptSerializer();
        static readonly Dictionary<string, Knob> knobs = new Dictionary<string, Knob>();
        static readonly object knobsLock = new object();
        static readonly object logLock = new object();
        static Timer pollTimer;
        static string logPath;

        static int Main(string[] args)
        {
            logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "outputvolume.log");
            try
            {
                Run(args).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception ex)
            {
                Log("Fatal: " + ex);
                return 1;
            }
        }

        static async Task Run(string[] args)
        {
            string port = null, pluginUuid = null, registerEvent = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                switch (args[i])
                {
                    case "-port": port = args[i + 1]; break;
                    case "-pluginUUID": pluginUuid = args[i + 1]; break;
                    case "-registerEvent": registerEvent = args[i + 1]; break;
                }
            }

            if (port == null || pluginUuid == null || registerEvent == null)
            {
                Log("Missing launch arguments: " + string.Join(" ", args));
                return;
            }

            socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri("ws://127.0.0.1:" + port), CancellationToken.None);
            await Send(new Dictionary<string, object> { { "event", registerEvent }, { "uuid", pluginUuid } });
            Log("Registered with Stream Dock on port " + port);

            // Keeps the knob title in sync if the volume is changed elsewhere (Windows mixer, keyboard keys...).
            pollTimer = new Timer(_ => RefreshAll().Wait(), null, 1000, 1000);

            var buffer = new byte[16384];
            var message = new MemoryStream();
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result =
                    await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                string text = Encoding.UTF8.GetString(message.ToArray());
                message.SetLength(0);

                try
                {
                    await Handle(text);
                }
                catch (Exception ex)
                {
                    Log("Error handling message: " + ex.Message);
                }
            }

            Log("Socket closed, exiting.");
        }

        static async Task Handle(string text)
        {
            var msg = json.Deserialize<Dictionary<string, object>>(text);
            object evObj, ctxObj, actionObj, payloadObj;
            if (msg == null || !msg.TryGetValue("event", out evObj)) return;

            string ev = evObj as string;
            string context = msg.TryGetValue("context", out ctxObj) ? ctxObj as string : null;
            string action = msg.TryGetValue("action", out actionObj) ? actionObj as string : null;
            var payload = msg.TryGetValue("payload", out payloadObj) ? payloadObj as Dictionary<string, object> : null;
            if (context == null) return;

            Knob knob;
            switch (ev)
            {
                case "willAppear":
                    knob = new Knob { Action = action };
                    ApplySettings(knob, GetDict(payload, "settings"));
                    lock (knobsLock) knobs[context] = knob;
                    await Refresh(context, knob);
                    break;

                case "willDisappear":
                    lock (knobsLock) knobs.Remove(context);
                    break;

                case "didReceiveSettings":
                    knob = Find(context);
                    if (knob == null) return;
                    ApplySettings(knob, GetDict(payload, "settings"));
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
                    await SendDeviceList(context, action);
                    break;

                case "sendToPlugin":
                    // The property inspector asks for the device list, and also sends its settings directly
                    // in case the app doesn't echo setSettings back as didReceiveSettings.
                    if (payload == null) return;
                    if (payload.ContainsKey("refreshDevices")) await SendDeviceList(context, action);
                    knob = Find(context);
                    var settings = GetDict(payload, "settings");
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

        static async Task Rotate(string context, Knob knob, int ticks)
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
                Log("Volume change failed: " + ex.Message);
            }

            // Shows the warning triangle if the device is missing.
            if (!ok) await ShowAlert(context);
            else await RefreshAll();
        }

        static async Task ToggleMute(string context, Knob knob)
        {
            bool ok = false;
            try
            {
                Audio.SetMuted(knob.DeviceId, !Audio.IsMuted(knob.DeviceId));
                ok = true;
            }
            catch (Exception ex)
            {
                Log("Mute toggle failed: " + ex.Message);
            }

            if (!ok) await ShowAlert(context);
            else await RefreshAll();
        }

        static async Task RefreshAll()
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
                await Send(new Dictionary<string, object>
                {
                    { "event", "setTitle" },
                    { "context", context },
                    { "payload", new Dictionary<string, object> { { "title", title }, { "target", 0 } } }
                });
            }

            if (state != knob.LastState)
            {
                knob.LastState = state;
                await Send(new Dictionary<string, object>
                {
                    { "event", "setState" },
                    { "context", context },
                    { "payload", new Dictionary<string, object> { { "state", state } } }
                });
            }
        }

        static Task SendDeviceList(string context, string action)
        {
            var devices = new List<Dictionary<string, object>>();
            string defaultName = "";
            try
            {
                foreach (var d in Audio.ListOutputs())
                    devices.Add(new Dictionary<string, object> { { "id", d.Key }, { "name", d.Value } });
                defaultName = Audio.DefaultOutputName();
            }
            catch (Exception ex)
            {
                Log("Listing devices failed: " + ex.Message);
            }

            return Send(new Dictionary<string, object>
            {
                { "event", "sendToPropertyInspector" },
                { "action", action },
                { "context", context },
                { "payload", new Dictionary<string, object> { { "devices", devices }, { "defaultName", defaultName } } }
            });
        }

        static Task ShowAlert(string context)
        {
            return Send(new Dictionary<string, object> { { "event", "showAlert" }, { "context", context } });
        }

        static Knob Find(string context)
        {
            Knob knob;
            lock (knobsLock) return knobs.TryGetValue(context, out knob) ? knob : null;
        }

        static Dictionary<string, object> GetDict(Dictionary<string, object> parent, string key)
        {
            object v;
            return parent != null && parent.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        static async Task Send(Dictionary<string, object> payload)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json.Serialize(payload));
            await sendLock.WaitAsync();
            try
            {
                if (socket != null && socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                        CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Log("Send failed: " + ex.Message);
            }
            finally
            {
                sendLock.Release();
            }
        }

        static void Log(string line)
        {
            try
            {
                lock (logLock)
                    File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
            }
            catch
            {
                // logging must never crash the plugin
            }
        }
    }

    /// <summary>Volume and mute for a Windows output device via the Core Audio API.</summary>
    internal static class Audio
    {
        const int eRender = 0;
        const int eConsole = 0;
        const int DEVICE_STATE_ACTIVE = 1;
        const int STGM_READ = 0;
        const int CLSCTX_ALL = 23;
        const ushort VT_LPWSTR = 31;

        static readonly PropertyKey FriendlyName =
            new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PropVariant pv);

        public static float GetVolume(string deviceId)
        {
            IAudioEndpointVolume vol = GetEndpointVolume(deviceId);
            try
            {
                float level;
                Marshal.ThrowExceptionForHR(vol.GetMasterVolumeLevelScalar(out level));
                return level;
            }
            finally
            {
                Marshal.ReleaseComObject(vol);
            }
        }

        public static void SetVolume(string deviceId, float level)
        {
            IAudioEndpointVolume vol = GetEndpointVolume(deviceId);
            try
            {
                Guid eventContext = Guid.Empty;
                Marshal.ThrowExceptionForHR(vol.SetMasterVolumeLevelScalar(level, ref eventContext));
            }
            finally
            {
                Marshal.ReleaseComObject(vol);
            }
        }

        public static bool IsMuted(string deviceId)
        {
            IAudioEndpointVolume vol = GetEndpointVolume(deviceId);
            try
            {
                bool muted;
                Marshal.ThrowExceptionForHR(vol.GetMute(out muted));
                return muted;
            }
            finally
            {
                Marshal.ReleaseComObject(vol);
            }
        }

        public static void SetMuted(string deviceId, bool mute)
        {
            IAudioEndpointVolume vol = GetEndpointVolume(deviceId);
            try
            {
                Guid eventContext = Guid.Empty;
                Marshal.ThrowExceptionForHR(vol.SetMute(mute, ref eventContext));
            }
            finally
            {
                Marshal.ReleaseComObject(vol);
            }
        }

        /// <summary>Active output devices as (device id, friendly name).</summary>
        public static List<KeyValuePair<string, string>> ListOutputs()
        {
            var result = new List<KeyValuePair<string, string>>();
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDeviceCollection collection = null;
            try
            {
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out collection));
                uint count;
                Marshal.ThrowExceptionForHR(collection.GetCount(out count));
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device;
                    if (collection.Item(i, out device) != 0) continue;
                    try
                    {
                        string id;
                        Marshal.ThrowExceptionForHR(device.GetId(out id));
                        result.Add(new KeyValuePair<string, string>(id, GetName(device)));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                if (collection != null) Marshal.ReleaseComObject(collection);
                Marshal.ReleaseComObject(enumerator);
            }
            return result;
        }

        public static string DefaultOutputName()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice device = null;
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out device) != 0) return "";
                return GetName(device);
            }
            finally
            {
                if (device != null) Marshal.ReleaseComObject(device);
                Marshal.ReleaseComObject(enumerator);
            }
        }

        static string GetName(IMMDevice device)
        {
            IPropertyStore store;
            if (device.OpenPropertyStore(STGM_READ, out store) != 0) return "Unknown device";
            try
            {
                PropertyKey key = FriendlyName;
                PropVariant value;
                if (store.GetValue(ref key, out value) != 0) return "Unknown device";
                try
                {
                    return value.vt == VT_LPWSTR ? Marshal.PtrToStringUni(value.pointer) : "Unknown device";
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        /// <summary>An empty device id means the current default output device.</summary>
        static IAudioEndpointVolume GetEndpointVolume(string deviceId)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice device = null;
            try
            {
                if (string.IsNullOrEmpty(deviceId))
                    Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out device));
                else
                    Marshal.ThrowExceptionForHR(enumerator.GetDevice(deviceId, out device));

                // GetDevice still returns unplugged/disabled devices; treat those as missing.
                int state;
                Marshal.ThrowExceptionForHR(device.GetState(out state));
                if (state != DEVICE_STATE_ACTIVE) throw new InvalidOperationException("Device is not active");

                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object volume;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out volume));
                return (IAudioEndpointVolume)volume;
            }
            finally
            {
                if (device != null) Marshal.ReleaseComObject(device);
                Marshal.ReleaseComObject(enumerator);
            }
        }
    }

    // ---- Core Audio COM interop (only the vtable slots up to what we call) ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid fmtid;
        public int pid;
    }

    /// <summary>PROPVARIANT: 8-byte header followed by a two-pointer union (16 bytes on x86, 24 on x64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort vt;
        public ushort reserved1, reserved2, reserved3;
        public IntPtr pointer;
        public IntPtr pointer2;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
