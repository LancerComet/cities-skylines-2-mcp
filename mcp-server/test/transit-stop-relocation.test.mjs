import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname, resolve} from 'node:path';
import {bridgeJson, isMutation} from '../dist/bridge-client.js';

test('迁站默认完整预览，旧桥接/错误布尔/无效身份及非有限坐标均不发送施工请求', async t=>{
  const writes=[]; let version='0.10.2';
  const server=createServer((req,res)=>{
    res.setHeader('content-type','application/json');
    const u=new URL(req.url,'http://127.0.0.1');
    if(u.pathname==='/ping') res.end(JSON.stringify({version}));
    else {writes.push(Object.fromEntries(u.searchParams)); res.end('{"dryRun":true,"applied":false}');}
  });
  await new Promise(r=>server.listen(0,'127.0.0.1',r));
  const transport=new StdioClientTransport({command:process.execPath,args:[resolve(dirname(fileURLToPath(import.meta.url)),'../dist/index.js')],
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${server.address().port}`}});
  const client=new Client({name:'relocate-boundary-test',version:'1'});
  t.after(async()=>{await client.close();server.closeAllConnections();server.close();});
  await client.connect(transport);
  const args={index:123,version:1,x:40,z:50,road:{index:456,version:1},expectedLoadedSaveGuid:'0123456789abcdef0123456789abcdef'};
  assert.equal((await client.callTool({name:'cs2_relocate_transit_stop',arguments:args})).isError,true);
  assert.equal(writes.length,0);
  version='0.10.3';
  for(const bad of [{dryRun:'false'},{dryRun:0},{dryRun:null},{expectedLoadedSaveGuid:'old-city'},{x:Infinity},{road:{index:1,version:1,force:true}}])
    assert.equal((await client.callTool({name:'cs2_relocate_transit_stop',arguments:{...args,...bad}})).isError,true);
  assert.equal(writes.length,0);
  const preview=await client.callTool({name:'cs2_relocate_transit_stop',arguments:args});
  assert.equal(preview.isError,undefined);
  assert.equal(JSON.parse(preview.content[0].text).applied,false);
  assert.equal(writes[0].dryRun,'true');
  assert.equal(writes[0].expectedLoadedSaveGuid,args.expectedLoadedSaveGuid);
  await client.callTool({name:'cs2_relocate_transit_stop',arguments:{...args,dryRun:false}});
  assert.equal(writes[1].dryRun,'false');
  assert.equal(writes[1].road,'456:1');
});

test('迁站应用响应未知不能自动重放，预览与写入的超时语义分开',async t=>{
  const seen=[];
  const server=createServer(req=>seen.push(req.url));
  await new Promise(r=>server.listen(0,'127.0.0.1',r));
  t.after(()=>{server.closeAllConnections();server.close();});
  const base=`http://127.0.0.1:${server.address().port}`;
  assert.equal(isMutation('/transit/stops/relocate?dryRun=true'),false);
  assert.equal(isMutation('/transit/stops/relocate?dryRun=false'),true);
  assert.equal(isMutation('/transit/stops/relocate'),true);
  await assert.rejects(bridgeJson('/transit/stops/relocate?dryRun=false',100,base),e=>e.mayHaveApplied&&!e.safeToRetry);
  assert.equal(seen.length,1);
  await assert.rejects(bridgeJson('/transit/stops/relocate?dryRun=true',100,base),e=>!e.mayHaveApplied&&!e.safeToRetry);
  assert.equal(seen.length,2);
});

test('正常迁站碰撞Target诊断与未知应用标记保留到SDK错误',async t=>{
  const details=[{error:'OverlapExisting',collisionTarget:{entity:{index:2,version:3},prefab:'Medium Road'}}];
  const server=createServer((_req,res)=>{res.writeHead(409,{'content-type':'application/json'});res.end(JSON.stringify({error:'native readback differs',mayHaveApplied:true,validationErrors:details}));});
  await new Promise(r=>server.listen(0,'127.0.0.1',r));
  t.after(()=>{server.closeAllConnections();server.close();});
  await assert.rejects(bridgeJson('/transit/stops/relocate?dryRun=false',1000,`http://127.0.0.1:${server.address().port}`),e=>{
    assert.deepEqual(e.validationErrors,details);
    return e.mayHaveApplied&&!e.safeToRetry;
  });
});
