import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {bridgeJson, BridgeError, isMutation, errorResult} from '../dist/bridge-client.js';

test('normal Anarchy configuration is a mutation, production and tax readback are read-only',()=>{
  assert.equal(isMutation('/mods/anarchy/normal-construction'),true);
  assert.equal(isMutation('/city/production'),false);
  assert.equal(isMutation('/city/taxes'),false);
  assert.equal(isMutation('/city/taxes/set?area=Residential&educationLevel=2&rate=9'),true);
});

async function mock(t, handler) {
  const server=createServer(handler);
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  t.after(()=>{server.closeAllConnections();server.close();});
  return `http://127.0.0.1:${server.address().port}`;
}

test('query space and literal plus retain distinct meanings',async t=>{
  const base=await mock(t,(req,res)=>{
    assert.equal(req.url,'/prefabs?query=A%20B%2BC');res.end('{"ok":true}');
  });
  assert.deepEqual(await bridgeJson('/prefabs?query=A+B%2BC',1000,base),{ok:true});
});

test('HTTP method is explicit at the caller; transport defaults to GET without token forwarding',async t=>{
  const previous=process.env.CS2MCP_TOKEN;process.env.CS2MCP_TOKEN='synthetic-token';
  t.after(()=>{if(previous===undefined)delete process.env.CS2MCP_TOKEN;else process.env.CS2MCP_TOKEN=previous;});
  const methods=[];
  const base=await mock(t,(req,res)=>{methods.push(req.method);assert.equal(req.headers['x-cs2mcp-token'],undefined);res.end('{}');});
  await bridgeJson('/state',1000,base);
  await bridgeJson('/city/taxes/set?rate=10',1000,base);
  await bridgeJson('/build/interchange/place?dryRun=true',1000,base,{method:'POST'});
  assert.deepEqual(methods,['GET','GET','POST']);
});

test('partial apply reported with HTTP 409 remains unsafe to replay',async t=>{
  const base=await mock(t,(_,res)=>{res.writeHead(409);res.end(JSON.stringify({error:'brush rejected after an earlier pass',mayHaveApplied:true}));});
  await assert.rejects(bridgeJson('/build/terrain',1000,base),error=>error.mayHaveApplied&&!error.safeToRetry);
});
test('loopback credentials, foreign origins and alternate request origins are rejected before IO',async()=>{
  for(const base of ['https://127.0.0.1','http://example.com','http://a:b@127.0.0.1','http://127.0.0.1/foo'])
    await assert.rejects(bridgeJson('/state',1000,base),error=>error instanceof BridgeError && !error.mayHaveApplied);
  for(const path of ['//example.com/path','/\\example.com/path']) await assert.rejects(bridgeJson(path,1000));
});
test('invalid success JSON is a protocol error, not a fabricated successful payload',async t=>{
  const base=await mock(t,(_,res)=>res.end('<html>not JSON</html>'));
  await assert.rejects(bridgeJson('/build/road',1000,base),error=>error.mayHaveApplied && !error.safeToRetry);
  await assert.rejects(bridgeJson('/state',1000,base),error=>!error.mayHaveApplied && !error.safeToRetry);
});
test('only explicit prequeue busy responses allow retry',async t=>{
  let calls=0;
  const base=await mock(t,(_,res)=>{calls++;res.writeHead(409);res.end('{"error":"another road operation is in progress, retry shortly"}');});
  await assert.rejects(bridgeJson('/build/road',1000,base),error=>error.safeToRetry && !error.mayHaveApplied && error.status===409);
  assert.equal(calls,1);
});

test('memory prequeue rejection is unapplied and requires an explicit fresh recovery',async t=>{
  const base=await mock(t,(_,res)=>{res.writeHead(409);res.end(JSON.stringify({error:'内存提交预算不足；施工/预览尚未排队。先核实际资源压力，待余量恢复后读取新现场。'}));});
  await assert.rejects(bridgeJson('/build/road-layout',1000,base),error=>!error.mayHaveApplied && !error.safeToRetry && error.status===409);
});
test('unknown native memory counters reject before enqueue without inviting retry',async t=>{
  let calls=0;
  const base=await mock(t,(_,res)=>{calls++;res.writeHead(409);res.end(JSON.stringify({error:'内存计数器无法核验；施工/预览尚未排队。读取/ping诊断。'}));});
  await assert.rejects(bridgeJson('/build/road-layout',1000,base),error=>!error.mayHaveApplied && !error.safeToRetry && error.status===409);
  assert.equal(calls,1);
});
test('native validation failure never invites automatic retry',async t=>{
  const base=await mock(t,(_,res)=>{res.writeHead(409);res.end('{"error":"OverlapExisting"}');});
  await assert.rejects(bridgeJson('/build/road',1000,base),error=>!error.safeToRetry && !error.mayHaveApplied);
});

test('native interrupted apply can be unknown on HTTP 409 and must never auto replay',async t=>{
  let calls=0;
  const base=await mock(t,(_,res)=>{calls++;res.writeHead(409);res.end(JSON.stringify({error:'native construction interrupted',mayHaveApplied:true,safeToRetry:false}));});
  await assert.rejects(bridgeJson('/build/upgrade',1000,base),error=>error.mayHaveApplied && !error.safeToRetry && error.status===409);
  assert.equal(calls,1);
});
test('native preview error locations survive MCP without changing retry semantics',async t=>{
  const details=[{error:'OverlapExisting',location:{x:12,y:34,z:56}}];
  const base=await mock(t,(_,res)=>{res.writeHead(409);res.end(JSON.stringify({error:'blocked',validationErrors:details}));});
  await assert.rejects(bridgeJson('/build/road/connect',1000,base),error=>{
    assert.deepEqual(JSON.parse(errorResult(error).content[0].text).validationErrors,details);
    return !error.safeToRetry && !error.mayHaveApplied;
  });
});
test('milestone reads use GET and explicit dismissal uses POST with uncertain failure metadata',async t=>{
  const methods=[];
  const base=await mock(t,(req,res)=>{
    methods.push(req.method);
    if(req.url.includes('dismiss=true')) {res.writeHead(500);res.end('{"error":"native dismissal failed"}');}
    else res.end('{"pendingBefore":true}');
  });
  await bridgeJson('/game/milestone-popup',1000,base);
  await bridgeJson('/game/milestone-popup?dismiss=false',1000,base);
  await assert.rejects(bridgeJson('/game/milestone-popup?dismiss=true',1000,base,{method:'POST'}),error=>error.mayHaveApplied&&!error.safeToRetry);
  assert.deepEqual(methods,['GET','GET','POST']);
  assert.equal(isMutation('/build/place?dryRun=true'),false);
  assert.equal(isMutation('/build/place?dryRun=false'),true);
  assert.equal(isMutation('/build/place'),true);
});
test('server errors retain uncertain mutation outcomes',async t=>{
  const base=await mock(t,(_,res)=>{res.writeHead(500);res.end('{"error":"failure after apply"}');});
  await assert.rejects(bridgeJson('/transit/lines/create',1000,base),error=>error.mayHaveApplied && !error.safeToRetry);
});
test('timeouts never resend a mutation; read-only failures have no city side effect',async t=>{
  let calls=0;const base=await mock(t,()=>{calls++;});
  await assert.rejects(bridgeJson('/city/tiles/buy',60,base),error=>error.mayHaveApplied && !error.safeToRetry);
  assert.equal(calls,1);
  await assert.rejects(bridgeJson('/city/demand',60,base),error=>!error.mayHaveApplied && !error.safeToRetry);
  assert.equal(calls,2);
});
test('outcome metadata survives MCP error serialization',()=>{
  const result=errorResult(new BridgeError('unknown',true,false,500));
  assert.equal(result.isError,true);
  assert.deepEqual(JSON.parse(result.content[0].text),{error:'unknown',mayHaveApplied:true,safeToRetry:false,status:500});
  assert.equal(isMutation('/build/road/connect?dryRun=true'),false);
  assert.equal(isMutation('/build/road?dryRun=true'),true);
  assert.equal(isMutation('/city/tiles/buy?dryRun=true'),true);
  assert.equal(isMutation('/transit/lines/vehicles/set'),true);
  assert.equal(isMutation('/entity/inspect'),false);
});

test('关闭回执只有严格证明handler未开始才保留明确未应用，仍禁止自动重试',async t=>{
  for (const [failurePhase,enqueued] of [['prequeue',false],['before_handler',true]]) {
    const payload={code:'bridge_stopped',error:'stopped',failurePhase,enqueued,handlerStarted:false,mayHaveApplied:false,safeToRetry:false};
    const base=await mock(t,(_,res)=>{res.writeHead(503);res.end(JSON.stringify(payload));});
    await assert.rejects(bridgeJson('/build/road',1000,base),error=>{
      assert.equal(error.mayHaveApplied,false);assert.equal(error.safeToRetry,false);
      const serialized=JSON.parse(errorResult(error).content[0].text);
      assert.equal(serialized.code,'bridge_stopped');assert.equal(serialized.enqueued,enqueued);
      assert.equal(serialized.failurePhase,failurePhase);return true;
    });
  }
});

test('缺失或自相矛盾的关闭证明及其它500仍是未知结果',async t=>{
  const proof={code:'bridge_stopped',error:'stopped',failurePhase:'prequeue',enqueued:false,handlerStarted:false,mayHaveApplied:false,safeToRetry:false};
  for(const delta of [{code:'other'},{handlerStarted:true},{handlerStarted:undefined},{enqueued:true},{failurePhase:'after_handler'},{safeToRetry:true},{mayHaveApplied:true}]){
    const base=await mock(t,(_,res)=>{res.writeHead(503);res.end(JSON.stringify({...proof,...delta}));});
    await assert.rejects(bridgeJson('/build/place',1000,base),error=>error.mayHaveApplied && !error.safeToRetry);
  }
});

test('资源拒绝保留原因和预算而不发自动重试或编造未发送HTTP',async t=>{
  let calls=0;
  const payload={code:'resource_budget',error:'budget insufficient',failurePhase:'prequeue',enqueued:false,handlerStarted:false,mayHaveApplied:false,safeToRetry:false,reason:'commit_reserve',resourceBudget:{availableCommitBytes:2,requiredCommitReserveBytes:3}};
  const base=await mock(t,(_,res)=>{calls++;res.writeHead(409);res.end(JSON.stringify(payload));});
  await assert.rejects(bridgeJson('/build/road-layout',1000,base),error=>{
    const value=JSON.parse(errorResult(error).content[0].text);
    assert.deepEqual(value.resourceBudget,payload.resourceBudget);assert.equal(value.reason,'commit_reserve');
    assert.equal('requestSent' in value,false);return !error.mayHaveApplied && !error.safeToRetry;
  });
  assert.equal(calls,1);
});
