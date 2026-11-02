// Mic Mute plugin for AJAZZ / MiraBox Stream Dock (Elgato-style SDK v2).
// Written in C# 5 so it compiles with the csc.exe that ships with Windows (.NET Framework 4.x).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace MicMute
{
    internal static class Program
    {
        // Button states as defined in manifest.json: 0 = mic live, 1 = mic muted.
        const int StateLive = 0;
        const int StateMuted = 1;

        static ClientWebSocket socket;
        static readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        static readonly JavaScriptSerializer json = new JavaScriptSerializer();
        static readonly HashSet<string> contexts = new HashSet<string>();
        static readonly object contextsLock = new object();
        static readonly object logLock = new object();
        static int lastState = -1;
        static System.Threading.Timer pollTimer;
        static string logPath;

        static int Main(string[] args)
        {
            logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "micmute.log");
            try
            {
                AppDomain.CurrentDomain.ProcessExit += (s, e) => Tray.Stop();
                Run(args).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception ex)
            {
                Log("Fatal: " + ex);
                return 1;
            }
            finally
            {
                Tray.Stop();
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

            // Taskbar tray icon mirroring the mute state; left-click toggles.
            Tray.Clicked = () => Task.Run(() => Toggle(null));
            Tray.Start(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "images"));
            await ApplyState(SafeCurrentState());

            // Keeps the key in sync if the mic is muted elsewhere (Teams, Windows settings, a headset button...).
            pollTimer = new System.Threading.Timer(_ => Poll(), null, 1000, 1000);

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
            object evObj, ctxObj;
            if (msg == null || !msg.TryGetValue("event", out evObj)) return;

            string ev = evObj as string;
            string context = msg.TryGetValue("context", out ctxObj) ? ctxObj as string : null;
            if (context == null) return;

            switch (ev)
            {
                case "willAppear":
                    lock (contextsLock) contexts.Add(context);
                    await SetState(context, SafeCurrentState());
                    break;

                case "willDisappear":
                    lock (contextsLock) contexts.Remove(context);
                    break;

                case "keyDown":
                    await Toggle(context);
                    break;
            }
        }

        static async Task Toggle(string context)
        {
            int state = -1;
            try
            {
                Audio.SetMuted(!Audio.IsMuted());
                state = Audio.IsMuted() ? StateMuted : StateLive;
            }
            catch (Exception ex)
            {
                Log("Toggle failed: " + ex.Message);
            }

            if (state < 0)
            {
                // Shows the warning triangle on the key (context is null when toggled from the tray icon).
                if (context != null)
                    await Send(new Dictionary<string, object> { { "event", "showAlert" }, { "context", context } });
                return;
            }

            await ApplyState(state);
        }

        static void Poll()
        {
            int state;
            try
            {
                state = Audio.IsMuted() ? StateMuted : StateLive;
            }
            catch
            {
                return; // no microphone right now; leave the key as is
            }

            if (state == lastState) return;
            ApplyState(state).Wait();
        }

        static int SafeCurrentState()
        {
            try { return Audio.IsMuted() ? StateMuted : StateLive; }
            catch { return StateLive; }
        }

        /// <summary>Pushes a mute state to the tray icon and every key showing this action.</summary>
        static Task ApplyState(int state)
        {
            lastState = state;
            Tray.SetMuted(state == StateMuted);
            return SetStateAll(state);
        }

        static async Task SetStateAll(int state)
        {
            List<string> targets;
            lock (contextsLock) targets = new List<string>(contexts);
            foreach (string ctx in targets)
                await SetState(ctx, state);
        }

        static Task SetState(string context, int state)
        {
            return Send(new Dictionary<string, object>
            {
                { "event", "setState" },
                { "context", context },
                { "payload", new Dictionary<string, object> { { "state", state } } }
            });
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

        internal static void Log(string line)
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

    /// <summary>Notification-area icon showing the mic state. Runs its own STA thread with a WinForms message loop.</summary>
    internal static class Tray
    {
        public static Action Clicked;

        static NotifyIcon icon;
        static Icon iconLive, iconMuted;
        static SynchronizationContext ui;
        static Thread thread;
        static readonly ManualResetEvent ready = new ManualResetEvent(false);

        public static void Start(string imagesDir)
        {
            thread = new Thread(() => Loop(imagesDir));
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.WaitOne(5000);
        }

        static void Loop(string imagesDir)
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                ui = SynchronizationContext.Current;
                iconLive = LoadIcon(Path.Combine(imagesDir, "micOn.png"));
                iconMuted = LoadIcon(Path.Combine(imagesDir, "micOff.png"));

                var menu = new ContextMenuStrip();
                menu.Items.Add("Hide tray icon", null, (s, e) => icon.Visible = false);

                icon = new NotifyIcon { Icon = iconLive, Text = "Mic live", ContextMenuStrip = menu, Visible = true };
                icon.MouseClick += (s, e) =>
                {
                    Action handler = Clicked;
                    if (e.Button == MouseButtons.Left && handler != null) handler();
                };
            }
            catch (Exception ex)
            {
                Program.Log("Tray icon failed: " + ex.Message);
                icon = null;
                return;
            }
            finally
            {
                ready.Set();
            }

            Application.Run();
        }

        static Icon LoadIcon(string path)
        {
            if (!File.Exists(path)) return SystemIcons.Application;
            using (var src = new Bitmap(path))
            using (var small = new Bitmap(src, 32, 32))
                return Icon.FromHandle(small.GetHicon());
        }

        public static void SetMuted(bool muted)
        {
            if (icon == null || ui == null) return;
            ui.Post(_ =>
            {
                icon.Icon = muted ? iconMuted : iconLive;
                icon.Text = muted ? "Mic muted" : "Mic live";
            }, null);
        }

        /// <summary>Removes the icon so no "ghost" stays in the tray after the plugin exits.</summary>
        public static void Stop()
        {
            NotifyIcon current = icon;
            if (current == null || ui == null || thread == null || !thread.IsAlive) return;
            icon = null;
            try
            {
                ui.Send(_ =>
                {
                    current.Visible = false;
                    current.Dispose();
                    Application.ExitThread();
                }, null);
            }
            catch
            {
                // process is exiting anyway
            }
        }
    }

    /// <summary>Mutes / unmutes the default Windows capture device via the Core Audio API.</summary>
    internal static class Audio
    {
        const int eCapture = 1;
        const int eConsole = 0;         // default device for most apps
        const int eCommunications = 2;  // default device for Teams, Zoom, Discord etc.
        const int CLSCTX_ALL = 23;

        public static bool IsMuted()
        {
            IAudioEndpointVolume vol = GetVolume(eConsole);
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

        /// <summary>Applies to both the default and the default-communications mic (usually the same device).</summary>
        public static void SetMuted(bool mute)
        {
            foreach (int role in new[] { eConsole, eCommunications })
            {
                IAudioEndpointVolume vol = GetVolume(role);
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
        }

        static IAudioEndpointVolume GetVolume(int role)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice device = null;
            try
            {
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(eCapture, role, out device));
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

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
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
