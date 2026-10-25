using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// 游戏状态与日志仅为隔离桩；HTTP解析、响应等待和BridgeRequest使用生产源码。
namespace Game.SceneFlow
{
    public sealed class GameManager
    {
        public static GameManager instance => null;
        public string gameMode => "Unknown";
        public bool isGameLoading => false;
    }
}
namespace CS2MCP
{
    public static class Mod
    {
        public const string Name = "CS2MCP";
        public const string Version = "test";
        public static ProbeLog Log { get; } = new ProbeLog();
    }
    public sealed class ProbeLog { public void Warn(string value) { } }
    public static class SaveTimeoutProbe
    {
        public static string Run()
        {
            int saveCalls = 0, ordinaryCalls = 0;
            var pickPort = new TcpListener(IPAddress.Loopback, 0);
            pickPort.Start();
            int port = ((IPEndPoint)pickPort.LocalEndpoint).Port;
            pickPort.Stop();
            var server = new HttpBridgeServer(port, request => {
                if (request.Path == "/game/save") Interlocked.Increment(ref saveCalls);
                else Interlocked.Increment(ref ordinaryCalls);
                _ = Task.Run(async () => {
                    await Task.Delay(11000);
                    request.Complete(BridgeResponse.Json(new { pipelineCompleted = true, previewIncluded = false }));
                });
            });
            server.Start();
            try {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) }) {
                    string token = Environment.GetEnvironmentVariable("CS2MCP_TOKEN");
                    if (!string.IsNullOrWhiteSpace(token)) client.DefaultRequestHeaders.Add("X-CS2MCP-Token", token.Trim());
                    var clock = Stopwatch.StartNew();
                    var save = client.PostAsync($"http://127.0.0.1:{port}/game/save?includePreview=false", null);
                    var ordinary = client.GetAsync($"http://127.0.0.1:{port}/ordinary-delay");
                    Task.WaitAll(new Task[] { save, ordinary }, TimeSpan.FromSeconds(25));
                    var saveResponse = save.GetAwaiter().GetResult();
                    var ordinaryResponse = ordinary.GetAwaiter().GetResult();
                    var saveBody = JObject.Parse(saveResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    var ordinaryBody = JObject.Parse(ordinaryResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    if (saveResponse.StatusCode != HttpStatusCode.OK || (bool?)saveBody["pipelineCompleted"] != true)
                        throw new InvalidOperationException($"11秒原生保存应返回完成，实际HTTP {(int)saveResponse.StatusCode}: {saveBody}");
                    if (ordinaryResponse.StatusCode != HttpStatusCode.ServiceUnavailable ||
                        (bool?)ordinaryBody["mayHaveApplied"] != true || (bool?)ordinaryBody["safeToRetry"] != false)
                        throw new InvalidOperationException($"普通请求仍应10秒超时并保守报告未知: {ordinaryResponse.StatusCode}: {ordinaryBody}");
                    if (saveCalls != 1 || ordinaryCalls != 1)
                        throw new InvalidOperationException("保存或普通请求被重复派发");
                    if ((int?)ordinaryBody["timeoutMs"] != 10000 || (bool?)ordinaryBody["enqueued"] != true ||
                        ordinaryBody["handlerStarted"]?.Type != JTokenType.Null)
                        throw new InvalidOperationException("普通请求超时预算或未知处理状态不正确");
                    var late = new BridgeRequest();
                    if (late.WaitForResponse(1) != null) throw new InvalidOperationException("未完成请求应超时");
                    late.Complete(BridgeResponse.Json(new { pipelineCompleted = true }));
                    bool lateCompletionPreserved = late.WaitForResponse(1)?.Status == 200;
                    if (!lateCompletionPreserved) throw new InvalidOperationException("响应超时不得取消迟到完成");
                    return JsonConvert.SerializeObject(new { saveStatus = (int)saveResponse.StatusCode,
                        ordinaryStatus = (int)ordinaryResponse.StatusCode, elapsedMs = clock.ElapsedMilliseconds,
                        saveCalls, ordinaryCalls, lateCompletionPreserved,
                        realHttpServerSourceUsed = true, gameOrSaveFilesTouched = false });
                }
            } finally { server.Stop(); }
        }
    }
}
