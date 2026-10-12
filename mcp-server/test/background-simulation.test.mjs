import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';

test('后台模拟拒绝旧桥接和非原生倍速，保留显式启停与暂停参数',async t=>{
  let version='0.10.1'; const writes=[]; const popups=[];
  const mock=createServer((req,res)=>{
    res.setHeader('content-type','application/json');
    const url=new URL(req.url,'http://127.0.0.1');
    if(url.pathname==='/ping')res.end(JSON.stringify({version}));
    else if(url.pathname==='/sim/control'){
      writes.push(Object.fromEntries(url.searchParams));res.end(JSON.stringify({ok:true}));
    }else if(url.pathname==='/game/milestone-popup'){
      popups.push(Object.fromEntries(url.searchParams));res.end(JSON.stringify({pendingBefore:true,dismissed:url.searchParams.get('dismiss')==='true'}));
    }else {res.statusCode=404;res.end('{}');}
  });
  await new Promise(r=>mock.listen(0,'127.0.0.1',r));
  t.after(()=>{mock.closeAllConnections();mock.close();});
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
  const client=new Client({name:'background-clock-test',version:'1'});
  t.after(()=>client.close());
  await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`,DOTENV_CONFIG_QUIET:'true'},stderr:'pipe'}));
  let result=await client.callTool({name:'cs2_set_simulation',arguments:{backgroundSimulation:true,speed:4}});
  assert.equal(result.isError,true);assert.equal(writes.length,0);
  version='0.10.2';
  for(const arguments_ of [{backgroundSimulation:true,speed:8},{backgroundSimulation:'true',speed:4}]){
    result=await client.callTool({name:'cs2_set_simulation',arguments:arguments_});
    assert.equal(result.isError,true);assert.equal(writes.length,0);
  }
  result=await client.callTool({name:'cs2_set_simulation',arguments:{backgroundSimulation:true,speed:4,paused:false}});
  assert.notEqual(result.isError,true);
  assert.deepEqual(writes.at(-1),{speed:'4',paused:'false',backgroundSimulation:'true'});
  result=await client.callTool({name:'cs2_set_simulation',arguments:{paused:true}});
  assert.notEqual(result.isError,true);assert.deepEqual(writes.at(-1),{paused:'true'});
  result=await client.callTool({name:'cs2_set_simulation',arguments:{backgroundSimulation:false}});
  assert.notEqual(result.isError,true);assert.deepEqual(writes.at(-1),{backgroundSimulation:'false'});
  version='0.10.1';
  result=await client.callTool({name:'cs2_milestone_popup',arguments:{dismiss:true}});
  assert.equal(result.isError,true);assert.equal(popups.length,0);
  version='0.10.2';
  result=await client.callTool({name:'cs2_milestone_popup',arguments:{dismiss:'true'}});
  assert.equal(result.isError,true);assert.equal(popups.length,0);
  result=await client.callTool({name:'cs2_milestone_popup',arguments:{}});
  assert.notEqual(result.isError,true);assert.deepEqual(popups.at(-1),{});
  result=await client.callTool({name:'cs2_milestone_popup',arguments:{dismiss:true}});
  assert.notEqual(result.isError,true);assert.deepEqual(popups.at(-1),{dismiss:'true'});
});
