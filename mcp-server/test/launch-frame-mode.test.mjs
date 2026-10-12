import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';

test('explicit launch mode gates the new bridge, preserves old calls and rejects conflicts before IO',async t=>{
  let version='0.10.2';const requests=[];
  const mock=createServer((req,res)=>{
    const url=new URL(req.url,'http://localhost');
    requests.push({path:url.pathname,method:req.method,params:Object.fromEntries(url.searchParams)});
    res.end(JSON.stringify(url.pathname==='/ping'?{version}:{ok:true}));
  });
  await new Promise(r=>mock.listen(0,'127.0.0.1',r));
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
  const client=new Client({name:'launch-mode-test',version:'1'});
  t.after(async()=>{await client.close();mock.closeAllConnections();mock.close();});
  await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`},stderr:'pipe'}));
  const call=arguments_=>client.callTool({name:'cs2_set_graphics_settings',arguments:arguments_});
  const schema=(await client.listTools()).tools.find(x=>x.name==='cs2_set_graphics_settings').inputSchema;
  assert.deepEqual(schema.properties.launchMode.enum,['background','foreground']);
  assert.ok(!(schema.required??[]).includes('launchMode'));
  for (const launchMode of ['background','foreground']) {
    requests.length=0;assert.equal((await call({launchMode,vSync:false})).isError,true);
    assert.deepEqual(requests,[{path:'/ping',method:'GET',params:{}}]);
  }
  // 原有10.2图形调用仍可用；新模式需要10.3，不将旧桥静默忽略新参数当成功。
  requests.length=0;assert.notEqual((await call({maxFps:60,vSync:false})).isError,true);
  assert.deepEqual(requests.at(-1),{path:'/settings/graphics/set',method:'POST',params:{maxFps:'60',vSync:'false'}});
  version='0.10.3';
  for (const [launchMode,maxFps] of [['background',30],['foreground',90]]) {
    requests.length=0;assert.notEqual((await call({launchMode,maxFps,vSync:false})).isError,true);
    assert.deepEqual(requests.at(-1),{path:'/settings/graphics/set',method:'POST',params:{maxFps:String(maxFps),vSync:'false',launchMode}});
    requests.length=0;assert.notEqual((await call({launchMode,vSync:false})).isError,true);
    assert.deepEqual(requests.at(-1).params,{vSync:'false',launchMode});
  }
  for(const args of [{launchMode:'foreground',maxFps:30},{launchMode:'background',maxFps:90},
    {launchMode:'foreground',vSync:true},{maxFps:60,vSync:true},{launchMode:'auto'},
    {launchMode:null},{maxFps:30.5},{maxFps:241},{vSync:'false'}]) {
    requests.length=0;assert.equal((await call(args)).isError,true);assert.deepEqual(requests,[]);
  }
});
