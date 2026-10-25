using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace Game.SceneFlow {
    public sealed class GameManager {
        public static GameManager instance => null;
        public string gameMode => "Unknown";
        public bool isGameLoading => false;
    }
}
namespace CS2MCP {
    public static class Mod {
        public const string Name="CS2MCP", Version="test";
        public static ProbeLog Log { get; }=new ProbeLog();
    }
    public sealed class ProbeLog { public void Warn(string text) {} }
    public static class HttpSecurityProbe {
        static int calls, captures, cases;
        static int Port(){var l=new TcpListener(IPAddress.Loopback,0);l.Start();int p=((IPEndPoint)l.LocalEndpoint).Port;l.Stop();return p;}
        static void Check(HttpClient c,int port,string path,string method,int status,int dispatched,string host=null,string origin=null,string token=null,int captured=0){
            int before=calls, captureBefore=captures;
            using(var req=new HttpRequestMessage(new HttpMethod(method),$"http://127.0.0.1:{port}"+path)){
                if(host!=null)req.Headers.Host=host;
                if(origin!=null)req.Headers.TryAddWithoutValidation("Origin",origin);
                if(token!=null)req.Headers.TryAddWithoutValidation("X-CS2MCP-Token",token);
                using(var response=c.SendAsync(req).GetAwaiter().GetResult()){
                    if((int)response.StatusCode!=status || calls-before!=dispatched || captures-captureBefore!=captured)
                        throw new InvalidOperationException($"HTTP case {cases}: {method} {path}, expected {status}/{dispatched}/{captured}, got {(int)response.StatusCode}/{calls-before}/{captures-captureBefore}");
                }
            }
            cases++;
        }
        public static string Run(string fixture){
            string old=Environment.GetEnvironmentVariable("CS2MCP_TOKEN");
            var routes=new Dictionary<string,bool>();
            foreach(JObject row in JArray.Parse(File.ReadAllText(fixture))){
                string path=(string)row["path"];bool actual=HttpBridgeServer.IsMutatingPath(new Uri("http://127.0.0.1"+path).AbsolutePath);
                if(actual!=(bool)row["post"])throw new InvalidOperationException("C# route policy differs: "+path);
                routes[path]=actual;
            }
            try{
                Environment.SetEnvironmentVariable("CS2MCP_TOKEN",null);
                int p=Port();var server=new HttpBridgeServer(p,r=>{calls++;r.Complete(BridgeResponse.Json(new{ok=true}));});server.Start();
                try{using(var c=new HttpClient{Timeout=TimeSpan.FromSeconds(5)}){
                    Check(c,p,"/ping","GET",403,0,"evil.example:"+p);
                    Check(c,p,"/ping","GET",403,0,"127.0.0.1:"+(p+1));
                    Check(c,p,"/ping","GET",403,0,origin:"null");
                    Check(c,p,"/ping","GET",403,0,origin:"");
                    Check(c,p,"/sim/control","GET",405,0);
                    Check(c,p,"/build/road/connect?dryRun=true","GET",405,0);
                    Check(c,p,"/sim/control","POST",200,1);
                    Check(c,p,"/state","GET",200,1,"localhost:"+p);
                }}finally{server.Stop();}
                Environment.SetEnvironmentVariable("CS2MCP_TOKEN"," fixture-token ");
                p=Port();server=new HttpBridgeServer(p,r=>{calls++;r.Complete(BridgeResponse.Json(new{ok=true}));},r=>{captures++;return BridgeResponse.Json(new{captureResult=true});});server.Start();
                try{using(var c=new HttpClient{Timeout=TimeSpan.FromSeconds(5)}){
                    Check(c,p,"/ping","GET",200,0);
                    Check(c,p,"/state","GET",401,0);
                    Check(c,p,"/state","GET",401,0,token:"wrong-token");
                    Check(c,p,"/state","GET",200,1,token:"fixture-token");
                    Check(c,p,"/screenshot?captureId=existing","GET",401,0);
                    Check(c,p,"/screenshot?captureId=existing","GET",200,0,token:"fixture-token",captured:1);
                }}finally{server.Stop();}
                return JsonConvert.SerializeObject(new{cases,routes,gameOrSaveFilesTouched=false});
            }finally{Environment.SetEnvironmentVariable("CS2MCP_TOKEN",old);}
        }
    }
}
