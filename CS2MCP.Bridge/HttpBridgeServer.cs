using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Game.SceneFlow;

namespace CS2MCP
{
    /// <summary>
    /// Minimal HTTP/1.1 server on top of TcpListener (avoids HttpListener's
    /// URL ACL requirements on Windows). Localhost only, Connection: close,
    /// single request per connection. The only intended client is the external
    /// cs2-mcp process, but curl works too for debugging.
    /// </summary>
    public sealed class HttpBridgeServer
    {
        private const int MaxHeaderBytes = 16 * 1024;
        private const int MaxBodyBytes = 1024 * 1024;
        private const int MainThreadTimeoutMs = 10000;
        private const string TokenHeader = "X-CS2MCP-Token";
        private const string TokenEnvVar = "CS2MCP_TOKEN";

        private readonly int m_Port;
        private readonly string m_Token;
        private readonly Action<BridgeRequest> m_EnqueueToMainThread;
        private TcpListener m_Listener;
        private Thread m_AcceptThread;
        private volatile bool m_Running;

        public HttpBridgeServer(int port, Action<BridgeRequest> enqueueToMainThread)
        {
            m_Port = port;
            m_EnqueueToMainThread = enqueueToMainThread;
            string token = Environment.GetEnvironmentVariable(TokenEnvVar);
            m_Token = string.IsNullOrEmpty(token) ? null : token.Trim();
        }

        public void Start()
        {
            if (m_Token == null)
            {
                Mod.Log.Warn($"{TokenEnvVar} is not set: requests are not authenticated (Host/Origin/POST checks still apply)");
            }
            m_Listener = new TcpListener(IPAddress.Loopback, m_Port);
            m_Listener.Start();
            m_Running = true;
            m_AcceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "CS2MCP.Bridge.Accept",
            };
            m_AcceptThread.Start();
        }

        public void Stop()
        {
            m_Running = false;
            try
            {
                m_Listener?.Stop();
            }
            catch
            {
                // listener already gone
            }
            m_Listener = null;
        }

        private void AcceptLoop()
        {
            while (m_Running)
            {
                TcpClient client;
                try
                {
                    client = m_Listener.AcceptTcpClient();
                }
                catch
                {
                    break; // Stop() closes the listener and unblocks Accept
                }
                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    client.ReceiveTimeout = 15000;
                    client.SendTimeout = 15000;

                    BridgeRequest request = ReadRequest(stream);
                    if (request == null)
                    {
                        WriteResponse(stream, BridgeResponse.Error(400, "malformed HTTP request"));
                        return;
                    }
                    WriteResponse(stream, Dispatch(request));
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"bridge client error: {e.Message}");
            }
        }

        private BridgeResponse Dispatch(BridgeRequest request)
        {
            // DNS rebinding: a browser page reaching us through its own
            // hostname still sends that hostname in Host.
            if (!IsLoopbackHost(request.Host))
            {
                return BridgeResponse.Error(403, "Host header must be 127.0.0.1:" + m_Port + " or localhost:" + m_Port);
            }
            // Browsers attach Origin to cross-site requests; the MCP server never does.
            if (request.Origin != null)
            {
                return BridgeResponse.Error(403, "cross-origin requests are not allowed");
            }

            // Liveness check, answered off the simulation thread so it works
            // even while the game is loading a save.
            if (request.Path == "/ping")
            {
                return HandlePing();
            }

            // Token is optional: enforced only when CS2MCP_TOKEN is set for the game process.
            if (m_Token != null && !TokenEquals(request.Token, m_Token))
            {
                return BridgeResponse.Error(401, "missing or invalid " + TokenHeader + " header");
            }
            if (IsMutatingPath(request.Path) && request.Method != "POST")
            {
                return BridgeResponse.Error(405, request.Path + " changes game state and requires POST");
            }

            m_EnqueueToMainThread(request);
            BridgeResponse response = request.WaitForResponse(MainThreadTimeoutMs);
            return response ?? BridgeResponse.Error(503,
                "simulation thread did not respond within 10s (game still loading, or bridge system disabled)");
        }

        private bool IsLoopbackHost(string host)
        {
            return host != null
                && (host.Equals("127.0.0.1:" + m_Port, StringComparison.OrdinalIgnoreCase)
                    || host.Equals("localhost:" + m_Port, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Constant-time comparison so the token can't be guessed by timing.</summary>
        private static bool TokenEquals(string supplied, string expected)
        {
            if (supplied == null)
            {
                return false;
            }
            byte[] a = Encoding.UTF8.GetBytes(supplied);
            byte[] b = Encoding.UTF8.GetBytes(expected);
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < b.Length; i++)
            {
                diff |= (i < a.Length ? a[i] : 0) ^ b[i];
            }
            return diff == 0;
        }

        /// <summary>
        /// Routes that change game state. Must match isMutatingPath in
        /// mcp-server/src/index.ts.
        /// </summary>
        public static bool IsMutatingPath(string path)
        {
            return path.StartsWith("/build/", StringComparison.Ordinal)
                || path.StartsWith("/sim/", StringComparison.Ordinal)
                || path.EndsWith("/set", StringComparison.Ordinal)
                || path == "/game/save"
                || path == "/city/tiles/buy"
                || path == "/transit/stops/place"
                || path == "/transit/lines/create"
                || path == "/transit/lines/delete";
        }

        private static BridgeResponse HandlePing()
        {
            string gameMode = "Unknown";
            bool isLoading = false;
            try
            {
                GameManager manager = GameManager.instance;
                if (manager != null)
                {
                    gameMode = manager.gameMode.ToString();
                    isLoading = manager.isGameLoading;
                }
            }
            catch
            {
                // GameManager not ready yet; report Unknown
            }
            return BridgeResponse.Json(new
            {
                ok = true,
                mod = Mod.Name,
                version = Mod.Version,
                gameMode,
                isLoading,
            });
        }

        private static BridgeRequest ReadRequest(NetworkStream stream)
        {
            // Read until end of headers (\r\n\r\n).
            var headerBytes = new MemoryStream();
            int matched = 0;
            while (matched < 4)
            {
                int b = stream.ReadByte();
                if (b < 0 || headerBytes.Length > MaxHeaderBytes)
                {
                    return null;
                }
                headerBytes.WriteByte((byte)b);
                bool expectCr = matched == 0 || matched == 2;
                matched = (expectCr && b == '\r') || (!expectCr && b == '\n') ? matched + 1 : (b == '\r' ? 1 : 0);
            }

            string headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length < 3)
            {
                return null;
            }

            var request = new BridgeRequest { Method = requestLine[0].ToUpperInvariant() };
            ParseUrl(requestLine[1], request);

            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }
                string name = lines[i].Substring(0, colon).Trim();
                string value = lines[i].Substring(colon + 1).Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(value, out contentLength);
                }
                else if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    request.Host = value;
                }
                else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase))
                {
                    request.Origin = value;
                }
                else if (name.Equals(TokenHeader, StringComparison.OrdinalIgnoreCase))
                {
                    request.Token = value;
                }
            }

            if (contentLength > 0)
            {
                if (contentLength > MaxBodyBytes)
                {
                    return null;
                }
                var body = new byte[contentLength];
                int read = 0;
                while (read < contentLength)
                {
                    int n = stream.Read(body, read, contentLength - read);
                    if (n <= 0)
                    {
                        return null;
                    }
                    read += n;
                }
                request.Body = Encoding.UTF8.GetString(body);
            }

            return request;
        }

        private static void ParseUrl(string rawUrl, BridgeRequest request)
        {
            int questionMark = rawUrl.IndexOf('?');
            if (questionMark < 0)
            {
                request.Path = rawUrl;
                return;
            }
            request.Path = rawUrl.Substring(0, questionMark);
            string queryText = rawUrl.Substring(questionMark + 1);
            foreach (string pair in queryText.Split('&'))
            {
                if (pair.Length == 0)
                {
                    continue;
                }
                int equals = pair.IndexOf('=');
                string key = equals < 0 ? pair : pair.Substring(0, equals);
                string value = equals < 0 ? "" : pair.Substring(equals + 1);
                request.Query[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
            }
        }

        private static void WriteResponse(NetworkStream stream, BridgeResponse response)
        {
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(response.Status).Append(response.Status == 200 ? " OK" : " Error").Append("\r\n");
            head.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
            head.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
            head.Append("Connection: close\r\n");
            head.Append("\r\n");
            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            if (response.Body.Length > 0)
            {
                stream.Write(response.Body, 0, response.Body.Length);
            }
            stream.Flush();
        }
    }
}
