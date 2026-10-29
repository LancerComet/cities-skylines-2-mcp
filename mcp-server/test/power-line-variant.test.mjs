import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';

test('power-line native underground selection preserves real transitions and rejects free surface cables',{skip:process.platform!=='win32'},()=>{
  const fixture=fileURLToPath(new URL('./fixtures/power-line-variant-probe.ps1',import.meta.url));
  const result=spawnSync(process.env.CS2_TEST_PWSH || 'pwsh',['-NoProfile','-File',fixture],{encoding:'utf8',timeout:30000,windowsHide:true});
  assert.equal(result.status,0,result.stderr||result.stdout);
  const report=JSON.parse(result.stdout.trim());
  assert.equal(report.passed,14);
  assert.equal(new Set(report.cases).size,report.passed);
  assert.equal(report.productionSourcesLinked,true);
  assert.equal(report.gameStarted,false);
  assert.equal(report.nativeConstructionVerified,false);
});
