import test from 'node:test';
import assert from 'node:assert/strict';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {createServer} from 'node:http';
import {dirname,resolve} from 'node:path';
import {fileURLToPath} from 'node:url';

test('正常双轨明示纵断面完整传递，默认预览且原生拒绝不回退重放',async t=>{
  const requests=[];
  let native={allowed:true,applied:false,nativePreviewCost:1400,errors:[],verticalProfile:{allowed:true}};
  const mock=createServer(async(req,res)=>{
    let body='';for await(const chunk of req)body+=chunk;
    requests.push({url:req.url,body});res.setHeader('Content-Type','application/json');
    res.end(JSON.stringify(req.url==='/ping'?{version:'0.10.5'}:native));
  });
  await new Promise(r=>mock.listen(0,'127.0.0.1',r));t.after(()=>{mock.closeAllConnections();mock.close();});
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
  const client=new Client({name:'track-layout-test',version:'1'});t.after(()=>client.close());
  await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,stderr:'pipe',
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`}}));
  const segment={prefab:'Double Train Track',curve:{a:{x:0,y:.25,z:0},b:{x:0,y:2.25,z:82},c:{x:0,y:4.25,z:164},d:{x:0,y:6.25,z:246}},
    nodes:{start:0,end:1},parentMesh:{start:0,end:0}};
  const args={x:3500,z:6594,preserveVerticalProfile:true,segments:[segment]};
  const call=a=>client.callTool({name:'cs2_build_road_layout',arguments:a});
  let result=await call(args);assert.notEqual(result.isError,true);
  assert.equal(JSON.parse(requests.at(-1).body).dryRun,true);
  assert.deepEqual(JSON.parse(requests.at(-1).body).segments,[segment]);
  assert.equal(JSON.parse(requests.at(-1).body).preserveVerticalProfile,true);
  requests.length=0;
  native={allowed:false,applied:false,errors:['OverlapExisting'],nativePreviewCost:1400,verticalProfile:{allowed:true}};
  result=await call({...args,dryRun:false});
  assert.deepEqual(JSON.parse(result.content[0].text),native);
  assert.equal(requests.filter(q=>q.url==='/build/road-layout').length,1);
  assert.equal(JSON.parse(requests.at(-1).body).dryRun,false);
  requests.length=0;
  result=await call({...args,segments:[{...segment,force:true}]});
  assert.equal(result.isError,true);assert.deepEqual(requests,[]);
});
