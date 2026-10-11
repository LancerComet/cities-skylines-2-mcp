import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {dirname,resolve} from 'node:path';
import {fileURLToPath} from 'node:url';

async function connect(t,handler) {
  const server=createServer(handler);
  await new Promise(done=>server.listen(0,'127.0.0.1',done));
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
  const client=new Client({name:'road-path-test',version:'1.0.0'});
  const transport=new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${server.address().port}`},stderr:'pipe'});
  t.after(async()=>{await client.close();server.closeAllConnections();server.close();});
  await client.connect(transport);
  return client;
}

for(const scenario of [
  {name:'native rejection',status:409,body:'{"error":"OverlapExisting"}',unknown:false},
  {name:'failure after apply',status:500,body:'{"error":"failed after apply"}',unknown:true},
  {name:'truncated success response',status:200,body:'{',unknown:true},
]) test(`road path preserves its prefix and stops on ${scenario.name}`,async t=>{
  let writes=0;
  const client=await connect(t,(req,res)=>{
    if(req.url==='/ping'){res.end('{"version":"0.10.2"}');return;}
    assert.equal(new URL(req.url,'http://localhost').pathname,'/build/road/connect');
    assert.equal(req.method,'POST');
    if(++writes===1)res.end('{"applied":true}');
    else{res.writeHead(scenario.status);res.end(scenario.body);}
  });
  const result=await client.callTool({name:'cs2_road_path',arguments:{prefab:'Test road',
    segments:Array.from({length:3},(_,i)=>({x1:i*80,z1:0,x2:(i+1)*80,z2:0}))}});
  assert.equal(result.isError,true);
  const data=JSON.parse(result.content[0].text);
  assert.equal(writes,2);assert.equal(data.completed,1);assert.equal(data.failedAt,1);
  assert.deepEqual(data.results,[{applied:true}]);
  assert.equal(data.failedSegmentMayHaveApplied,scenario.unknown);
  assert.equal(data.mayHaveApplied,true);assert.equal(data.partialChangesRemain,true);assert.equal(data.safeToRetry,false);
});

test('zoning never reverses roads when an old response lacks refresh metadata',async t=>{
  const requests=[];
  const client=await connect(t,(req,res)=>{requests.push(req.url);res.end('{"cellsChanged":1}');});
  const result=await client.callTool({name:'cs2_zone_area',arguments:{zone:'Test zone',x:0,z:0}});
  assert.notEqual(result.isError,true);
  assert.equal(requests.length,1);
  assert.equal(new URL(requests[0],'http://localhost').pathname,'/build/zone');
});
