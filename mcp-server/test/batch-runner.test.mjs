import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {mkdtemp, writeFile, readFile, rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {resolve, dirname} from 'node:path';
import {fileURLToPath} from 'node:url';
import {spawn} from 'node:child_process';
import {runVerifiedBatch} from '../src/batch-runner.mjs';

const steps = [1, 2, 3].map(index => ({tool: `cs2_step_${index}`, arguments: {index}}));
const success = {content: [{type: 'text', text: '{"applied":true}'}]};
test('整批预检拒绝参数拼写或非法结构，任何步骤均未发送', async () => {
  let checks = 0; let sent = 0;
  for (const invalid of [
    [...steps, {tool:'cs2_set_simulation', args:{paused:true}}],
    [...steps, {tool:'cs2_set_simulation', arguments:null}],
    [...steps, {tool:'cs2_set_simulation', arguments:[]}],
    [...steps, {tool:'cs2_set_simulation', arguments:false}],
    [...steps, {tool:2}],
    {steps},
  ]) await assert.rejects(runVerifiedBatch(invalid, {
    checkCity: async () => {checks++;}, execute: async () => {sent++; return success;},
  }), TypeError);
  assert.equal(checks, 0); assert.equal(sent, 0);
});
test('中途身份核验失败保留成功前缀，当前及后续施工没有发送', async () => {
  let checks = 0; const sent = [];
  const result = await runVerifiedBatch(steps, {
    checkCity: async () => {if (++checks === 2) throw Object.assign(new Error('城市已切换'), {code: 'CITY_CONTEXT_MISMATCH'});},
    execute: async name => {sent.push(name); return success;},
  });
  assert.deepEqual(sent, ['cs2_step_1']); assert.equal(result.completed, 1); assert.equal(result.failedAt, 1);
  assert.equal(result.results[0].result, success); assert.equal(result.results.length, 2);
  assert.deepEqual(JSON.parse(result.error.content[0].text), {error: '城市已切换', code: 'CITY_CONTEXT_MISMATCH',
    failurePhase: 'identity_check', requestSent: false, mayHaveApplied: false, safeToRetry: false});
  assert.equal(result.partialChangesRemain, true);
});
test('施工或写日志异常不重试，保留前缀并声明当前步骤结果未知', async () => {
  let sent = 0;
  const result = await runVerifiedBatch(steps, {checkCity: async () => {}, execute: async () => {
    if (++sent === 2) throw new Error('连接断开'); return success;
  }});
  assert.equal(sent, 2); assert.equal(result.completed, 1); assert.equal(result.results[0].result, success);
  assert.equal(JSON.parse(result.error.content[0].text).mayHaveApplied, true);
  assert.equal(JSON.parse(result.error.content[0].text).safeToRetry, false);
});
test('旧版纯文本错误仍生成失败结果，不因解析错误丢掉前缀', async () => {
  const result = await runVerifiedBatch(steps, {checkCity: async () => {}, execute: async () =>
    ({isError: true, content: [{type: 'text', text: 'native validation failed'}]})});
  assert.equal(result.completed, 0); assert.equal(result.failedAt, 0); assert.equal(result.results.length, 1);
  assert.equal(result.partialChangesRemain, true);
});
test('CLI断线后结果文件保留前缀，再调用同一批次不会重放', async t => {
  const scratch = await mkdtemp(resolve(tmpdir(), 'cs2-mcp-batch-')); t.after(() => rm(scratch, {recursive: true, force: true}));
  let checks = 0; let calls = 0; let healthy = false;
  const mock = createServer((req, res) => {
    if (req.url === '/state') {
      if (!healthy && ++checks === 3) {req.socket.destroy(); return;}
      res.end(JSON.stringify({cityLoaded: true, isLoading: false, cityName: '批次测试城', loadedSaveGuid:'1234567890abcdef1234567890abcdef', loadPurpose:'LoadGame'}));
    } else if (req.url === '/city/overview') {calls++; res.end('{"cityName":"批次测试城"}');}
    else {res.statusCode = 404; res.end('{"error":"unexpected path"}');}
  });
  await new Promise(resolve => mock.listen(0, '127.0.0.1', resolve));
  t.after(() => {mock.closeAllConnections(); mock.close();});
  const context = resolve(scratch, 'context.json'); const batch = resolve(scratch, 'batch.json');
  await writeFile(context, JSON.stringify({cityId: 'test-city', cityName: '批次测试城', manualSavePath: './test.cok', journalDirectory: './journal',loadedSaveGuid:'1234567890abcdef1234567890abcdef'}));
  await writeFile(batch, JSON.stringify(Array(3).fill({tool: 'cs2_city_overview', arguments: {}})));
  const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
  const run = () => new Promise((done, reject) => {
    const child = spawn(process.execPath, [resolve(root, 'agent-control.mjs'), '--context', context, '--batch', batch], {
      cwd: root, env: {...process.env, CS2_BRIDGE_URL: `http://127.0.0.1:${mock.address().port}`},
    });
    let stdout = '', stderr = ''; child.stdout.on('data', chunk => stdout += chunk); child.stderr.on('data', chunk => stderr += chunk);
    child.on('error', reject); child.on('close', code => done({code, stdout, stderr}));
  });
  const first = await run(); assert.equal(first.code, 1, first.stderr); assert.equal(calls, 1, first.stdout);
  const report = JSON.parse(await readFile(resolve(scratch, 'batch.results.json'), 'utf8'));
  assert.equal(report.length, 2); assert.notEqual(report[0].result.isError, true);
  assert.equal(JSON.parse(report[1].result.content[0].text).requestSent, false);
  healthy = true;
  const second = await run(); assert.equal(second.code, 1); assert.match(second.stderr, /已有结果/);
  assert.equal(calls, 1); assert.deepEqual(JSON.parse(await readFile(resolve(scratch, 'batch.results.json'), 'utf8')), report);
});
