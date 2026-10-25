import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {readFile} from 'node:fs/promises';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {fileURLToPath} from 'node:url';
import {bridgeJson,requiresPost,isMutation} from '../dist/bridge-client.js';

async function mock(t,handler){
  const server=createServer(handler);await new Promise(r=>server.listen(0,'127.0.0.1',r));
  t.after(()=>new Promise(r=>server.close(r)));return `http://127.0.0.1:${server.address().port}`;
}
const fixtures=JSON.parse(await readFile(new URL('./fixtures/http-security-routes.json',import.meta.url),'utf8'));
test('transport POST covers local write routes while a normal preview stays non-mutating',()=>{
  for(const f of fixtures)assert.equal(requiresPost(f.path),f.post,f.path);
  assert.equal(isMutation('/build/road/connect?dryRun=true'),false);
  assert.equal(isMutation('/build/road/connect?dryRun=false'),true);
});
test('SDK uses POST for writes and previews, preserves JSON body, and leaves reads GET',async t=>{
  const seen=[];
  const base=await mock(t,async(req,res)=>{let body='';for await(const c of req)body+=c;seen.push({path:req.url,method:req.method,body,type:req.headers['content-type']});res.end('{"ok":true}');});
  await bridgeJson('/state',1000,base);
  await bridgeJson('/sim/control?paused=true',1000,base);
  await bridgeJson('/build/road-layout?dryRun=true',1000,base,{method:'GET'});
  await bridgeJson('/build/road-layout',1000,base,{method:'POST',headers:{'Content-Type':'application/json'},body:'{"segments":[]}'});
  assert.deepEqual(seen.map(x=>x.method),['GET','POST','POST','POST']);
  assert.equal(seen[3].body,'{"segments":[]}');assert.equal(seen[3].type,'application/json');
});
test('configured token reaches the shared client without replacing content headers',async t=>{
  const old=process.env.CS2MCP_TOKEN;let client;
  try{process.env.CS2MCP_TOKEN=' fixture-token ';client=await import('../dist/bridge-client.js?http-security-token-probe');}
  finally{if(old===undefined)delete process.env.CS2MCP_TOKEN;else process.env.CS2MCP_TOKEN=old;}
  const base=await mock(t,(req,res)=>{assert.equal(req.headers['x-cs2mcp-token'],'fixture-token');assert.equal(req.headers['content-type'],'application/json');res.end('{"ok":true}');});
  await client.bridgeJson('/game/save',1000,base,{headers:{'Content-Type':'application/json'}});
});
test('actual C# HTTP server rejects before enqueue, protects capture reads and matches SDK route policy',{
  skip:process.platform!=='win32',timeout:30000,
},async()=>{
  const script=fileURLToPath(new URL('./fixtures/http-security-probe.ps1',import.meta.url));
  const {stdout}=await promisify(execFile)(process.env.CS2_TEST_PWSH||'pwsh',['-NoProfile','-File',script],{timeout:25000,encoding:'utf8',windowsHide:true});
  const result=JSON.parse(stdout.trim().split(/\r?\n/).at(-1));
  assert.equal(result.cases,14);assert.equal(result.gameOrSaveFilesTouched,false);
  for(const f of fixtures)assert.equal(result.routes[f.path],requiresPost(f.path),f.path);
});
