import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';

test('native rebuild rejects old bridges and defaults to a cleared preview',async t=>{
  let version='0.10.1';const requests=[];
  const mock=createServer((req,res)=>{requests.push(req.url);res.end(JSON.stringify(req.url==='/ping'?{version}:{ok:true}));});
  await new Promise(resolve=>mock.listen(0,'127.0.0.1',resolve));t.after(()=>{mock.closeAllConnections();mock.close();});
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
  const client=new Client({name:'rebuild-contract-test',version:'1'});
  await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`},stderr:'pipe'}));t.after(()=>client.close());
  let result=await client.callTool({name:'cs2_rebuild_service_building',arguments:{index:123,version:4}});
  assert.equal(result.isError,true);assert.deepEqual(requests,['/ping']);
  version='0.10.2';requests.length=0;
  result=await client.callTool({name:'cs2_rebuild_service_building',arguments:{index:123,version:4}});
  assert.notEqual(result.isError,true);
  let url=new URL(requests.at(-1),'http://localhost');assert.equal(url.pathname,'/build/building/rebuild');
  assert.equal(url.searchParams.get('index'),'123');assert.equal(url.searchParams.get('version'),'4');assert.equal(url.searchParams.get('dryRun'),'true');
  requests.length=0;
  result=await client.callTool({name:'cs2_rebuild_service_building',arguments:{index:123,version:4,dryRun:false}});
  assert.notEqual(result.isError,true);url=new URL(requests.at(-1),'http://localhost');assert.equal(url.searchParams.get('dryRun'),'false');
  requests.length=0;
  result=await client.callTool({name:'cs2_rebuild_service_building',arguments:{index:0,version:4}});
  assert.equal(result.isError,true);assert.deepEqual(requests,[]);
});
