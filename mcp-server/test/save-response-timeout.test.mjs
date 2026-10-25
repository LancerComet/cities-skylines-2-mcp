import test from 'node:test';
import assert from 'node:assert/strict';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {fileURLToPath} from 'node:url';

test('真实HTTP桥接等待11秒保存完成；普通请求仍按10秒未知结果返回且不重发', {
  skip: process.platform !== 'win32', timeout: 40_000,
}, async () => {
  const script=fileURLToPath(new URL('./fixtures/http-save-timeout-probe.ps1',import.meta.url));
  const {stdout}=await promisify(execFile)(process.env.CS2_TEST_PWSH || 'pwsh',
    ['-NoProfile','-File',script],{timeout:35_000,encoding:'utf8',windowsHide:true});
  const result=JSON.parse(stdout.trim().split(/\r?\n/).at(-1));
  assert.equal(result.saveStatus,200);
  assert.equal(result.ordinaryStatus,503);
  assert.equal(result.saveCalls,1);
  assert.equal(result.ordinaryCalls,1);
  assert.equal(result.lateCompletionPreserved,true);
  assert.ok(result.elapsedMs>=10_500);
  assert.equal(result.realHttpServerSourceUsed,true);
  assert.equal(result.gameOrSaveFilesTouched,false);
});
