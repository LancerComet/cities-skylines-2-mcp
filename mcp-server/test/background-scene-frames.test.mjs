import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');

test('场景帧策略保留原生屏障、一次调用、所有权失败与恢复证据',{skip:process.platform!=='win32'},()=>{
  const r=spawnSync(process.env.CS2_TEST_PWSH || 'pwsh',['-NoProfile','-File',resolve(root,'test/fixtures/background-scene-frames-probe.ps1')],{encoding:'utf8',timeout:30000,windowsHide:true});
  assert.equal(r.status,0,r.stderr||r.stdout); const report=JSON.parse(r.stdout.trim());
  assert.equal(report.passed,8); assert.equal(new Set(report.cases).size,8); assert.equal(report.nativeMemoryBenefitVerified,false);
});

test('后台场景帧公共0.10.6版本门、非法布尔和输出恢复冲突在发送前拒绝',async t=>{
  let version='0.10.5'; const requests=[];
  const mock=createServer((req,res)=>{ requests.push(req.url); res.setHeader('content-type','application/json'); res.end(JSON.stringify(req.url==='/ping'?{version}:{memoryBenefitVerified:false})); });
  await new Promise(r=>mock.listen(0,'127.0.0.1',r)); t.after(()=>{mock.closeAllConnections();mock.close();});
  const client=new Client({name:'scene-frames-contract',version:'1'});
  await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`},stderr:'pipe'})); t.after(()=>client.close());
  const call=args=>client.callTool({name:'cs2_set_graphics_settings',arguments:args});
  for(const renderBackgroundScene of [true,false]) {
    requests.length=0; assert.equal((await call({renderBackgroundScene})).isError,true); assert.deepEqual(requests,['/ping']);
  }
  for(const args of [{renderBackgroundScene:'false'},{renderBackgroundScene:null},{renderBackgroundScene:true,launchMode:'foreground'},{renderBackgroundScene:true,offscreenSceneOutput:false}]) {
    requests.length=0; assert.equal((await call(args)).isError,true); assert.deepEqual(requests,[]);
  }
  version='0.10.6';
  for(const renderBackgroundScene of [true,false]) {
    requests.length=0; assert.notEqual((await call({renderBackgroundScene})).isError,true);
    assert.deepEqual(requests,['/ping',`/settings/graphics/set?renderBackgroundScene=${renderBackgroundScene}`]);
  }
  version='0.10.6'; requests.length=0;
  assert.notEqual((await call({submitBackgroundCommands:false})).isError,true);
  assert.equal(new URL(requests.at(-1),'http://localhost').searchParams.has('renderBackgroundScene'),false);
});
