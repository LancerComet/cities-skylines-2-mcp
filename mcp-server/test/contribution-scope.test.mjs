import test from 'node:test';
import assert from 'node:assert/strict';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {createServer} from 'node:http';
import {dirname,resolve} from 'node:path';
import {fileURLToPath} from 'node:url';

test('公开贡献不重新暴露PR4工具、航空创建或布局替换参数；未知参数零IO',async t=>{
  const requests=[];const mock=createServer((req,res)=>{requests.push(req.url);res.end('{"version":"0.10.5"}');});
  await new Promise(r=>mock.listen(0,'127.0.0.1',r));
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');const client=new Client({name:'public-scope',version:'1'});
  t.after(async()=>{await client.close();mock.closeAllConnections();mock.close();});
  await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,stderr:'pipe',env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`}}));
  const {tools}=await client.listTools();
  for(const name of ['cs2_build_grid','cs2_cancel_grid','cs2_create_route','cs2_delete_district','cs2_district_detail','cs2_grid_status','cs2_production','cs2_rename_district','cs2_replace_net','cs2_reshape_district','cs2_resource_tax','cs2_resources','cs2_specialized_area'])
    assert.equal(tools.some(x=>x.name===name),false,name);
  const layout=tools.find(x=>x.name==='cs2_build_road_layout');
  assert.equal(layout.inputSchema.properties.replaceRoads,undefined);
  assert.equal(layout.inputSchema.additionalProperties,false);
  assert.equal(tools.find(x=>x.name==='cs2_create_transit_line').inputSchema.properties.type.enum.includes('airplane'),false);
  const args={x:0,z:0,segments:[{prefab:'ordinary road',curve:Object.fromEntries(['a','b','c','d'].map((k,i)=>[k,{x:i*40,y:0,z:0}])),nodes:{start:0,end:1}}]};
  for(const extra of [{replaceRoads:[{index:1,version:1}]},{force:true}])
    assert.equal((await client.callTool({name:layout.name,arguments:{...args,...extra}})).isError,true);
  assert.deepEqual(requests,[]);
});
