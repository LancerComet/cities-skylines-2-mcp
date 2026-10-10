import test from 'node:test';
import assert from 'node:assert/strict';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';
import {createServer} from 'node:http';

test('layout POST retains large UTF8 geometry, requires new bridge and previews by default',async t=>{
  let version='0.10.0';const requests=[];
  const mock=createServer(async(req,res)=>{
    let body='';for await(const chunk of req)body+=chunk;
    requests.push({url:req.url,method:req.method,body});res.setHeader('Content-Type','application/json');
    res.end(JSON.stringify(req.url==='/ping'?{version}:{ok:true}));
  });
  await new Promise(resolve=>mock.listen(0,'127.0.0.1',resolve));t.after(()=>{mock.closeAllConnections();mock.close();});
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');const client=new Client({name:'layout-test',version:'1'});
  const transport=new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,stderr:'pipe',
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`}});
  t.after(()=>client.close());await client.connect(transport);
  const segment={prefab:'五车道 + 主线',curve:{a:{x:0,y:10,z:0},b:{x:40,y:10,z:0},c:{x:80,y:10,z:0},d:{x:120,y:10,z:0}},nodes:{start:0,end:1},upgrades:{general:'Elevated',left:'0',right:'0'}};
  const args={x:100,z:200,segments:Array.from({length:100},(_,i)=>({...segment,nodes:{start:i,end:i+1}}))};
  let result=await client.callTool({name:'cs2_build_road_layout',arguments:args});assert.equal(result.isError,true);assert.deepEqual(requests.map(r=>r.url),['/ping']);
  version='0.10.1';requests.length=0;result=await client.callTool({name:'cs2_build_road_layout',arguments:args});assert.notEqual(result.isError,true);
  const request=requests.at(-1);assert.equal(request.method,'POST');assert.equal(request.url,'/build/road-layout');
  const parsed=JSON.parse(request.body);assert.equal(parsed.dryRun,true);assert.deepEqual(parsed.segments,args.segments);assert.ok(Buffer.byteLength(request.body)>20000);assert.equal(parsed.force,undefined);
  requests.length=0;result=await client.callTool({name:'cs2_build_road_layout',arguments:{...args,dryRun:false}});assert.notEqual(result.isError,true);assert.equal(JSON.parse(requests.at(-1).body).dryRun,false);
  requests.length=0;const terrainCoordinate={...segment,curve:Object.fromEntries(Object.entries(segment.curve).map(([k,p])=>[k,{...p,y:100}]))};
  result=await client.callTool({name:'cs2_build_road_layout',arguments:{x:0,z:0,segments:[terrainCoordinate]}});assert.notEqual(result.isError,true);
  assert.equal(JSON.parse(requests.at(-1).body).segments[0].curve.a.y,100);
  requests.length=0;result=await client.callTool({name:'cs2_build_road_layout',arguments:{x:0,z:0,segments:[{...segment,force:true}]}});assert.equal(result.isError,true);assert.deepEqual(requests,[]);
});
