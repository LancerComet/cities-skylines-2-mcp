import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';
import {bridgeJson,isMutation} from '../dist/bridge-client.js';

test('全图水路读取阻止旧桥接与越界预算，完整传回港口内网和缺失状态',async t=>{
  let version='0.10.4';const requests=[];
  const snapshot={matched:2,returned:2,truncated:false,complete:false,missing:[{entity:{index:9,version:2},curveKnown:false}],waterways:[
    {entity:{index:8,version:1},prefab:'Pier Seaway',owner:{index:44,version:2},curve:{a:{x:-7168,y:10,z:0},b:{x:-2000,y:10,z:0},c:{x:2000,y:10,z:0},d:{x:7168,y:10,z:0}}},
    {entity:{index:9,version:2},prefab:'Narrow Boatway',curve:null}
  ]};
  const bridge=createServer((req,res)=>{
    requests.push({method:req.method,url:req.url});res.setHeader('content-type','application/json');
    res.end(JSON.stringify(req.url==='/ping'?{version}:snapshot));
  });
  await new Promise(r=>bridge.listen(0,'127.0.0.1',r));
  t.after(()=>{bridge.closeAllConnections();bridge.close();});
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
  const client=new Client({name:'waterway-read-test',version:'1'});
  t.after(()=>client.close());
  await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${bridge.address().port}`,DOTENV_CONFIG_QUIET:'true'},stderr:'pipe'}));
  const tool=(await client.listTools()).tools.find(t=>t.name==='cs2_waterway_graph');
  assert.equal(tool.annotations.readOnlyHint,true);assert.equal(tool.annotations.destructiveHint,false);
  const old=await client.callTool({name:tool.name,arguments:{}});
  assert.equal(old.isError,true);assert.equal(JSON.parse(old.content[0].text).mayHaveApplied,false);
  assert.deepEqual(requests,[{method:'GET',url:'/ping'}]);
  version='0.10.5';requests.length=0;
  for(const args of [{limit:0},{limit:3001},{limit:1.5},{expectedLoadedSaveGuid:'bad'}])
    assert.equal((await client.callTool({name:tool.name,arguments:args})).isError,true);
  assert.deepEqual(requests,[]);
  const guid='3699f362ae2b65cd91f5fa7aab4701ba';
  const result=await client.callTool({name:tool.name,arguments:{limit:3000,expectedLoadedSaveGuid:guid}});
  assert.deepEqual(JSON.parse(result.content[0].text),snapshot);
  const sent=requests.at(-1);const url=new URL(sent.url,'http://localhost');
  assert.equal(sent.method,'GET');assert.equal(url.pathname,'/city/waterway-graph');
  assert.deepEqual(Object.fromEntries(url.searchParams),{limit:'3000',expectedLoadedSaveGuid:guid});
  assert.equal(isMutation(sent.url),false);
});

test('全图水路只读请求的损坏响应保留非写入语义',async t=>{
  const bridge=createServer((req,res)=>{res.writeHead(200,{'content-type':'application/json'});res.end('{');});
  await new Promise(r=>bridge.listen(0,'127.0.0.1',r));
  t.after(()=>{bridge.closeAllConnections();bridge.close();});
  await assert.rejects(bridgeJson('/city/waterway-graph?limit=3000',1000,`http://127.0.0.1:${bridge.address().port}`),
    e=>e.mayHaveApplied===false && e.safeToRetry===false);
});
