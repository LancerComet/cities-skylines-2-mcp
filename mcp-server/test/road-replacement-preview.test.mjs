import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';
import {isMutation,bridgeJson} from '../dist/bridge-client.js';

test('原位扩路预览拒绝会忽略dryRun的旧桥接及非布尔值，并保留默认实际替换',async t=>{
  let version='0.10.4';const requests=[];
  const bridge=createServer((req,res)=>{
    res.setHeader('content-type','application/json');const u=new URL(req.url,'http://127.0.0.1');
    if(u.pathname==='/ping')res.end(JSON.stringify({version}));
    else if(u.pathname==='/build/road/replace'){
      requests.push(Object.fromEntries(u.searchParams));
      res.end(JSON.stringify({dryRun:u.searchParams.get('dryRun')==='true',applied:u.searchParams.get('dryRun')!=='true'}));
    }else {res.statusCode=404;res.end('{}');}
  });
  await new Promise(r=>bridge.listen(0,'127.0.0.1',r));t.after(()=>new Promise(r=>bridge.close(r)));
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');const client=new Client({name:'road-preview-test',version:'1'});
  t.after(()=>client.close());await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${bridge.address().port}`,DOTENV_CONFIG_QUIET:'true'},stderr:'pipe'}));
  const args={roads:[{index:11,version:2},{index:12,version:3}],prefab:'XL Road Divided'};
  const old=await client.callTool({name:'cs2_replace_road',arguments:{...args,dryRun:true}});
  assert.equal(old.isError,true);assert.equal(JSON.parse(old.content[0].text).mayHaveApplied,false);assert.equal(requests.length,0);
  version='0.10.5';
  for(const dryRun of ['true',1,null])assert.equal((await client.callTool({name:'cs2_replace_road',arguments:{...args,dryRun}})).isError,true);
  assert.equal(requests.length,0);
  const preview=await client.callTool({name:'cs2_replace_road',arguments:{...args,invert:true,force:false,dryRun:true}});
  assert.equal(preview.isError,undefined);assert.equal(JSON.parse(preview.content[0].text).applied,false);
  assert.deepEqual(requests.at(-1),{roads:'11:2;12:3',prefab:'XL Road Divided',invert:'true',dryRun:'true'});
  const applied=await client.callTool({name:'cs2_replace_road',arguments:args});
  assert.equal(JSON.parse(applied.content[0].text).applied,true);assert.equal('dryRun' in requests.at(-1),false);
  const explicit=await client.callTool({name:'cs2_replace_road',arguments:{...args,dryRun:false}});
  assert.equal(JSON.parse(explicit.content[0].text).applied,true);assert.equal(requests.at(-1).dryRun,'false');
});

test('原位扩路预览的传输结果未知不误报实际替换，真正替换仍保持未知状态',async t=>{
  const server=createServer((req,res)=>{res.writeHead(200,{'content-type':'application/json'});res.end('{');});
  await new Promise(r=>server.listen(0,'127.0.0.1',r));t.after(()=>new Promise(r=>server.close(r)));
  const base=`http://127.0.0.1:${server.address().port}`;
  assert.equal(isMutation('/build/road/replace?dryRun=true'),false);
  for(const path of ['/build/road/replace','/build/road/replace?dryRun=false','/build/road/replace?dryRun=True']){
    assert.equal(isMutation(path),true);await assert.rejects(bridgeJson(path,1000,base),e=>e.mayHaveApplied===true);
  }
  await assert.rejects(bridgeJson('/build/road/replace?dryRun=true',1000,base),e=>e.mayHaveApplied===false);
});
