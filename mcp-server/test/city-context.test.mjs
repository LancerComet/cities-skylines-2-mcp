import test from 'node:test';
import assert from 'node:assert/strict';
import {resolve} from 'node:path';
import {parseCityContext,loadCityContext,assertLoadedCity,assertRecordOwner,assertManualSaveTarget} from '../src/city-context.mjs';

const data={cityId:'city-a',cityName:'同名城市',manualSavePath:'./saves/a.cok',journalDirectory:'./a-records',loadedSaveGuid:'1234567890abcdef1234567890abcdef'};
const source=resolve('contexts/a.json');
test('缺少显式城市上下文时不会回落到旧城',async()=>{
 await assert.rejects(loadCityContext(undefined),/无默认城市/);
 assert.throws(()=>parseCityContext({...data,journalDirectory:undefined},source),/journalDirectory/);
});
test('不同存档可以同名，记录路径和主档仍独立',()=>{
 const a=parseCityContext(data,source);
 const b=parseCityContext({...data,cityId:'city-b',manualSavePath:'./saves/b.cok',journalDirectory:'./b-records'},source);
 assert.notEqual(a.manualSavePath,b.manualSavePath);assert.notEqual(a.journalDirectory,b.journalDirectory);
 assert.throws(()=>assertRecordOwner(b,a),{code:'CITY_CONTEXT_MISMATCH'});
});
test('同代号指向另一个主档也不能覆盖既有记录',()=>{
 const a=parseCityContext(data,source);
 assert.throws(()=>assertRecordOwner(a,{...a,manualSavePath:resolve('another.cok')}),{code:'CITY_CONTEXT_MISMATCH'});
 assert.doesNotThrow(()=>assertRecordOwner(a,a));
});
test('切换城市、加载中或身份未知时停止执行',()=>{
 const a=parseCityContext(data,source);
 for(const state of [{cityLoaded:true,isLoading:false,cityName:'其他城'},{cityLoaded:true,isLoading:true,cityName:a.cityName},{cityLoaded:false,isLoading:false,cityName:a.cityName},{}])assert.throws(()=>assertLoadedCity(a,state),{code:'CITY_CONTEXT_MISMATCH'});
 assert.doesNotThrow(()=>assertLoadedCity(a,{cityLoaded:true,isLoading:false,cityName:a.cityName,loadedSaveGuid:a.loadedSaveGuid,loadPurpose:'LoadGame'}));
});
test('同名不同GUID与无原生GUID都被拒绝',()=>{
 const a=parseCityContext(data,source);
 for(const guid of ['abcdef1234567890abcdef1234567890',undefined,'0'.repeat(32)])assert.throws(()=>assertLoadedCity(a,{cityLoaded:true,isLoading:false,cityName:a.cityName,loadedSaveGuid:guid,loadPurpose:'LoadGame'}),{code:'CITY_CONTEXT_MISMATCH'});
 for(const loadedSaveGuid of [undefined,'bad','0'.repeat(32)])assert.throws(()=>parseCityContext({...data,loadedSaveGuid},source),/loadedSaveGuid/);
});
test('保存不改变已加载GUID，重载后须重新接管',()=>{
 const a=parseCityContext(data,source);
 const state={cityLoaded:true,isLoading:false,cityName:a.cityName,loadedSaveGuid:a.loadedSaveGuid.toUpperCase(),loadPurpose:'LoadGame'};
 assert.doesNotThrow(()=>assertLoadedCity(a,state));
 assert.throws(()=>assertLoadedCity(a,{...state,loadPurpose:'NewGame'}),{code:'CITY_CONTEXT_MISMATCH'});
});
test('公开监控拒绝经营策略配置，不能默默沿用旧干预偏好',()=>{
 assert.throws(()=>parseCityContext({...data,watchPolicy:{restoreBudgetPercent:100}},source),/仅只读/);
});
test('原生手动保存名须显式绑定，不能从路径推断或静默用默认名',()=>{
 const a=parseCityContext(data,source);
 assert.throws(()=>assertManualSaveTarget(a,{name:'a'}),{code:'CITY_CONTEXT_MISMATCH'});
 const b=parseCityContext({...data,manualSaveName:'Example Main'},source);
 assert.doesNotThrow(()=>assertManualSaveTarget(b,{name:'Example Main'}));
 for(const name of [undefined,'MCP Save','Another Main'])
  assert.throws(()=>assertManualSaveTarget(b,{name}),{code:'CITY_CONTEXT_MISMATCH'});
});
