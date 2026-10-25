import test from 'node:test';
import assert from 'node:assert/strict';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {createServer} from 'node:http';
import {dirname,resolve} from 'node:path';
import {fileURLToPath} from 'node:url';
import {execFileSync} from 'node:child_process';

test('实际C#原生边界接受纵断面字段，同时拒绝force和未知字段',{skip:process.platform!=='win32'},()=>{
  const script=resolve(dirname(fileURLToPath(import.meta.url)),'fixtures/road-profile-input-probe.ps1');
  const out=execFileSync('pwsh',['-NoProfile','-File',script],{encoding:'utf8'});
  assert.match(out,/native_boundary_known_7_unknown_7_pass/);
});

test('显式纵断面要求106，旧请求兼容，原生拒绝与费用完整回传',async t=>{
  let version='0.10.4';const requests=[];
  const native={dryRun:true,allowed:false,applied:false,nativePreviewCost:1234,errors:['OverlapExisting'],
    verticalProfile:{requested:true,processed:true,restoredCourses:2,allowed:true,error:null}};
  const mock=createServer(async(req,res)=>{
    let body='';for await(const c of req)body+=c;
    requests.push({url:req.url,body});res.setHeader('content-type','application/json');
    res.end(JSON.stringify(req.url==='/ping'?{version}:native));
  });
  await new Promise(r=>mock.listen(0,'127.0.0.1',r));t.after(()=>{mock.closeAllConnections();mock.close();});
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
  const client=new Client({name:'road-profile-test',version:'1'});t.after(()=>client.close());
  await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,stderr:'pipe',
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`}}));
  const args={x:0,z:0,segments:[{prefab:'Highway Oneway - 5 lanes',curve:Object.fromEntries(['a','b','c','d'].map((k,i)=>[k,{x:i*40,y:i,z:0}])),nodes:{start:0,end:1}}]};
  const call=a=>client.callTool({name:'cs2_build_road_layout',arguments:a});
  assert.equal((await call({...args,preserveVerticalProfile:true})).isError,true);
  assert.deepEqual(requests.map(x=>x.url),['/ping']);
  requests.length=0;
  assert.notEqual((await call(args)).isError,true);
  assert.equal(JSON.parse(requests.at(-1).body).preserveVerticalProfile,undefined);
  requests.length=0;version='0.10.5';
  const result=await call({...args,preserveVerticalProfile:true});
  assert.notEqual(result.isError,true);assert.deepEqual(JSON.parse(result.content[0].text),native);
  assert.equal(requests.filter(x=>x.url==='/build/road-layout').length,1);
  assert.equal(JSON.parse(requests.at(-1).body).preserveVerticalProfile,true);
  assert.equal(JSON.parse(requests.at(-1).body).dryRun,true);
  for(const bad of ['true',1,null]) {
    requests.length=0;assert.equal((await call({...args,preserveVerticalProfile:bad})).isError,true);assert.deepEqual(requests,[]);
  }
});
