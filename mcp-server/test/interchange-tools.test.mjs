import test from 'node:test';
import assert from 'node:assert/strict';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';
import {createServer} from 'node:http';

test('asset tools refuse old bridges and preserve preview, paging and Chinese exact names',async t=>{
  let version='0.4.0'; const requests=[];
  const mock=createServer((req,res)=>{
    requests.push(req.url); res.setHeader('Content-Type','application/json');
    res.end(JSON.stringify(req.url==='/ping'?{version}:{ok:true}));
  });
  await new Promise(resolve=>mock.listen(0,'127.0.0.1',resolve));
  t.after(()=>{mock.closeAllConnections();mock.close();});
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
  const client=new Client({name:'asset-contract-test',version:'1'});
  const transport=new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`},stderr:'pipe'});
  t.after(()=>client.close()); await client.connect(transport);
  const prefab='奇怪的立交 + 04';
  for(const [name,args] of [['cs2_interchange_info',{name:prefab}],['cs2_find_prefabs',{category:'asset'}],['cs2_place_interchange',{prefab,x:10,z:20}]]){
    requests.length=0;const result=await client.callTool({name,arguments:args});
    assert.equal(result.isError,true);assert.deepEqual(requests,['/ping']);
  }
  version='0.10.0';requests.length=0;
  let result=await client.callTool({name:'cs2_interchange_info',arguments:{name:prefab,offset:100,limit:105}});
  assert.notEqual(result.isError,true);let url=new URL(requests.at(-1),'http://localhost');
  assert.equal(url.pathname,'/prefabs/interchange');assert.equal(url.searchParams.get('name'),prefab);
  assert.equal(url.searchParams.get('offset'),'100');assert.equal(url.searchParams.get('limit'),'105');
  requests.length=0;result=await client.callTool({name:'cs2_place_interchange',arguments:{prefab,x:10,z:20,rotation:90,elevation:8}});
  assert.notEqual(result.isError,true);url=new URL(requests.at(-1),'http://localhost');
  assert.equal(url.pathname,'/build/interchange/place');assert.equal(url.searchParams.get('prefab'),prefab);
  assert.equal(url.searchParams.get('dryRun'),'true');assert.equal(url.searchParams.has('force'),false);
  assert.equal(url.searchParams.get('elevation'),'8');assert.equal(url.searchParams.get('rotation'),'90');
  requests.length=0;result=await client.callTool({name:'cs2_place_interchange',arguments:{prefab,x:10,z:20,dryRun:false}});
  assert.notEqual(result.isError,true);assert.equal(new URL(requests.at(-1),'http://localhost').searchParams.get('dryRun'),'false');
  requests.length=0;result=await client.callTool({name:'cs2_place_interchange',arguments:{prefab,x:10,z:20,elevation:90}});
  assert.equal(result.isError,true);assert.deepEqual(requests,[]);
});
