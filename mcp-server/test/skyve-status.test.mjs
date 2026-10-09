import test from 'node:test';
import assert from 'node:assert/strict';
import {parseModdingLog,parseGameVersion,summarizeCompatibility,warningLines,redact,readSkyveStatus} from '../dist/skyve-status.js';
import {mkdtemp,mkdir,writeFile,rm} from 'node:fs/promises';
import {join} from 'node:path';
import {tmpdir} from 'node:os';

test('latest complete native playset excludes removed mods and dependency DLLs',()=>{
  const section=`======= Active Playset =======\n City (42)\n======= Enabled Mods =======\n - Road Builder [BETA] v0.7.0 (87190)\n - Skyve v1.3.1 (75804)\n[date] [INFO]  Loaded CS2MCP, Version=0.4.0.0\n[date] [INFO]  Loaded 0Harmony, Version=2.2.2.0\n[date] [INFO]  Mods initialized in 1ms`;
  const value=parseModdingLog('======= Active Playset =======\n Old (1)\n'+section);
  assert.equal(value.valid,true);assert.equal(value.playset.name,'City');assert.equal(value.mods.length,2);
  assert.equal(value.mods[0].name,'Road Builder [BETA]');assert.deepEqual(value.loadedAssemblies,['CS2MCP','0Harmony']);
  assert.equal(parseModdingLog(section.replace('Mods initialized in','Pending initialization')).valid,false);
  assert.equal(parseModdingLog('no active playset').valid,false);
});
test('optional removed assets are not incorrectly classified as required dependencies',()=>{
  const cache={Packages:[{Id:'80529',Stability:1,ReviewedGameVersion:'1.5.2f1',Interactions:[
    {Type:8,Packages:[{Id:'80528',Name:'ExtraDetailingTools'}]},
    {Type:7,Packages:[{Id:'75724',Name:'ExtraLib'}]},
    {Type:7,Packages:[{Id:'dlc',Name:'DLC',IsDlc:true}]},
  ],AuthorId:'private',ThumbnailUrl:'unused',Note:'review only'}]};
  const result=summarizeCompatibility(cache,[{id:'80529',name:'ExtraAssetsImporter',version:'1.7.6'}],'1.6.2f1');
  assert.equal(result[0].interactions[0].packages[0].missingRequired,false);
  assert.equal(result[0].interactions[1].packages[0].missingRequired,true);
  assert.equal(result[0].interactions[2].packages[0].enabled,null);
  assert.equal(result[0].reviewedForObservedVersion,false);
  assert.equal(result[0].stability,'Stable');assert.equal('AuthorId' in result[0],false);
});
test('unknown cache shape and absent reviews are not silently marked healthy',()=>{
  assert.throws(()=>summarizeCompatibility({Packages:{}},[],null));
  assert.equal(summarizeCompatibility({Packages:[]},[{id:'1'}],null)[0].reviewed,false);
});
test('CS2 version is not confused with Unity engine version',()=>{
  assert.equal(parseGameVersion('Initialize engine version: 2022.3.71f1\nGame version: 1.6.2f1 (build)'), '1.6.2f1');
  assert.equal(parseGameVersion('Initialize engine version: 2022.3.71f1'),null);
  assert.equal(warningLines('Not serializable type: Anarchy.Components.TransformRecord',5).length,1);
  assert.deepEqual(warningLines('NullReferenceException: Object reference not set\n at unrelated stack',5),['NullReferenceException: Object reference not set']);
});

test('large logs preserve version from bounded head and separate native startup errors from tail',async t=>{
  const root=await mkdtemp(join(tmpdir(),'cs2-skyve-read-test-'));
  t.after(()=>rm(root,{recursive:true,force:true}));
  await mkdir(join(root,'Logs'));
  await writeFile(join(root,'Logs','Modding.log'),'No completed mod initialization\n');
  await writeFile(join(root,'Player.log'),'Game version: 1.6.2f1 (build)\n'+'.\n'.repeat(600000)+'NullReferenceException: update failed\n');
  await writeFile(join(root,'Logs','SceneFlow.log'),'[SceneFlow] [ERROR] A platform service integration failed to initialize\n[SceneFlow] [FATAL] Asset database failed\n');
  const result=await readSkyveStatus(true,3,root);
  assert.equal(result.observedGameVersion,'1.6.2f1');
  assert.equal(result.observed.valid,false);
  assert.equal(result.sources['Player.log version'].readMode,'head');
  assert.equal(result.sources['Player.log version'].truncated,true);
  assert.equal(result.sources['Player.log warnings'].readMode,'tail');
  assert.equal(result.sources['Player.log warnings'].truncated,true);
  assert.equal(result.logs['SceneFlow.log'].length,2);
  assert.deepEqual(result.logs['Player.log'],['NullReferenceException: update failed']);
  const silent=await readSkyveStatus(false,3,root);
  assert.equal(silent.observedGameVersion,'1.6.2f1');
  assert.deepEqual(silent.logs,{}); assert.equal(silent.sources['SceneFlow.log'],undefined);
});
test('log diagnostics bound results and never expose account/authentication lines',()=>{
  const log='[INFO] unrelated\n[WARN] token=private\n[ERROR] authorization: Bearer private\n[WARN] UserIdentifier: 12345678901234567\n[WARN] missing /Maps/12345678901234567\n[ERROR] contact a@example.com';
  assert.deepEqual(warningLines(log,10),['[WARN] missing /Maps/[account-redacted]','[ERROR] contact [email-redacted]']);
  assert.equal(warningLines(log,1).length,1);
  assert.ok(!redact('token="secret value" password=xyz Bearer private').includes('secret value'));
});
test('missing local sources are separately reported, no subscription/auth writes occur',async()=>{
  const result=await readSkyveStatus(false,20,new URL('./nonexistent/',import.meta.url).pathname);
  assert.equal(result.readOnly,true);assert.equal(result.observed,null);assert.equal(result.compatibility,null);
  assert.ok(result.errors.length>=3);assert.equal(result.skyveInstalled,false);
});
