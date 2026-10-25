import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {dirname,resolve} from 'node:path';

test('生产加载就绪策略等待原生覆盖屏及全部三组任务，不把Game模式当完整就绪',{skip:process.platform!=='win32'},()=>{
 const probe=resolve(dirname(fileURLToPath(import.meta.url)),'fixtures/ui-load-readiness-probe.ps1');
 const result=spawnSync(process.env.CS2_TEST_PWSH || 'pwsh',['-NoProfile','-File',probe],{encoding:'utf8',timeout:30000,windowsHide:true});
 assert.equal(result.status,0,result.stderr||result.stdout);
 assert.deepEqual(JSON.parse(result.stdout.trim()),{passed:9,nativeLoadOrPauseBarrierBypassed:false});
});
