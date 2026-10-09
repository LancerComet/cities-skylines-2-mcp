import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';

test('batch removal gates versions, preserves complete identities and defaults to native preview',async t=>{
  let version='0.4.0';const requests=[];
  const mock=createServer(async(req,res)=>{
    let body='';for await(const chunk of req)body+=chunk;
    requests.push({url:req.url,method:req.method,body});
    res.end(JSON.stringify(req.url==='/ping'?{version}:{ok:true}));
  });
  await new Promise(resolve=>mock.listen(0,'127.0.0.1',resolve));t.after(()=>{mock.closeAllConnections();mock.close();});
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
  const client=new Client({name:'removal-contract-test',version:'1'});
  const transport=new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`},stderr:'pipe'});
  await client.connect(transport);t.after(()=>client.close());
  const roads=[{index:123,version:4},{index:456,version:7}];
  let result=await client.callTool({name:'cs2_remove_road_layout',arguments:{roads}});
  assert.equal(result.isError,true);assert.deepEqual(requests.map(r=>r.url),['/ping']);
  version='0.10.0';requests.length=0;
  result=await client.callTool({name:'cs2_remove_road_layout',arguments:{roads}});
  assert.notEqual(result.isError,true);assert.equal(requests.at(-1).method,'POST');
  assert.equal(requests.at(-1).url,'/build/road-layout/remove');
  assert.deepEqual(JSON.parse(requests.at(-1).body),{roads,dryRun:true});
  requests.length=0;
  result=await client.callTool({name:'cs2_remove_road_layout',arguments:{roads:[roads[0],roads[0]],dryRun:false}});
  assert.equal(result.isError,true);assert.deepEqual(requests,[]);
  for(const invalid of [[{index:123}],[],[{index:123,version:4,force:true}]]){
    result=await client.callTool({name:'cs2_remove_road_layout',arguments:{roads:invalid}});
    assert.equal(result.isError,true);assert.deepEqual(requests,[]);
  }
});
