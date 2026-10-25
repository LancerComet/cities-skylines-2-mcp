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
  for(const name of ['cs2_processing_summary','cs2_extractor_calibration','cs2_industry_work_routes'])
    assert.equal(tools.find(x=>x.name===name).annotations.readOnlyHint,true,name);
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

test('PR4之外的三项独有诊断保留，只读、旧公共版拒绝且未知字段原样返回',async t=>{
  let version='0.10.4';const requests=[];const witness={complete:false,unknown:null,nativeCounts:[3,7]};
  const mock=createServer((req,res)=>{requests.push({path:req.url,method:req.method});res.end(JSON.stringify(req.url==='/ping'?{version}:witness));});
  await new Promise(r=>mock.listen(0,'127.0.0.1',r));
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');const client=new Client({name:'independent-read-scope',version:'1'});
  t.after(async()=>{await client.close();mock.closeAllConnections();mock.close();});
  await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,stderr:'pipe',env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`}}));
  const guid='1234567890abcdef1234567890abcdef';
  const cases=[['cs2_processing_summary',{},'/city/processing-summary'],['cs2_extractor_calibration',{expectedLoadedSaveGuid:guid},'/city/extractor-calibration'],['cs2_industry_work_routes',{index:1,version:1,expectedLoadedSaveGuid:guid},'/city/industry-work-routes']];
  for(const [name,args] of cases){requests.length=0;assert.equal((await client.callTool({name,arguments:args})).isError,true);assert.deepEqual(requests,[{path:'/ping',method:'GET'}]);}
  version='0.10.5';
  for(const [name,args,path] of cases){requests.length=0;const result=await client.callTool({name,arguments:args});assert.notEqual(result.isError,true);assert.deepEqual(JSON.parse(result.content[0].text),witness);assert.equal(new URL(requests.at(-1).path,'http://localhost').pathname,path);assert.equal(requests.at(-1).method,'GET');}
});
