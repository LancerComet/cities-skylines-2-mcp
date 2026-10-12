#!/usr/bin/env node
/** 通过同一 MCP 服务操作游戏，不控制桌面；图片直接落盘以免打印 base64。 */
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { readFile, mkdir, writeFile, appendFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import Ajv from 'ajv';
import {loadCityContext,assertLoadedCity,assertRecordOwner,assertManualSaveTarget} from './src/city-context.mjs';
import {runVerifiedBatch, validateBatchSteps} from './src/batch-runner.mjs';

const root = dirname(fileURLToPath(import.meta.url));
const cliArgs = process.argv.slice(2);
const contextIndex = cliArgs.indexOf('--context');
const contextFile = contextIndex >= 0 ? cliArgs[contextIndex + 1] : undefined;
if (contextIndex >= 0) cliArgs.splice(contextIndex, 2);
const [tool, argsFile, outputDirArg] = cliArgs;
if (!tool || tool === '--help') {
  console.error('用法：node agent-control.mjs --help | --list | --context <城市上下文.json> (--health | --watch | --batch <批次JSON文件> | <工具名> [参数JSON文件] [图片目录])');
  process.exit(tool === '--help' ? 0 : 2);
}
let cityContext;
try {
 if (tool !== '--list') {
  cityContext = await loadCityContext(contextFile);
 }
} catch (error) { console.error(error.message); process.exit(2); }
const client = new Client({ name: 'cs2-city-control', version: '0.10.3' });
const transport = new StdioClientTransport({
  command: process.execPath,
  args: [resolve(root, 'dist/index.js')],
  cwd: root,
  env: { ...process.env, CS2_BRIDGE_URL: process.env.CS2_BRIDGE_URL || 'http://127.0.0.1:8642', DOTENV_CONFIG_QUIET: 'true' },
  stderr: 'pipe',
});

function assertCity(state) {
 assertLoadedCity(cityContext,state);
}
async function checkCity() {
 const result = await client.callTool({name: 'cs2_game_state', arguments: {}}, undefined, {timeout: 15000});
 if (result.isError) throw new Error('无法核验当前城市身份。');
 assertCity(JSON.parse(result.content.find(block => block.type === 'text').text));
}

async function execute(toolName, args, outputDir, journal) {
  if (args.force === true) throw new Error('正常游戏施工不接受 force=true。');
  if (toolName === 'cs2_save_game') assertManualSaveTarget(cityContext,args);
  let result;
  let toolResponseReceived = false;
  for (let attempt = 0; ; attempt++) {
    toolResponseReceived = false;
    // 只有已明确尚未入队的busy会重试；重试前重新核本次加载身份。
    if (attempt > 0) await checkCity();
    try {
      result = await client.callTool({ name: toolName, arguments: args }, undefined, { timeout: 60000 });
      toolResponseReceived = true;
    } catch (error) {
      // 传输断开不能丢掉已应用前缀，也不能推定本次施工没有发生。
      result = {isError:true,content:[{type:'text',text:JSON.stringify({error:error.message,mayHaveApplied:true,safeToRetry:false,transportFailure:true})}]};
    }
    // 此错误明确表示尚未入队；只重试这个短暂忙状态，不重放超时或未知失败的施工。
    const busy = result.isError && result.content.some(block => {
      if(block.type !== 'text') return false;
      try { const error=JSON.parse(block.text); return error.safeToRetry === true && error.mayHaveApplied === false; }
      catch { return block.text === 'another build operation is in progress, retry shortly'; }
    });
    if (!busy || attempt >= 20) break;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const cleaned = { ...result, content: [] };
  try {
    for (let index = 0; index < result.content.length; index++) {
      const block = result.content[index];
      if (block.type === 'image') {
        await mkdir(outputDir, { recursive: true });
        const ext = block.mimeType === 'image/jpeg' ? 'jpg' : 'png';
        const path = resolve(outputDir, `${stamp}-${toolName}-${index}.${ext}`);
        await writeFile(path, Buffer.from(block.data, 'base64'));
        cleaned.content.push({ type: 'saved_image', mimeType: block.mimeType, path });
      } else cleaned.content.push(block);
    }
    await appendFile(resolve(journal, '操作记录.jsonl'), JSON.stringify({
      at: new Date().toISOString(), cityId: cityContext.cityId, cityName: cityContext.cityName, loadedSaveGuid:cityContext.loadedSaveGuid, tool: toolName, arguments: args, result: cleaned,
    }) + '\n');
  } catch (error) {
    // 游戏调用已经结束，本地落盘失败不能丢掉回执或被当成可重发工具的失败。
    const toolResult = { ...result, content: result.content.map((block, index) =>
      block.type === 'image' ? {type: 'image_metadata', index, mimeType: block.mimeType,
        byteLength: Buffer.byteLength(block.data, 'base64')} : block) };
    return {isError:true,content:[{type:'text',text:JSON.stringify({
      error: `本地结果或操作日志未写入：${error.message}；先核原生回执与现场，不能重发工具。`,
      failurePhase:'local_result_persistence',toolResponseReceived,
      mayHaveApplied:true,safeToRetry:false,toolResult,
      savedImages:cleaned.content.filter(block=>block.type==='saved_image'),
    })}]};
  }
  return cleaned;
}

async function collectHealth(outputDir, journal, quiet = false) {
  await checkCity();
  const path = resolve(journal, '当前城市健康.json');
  let previous;
  try { previous = JSON.parse(await readFile(path, 'utf8')); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  const requests = [
    ['state', 'cs2_game_state', {}], ['events', 'cs2_events', {}],
    ['development', 'cs2_development', {}], ['facilities', 'cs2_facilities', {}], ['indicators', 'cs2_city_indicators', {}], ['happiness', 'cs2_happiness', {}], ['policies', 'cs2_policies', {}], ['overview', 'cs2_city_overview', {}], ['notifications', 'cs2_notifications', { limit: 500 }],
    ['services', 'cs2_city_services', {}], ['labor', 'cs2_labor', {}],
    ['budget', 'cs2_budget', {}], ['demand', 'cs2_demand', {}],
    ['traffic', 'cs2_traffic', { limit: 30 }], ['vehicles', 'cs2_vehicles', { limit: 30 }],
  ];
  const entries = await Promise.all(requests.map(async ([key, name, args]) => {
    const result = await execute(name, args, outputDir, journal);
    if (result.isError) throw new Error(`${name}: ${JSON.stringify(result.content)}`);
    return [key, JSON.parse(result.content.find(block => block.type === 'text').text)];
  }));
  const snapshot = { capturedAt: new Date().toISOString(), cityId: cityContext.cityId, ...Object.fromEntries(entries) };
  assertCity(snapshot.state);
  if (snapshot.overview.cityName !== cityContext.cityName) throw new Error('采集期间城市变化；不覆盖本城健康。');
  await checkCity(); // 同名切城也不能把混合采集标为完整健康。
  if (previous && previous.cityId === cityContext.cityId && previous.overview?.cityName === cityContext.cityName
      && previous.state?.loadedSaveGuid?.toLowerCase() === cityContext.loadedSaveGuid) {
    const actualSeconds = (Date.parse(snapshot.capturedAt) - Date.parse(previous.capturedAt)) / 1000;
    const gameMinutes = (Date.parse(snapshot.overview.gameDateTime.replace(' ', 'T') + ':00Z') -
      Date.parse(previous.overview.gameDateTime.replace(' ', 'T') + ':00Z')) / 60000;
    const framesAdvanced = previous.state?.simulation?.frameIndex === undefined ? null :
      snapshot.state.simulation.frameIndex - previous.state.simulation.frameIndex;
    snapshot.simulationObservation = { actualSeconds, gameMinutes, framesAdvanced,
      nativeGameHoursAdvanced: framesAdvanced !== null && framesAdvanced >= 0 ? framesAdvanced / (262144 / 24) : null,
      effectiveSpeed: snapshot.state.simulation.effectiveSpeed,
      gameHoursPerRealMinute: actualSeconds > 0 ? gameMinutes / actualSeconds : null,
      note: '此区间含施工暂停，记录实际日期推进；不能把设置倍速当成实际模拟吞吐量。' };
  }
  snapshot.issueGroups = {};
  for (const warning of snapshot.notifications.notifications) {
    (snapshot.issueGroups[warning.type] ??= []).push(warning);
  }
  await writeFile(path, JSON.stringify(snapshot, null, 2));
  if (!quiet) console.log(JSON.stringify({ path, overview: snapshot.overview,
    warningCounts: snapshot.notifications.countsByType,
    warningDetailsComplete: snapshot.notifications.total === snapshot.notifications.returned,
    disasters: { active: snapshot.events.events.filter(event => event.active),
      burningBuildings: snapshot.events.burningBuildings, burningOtherObjects: snapshot.events.burningOtherObjects },
    developmentPoints: snapshot.development.points,
    services: snapshot.services, labor: snapshot.labor,
    budgetBalance: snapshot.budget.balance, simulationObservation: snapshot.simulationObservation,
    traffic: { flow: snapshot.traffic.cityTrafficFlowPercent,
      volume: snapshot.traffic.cityTrafficVolume, bottlenecks: snapshot.traffic.bottlenecks },
  }, null, 2));
  return snapshot;
}

try {
  await client.connect(transport);
  if (tool === '--list') {
    const result = await client.listTools();
    console.log(JSON.stringify({ count: result.tools.length, tools: result.tools }, null, 2));
  } else {
    await checkCity();
    const journal = cityContext.journalDirectory;
    const outputDir = resolve(outputDirArg || resolve(journal, '游戏截图'));
    await mkdir(journal, { recursive: true });
    const ownerPath = resolve(journal, '城市控制身份.json');
    try {
      await writeFile(ownerPath, JSON.stringify({cityId: cityContext.cityId, cityName: cityContext.cityName, manualSavePath: cityContext.manualSavePath}, null, 2), {flag: 'wx'});
    } catch (error) {
      if (error.code !== 'EEXIST') throw error;
      assertRecordOwner(cityContext,JSON.parse(await readFile(ownerPath, 'utf8')));
    }
    const input = argsFile ? JSON.parse(await readFile(resolve(argsFile), 'utf8')) : {};
    if (tool === '--watch') {
      const intervalSeconds = 60;
      await writeFile(resolve(journal, '经营监控进程.json'), JSON.stringify({ pid: process.pid, startedAt: new Date().toISOString(), intervalSeconds, readOnly:true }));
      let previousSummary = '';
      let failedReads = 0;
      for (;;) {
        try {
          const snapshot = await collectHealth(outputDir, journal, true);
          const summary = { at:snapshot.capturedAt, cityId:cityContext.cityId, loadedSaveGuid:cityContext.loadedSaveGuid,
            population:snapshot.overview.population, money:snapshot.overview.money, balance:snapshot.budget.balance,
            warningCounts:snapshot.notifications.countsByType, burningBuildings:snapshot.events.burningBuildings,
            activeEvents:snapshot.events.events.filter(event=>event.active).map(event=>event.name),
            happinessFactors:snapshot.happiness.factors ?? [], readOnly:true };
          await writeFile(resolve(journal, '经营监控最新.json'), JSON.stringify(summary, null, 2));
          const changedKey = JSON.stringify([summary.warningCounts,summary.burningBuildings,summary.activeEvents,summary.happinessFactors]);
          if (changedKey !== previousSummary) console.log(JSON.stringify(summary));
          previousSummary = changedKey;
          failedReads = 0;
          await writeFile(resolve(journal, '经营监控连接.json'), JSON.stringify({at:snapshot.capturedAt, connected:true, pid:process.pid}));
        } catch (error) {
          if (error.code === 'CITY_CONTEXT_MISMATCH' || /城市变化/.test(error.message)) throw error;
          failedReads++;
          const connection={at:new Date().toISOString(), connected:false, pid:process.pid,
            consecutiveFailures:failedReads, retrySeconds:Math.min(300,failedReads*intervalSeconds), error:error.message};
          await writeFile(resolve(journal, '经营监控连接.json'), JSON.stringify(connection));
          if (failedReads === 1) console.error(JSON.stringify(connection));
        }
        await new Promise(resolve=>setTimeout(resolve,failedReads ? Math.min(300,failedReads*intervalSeconds)*1000 : intervalSeconds*1000));
      }
    } else if (tool === '--health') {
      await collectHealth(outputDir, journal);
    } else if (tool === '--batch') {
      validateBatchSteps(input);
      if (!Array.isArray(input) || input.length === 0 || input.some(step =>
        typeof step.tool !== 'string' || !step.tool.startsWith('cs2_') ||
        step.arguments?.force === true)) {
        throw new Error('批次必须是非空的 {tool, arguments} 数组，不能绕过解锁。');
      }
      // 先核对整批参数，避免前面的暂停/施工已执行，后面才发现拼错工具或参数。
      const catalog = await client.listTools();
      const validator = new Ajv({ strict: false, allErrors: true });
      for (const step of input) {
        if (step.tool === 'cs2_save_game') assertManualSaveTarget(cityContext,step.arguments);
        const definition = catalog.tools.find(item => item.name === step.tool);
        if (!definition) throw new Error(`批次工具不存在：${step.tool}`);
        const validate = validator.compile(definition.inputSchema);
        if (!validate(step.arguments || {})) throw new Error(`批次参数不合法 ${step.tool}：${validator.errorsText(validate.errors)}`);
      }
      const resultPath = resolve(argsFile).replace(/\.json$/i, '') + '.results.json';
      // 历史结果不可覆盖并重放。空数组仅占位；异常退出须连同本城操作日志核验。
      try { await writeFile(resultPath, '[]', {flag: 'wx'}); }
      catch (error) {
        if (error.code === 'EEXIST') throw new Error('此批次已有结果，禁止覆盖或重放；读取现场后创建新批次。');
        throw error;
      }
      const {results, ...summary} = await runVerifiedBatch(input, {
        checkCity, execute: (name, args) => execute(name, args, outputDir, journal),
      });
      await writeFile(resultPath, JSON.stringify(results, null, 2));
      if (summary.failedAt !== undefined) process.exitCode = 1;
      console.log(JSON.stringify({...summary, journal, resultPath,
        note: '结果包含已执行前缀。身份核验失败的当前步骤没有发送；不得重放整批，先读取现场。'}, null, 2));
    } else {
      const result = await execute(tool, input, outputDir, journal);
      console.log(JSON.stringify(result, null, 2));
      if (result.isError) process.exitCode = 1;
    }
  }
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
} finally {
  await client.close();
}
