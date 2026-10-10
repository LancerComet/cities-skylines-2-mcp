import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';

test('图形设置拒绝旧桥接及非法范围，保留原生布尔值和帧率',async t=>{
  let version='0.10.0'; const requests=[];
  const mock=createServer((req,res)=>{
    res.setHeader('content-type','application/json');
    if(req.url==='/ping')res.end(JSON.stringify({version}));
    else {requests.push({url:req.url,method:req.method});res.end('{"baseFrameRateLimit":30}');}
  });
  await new Promise(r=>mock.listen(0,'127.0.0.1',r));
  t.after(()=>{mock.closeAllConnections();mock.close();});
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
  const client=new Client({name:'graphics-settings-test',version:'1'});
  t.after(()=>client.close());
  await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`},stderr:'pipe'}));
  const args={maxFps:30,vSync:false};
  assert.equal((await client.callTool({name:'cs2_set_graphics_settings',arguments:args})).isError,true);
  assert.equal(requests.length,0);
  version='0.10.1';
  for(const invalid of [{maxFps:20},{maxFps:241},{maxFps:30.5},{vSync:'false'}])
    assert.equal((await client.callTool({name:'cs2_set_graphics_settings',arguments:invalid})).isError,true);
  assert.equal(requests.length,0);
  assert.notEqual((await client.callTool({name:'cs2_set_graphics_settings',arguments:args})).isError,true);
  const url=new URL(requests[0].url,'http://127.0.0.1');
  assert.equal(url.pathname,'/settings/graphics/set');
  assert.equal(url.searchParams.get('maxFps'),'30');
  assert.equal(url.searchParams.get('vSync'),'false');
  assert.equal(requests[0].method,'POST');
});
