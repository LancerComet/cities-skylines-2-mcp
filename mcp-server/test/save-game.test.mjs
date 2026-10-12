import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';

test('background save keeps native save semantics and rejects unsupported or invalid preview options before writing',async t=>{
  const writes=[];
  let version='0.10.2';
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
  version='0.10.1';
  const oldBridgeResult=await client.callTool({name:'cs2_save_game',arguments:{name:'本城授权主档',includePreview:false}});
  assert.equal(oldBridgeResult.isError,true);
  assert.equal(writes.length,1);
  const normalResult=await client.callTool({name:'cs2_save_game',arguments:{name:'本城授权主档'}});
  assert.equal(normalResult.isError,undefined);
  assert.deepEqual(writes[1],{name:'本城授权主档'});
});

test('queued native save interruption remains unknown and never resubmits the save',async t=>{
  let writes=0;
  const bridge=createServer((req,res)=>{
    res.setHeader('content-type','application/json');
    const url=new URL(req.url,'http://127.0.0.1');
    if(url.pathname==='/ping')res.end(JSON.stringify({ok:true,mod:'CS2MCP',version:'0.10.4',gameMode:'Game',isLoading:false}));
    else if(url.pathname==='/game/save'){
      writes++;
      res.statusCode=503;
      res.end(JSON.stringify({error:'保存协程已中断；核对实际主档，不能盲重试',mayHaveApplied:true,
        safeToRetry:false,failurePhase:'native_save_queued_interrupted'}));
    }else {res.statusCode=404;res.end('{}');}
  });
  await new Promise(r=>bridge.listen(0,'127.0.0.1',r));
  t.after(()=>new Promise(r=>bridge.close(r)));
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
  const client=new Client({name:'queued-save-test',version:'1'});
  t.after(()=>client.close());
  await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,
    env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${bridge.address().port}`,DOTENV_CONFIG_QUIET:'true'},stderr:'pipe'}));
  const result=await client.callTool({name:'cs2_save_game',arguments:{name:'本城授权主档',includePreview:false}});
  assert.equal(result.isError,true);
  const failure=JSON.parse(result.content[0].text);
  assert.equal(failure.status,503);
  assert.equal(failure.mayHaveApplied,true);
  assert.equal(failure.safeToRetry,false);
  assert.equal(writes,1);
});
