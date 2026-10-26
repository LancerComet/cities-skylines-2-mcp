using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;

namespace Game.SceneFlow
{
 public class GameManager {public static GameManager instance;public bool isGameLoading;public string gameMode="Game";}
}
namespace CS2MCP
{
 public static class Mod {public const string Name="probe",Version="0.9.104-local";public static Logger Log=new Logger();public class Logger{public void Warn(string s){}}}
 public static class NativeCaptureOperationProbe
 {
  static void Check(bool b,string s){if(!b)throw new Exception(s);}
  public static object Run()
  {
   var cases=new List<string>();var now=new DateTime(2030,1,1,0,0,0,DateTimeKind.Utc);
   var op=new NativeCaptureOperation(()=>now);string guid="1234567890abcdef1234567890abcdef";
   Check(!op.IsInFlight,"default has no camera borrower");
   Check(op.TryBegin(guid,1,1280,out var a)&&op.IsInFlight,"first begin");Check(!op.TryBegin(guid,1,1280,out var duplicate)&&duplicate.captureId==a.captureId,"unknown begin does not duplicate");
   cases.Add("single_pending_capture_rejects_duplicate_start");
   Check(op.TryMarkRunning(a.captureId)&&!op.TryMarkRunning(a.captureId),"one render dispatch");
   Check(op.Read(a.captureId,out var absent).status=="running"&&absent==null&&op.IsInFlight,"running status");
   byte[] png={137,80,78,71,13,10,26,10,0};Check(op.Complete(a.captureId,png,new{restored=true}),"completion");
   Check(!op.IsInFlight&&op.Read(a.captureId,out var first).resultAvailable&&op.Read(a.captureId,out var second).resultAvailable&&ReferenceEquals(first,png)&&ReferenceEquals(first,second),"same retained result");
   cases.Add("late_completion_and_repeat_retrieval_do_not_render_again");
   Check(op.TryBegin(guid,2,1280,out var b),"next capture");Check(op.Read(a.captureId,out absent).status=="not_found"&&!op.Complete(a.captureId,png,null),"stale result cannot replace next");
   op.Fail(b.captureId,"native_failed");Check(op.Read(b.captureId,out absent).error=="native_failed"&&absent==null,"failure stored");
   cases.Add("replacement_and_stale_completion_are_distinct");
   Check(op.TryBegin(guid,2,1280,out var queued),"queued");now=now.AddSeconds(31);Check(!op.TryMarkRunning(queued.captureId),"late queued render refused");
   Check(op.Read(queued.captureId,out absent).status=="failed"&&!op.ReadLatest().mayHaveRendered,"no frame deadline");
   cases.Add("missing_end_of_frame_expires_before_render");
   Check(op.TryBegin(guid,2,1280,out var cancel)&&op.TryMarkRunning(cancel.captureId),"running before load");op.CancelAndClearResult("new_scene");
   Check(!op.Complete(cancel.captureId,png,null)&&op.Read(cancel.captureId,out absent).status=="cancelled"&&absent==null,"old scene cannot deliver");
   cases.Add("load_or_exit_invalidates_old_result");
   int rejected=0;foreach(var width in new[]{0,63,3841}){try{op.TryBegin(guid,1,width,out _);}catch(ArgumentException){rejected++;}}
   try{op.TryBegin("bad",1,1280,out _);}catch(ArgumentException){rejected++;}
   Check(rejected==4,"native identity and size checks");
   cases.Add("invalid_start_has_no_active_render");
   op.TryBegin(guid,1,1280,out var invalidPng);op.TryMarkRunning(invalidPng.captureId);
   try{op.Complete(invalidPng.captureId,new byte[8],null);Check(false,"bad PNG");}catch(ArgumentException){}
   Check(op.Read(invalidPng.captureId,out absent).status=="running"&&absent==null,"bad result not published");
   cases.Add("invalid_png_not_published_as_completed");

   var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();int enqueued=0;
   NativeCaptureOperation.Shared.TryBegin(guid,1,1280,out var httpOp);
   var server=new HttpBridgeServer(port,_=>Interlocked.Increment(ref enqueued),NativeCaptureResultReader.Read);server.Start();
   try{
    using(var client=new HttpClient{Timeout=TimeSpan.FromSeconds(3)}){
     string url="http://127.0.0.1:"+port+"/screenshot?source=gameCamera&captureId="+httpOp.captureId;
     var response=client.GetAsync(url).GetAwaiter().GetResult();Check((int)response.StatusCode==202,"queued HTTP status");response.Dispose();
     NativeCaptureOperation.Shared.TryMarkRunning(httpOp.captureId);
     response=client.GetAsync(url).GetAwaiter().GetResult();Check((int)response.StatusCode==202,"running HTTP independent of ECS");response.Dispose();
     NativeCaptureOperation.Shared.Complete(httpOp.captureId,png,null);
     for(int i=0;i<2;i++){
      response=client.GetAsync(url).GetAwaiter().GetResult();byte[] data=response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
      Check((int)response.StatusCode==200&&response.Content.Headers.ContentType.MediaType=="image/png"&&Convert.ToBase64String(data)==Convert.ToBase64String(png),"PNG HTTP retrieval");response.Dispose();
     }
     response=client.GetAsync(url+"&width=1280").GetAwaiter().GetResult();Check((int)response.StatusCode==400,"invalid result query");response.Dispose();
    }
   }finally{server.Stop();NativeCaptureOperation.Shared.CancelAndClearResult("test_done");}
   Check(enqueued==0,"result reads must not depend on game update");cases.Add("real_http_result_query_bypasses_game_queue");
   return new{passed=cases.Count,cases,realHttpSourceUsed=true,resultQueriesEnqueuedToGame=enqueued,nativeRenderOrCityUsed=false};
  }
 }
}
