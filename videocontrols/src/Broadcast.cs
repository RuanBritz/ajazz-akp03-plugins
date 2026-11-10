using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace VideoControls
{
    /// <summary>Broadcast isn't running, or something other than Broadcast is on its port.</summary>
    internal class BroadcastUnavailableException : Exception
    {
        public BroadcastUnavailableException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>A Broadcast tool rejected the call (structuredContent.error, or a JSON-RPC error).</summary>
    internal class BroadcastToolException : Exception
    {
        public readonly int Code;
        public readonly bool Retryable;

        public BroadcastToolException(int code, string message, bool retryable)
            : base(message + " (" + code + ")")
        {
            Code = code;
            Retryable = retryable;
        }
    }

    /// <summary>
    /// Minimal MCP client for NVIDIA Broadcast's local gateway (Streamable HTTP on 127.0.0.1, no auth).
    /// The port is read from %APPDATA%\nvidia-broadcast\gateway.json (base64 JSON {"port":n}) on every
    /// connect, since Broadcast moves to 18101-18109 when 18100 is taken. Thread-safe: one call at a time.
    /// </summary>
    internal static class Broadcast
    {
        const string ProtocolVersion = "2025-06-18";
        const string ExpectedServerName = "nvidia-broadcast";

        // Gateway tool error codes (see Broadcast's mcp-tool-contract.md).
        public const int InvalidParams = -32602;
        public const int Internal = -32603;
        public const int RateLimited = -33102;
        public const int TooManyInFlight = -33103;

        static readonly object sync = new object();
        static string endpoint;   // null until a session is set up
        static string sessionId;  // Mcp-Session-Id, if the server issues one (2.3.0 doesn't)
        static int nextId = 1;

        /// <summary>
        /// Calls a gateway tool and returns its structuredContent. Reconnects once if the session or the
        /// connection is gone, and retries once on a retryable error or a rate limit.
        /// </summary>
        public static Dictionary<string, object> Call(string tool, Dictionary<string, object> args, int timeoutMs)
        {
            lock (sync)
            {
                bool reconnected = false, retried = false;
                while (true)
                {
                    try
                    {
                        if (endpoint == null) Connect();
                        var result = Rpc("tools/call", new Dictionary<string, object>
                        {
                            { "name", tool },
                            { "arguments", args ?? new Dictionary<string, object>() }
                        }, timeoutMs);
                        return ToolResult(result);
                    }
                    catch (WebException ex)
                    {
                        // Connection refused / reset, or 400/404 for an expired session: start over once.
                        endpoint = null;
                        sessionId = null;
                        if (reconnected) throw new BroadcastUnavailableException("Broadcast unavailable: " + ex.Message, ex);
                        reconnected = true;
                    }
                    catch (BroadcastToolException ex)
                    {
                        if (retried) throw;
                        if (ex.Retryable && ex.Code == Internal) Thread.Sleep(500);
                        else if (ex.Code == RateLimited || ex.Code == TooManyInFlight) Thread.Sleep(1500);
                        else throw;
                        retried = true;
                    }
                }
            }
        }

        /// <summary>initialize + notifications/initialized, refusing anything that isn't NVIDIA Broadcast.</summary>
        static void Connect()
        {
            int port = Port();
            endpoint = "http://127.0.0.1:" + port + "/gateway";
            sessionId = null;

            Dictionary<string, object> result;
            try
            {
                result = Rpc("initialize", new Dictionary<string, object>
                {
                    { "protocolVersion", ProtocolVersion },
                    { "capabilities", new Dictionary<string, object>() },
                    { "clientInfo", new Dictionary<string, object> { { "name", "videocontrols" }, { "version", "1.1.0" } } }
                }, 5000);
            }
            catch
            {
                endpoint = null;
                throw;
            }

            var serverInfo = StreamDock.GetDict(result, "serverInfo");
            object name;
            if (serverInfo == null || !serverInfo.TryGetValue("name", out name) || !(name is string) ||
                (string)name != ExpectedServerName)
            {
                endpoint = null;
                throw new BroadcastUnavailableException("Port " + port + " is not NVIDIA Broadcast; not sending anything.");
            }

            Post(Envelope("notifications/initialized", null, null), 5000);
        }

        /// <summary>Reads the gateway port Broadcast last published.</summary>
        static int Port()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"nvidia-broadcast\gateway.json");
            try
            {
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(File.ReadAllText(path).Trim()));
                var record = StreamDock.Json.Deserialize<Dictionary<string, object>>(json);
                int port = Convert.ToInt32(record["port"]);
                if (port < 1 || port > 65535) throw new FormatException("port " + port);
                return port;
            }
            catch (Exception ex)
            {
                throw new BroadcastUnavailableException("Broadcast gateway not found (" + path + "): " + ex.Message, ex);
            }
        }

        static Dictionary<string, object> Envelope(string method, object parameters, int? id)
        {
            var msg = new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "method", method } };
            if (id.HasValue) msg["id"] = id.Value;
            if (parameters != null) msg["params"] = parameters;
            return msg;
        }

        /// <summary>Sends a JSON-RPC request and returns its "result", throwing on a JSON-RPC error.</summary>
        static Dictionary<string, object> Rpc(string method, Dictionary<string, object> parameters, int timeoutMs)
        {
            string body = Post(Envelope(method, parameters, nextId++), timeoutMs);
            var response = StreamDock.Json.Deserialize<Dictionary<string, object>>(body);

            var error = StreamDock.GetDict(response, "error");
            if (error != null) throw ToolError(error);

            var result = StreamDock.GetDict(response, "result");
            if (result == null) throw new BroadcastToolException(Internal, "Empty response to " + method, false);
            return result;
        }

        /// <summary>Unwraps a tools/call result: structuredContent on success, a tool error otherwise.</summary>
        static Dictionary<string, object> ToolResult(Dictionary<string, object> result)
        {
            var structured = StreamDock.GetDict(result, "structuredContent");
            object isError;
            if (result.TryGetValue("isError", out isError) && isError is bool && (bool)isError)
            {
                var error = StreamDock.GetDict(structured, "error");
                if (error != null) throw ToolError(error);
                throw new BroadcastToolException(Internal, "Tool failed", false);
            }
            return structured ?? new Dictionary<string, object>();
        }

        static BroadcastToolException ToolError(Dictionary<string, object> error)
        {
            object code, message, retryable;
            int c = error.TryGetValue("code", out code) ? Convert.ToInt32(code) : Internal;
            string m = error.TryGetValue("message", out message) && message != null ? message.ToString() : "Unknown error";
            bool r = error.TryGetValue("retryable", out retryable) && retryable is bool && (bool)retryable;
            return new BroadcastToolException(c, m, r);
        }

        /// <summary>POSTs one message. The reply is plain JSON or an SSE stream; for SSE the last data: line wins.</summary>
        static string Post(Dictionary<string, object> message, int timeoutMs)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(StreamDock.Json.Serialize(message));
            var request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "POST";
            request.Proxy = null; // never route loopback traffic through a system proxy
            request.ContentType = "application/json";
            request.Accept = "application/json, text/event-stream";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.KeepAlive = true;
            request.Headers["MCP-Protocol-Version"] = ProtocolVersion;
            if (sessionId != null) request.Headers["Mcp-Session-Id"] = sessionId;
            request.ContentLength = bytes.Length;
            using (Stream s = request.GetRequestStream()) s.Write(bytes, 0, bytes.Length);

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                string id = response.Headers["Mcp-Session-Id"];
                if (!string.IsNullOrEmpty(id)) sessionId = id;

                string text = reader.ReadToEnd();
                string type = response.ContentType ?? "";
                if (type.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) < 0) return text;

                string last = null;
                foreach (string line in text.Split('\n'))
                {
                    string l = line.TrimEnd('\r');
                    if (l.StartsWith("data:")) last = l.Substring(5).TrimStart();
                }
                return last ?? "";
            }
        }

        // --- JSON helpers for tool results ---

        public static IEnumerable<Dictionary<string, object>> GetList(Dictionary<string, object> parent, string key)
        {
            object v;
            var list = parent != null && parent.TryGetValue(key, out v) ? v as IEnumerable : null;
            if (list == null) yield break;
            foreach (object item in list)
            {
                var d = item as Dictionary<string, object>;
                if (d != null) yield return d;
            }
        }

        public static string GetString(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? v.ToString() : null;
        }

        public static int GetInt(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt32(v); }
            catch { return 0; }
        }

        public static bool GetBool(Dictionary<string, object> d, string key, bool fallback)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v is bool ? (bool)v : fallback;
        }
    }
}
