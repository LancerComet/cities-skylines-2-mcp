import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';

async function connect(t,handler) {
  const mock=createServer(handler);
  await new Promise(r=>mock.listen(0,'127.0.0.1',r));
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
  const client=new Client({name:'native-inspection-test',version:'1'});
  t.after(async()=>{await client.close();mock.closeAllConnections();mock.close();});
  await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`},stderr:'pipe'}));
  return client;
}

test('utility ports and resource diagnostics preserve native values and use read-only requests',async t=>{
  let version='0.10.1';const requests=[];
  const payload={portsComplete:true,ports:[{entity:{index:12,version:3},capacity:1073741823,flow:0}],
    sewage:{exportedForPaidDisposal:123},renderFrameRateSincePreviousRead:null};
  const client=await connect(t,(req,res)=>{requests.push({url:req.url,method:req.method});
    res.end(JSON.stringify(req.url==='/ping'?{version}:payload));});
  const {tools}=await client.listTools();
  assert.equal(tools.length,118);
  for(const name of ['cs2_utilities','cs2_outside_connections','cs2_resource_diagnostics'])
    assert.equal(tools.find(x=>x.name===name).annotations.readOnlyHint,true);
  for(const name of ['cs2_utilities','cs2_outside_connections']) {
    requests.length=0;
    assert.equal((await client.callTool({name,arguments:{}})).isError,true);
    assert.deepEqual(requests,[{url:'/ping',method:'GET'}]);
  }
  version='0.10.2';
  for(const [name,path] of [['cs2_utilities','/city/utilities'],['cs2_outside_connections','/city/outside-connections'],
                          ['cs2_resource_diagnostics','/diagnostics/resources']]) {
    requests.length=0;
    const result=await client.callTool({name,arguments:{}});
    assert.notEqual(result.isError,true);
    assert.deepEqual(JSON.parse(result.content[0].text),payload);
    assert.deepEqual(requests.at(-1),{url:path,method:'GET'});
  }
});

test('crosswalks require an explicit junction approach and updated bridge before submission',async t=>{
  let version='0.10.1';const requests=[];
  const client=await connect(t,(req,res)=>{requests.push({url:req.url,method:req.method});
    res.end(JSON.stringify(req.url==='/ping'?{version}:{applied:true}));});
  const edge={index:123,version:4};
  for(const versionToUse of ['0.10.1','0.10.2']) {
    version=versionToUse;requests.length=0;
    const result=await client.callTool({name:'cs2_apply_road_service',arguments:{...edge,prefab:'Crosswalk',remove:true}});
    assert.equal(result.isError,true);assert.deepEqual(requests,[]);
  }
  const args={action:'nodeController.crosswalk',payload:{entity:{index:99,version:2},edge,mode:2,dryRun:false}};
  version='0.10.1';requests.length=0;
  assert.equal((await client.callTool({name:'cs2_mod_action',arguments:args})).isError,true);
  assert.deepEqual(requests,[{url:'/ping',method:'GET'}]);
  version='0.10.2';requests.length=0;
  assert.notEqual((await client.callTool({name:'cs2_mod_action',arguments:args})).isError,true);
  const url=new URL(requests.at(-1).url,'http://localhost');
  assert.equal(url.pathname,'/build/mod/action');assert.equal(requests.at(-1).method,'POST');
  assert.deepEqual(JSON.parse(url.searchParams.get('payload')),args.payload);
  version='0.10.1';requests.length=0;
  assert.equal((await client.callTool({name:'cs2_upgrade_road',arguments:{...edge,upgrades:'trees',side:'left'}})).isError,true);
  assert.deepEqual(requests,[{url:'/ping',method:'GET'}]);
});

test('road elevations are finite inputs whose native range belongs to the bridge',async t=>{
  const requests=[];
  const client=await connect(t,(req,res)=>{requests.push(req.url);res.end(JSON.stringify(req.url==='/ping'?{version:'0.10.2'}:{applied:true}));});
  const result=await client.callTool({name:'cs2_connect_road',arguments:{prefab:'Test network',x1:0,z1:0,x2:80,z2:0,e1:-80,e2:-80,dryRun:true}});
  assert.notEqual(result.isError,true);
  const url=new URL(requests.at(-1),'http://localhost');
  assert.equal(url.pathname,'/build/road/connect');assert.equal(url.searchParams.get('e1'),'-80');
  const {tools}=await client.listTools();
  const elevation=tools.find(x=>x.name==='cs2_connect_road').inputSchema.properties.e1;
  assert.equal(elevation.minimum,undefined);assert.equal(elevation.maximum,undefined);
});
