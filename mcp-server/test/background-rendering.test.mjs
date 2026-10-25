import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';

test('生产C#后台渲染策略：配置保留、原生回读、加载及失败所有权',{skip:process.platform!=='win32'},()=>{
  const script=resolve(dirname(fileURLToPath(import.meta.url)),'fixtures/background-rendering-probe.ps1');
  const result=spawnSync(process.env.CS2_TEST_PWSH || 'pwsh',['-NoProfile','-File',script],{encoding:'utf8',timeout:30000,windowsHide:true});
  assert.equal(result.status,0,result.stderr||result.stdout);
  const report=JSON.parse(result.stdout.trim());
  assert.equal(report.passed,9);
  assert.equal(new Set(report.cases).size,9);
  assert.equal(report.nativeRendererAndWattsVerified,false);
});
