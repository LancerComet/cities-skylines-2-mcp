import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';

test('background save keeps native save semantics and rejects unsupported or invalid preview options before writing',async t=>{
  const writes=[];
  let version='0.10.1';
  const bridge=createServer((req,res)=>{
    res.setHeader('content-type','application/json');
    const url=new URL(req.url,'http://127.0.0.1');
    if(url.pathname==='/ping')res.end(JSON.stringify({ok:true,mod:'CS2MCP',version,gameMode:'Game',isLoading:false}));
    else if(url.pathname==='/game/save'){
      writes.push(Object.fromEntries(url.searchParams));
      res.end(JSON.stringify({pipelineCompleted:true,previewIncluded:url.searchParams.get('includePreview')!=='false'}));
    }else {res.statusCode=404;res.end('{}');}
  });
  await new Promise(r=>bridge.listen(0,'127.0.0.1',r));
  t.after(()=>new Promise(r=>bridge.close(r)));
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
  const client=new Client({name:'save-test',version:'1'});
  t.after(()=>client.close());
  await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${bridge.address().port}`,DOTENV_CONFIG_QUIET:'true'},stderr:'pipe'}));
  const result=await client.callTool({name:'cs2_save_game',arguments:{name:'本城授权主档',includePreview:false}});
  assert.equal(result.isError,undefined);
  assert.deepEqual(writes,[{name:'本城授权主档',includePreview:'false'}]);
  assert.equal(JSON.parse(result.content[0].text).previewIncluded,false);
  for(const invalid of ['false',0,null]){
    const invalidResult=await client.callTool({name:'cs2_save_game',arguments:{name:'本城授权主档',includePreview:invalid}});
    assert.equal(invalidResult.isError,true);
  }
  assert.equal(writes.length,1);
  version='0.10.0';
  const oldBridgeResult=await client.callTool({name:'cs2_save_game',arguments:{name:'本城授权主档',includePreview:false}});
  assert.equal(oldBridgeResult.isError,true);
  assert.equal(writes.length,1);
  const normalResult=await client.callTool({name:'cs2_save_game',arguments:{name:'本城授权主档'}});
  assert.equal(normalResult.isError,undefined);
  assert.deepEqual(writes[1],{name:'本城授权主档'});
});
