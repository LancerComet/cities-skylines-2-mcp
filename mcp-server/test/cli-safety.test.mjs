import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {mkdtemp,writeFile,readFile,rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {resolve,dirname} from 'node:path';
import {fileURLToPath} from 'node:url';
import {spawn} from 'node:child_process';

const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
const guid='1234567890abcdef1234567890abcdef';
const state={cityLoaded:true,isLoading:false,cityName:'Example City',loadedSaveGuid:guid,loadPurpose:'LoadGame',simulation:{frameIndex:1,effectiveSpeed:1}};
async function fixture(t,handler) {
 const scratch=await mkdtemp(resolve(tmpdir(),'cs2-cli-contract-'));
 const server=createServer(handler);await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const context=resolve(scratch,'context.json');
 await writeFile(context,JSON.stringify({cityId:'example-city',cityName:'Example City',manualSaveName:'Example Main',
  manualSavePath:'./example.cok',journalDirectory:'./journal',loadedSaveGuid:guid}));
 t.after(async()=>{server.closeAllConnections();server.close();await rm(scratch,{recursive:true,force:true});});
 const run=args=>{
  const child=spawn(process.execPath,[resolve(root,'agent-control.mjs'),'--context',context,...args],
   {cwd:root,env:{...process.env,CS2_BRIDGE_URL:`http://127.0.0.1:${server.address().port}`}});
  let stdout='',stderr='';
  child.stdout.on('data',c=>stdout+=c);child.stderr.on('data',c=>stderr+=c);
  const done=new Promise((resolveRun,reject)=>{child.on('error',reject);child.on('close',code=>resolveRun({code,stdout,stderr}));});
  t.after(()=>{if(child.exitCode===null)child.kill();});
  return {child,done};
 };
 return {scratch,run};
}

test('CLI preflights the entire schema, excluded tools and manual save target before any batch action',async t=>{
 const sent=[];
 const {scratch,run}=await fixture(t,(req,res)=>{sent.push(req.url);res.end(JSON.stringify(req.url==='/state'?state:{ok:true}));});
 const invalidBatches=[
  [{tool:'cs2_city_overview',arguments:{}},{tool:'cs2_set_simulation',arguments:{speed:8}}],
  [{tool:'cs2_city_overview',arguments:{}},{tool:'cs2_production',arguments:{}}],
  [{tool:'cs2_city_overview',arguments:{}},{tool:'cs2_save_game',arguments:{name:'Another Main'}}],
 ];
 for(const [index,input] of invalidBatches.entries()) {
  const file=resolve(scratch,`invalid-${index}.json`);await writeFile(file,JSON.stringify(input));sent.length=0;
  const result=await run(['--batch',file]).done;assert.equal(result.code,1,result.stderr);
  assert.deepEqual(sent,['/state']);
 }
});

test('CLI default watch remains read-only even with severe synthetic issues and has no private alarm threshold',async t=>{
 const writes=[];
 const responses={
  '/state':state,'/city/events':{events:[],burningBuildings:20,burningOtherObjects:0},
  '/city/development':{points:0},'/city/happiness':{factors:[{roundedContribution:-10}]},
  '/city/overview':{cityName:'Example City',population:100,money:0,simulationPaused:false},
  '/city/notifications':{notifications:[],countsByType:{'Water Notification':20,'Crime Scene':20},total:0,returned:0},
  '/city/services':{water:{freshCapacity:0,freshConsumption:100,sewageCapacity:0,sewageConsumption:100}},
  '/city/budget':{balance:-100},'/city/traffic':{cityTrafficFlowPercent:0,cityTrafficVolume:0,bottlenecks:[]},
 };
 const {scratch,run}=await fixture(t,(req,res)=>{
  const path=new URL(req.url,'http://localhost').pathname;
  if(path.includes('/set')||path==='/sim/control'||path==='/game/save')writes.push(path);
  res.end(JSON.stringify(responses[path]??{}));
 });
 const {child,done}=run(['--watch']);
 await new Promise((resolveReady,reject)=>{
  const timeout=setTimeout(()=>reject(new Error('watch did not emit its first summary')),5000);
  let output='';
  child.stdout.on('data',chunk=>{output+=chunk;if(output.includes('"warningCounts"')){clearTimeout(timeout);resolveReady();}});
  child.on('close',()=>{clearTimeout(timeout);reject(new Error('watch exited before first summary'));});
 });
 child.kill();await done;
 const report=JSON.parse(await readFile(resolve(scratch,'journal/经营监控最新.json'),'utf8'));
 assert.deepEqual(writes,[]);assert.equal(report.actions,undefined);assert.equal(report.readOnly,true);assert.equal(report.happinessAttention,undefined);
 const metadata=JSON.parse(await readFile(resolve(scratch,'journal/经营监控进程.json'),'utf8'));
 assert.equal(metadata.intervalSeconds,60);assert.equal(metadata.readOnly,true);
});
