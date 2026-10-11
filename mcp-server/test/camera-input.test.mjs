import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {dirname,resolve} from 'node:path';
import {fileURLToPath} from 'node:url';

test('镜头越界与旧桥接的清除视图请求在写入前拒绝',async t=>{
 const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
 let version='0.4.0';const writes=[];
 const mock=createServer((req,res)=>{
  if(req.url==='/ping')res.end(JSON.stringify({version}));
  else if(req.url.startsWith('/camera/set?')){writes.push(req.url);res.end('{"ok":true}');}
  else {res.statusCode=404;res.end('{}');}
 });
 await new Promise(done=>mock.listen(0,'127.0.0.1',done));
 const client=new Client({name:'camera-test',version:'1'});
 const transport=new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
  env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`},stderr:'pipe'});
 t.after(async()=>{await client.close();mock.closeAllConnections();mock.close();});
 await client.connect(transport);
 for(const args of [{angleY:90},{zoom:10001},{x:8000,z:0}]){
  const result=await client.callTool({name:'cs2_set_camera',arguments:args});assert.equal(result.isError,true);
 }
 const old=await client.callTool({name:'cs2_set_camera',arguments:{clearInfoView:true}});
 assert.equal(old.isError,true);assert.equal(writes.length,0);
 version='0.10.2';
 const good=await client.callTool({name:'cs2_set_camera',arguments:{clearInfoView:true,zoom:800}});
 assert.notEqual(good.isError,true);assert.equal(writes.length,1);assert.match(writes[0],/clearInfoView=true/);
});
