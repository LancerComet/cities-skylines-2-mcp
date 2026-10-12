import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname, resolve} from 'node:path';

test('轨道诊断SDK分页及边界：只读查询、0基站段、重复目标和Owner缺口原样返回', async t => {
  const requests=[];
  let version='0.10.2';
  const networkPath={totalPathElements:300,offset:128,returned:128,nextPathOffset:256,pageComplete:false,complete:false,
    elements:[{pathIndex:128,target:{entity:{index:9,version:2}},ownerTraceComplete:false,ownerTraceTermination:'missingEntity'},
      {pathIndex:129,target:{entity:{index:9,version:2}},ownerTraceComplete:true}]};
  const server=createServer((req,res)=>{
    const u=new URL(req.url,'http://127.0.0.1');
    if(u.pathname==='/ping') {res.setHeader('content-type','application/json');res.end(JSON.stringify({version}));return;}
    requests.push({method:req.method,path:u.pathname,params:Object.fromEntries(u.searchParams)});
    res.setHeader('content-type','application/json');
    res.end(JSON.stringify({lines:[{pathDiagnostics:{segments:[{roadPath:{roads:[],complete:true},networkPath}]} }]}));
  });
  await new Promise(r=>server.listen(0,'127.0.0.1',r));
  const transport=new StdioClientTransport({command:process.execPath,args:[resolve(dirname(fileURLToPath(import.meta.url)),'../dist/index.js')],
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${server.address().port}`}});
  const client=new Client({name:'track-trace-boundary',version:'1'});
  t.after(async()=>{await client.close();server.closeAllConnections();server.close();});
  await client.connect(transport);
  const args={type:'metro',query:'M1',limit:1,includePathTrace:true,pathSegment:0,pathOffset:128,pathLimit:128};
  assert.equal((await client.callTool({name:'cs2_list_transit_lines',arguments:args})).isError,true);
  assert.equal(requests.length,0);
  version='0.10.3';
  const result=await client.callTool({name:'cs2_list_transit_lines',arguments:args});
  assert.equal(result.isError,undefined);
  assert.deepEqual(JSON.parse(result.content[0].text).lines[0].pathDiagnostics.segments[0].networkPath,networkPath);
  assert.equal(requests[0].method,'GET');
  assert.equal(requests[0].path,'/transit/lines');
  assert.deepEqual(requests[0].params,{type:'metro',query:'M1',includePathTrace:'true',pathSegment:'0',pathOffset:'128',pathLimit:'128',limit:'1'});
  for(const bad of [{pathLimit:257},{pathLimit:0},{pathOffset:-1},{pathSegment:-1},{includePathTrace:'true'}])
    assert.equal((await client.callTool({name:'cs2_list_transit_lines',arguments:{...args,...bad}})).isError,true);
  assert.equal(requests.length,1);
  await client.callTool({name:'cs2_list_transit_lines',arguments:{includeStops:true,limit:1}});
  assert.deepEqual(requests[1].params,{includeStops:'true',limit:'1'});
});
