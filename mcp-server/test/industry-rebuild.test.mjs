import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';
import {isMutation,requiresPost} from '../dist/bridge-client.js';

test('industry rebuild requires the current bridge and carries full Hub/parent identity into a normal POST preview',async t=>{
  let version='0.10.6';const requests=[];
  const mock=createServer((req,res)=>{requests.push({url:req.url,method:req.method});res.end(JSON.stringify(req.url==='/ping'?{version}:{allowed:true,applied:false}));});
  await new Promise(resolve=>mock.listen(0,'127.0.0.1',resolve));t.after(()=>{mock.closeAllConnections();mock.close();});
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
  const client=new Client({name:'industry-rebuild-contract',version:'1'});
  await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`},stderr:'pipe'}));t.after(()=>client.close());
  const args={index:123,version:4,parent:{index:45,version:6},expectedLoadedSaveGuid:'b'.repeat(32)};
  let result=await client.callTool({name:'cs2_rebuild_industry_hub',arguments:args});
  assert.equal(result.isError,true);assert.deepEqual(requests.map(r=>r.url),['/ping']);
  version='0.10.7';requests.length=0;
  result=await client.callTool({name:'cs2_rebuild_industry_hub',arguments:args});assert.notEqual(result.isError,true);
  let request=requests.at(-1),url=new URL(request.url,'http://localhost');
  assert.equal(request.method,'POST');assert.equal(url.pathname,'/build/industry/rebuild');
  for(const [key,value] of Object.entries({index:'123',version:'4',parentIndex:'45',parentVersion:'6',expectedLoadedSaveGuid:'b'.repeat(32),dryRun:'true'}))assert.equal(url.searchParams.get(key),value);
  assert.equal(url.searchParams.has('parent'),false);
  requests.length=0;
  result=await client.callTool({name:'cs2_rebuild_industry_hub',arguments:{...args,dryRun:false}});
  assert.notEqual(result.isError,true);assert.equal(new URL(requests.at(-1).url,'http://localhost').searchParams.get('dryRun'),'false');
  for(const bad of [{...args,index:0},{...args,parent:{index:45,version:0}},{...args,expectedLoadedSaveGuid:'not-a-guid'}]){
    requests.length=0;result=await client.callTool({name:'cs2_rebuild_industry_hub',arguments:bad});
    assert.equal(result.isError,true);assert.deepEqual(requests,[]);
  }
});

test('industry preview transport remains POST while application uncertainty is classified separately',()=>{
  assert.equal(requiresPost('/build/industry/rebuild?dryRun=true'),true);
  assert.equal(isMutation('/build/industry/rebuild?dryRun=true'),false);
  assert.equal(isMutation('/build/industry/rebuild?dryRun=false'),true);
  assert.equal(isMutation('/build/industry/rebuild'),true);
});
