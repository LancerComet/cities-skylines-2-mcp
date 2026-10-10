import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';

test('建筑预览拒绝旧桥接和错误布尔参数，防止预览请求变成实际施工',async t=>{
  const writes=[]; let version='0.10.0';
  const bridge=createServer((req,res)=>{
    res.setHeader('content-type','application/json'); const u=new URL(req.url,'http://127.0.0.1');
    if(u.pathname==='/ping')res.end(JSON.stringify({ok:true,mod:'CS2MCP',version,gameMode:'Game',isLoading:false}));
    else if(u.pathname==='/build/place'){writes.push(Object.fromEntries(u.searchParams));res.end(JSON.stringify({dryRun:u.searchParams.get('dryRun')==='true',applied:u.searchParams.get('dryRun')!=='true'}));}
    else {res.statusCode=404;res.end('{}');}
  });
  await new Promise(r=>bridge.listen(0,'127.0.0.1',r)); t.after(()=>new Promise(r=>bridge.close(r)));
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..'); const client=new Client({name:'preview-test',version:'1'});
  t.after(()=>client.close()); await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${bridge.address().port}`,DOTENV_CONFIG_QUIET:'true'},stderr:'pipe'}));
  const args={prefab:'PostOffice01',x:100,z:200,dryRun:true};
  assert.equal((await client.callTool({name:'cs2_place_building',arguments:args})).isError,true); assert.equal(writes.length,0);
  version='0.10.1';
  for(const invalid of ['true',1,null])assert.equal((await client.callTool({name:'cs2_place_building',arguments:{...args,dryRun:invalid}})).isError,true);
  assert.equal(writes.length,0);
  const r=await client.callTool({name:'cs2_place_building',arguments:args}); assert.equal(r.isError,undefined);
  assert.equal(JSON.parse(r.content[0].text).applied,false); assert.deepEqual(writes,[{prefab:'PostOffice01',x:'100',z:'200',dryRun:'true'}]);
});
