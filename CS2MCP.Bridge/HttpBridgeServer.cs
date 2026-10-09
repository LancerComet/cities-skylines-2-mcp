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

        private readonly int m_Port;
        private readonly Action<BridgeRequest> m_EnqueueToMainThread;
        private TcpListener m_Listener;
        private Thread m_AcceptThread;
        private volatile bool m_Running;

        public HttpBridgeServer(int port, Action<BridgeRequest> enqueueToMainThread)
        {
            m_Port = port;
            m_EnqueueToMainThread = enqueueToMainThread;
        }

        public void Start()
        {
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
            // Liveness check, answered off the simulation thread so it works
            // even while the game is loading a save.
            if (request.Path == "/ping")
            {
                return HandlePing();
            }

            if (request.Path.StartsWith("/build/", StringComparison.Ordinal)) {
                var memory = RuntimeMemory.Read();
                if (!memory.constructionAllowed)
                    return BridgeResponse.Error(409, memory.unavailableReason != null
                        ? "内存计数器无法核验；施工/预览尚未排队。读取/ping诊断，禁止自动重试旧批次；保存和正常退出仍可用。"
                        : "内存提交预算不足；施工/预览尚未排队。先保存并正常退出重启，读取新现场后继续，禁止自动重试旧批次。");
            }
            m_EnqueueToMainThread(request);
            BridgeResponse response = request.WaitForResponse(MainThreadTimeoutMs);
            return response ?? BridgeResponse.Error(503,
                "simulation thread did not respond within 10s (game still loading, or bridge system disabled)");
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
