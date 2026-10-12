// 本城上下文是批次/监控记录归属的唯一入口，不推断默认城市。
import {readFile} from 'node:fs/promises';
import {resolve,dirname} from 'node:path';

export function parseCityContext(data,sourcePath) {
 for(const key of ['cityId','cityName','manualSavePath','journalDirectory']) {
  if(typeof data?.[key]!=='string'||!data[key].trim())throw new Error(`城市上下文缺少${key}。`);
 }
 if(typeof data.loadedSaveGuid!=='string'||! /^[0-9a-f]{32}$/i.test(data.loadedSaveGuid)||/^0{32}$/.test(data.loadedSaveGuid))throw new Error('城市上下文必须提供本次原生实际加载的 loadedSaveGuid；保存后的文件新 GUID 不能替代未重载会话的加载 GUID。');
 if(data.watchPolicy !== undefined)throw new Error("公开监控仅只读，不接受经营干预配置 watchPolicy。");
 if(data.manualSaveName !== undefined && (typeof data.manualSaveName !== 'string' || !data.manualSaveName.trim()))throw new Error('manualSaveName 必须是明确的原生手动主档名称。');
 const contextPath=resolve(sourcePath);
 return {...data,loadedSaveGuid:data.loadedSaveGuid.toLowerCase(),contextPath,journalDirectory:resolve(dirname(contextPath),data.journalDirectory),manualSavePath:resolve(dirname(contextPath),data.manualSavePath)};
}

/** 名称和路径不是同一事实；保存须显式绑定原生名称，不能从文件名猜主档。 */
export function assertManualSaveTarget(context,args) {
 if(!context.manualSaveName||args?.name!==context.manualSaveName) {
  const error=new Error('保存仅允许上下文明确的 manualSaveName；不能保存到默认名或另一个手动主档。');
  error.code='CITY_CONTEXT_MISMATCH';throw error;
 }
}
export async function loadCityContext(path) {
 if(!path||path.startsWith('--'))throw new Error('必须提供 --context <本城控制上下文.json>；无默认城市或运行记录目录。');
 return parseCityContext(JSON.parse((await readFile(resolve(path),'utf8')).replace(/^\uFEFF/,'')),path);
}
export function assertLoadedCity(context,state) {
 if(state?.cityLoaded!==true||state.isLoading!==false||state.cityName!==context.cityName
    ||state.loadPurpose!=='LoadGame'||typeof state.loadedSaveGuid!=='string'
    ||state.loadedSaveGuid.toLowerCase()!==context.loadedSaveGuid) {
  const error=new Error('当前加载GUID或城市不符、身份未知或加载中；停止本城调用和健康采样。');
  error.code='CITY_CONTEXT_MISMATCH';throw error;
 }
}
export function assertRecordOwner(context,owner) {
 const normalized=path=>process.platform==='win32'?resolve(path).toLowerCase():resolve(path);
 if(owner.cityId!==context.cityId||normalized(owner.manualSavePath)!==normalized(context.manualSavePath)) {
  const error=new Error('记录目录属于另一个存档，禁止覆盖。');error.code='CITY_CONTEXT_MISMATCH';throw error;
 }
}
