import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';

test('生产内存策略回归真实误拒、按操作分流、独立计数及未应用语义',{skip:process.platform!=='win32'},()=>{
  const script=resolve(dirname(fileURLToPath(import.meta.url)),'fixtures/memory-pressure-probe.ps1');
  const result=spawnSync(process.env.CS2_TEST_PWSH || 'pwsh',['-NoProfile','-File',script],{encoding:'utf8',timeout:30000,windowsHide:true});
  assert.equal(result.status,0,result.stderr||result.stdout);
  const report=JSON.parse(result.stdout.trim());
  assert.ok(report.passed>=20);
  assert.equal(new Set(report.cases).size,report.passed);
  assert.equal(report.productionSourcesLinked,true);
  assert.equal(report.gameStarted,false);
  assert.equal(report.systemSettingsChanged,false);
});
