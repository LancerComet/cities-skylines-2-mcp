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
        // 大城原生异步保存可正常超过10秒；SDK保存预算为60秒，留出传输余量。
        private const int SaveResponseTimeoutMs = 45000;
        private const string TokenHeader = "X-CS2MCP-Token";
        private const string TokenEnvVar = "CS2MCP_TOKEN";

        private readonly int m_Port;
        private readonly string m_Token;
        private readonly Action<BridgeRequest> m_EnqueueToMainThread;
        private readonly Func<BridgeRequest, BridgeResponse> m_ReadNativeCapture;
        private readonly object m_DispatchGate = new object();
        private TcpListener m_Listener;
        private Thread m_AcceptThread;
        private volatile bool m_Running;

        public HttpBridgeServer(int port, Action<BridgeRequest> enqueueToMainThread, Func<BridgeRequest, BridgeResponse> readNativeCapture = null)
        {
            m_Port = port;
            m_EnqueueToMainThread = enqueueToMainThread;
            m_ReadNativeCapture = readNativeCapture;
            string token = Environment.GetEnvironmentVariable(TokenEnvVar);
            m_Token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
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
            // 与请求入队共用同步门：Stop返回后，不再有旧连接交付新请求。
            lock (m_DispatchGate)
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
            // 所有拒绝发生在入队及原生取景结果领取之前；保留生产关闭同步门。
            if (!IsLoopbackHost(request.Host))
                return BridgeResponse.Error(403, "Host header must be 127.0.0.1:" + m_Port + " or localhost:" + m_Port);
            if (request.Origin != null)
                return BridgeResponse.Error(403, "cross-origin requests are not allowed");
            lock (m_DispatchGate)
            {
                if (!m_Running) return StoppedBeforeHandler(enqueued: false);
                // Liveness check, answered off the simulation thread so it works
                // even while the game is loading a save.
                if (request.Path == "/ping")
                {
                    return HandlePing();
                }
                // ping免token，其余读写均受可选token保护。预览也使用POST，但不是已应用。
                if (m_Token != null && !TokenEquals(request.Token, m_Token))
                    return BridgeResponse.Error(401, "missing or invalid " + TokenHeader + " header");
                if (IsMutatingPath(request.Path) && request.Method != "POST")
                    return BridgeResponse.Error(405, request.Path + " changes game state and requires POST");
                // 只领取既有托管结果；即使相机首帧耗时，也不再排到被其占用的游戏更新中。
                if (request.Path == "/screenshot" && request.Query.ContainsKey("captureId"))
                    return m_ReadNativeCapture != null ? m_ReadNativeCapture(request)
                        : BridgeResponse.Error(409, "当前桥接没有可查询的原生取景结果");

                if (request.Path.StartsWith("/build/", StringComparison.Ordinal) || request.Path == "/transit/stops/relocate") {
                    var memory = RuntimeMemory.Read();
                    if (!memory.constructionAllowed)
                        return BridgeResponse.Json(new {
                            code = "resource_budget", failurePhase = "prequeue", enqueued = false,
                            handlerStarted = false, mayHaveApplied = false, safeToRetry = false,
                            reason = memory.unavailableReason != null ? "counter_unavailable" : memory.constructionFailureReason,
                            error = memory.unavailableReason != null
                                ? "内存计数器无法核验；施工/预览尚未排队。先核计数器，不自动重试或重启；保存和正常退出仍可用。"
                                : "内存提交预算不足；施工/预览尚未排队。先核实际资源压力，待余量恢复后读取新现场；不自动重试旧批次或要求重启。",
                            resourceBudget = memory
                        }, 409);
                }
                m_EnqueueToMainThread(request);
            }
            // 等待不能持有关闭门；StopServer需要完成仍未处理的排队请求。
            int timeoutMs = request.Path == "/game/save" ? SaveResponseTimeoutMs : MainThreadTimeoutMs;
            BridgeResponse response = request.WaitForResponse(timeoutMs);
            return response ?? BridgeResponse.Json(new {
                code = "response_timeout",
                error = $"simulation thread did not respond within {timeoutMs / 1000}s; result unknown, query actual state before any further write",
                failurePhase = "waiting_for_response", enqueued = true,
                handlerStarted = (bool?)null, mayHaveApplied = true, safeToRetry = false,
                timeoutMs
            }, 503);
        }

        /// <summary>只用于尚未交付或尚在队列的请求；已交给处理器的异步结果不能由停止路径覆盖。</summary>
        internal static BridgeResponse StoppedBeforeHandler(bool enqueued)
        {
            return BridgeResponse.Json(new {
                code = "bridge_stopped", error = "桥接已停止，请求尚未开始处理；不自动重试。",
                failurePhase = enqueued ? "before_handler" : "prequeue", enqueued,
                handlerStarted = false, mayHaveApplied = false, safeToRetry = false
            }, 503);
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
        /// mcp-server/src/bridge-client.ts (requiresPost).
        /// </summary>
        public static bool IsMutatingPath(string path)
        {
            return path == "/game/milestone-popup" || path.StartsWith("/build/", StringComparison.Ordinal)
                || path.StartsWith("/sim/", StringComparison.Ordinal)
                || path.EndsWith("/set", StringComparison.Ordinal)
                || path.EndsWith("/purchase", StringComparison.Ordinal)
                || path.EndsWith("/buy", StringComparison.Ordinal)
                || path.EndsWith("/save", StringComparison.Ordinal)
                || path.EndsWith("/control", StringComparison.Ordinal)
                || path.EndsWith("/run", StringComparison.Ordinal)
                || path.EndsWith("/create", StringComparison.Ordinal)
                || path.EndsWith("/delete", StringComparison.Ordinal)
                || path.EndsWith("/place", StringComparison.Ordinal)
                || path == "/transit/stops/relocate"
                || path == "/mods/anarchy/normal-construction"
                || path == "/mods/anarchy/props/refresh"
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
                memory = RuntimeMemory.Read(),
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
            if (requestLine.Length != 3 || (requestLine[2] != "HTTP/1.1" && requestLine[2] != "HTTP/1.0")
                || !requestLine[1].StartsWith("/", StringComparison.Ordinal) || requestLine[1].StartsWith("//", StringComparison.Ordinal))
            {
                return null;
            }

            var request = new BridgeRequest { Method = requestLine[0].ToUpperInvariant() };
            ParseUrl(requestLine[1], request);

            int contentLength = 0;
            bool hasContentLength = false;
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
                    if (hasContentLength || !int.TryParse(value, out contentLength) || contentLength < 0 || contentLength > MaxBodyBytes)
                        return null;
                    hasContentLength = true;
                }
                // This minimal server supports only length-delimited bodies.
                if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) return null;
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
                request.Query[Uri.UnescapeDataString(key.Replace("+", " "))] = Uri.UnescapeDataString(value.Replace("+", " "));
            }
        }

        private static void WriteResponse(NetworkStream stream, BridgeResponse response)
        {
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(response.Status).Append(response.Status == 200 ? " OK" : response.Status == 202 ? " Accepted" : " Error").Append("\r\n");
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
