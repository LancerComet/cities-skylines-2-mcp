import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';
import {isMutation,bridgeJson} from '../dist/bridge-client.js';

test('扩建预览和成长目录不误报写入，明确应用的未知结果禁止重放',async t=>{
  const path='/build/building/upgrade/relocate';
  assert.equal(isMutation(path),true);
  assert.equal(isMutation(`${path}?dryRun=true`),false);
  assert.equal(isMutation(`${path}?dryRun=false`),true);
  assert.equal(isMutation('/prefabs/growables'),false);
  let calls=0;
  const server=createServer((_,res)=>{calls++;res.writeHead(409);res.end(JSON.stringify({error:'applied without complete readback',mayHaveApplied:true,safeToRetry:false}));});
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));t.after(()=>{server.closeAllConnections();server.close();});
  await assert.rejects(bridgeJson(`${path}?dryRun=false`,1000,`http://127.0.0.1:${server.address().port}`),e=>e.mayHaveApplied&&!e.safeToRetry);
  assert.equal(calls,1);
});

test('新工具拒绝旧桥接、完整身份必填且扩建搬迁默认只预览',async t=>{
  let version='0.10.4';const requests=[];
  const mock=createServer((req,res)=>{requests.push(req.url);res.end(JSON.stringify(req.url==='/ping'?{version}:{ok:true}));});
  await new Promise(resolve=>mock.listen(0,'127.0.0.1',resolve));t.after(()=>{mock.closeAllConnections();mock.close();});
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
  const client=new Client({name:'native-upgrade-growable-contract',version:'1'});
  await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`},stderr:'pipe'}));t.after(()=>client.close());
  const args={index:123,version:4,owner:'456:7',expectedLoadedSaveGuid:'1d3fa87430fb800b93c0aac4f0b74cf2',x:1740,z:-1966};
  for(const name of ['cs2_relocate_building_upgrade','cs2_growable_catalog']){
    requests.length=0;const result=await client.callTool({name,arguments:name==='cs2_growable_catalog'?{}:args});
    assert.equal(result.isError,true);assert.deepEqual(requests,['/ping']);
  }
  version='0.10.5';requests.length=0;
  let result=await client.callTool({name:'cs2_relocate_building_upgrade',arguments:args});
  assert.notEqual(result.isError,true);
  let url=new URL(requests.at(-1),'http://localhost');
  assert.equal(url.pathname,'/build/building/upgrade/relocate');assert.equal(url.searchParams.get('dryRun'),'true');
  assert.equal(url.searchParams.get('owner'),args.owner);assert.equal(url.searchParams.get('expectedLoadedSaveGuid'),args.expectedLoadedSaveGuid);
  requests.length=0;
  result=await client.callTool({name:'cs2_relocate_building_upgrade',arguments:{...args,dryRun:false}});
  assert.notEqual(result.isError,true);url=new URL(requests.at(-1),'http://localhost');assert.equal(url.searchParams.get('dryRun'),'false');
  for(const delta of [{index:0},{owner:undefined},{owner:'456'},{expectedLoadedSaveGuid:undefined},{expectedLoadedSaveGuid:'old'},{dryRun:'false'},{x:null}]){
    requests.length=0;result=await client.callTool({name:'cs2_relocate_building_upgrade',arguments:{...args,...delta}});
    assert.equal(result.isError,true);assert.deepEqual(requests,[]);
  }
  requests.length=0;result=await client.callTool({name:'cs2_growable_catalog',arguments:{}});
  assert.notEqual(result.isError,true);assert.deepEqual(requests,['/ping','/prefabs/growables']);
  const catalog=(await client.listTools()).tools.find(x=>x.name==='cs2_growable_catalog');
  assert.equal(catalog.annotations.readOnlyHint,true);
});
