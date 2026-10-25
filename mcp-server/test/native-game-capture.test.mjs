import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');

test('显式游戏相机取景按版本拒绝，旧默认保留，非法输入不调用原生',async t=>{
  let version='0.10.4',badPng=false;const requests=[];
  const captureId='0123456789abcdef0123456789abcdef';
  const png=Buffer.from('89504e470d0a1a0a00000000','hex');
  const mock=createServer((req,res)=>{
    requests.push(req.url);
    if(req.url==='/ping'){res.setHeader('content-type','application/json');res.end(JSON.stringify({version}));}
    else if(req.url.includes('source=gameCamera')&&!req.url.includes('captureId=')){
      res.statusCode=202;res.setHeader('content-type','application/json');res.end(JSON.stringify({captureId,status:'queued',safeToRetry:false}));
    }else {res.setHeader('content-type',badPng?'application/json':'image/png');res.end(badPng?'{}':png);}
  });
  await new Promise(r=>mock.listen(0,'127.0.0.1',r));t.after(()=>{mock.closeAllConnections();mock.close();});
  const client=new Client({name:'native-game-capture',version:'1'});
  await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`},stderr:'pipe'}));t.after(()=>client.close());
  const call=arguments_=>client.callTool({name:'cs2_screenshot',arguments:arguments_});
  assert.equal((await call({source:'gameCamera'})).isError,true);assert.deepEqual(requests,['/ping']);
  requests.length=0;let result=await call({width:640});assert.notEqual(result.isError,true);assert.deepEqual(requests,['/screenshot?width=640']);assert.equal(result.content[0].type,'image');
  for(const args of [{source:'desktop'},{source:null},{width:63},{width:3841},{width:128.5},{captureId},{source:'gameCamera',captureId,width:1280},{source:'gameCamera',captureId:'bad'}]){
    requests.length=0;assert.equal((await call(args)).isError,true);assert.deepEqual(requests,[]);
  }
  version='0.10.5';requests.length=0;result=await call({source:'gameCamera',width:1280});assert.notEqual(result.isError,true);assert.deepEqual(requests,['/ping','/screenshot?width=1280&source=gameCamera']);assert.equal(JSON.parse(result.content[0].text).captureId,captureId);
  requests.length=0;result=await call({source:'gameCamera',captureId});assert.notEqual(result.isError,true);assert.deepEqual(requests,['/ping',`/screenshot?source=gameCamera&captureId=${captureId}`]);assert.equal(result.content[0].data,png.toString('base64'));
  badPng=true;assert.equal((await call({source:'gameCamera',captureId})).isError,true);
  const listing=await client.listTools();assert.equal(listing.tools.find(x=>x.name==='cs2_screenshot').annotations.readOnlyHint,true);
});
