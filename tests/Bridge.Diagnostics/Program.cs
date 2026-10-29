using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using CS2MCP;
using Newtonsoft.Json.Linq;

int checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++;
}

var request = new BridgeRequest();
foreach (string value in new[] { "NaN", "Infinity", "-Infinity", "1e1000", "not-a-number" })
{
    request.Query["value"] = value;
    Check(!request.TryGetFloat("value", out _), "non-finite float accepted");
}
request.Query["value"] = "1.25";
Check(request.TryGetFloat("value", out float parsed) && parsed == 1.25f, "finite float rejected");
Check(JObject.Parse(Encoding.UTF8.GetString(BridgeResponse.Error(409, "partial", true).Body))["mayHaveApplied"].Value<bool>(),
    "partial application metadata lost");

// Run the same production-linked admission cases on Windows and Linux CI.
// Capacity belongs to the requested allocation; physical pressure and a large
// private working set must not become a blanket stop/restart policy.
var memoryChecks = JObject.Parse(MemoryPressureProbe.Run());
Check(memoryChecks["productionSourcesLinked"].Value<bool>(), "memory diagnostic detached from production sources");
Check(memoryChecks["passed"].Value<int>() >= 20, "memory admission coverage unexpectedly reduced");
checks += memoryChecks["passed"].Value<int>();
// Native cable selection and fixed-node transitions share these production rules
// with the Windows probe; exercise them on every CI platform as well.
var powerLineChecks = JObject.Parse(PowerLineVariantProbe.Run());
Check(powerLineChecks["productionSourcesLinked"].Value<bool>(), "power-line diagnostic detached from production sources");
Check(powerLineChecks["passed"].Value<int>() == 14, "power-line transition coverage unexpectedly reduced");
checks += powerLineChecks["passed"].Value<int>();
Check(LaunchFramePolicy.Resolve("background") == 30, "background mode incorrect");
Check(LaunchFramePolicy.Resolve("foreground") == 90, "foreground mode incorrect");
foreach (string mode in new[] { null, "", "BACKGROUND", "unknown" })
    Check(LaunchFramePolicy.Resolve(mode) == null, "unspecified/invalid mode overwrote player setting");

string fixture = Path.GetTempFileName();
try
{
    string[] keys = { "MinimumDuration", "MaximumDuration", "TargetDurationMultiplier", "LaneOccupiedMultiplier", "IntervalFactor" };
    File.WriteAllText(fixture, string.Join(",", Array.ConvertAll(keys, key => "{key:\"" + key + "\",min:0,max:10,step:1}")));
    var limits = SignalUiLimits.Read(fixture);
    foreach (string key in keys)
    {
        Check(limits.Validate(key, 0) == 0, "minimum rejected");
        Check(limits.Validate(key, 10) == 10, "maximum rejected");
        foreach (float value in new[] { -1f, 11f, float.NaN, float.PositiveInfinity })
        {
            bool rejected = false;
            try { limits.Validate(key, value); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "out-of-range signal value admitted");
        }
    }
    File.WriteAllText(fixture, "unsupported UI schema");
    bool invalidSchema = false;
    try { SignalUiLimits.Read(fixture); } catch (ArgumentException) { invalidSchema = true; }
    Check(invalidSchema, "unknown UI format admitted");
}
finally { File.Delete(fixture); }

var portProbe = new TcpListener(IPAddress.Loopback, 0);
portProbe.Start();
int serverPort = ((IPEndPoint)portProbe.LocalEndpoint).Port;
portProbe.Stop();
var server = new HttpBridgeServer(serverPort, req => req.Complete(BridgeResponse.Json(new { req.Query, req.Body })));
server.Start();
try
{
    var listener = (TcpListener)typeof(HttpBridgeServer).GetField("m_Listener", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server);
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    string Send(string raw)
    {
        using var client = new TcpClient();
        client.ReceiveTimeout = 3000;
        client.Connect(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        byte[] bytes = Encoding.UTF8.GetBytes(raw);
        stream.Write(bytes, 0, bytes.Length);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
    string reply = Send($"GET /echo?q=A+B%2BC HTTP/1.1\r\nHost: localhost:{port}\r\n\r\n");
    var payload = JObject.Parse(reply.Substring(reply.IndexOf("\r\n\r\n") + 4));
    Check(payload["Query"]["q"].Value<string>() == "A B+C", "space/plus query decoding failed");
    string body = "{\"name\":\"测试道路\"}";
    reply = Send($"POST /echo HTTP/1.1\r\nHost: localhost:{port}\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body);
    payload = JObject.Parse(reply.Substring(reply.IndexOf("\r\n\r\n") + 4));
    Check(payload["Body"].Value<string>() == body, "UTF8 body corrupted");
    foreach (string headers in new[] {
        "Content-Length: -1", "Content-Length: invalid", "Content-Length: 1048577",
        "Content-Length: 0\r\nContent-Length: 0", "Transfer-Encoding: chunked"
    }) Check(Send("POST /echo HTTP/1.1\r\n" + headers + "\r\n\r\n").StartsWith("HTTP/1.1 400"), "malformed body framing admitted");
    Check(Send("GET http://example.com/echo HTTP/1.1\r\n\r\n").StartsWith("HTTP/1.1 400"), "non-local request target admitted");
}
finally { server.Stop(); }

// 调度与Stop的真实同步门：仅尚未交付处理器的队列可以由关闭路径完成。
var entered = new System.Threading.ManualResetEventSlim();
var release = new System.Threading.ManualResetEventSlim();
BridgeRequest queued = null;
var stoppingServer = new HttpBridgeServer(0, req => { queued = req; entered.Set(); release.Wait(); });
stoppingServer.Start();
var dispatch = typeof(HttpBridgeServer).GetMethod("Dispatch", BindingFlags.Instance | BindingFlags.NonPublic);
var pending = System.Threading.Tasks.Task.Run(() => (BridgeResponse)dispatch.Invoke(stoppingServer, new object[] { new BridgeRequest { Path = "/probe", Host = "127.0.0.1:0" } }));
try
{
    Check(entered.Wait(3000), "dispatch did not reach queue boundary");
    var stopping = System.Threading.Tasks.Task.Run(stoppingServer.Stop);
    Check(!stopping.Wait(50), "Stop bypassed active enqueue ownership gate");
    release.Set();
    Check(stopping.Wait(3000), "Stop did not finish after enqueue completed");
    queued.Complete(HttpBridgeServer.StoppedBeforeHandler(enqueued: true));
    Check(pending.Wait(3000), "unhandled queued request did not receive shutdown result");
    var beforeHandler = JObject.Parse(Encoding.UTF8.GetString(pending.Result.Body));
    Check(beforeHandler["failurePhase"].Value<string>() == "before_handler" && beforeHandler["enqueued"].Value<bool>()
        && !beforeHandler["handlerStarted"].Value<bool>() && !beforeHandler["mayHaveApplied"].Value<bool>(), "queued shutdown proof incorrect");
    var rejected = (BridgeResponse)dispatch.Invoke(stoppingServer, new object[] { new BridgeRequest { Path = "/probe", Host = "127.0.0.1:0" } });
    var prequeue = JObject.Parse(Encoding.UTF8.GetString(rejected.Body));
    Check(prequeue["code"].Value<string>() == "bridge_stopped" && prequeue["failurePhase"].Value<string>() == "prequeue"
        && !prequeue["enqueued"].Value<bool>() && !prequeue["safeToRetry"].Value<bool>(), "post-stop request was admitted or replay suggested");
}
finally { release.Set(); stoppingServer.Stop(); entered.Dispose(); release.Dispose(); }
Console.WriteLine($"{checks} diagnostic checks passed; production sources linked; gameplay not emulated.");
