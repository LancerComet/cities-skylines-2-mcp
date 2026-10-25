import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {createServer} from 'node:http';
import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');

test('生产C#场景委托归属：已有绘制、部分失败、撤销失败、未知与加载释放',{skip:process.platform!=='win32'},()=>{
  const r=spawnSync(process.env.CS2_TEST_PWSH || 'pwsh',['-NoProfile','-File',resolve(root,'test/fixtures/background-scene-rendering-probe.ps1')],{encoding:'utf8',timeout:30000,windowsHide:true});
  assert.equal(r.status,0,r.stderr||r.stdout);const report=JSON.parse(r.stdout.trim());
  assert.equal(report.passed,16);assert.equal(new Set(report.cases).size,16);assert.equal(report.nativeRendererMemoryAndWattsVerified,false);
});

test('场景绘制开关版本门、布尔类型与前台冲突，旧调用保持兼容',async t=>{
  let version='0.10.4';const requests=[];
  const mock=createServer((req,res)=>{requests.push(req.url);res.setHeader('content-type','application/json');res.end(JSON.stringify(req.url==='/ping'?{version}:{nativeHookInstalled:null}));});
  await new Promise(r=>mock.listen(0,'127.0.0.1',r));t.after(()=>{mock.closeAllConnections();mock.close();});
  const client=new Client({name:'scene-contract',version:'1'});
  await client.connect(new StdioClientTransport({command:process.execPath,args:[resolve(root,'dist/index.js')],cwd:root,env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${mock.address().port}`},stderr:'pipe'}));t.after(()=>client.close());
  const call=args=>client.callTool({name:'cs2_set_graphics_settings',arguments:args});
  for(const suspendSceneRendering of [true,false]) {
    requests.length=0;assert.equal((await call({suspendSceneRendering})).isError,true);assert.deepEqual(requests,['/ping']);
  }
  for(const args of [{suspendSceneRendering:'false'},{suspendSceneRendering:null},{suspendSceneRendering:true,launchMode:'foreground'}]) {
    requests.length=0;assert.equal((await call(args)).isError,true);assert.deepEqual(requests,[]);
  }
  version='0.10.5';
  for(const suspendSceneRendering of [true,false]) {
    requests.length=0;const r=await call({suspendSceneRendering});assert.notEqual(r.isError,true);
    assert.deepEqual(requests,['/ping',`/settings/graphics/set?suspendSceneRendering=${suspendSceneRendering}`]);
    assert.equal(JSON.parse(r.content[0].text).nativeHookInstalled,null);
  }
  version='0.10.3';requests.length=0;
  assert.notEqual((await call({launchMode:'background',vSync:false})).isError,true);
  assert.equal(new URL(requests.at(-1),'http://localhost').searchParams.has('suspendSceneRendering'),false);
});
