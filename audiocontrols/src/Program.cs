// Audio Controls plugin for AJAZZ / MiraBox Stream Dock (Elgato-style SDK v2).
// One process serves every action: mic mute, per-device output volume and output device switching.
// Written in C# 5 so it compiles with the csc.exe that ships with Windows (.NET Framework 4.x).

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AudioControls
{
    /// <summary>One action type from manifest.json. Receives the events for every key/knob showing it.</summary>
    internal abstract class PluginAction
    {
        public abstract string Uuid { get; }
        public abstract Task Handle(string ev, string context, Dictionary<string, object> payload);

        /// <summary>Called every second to pick up changes made outside the plugin.</summary>
        public abstract Task Poll();
    }

    internal static class Program
    {
        static readonly PluginAction[] actions =
        {
            new MicMuteAction(),
            new VolumeAction(),
            new OutputSwitchAction()
        };

        static Timer pollTimer;
        static int polling;

        static int Main(string[] args)
        {
            StreamDock.LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audiocontrols.log");
            try
            {
                Run(args).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception ex)
            {
                StreamDock.Log("Fatal: " + ex);
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
                StreamDock.Log("Missing launch arguments: " + string.Join(" ", args));
                return;
            }

            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri("ws://127.0.0.1:" + port), CancellationToken.None);
            StreamDock.Socket = socket;
            await StreamDock.Send(new Dictionary<string, object> { { "event", registerEvent }, { "uuid", pluginUuid } });
            StreamDock.Log("Registered with Stream Dock on port " + port);

            pollTimer = new Timer(_ => PollAll(), null, 1000, 1000);

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
                    await Dispatch(text);
                }
                catch (Exception ex)
                {
                    StreamDock.Log("Error handling message: " + ex.Message);
                }
            }

            StreamDock.Log("Socket closed, exiting.");
        }

        /// <summary>Routes a Stream Dock event to the action it belongs to.</summary>
        static Task Dispatch(string text)
        {
            var msg = StreamDock.Json.Deserialize<Dictionary<string, object>>(text);
            object evObj, ctxObj, actionObj, payloadObj;
            if (msg == null || !msg.TryGetValue("event", out evObj)) return Task.FromResult(0);

            string ev = evObj as string;
            string context = msg.TryGetValue("context", out ctxObj) ? ctxObj as string : null;
            string uuid = msg.TryGetValue("action", out actionObj) ? actionObj as string : null;
            var payload = msg.TryGetValue("payload", out payloadObj) ? payloadObj as Dictionary<string, object> : null;
            if (context == null || uuid == null) return Task.FromResult(0);

            foreach (PluginAction action in actions)
            {
                if (action.Uuid == uuid) return action.Handle(ev, context, payload);
            }
            return Task.FromResult(0);
        }

        static void PollAll()
        {
            // Skip a tick rather than pile up if the previous poll is still running.
            if (Interlocked.Exchange(ref polling, 1) == 1) return;
            try
            {
                foreach (PluginAction action in actions)
                {
                    try
                    {
                        action.Poll().Wait();
                    }
                    catch (Exception ex)
                    {
                        StreamDock.Log("Poll failed for " + action.Uuid + ": " + ex.Message);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref polling, 0);
            }
        }
    }

    /// <summary>Messages to the Stream Dock app, plus logging and settings helpers shared by all actions.</summary>
    internal static class StreamDock
    {
        public static ClientWebSocket Socket;
        public static string LogPath;
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        static readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        static readonly object logLock = new object();

        public static Task SetState(string context, int state)
        {
            return Send(new Dictionary<string, object>
            {
                { "event", "setState" },
                { "context", context },
                { "payload", new Dictionary<string, object> { { "state", state } } }
            });
        }

        public static Task SetTitle(string context, string title)
        {
            return Send(new Dictionary<string, object>
            {
                { "event", "setTitle" },
                { "context", context },
                { "payload", new Dictionary<string, object> { { "title", title }, { "target", 0 } } }
            });
        }

        /// <summary>Replaces the key image; image is a "data:image/png;base64,..." URL.</summary>
        public static Task SetImage(string context, string image)
        {
            return Send(new Dictionary<string, object>
            {
                { "event", "setImage" },
                { "context", context },
                { "payload", new Dictionary<string, object> { { "image", image }, { "target", 0 } } }
            });
        }

        /// <summary>Shows the warning triangle on the key or knob.</summary>
        public static Task ShowAlert(string context)
        {
            return Send(new Dictionary<string, object> { { "event", "showAlert" }, { "context", context } });
        }

        public static Task SendToInspector(string action, string context, Dictionary<string, object> payload)
        {
            return Send(new Dictionary<string, object>
            {
                { "event", "sendToPropertyInspector" },
                { "action", action },
                { "context", context },
                { "payload", payload }
            });
        }

        /// <summary>Active output devices plus the current default's name, for the settings panels.</summary>
        public static Task SendOutputDevices(string action, string context)
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

            return SendToInspector(action, context,
                new Dictionary<string, object> { { "devices", devices }, { "defaultName", defaultName } });
        }

        public static async Task Send(Dictionary<string, object> payload)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Json.Serialize(payload));
            await sendLock.WaitAsync();
            try
            {
                if (Socket != null && Socket.State == WebSocketState.Open)
                {
                    await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
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

        public static Dictionary<string, object> GetDict(Dictionary<string, object> parent, string key)
        {
            object v;
            return parent != null && parent.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        public static void Log(string line)
        {
            try
            {
                lock (logLock)
                    File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
            }
            catch
            {
                // logging must never crash the plugin
            }
        }
    }
}
