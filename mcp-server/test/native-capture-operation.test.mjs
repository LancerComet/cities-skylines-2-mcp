import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
test('生产取景结果槽与真实HTTP领取：单次、迟到结果、失效、无ECS排队',{skip:process.platform!=='win32'},()=>{
 const script=fileURLToPath(new URL('./fixtures/native-capture-operation-probe.ps1',import.meta.url));
 const r=spawnSync(process.env.CS2_TEST_PWSH || 'pwsh',['-NoProfile','-File',script],{encoding:'utf8',timeout:30000,windowsHide:true});
 assert.equal(r.status,0,r.stderr||r.stdout);const v=JSON.parse(r.stdout.trim());
 assert.equal(v.passed,8);assert.equal(v.realHttpSourceUsed,true);assert.equal(v.resultQueriesEnqueuedToGame,0);assert.equal(v.nativeRenderOrCityUsed,false);
});
