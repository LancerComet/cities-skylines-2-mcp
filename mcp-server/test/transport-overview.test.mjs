import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {dirname,resolve} from 'node:path';
import {fileURLToPath} from 'node:url';

test('全部运输统计区分非线路服务与零线路；渡轮不混入客船或货运',async t=>{
 const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
 let version='0.4.0'; const requests=[];
 const mock=createServer((req,res)=>{
  if(req.url==='/ping')res.end(JSON.stringify({version}));
  else {requests.push(req.url);res.end(JSON.stringify(req.url==='/transit/overview'
   ?{passengers:[{type:'taxi',showsLines:false,lineCount:null},{type:'ferry',showsLines:true,lineCount:0}],cargo:[{type:'airplane',cargoCountRaw:0}]}
   :{created:true,type:'ferry'}));}
 });
 await new Promise(done=>mock.listen(0,'127.0.0.1',done));
 const client=new Client({name:'transport-test',version:'1'});
 const transport=new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
  env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`},stderr:'pipe'});
 t.after(async()=>{await client.close();mock.closeAllConnections();mock.close();});
 await client.connect(transport);
 assert.equal((await client.callTool({name:'cs2_transport_overview',arguments:{}})).isError,true);
 assert.equal((await client.callTool({name:'cs2_facility_catalog',arguments:{}})).isError,true);
 const stops=[{index:1,version:1},{index:2,version:1}];
 assert.equal((await client.callTool({name:'cs2_create_transit_line',arguments:{type:'ferry',stops}})).isError,true);
 assert.equal(requests.length,0);
 version='0.10.1';
 const overview=await client.callTool({name:'cs2_transport_overview',arguments:{}});
 assert.notEqual(overview.isError,true);
 const result=JSON.parse(overview.content[0].text);
 assert.equal(result.passengers[0].lineCount,null);assert.equal(result.passengers[1].lineCount,0);
 assert.equal((await client.callTool({name:'cs2_create_transit_line',arguments:{type:'ferry',cargo:true,stops}})).isError,true);
 assert.equal(requests.length,1);
 const line=await client.callTool({name:'cs2_create_transit_line',arguments:{type:'ferry',stops}});
 assert.notEqual(line.isError,true);
 assert.match(requests[1],/^\/transit\/lines\/create\?/);assert.match(requests[1],/type=ferry/);
 assert.doesNotMatch(requests[1],/type=ship/);
});
