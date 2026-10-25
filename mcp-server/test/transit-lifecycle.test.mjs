import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {createServer} from 'node:http';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';
import {bridgeJson,errorResult} from '../dist/bridge-client.js';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');

test('实际生产交通生命周期partial：首帧前切换、提交中断、清理及玩家工具归属',{skip:process.platform!=='win32'},()=>{
  const r=spawnSync(process.env.CS2_TEST_PWSH || 'pwsh',['-NoProfile','-File',resolve(root,'test/fixtures/transit-lifecycle-probe.ps1')],{encoding:'utf8',timeout:30000,windowsHide:true});
  assert.equal(r.status,0,r.stderr||r.stdout);const report=JSON.parse(r.stdout.trim());assert.equal(report.passed,8);
  assert.equal(new Set(report.cases).size,8);assert.equal(report.nativeToolUpdateAndCityAcceptanceVerified,false);
});

test('409提交后中断和未核实提交跨SDK保留未知；未提交也不自动重试',async t=>{
  let payload,requests=0,status=409;
  const mock=createServer((req,res)=>{requests++;res.writeHead(status,{'content-type':'application/json'});res.end(JSON.stringify(payload));});
  await new Promise(r=>mock.listen(0,'127.0.0.1',r));t.after(()=>{mock.closeAllConnections();mock.close();});
  const base=`http://127.0.0.1:${mock.address().port}`;
  for(const row of [
    {code:'transit_tool_interrupted',failurePhase:'tool_interrupted',mayHaveApplied:true},
    {code:'transit_commit_unverified',failurePhase:'commit_readback',mayHaveApplied:true},
    {code:'transit_tool_interrupted',failurePhase:'tool_interrupted',mayHaveApplied:false},
    {code:'transit_tool_failed',failurePhase:'tool_failed',mayHaveApplied:false},
  ]) {
    payload={error:'inspect city first',...row,safeToRetry:false,operation:'Line',stage:'Finish',target:{index:987,version:4}};
    status=row.code==='transit_tool_failed'?500:409;requests=0;
    let error;try {await bridgeJson('/transit/lines/create',1000,base);} catch(e){error=e;}
    assert.ok(error);const result=JSON.parse(errorResult(error).content[0].text);
    assert.equal(result.mayHaveApplied,row.mayHaveApplied||status>=500);assert.equal(result.safeToRetry,false);
    for(const key of ['code','failurePhase','operation','stage','target']) assert.deepEqual(result[key],payload[key]);
    assert.equal(requests,1);
  }
});
