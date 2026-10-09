import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { bridgeJson, BridgeError, jsonResult, errorResult } from './bridge-client.js';

export const modActions = ['roadBuilder.createTemplate','network.prepare','network.apply','network.cancel','nodeController.crosswalk',
  'betterBulldozer.removeDecoration','betterBulldozer.restoreDecorations','traffic.removeUnsafe','traffic.removeUTurns','traffic.resetConnections','traffic.resetPriorities',
  'traffic.prepareConnection','traffic.previewConnection','traffic.preparePriority','traffic.previewPriority','traffic.apply','traffic.cancel',
  'signals.initialize','signals.addMember','signals.removeMember','signals.addPhase','signals.copyPhase','signals.removePhase','signals.swapPhases','signals.setPhase',
  'signals.setEdgeMask','signals.setLaneMask','signals.setName','signals.setTemplate','signals.removeTemplate'] as const;
const mods = ['Anarchy','RoadBuilder','NetworkTools','NodeController','MoveIt','Traffic','TrafficLightManager.Code','BetterBulldozer','FindIt','ExtraAssetsImporter','ExtraLib','UnifiedIconLibrary','AssetIconLibrary','I18NEverywhere','AchievementFixer','Skyve Mod'] as const;
export const entityIdentity = z.object({index:z.number().int().positive(),version:z.number().int().positive()}).strict();
export const helperSchema = z.object({ElevationStepSlider:z.boolean().optional(),NetworkUpgradesToolOptions:z.boolean().optional(),NetworkUpgradesPrefabs:z.boolean().optional(),
  ShowElevationToolOption:z.boolean().optional(),ResetElevationWhenChangingPrefab:z.boolean().optional(),ResetNetworkToolOptionsWhenChangingPrefab:z.boolean().optional(),
  ReplaceUpgradesBehavior:z.boolean().optional(),PreventAccidentalPropCulling:z.boolean().optional(),PropRefreshFrequency:z.number().int().min(1).max(600).optional(),
  ConstrainBrush:z.boolean().optional(),DisableAnarchyWhileBrushing:z.boolean().optional(),ShowTooltip:z.boolean().optional(),ToolIcon:z.boolean().optional()}).strict();
function payloadPath(route:string,payload:unknown,extra:Record<string,string>={}) {
  const serialized=JSON.stringify(payload);
  if(serialized.length>16000)throw new BridgeError('模组参数超过16000字符，未发送请求。');
  return `${route}?${new URLSearchParams({...extra,payload:serialized})}`;
}
const readOnly={readOnlyHint:true,destructiveHint:false,openWorldHint:false};
const write={readOnlyHint:false,destructiveHint:true,openWorldHint:false};
export function registerModTools(server:McpServer,requireVersion:()=>Promise<void>,requireObjectPreviewVersion:()=>Promise<void> = requireVersion) {
  server.registerTool('cs2_mod_assets',{title:'搜索已注册可见道具',description:'分页搜索有原生放置定义的静态道具，包括已导入资产；读活本地化中文名与正常解锁，不返回独立不可见资产。',annotations:readOnly,
    inputSchema:{query:z.string().optional(),offset:z.number().int().nonnegative().default(0),limit:z.number().int().min(1).max(200).default(50)}},async(args)=>{
      try{await requireVersion();return jsonResult(await bridgeJson(`/mods/assets?${new URLSearchParams(Object.entries(args).map(([k,v])=>[k,String(v)]))}`));}catch(e){return errorResult(e);}});
  server.registerTool('cs2_place_prop',{title:'正常放置可见静态道具',description:'对cs2_mod_assets查出的正常可用道具走原生预览、Validation、收费与应用。dryRun默认true，只有显式false才施工；不提供force或碰撞忽略。',annotations:write,
    inputSchema:{prefab:z.string().min(1),x:z.number().finite().min(-7168).max(7168),z:z.number().finite().min(-7168).max(7168),y:z.number().finite().min(-100).max(3000).optional(),rotation:z.number().finite().default(0),dryRun:z.boolean().default(true)}},async(args)=>{
      try{await requireObjectPreviewVersion();return jsonResult(await bridgeJson(`/build/prop/place?${new URLSearchParams(Object.entries(args).map(([k,v])=>[k,String(v)]))}`,30000));}catch(e){return errorResult(e);}});
  server.registerTool('cs2_mod_catalog',{title:'已加载模组功能与原生接口清单',description:'实时核对16种模组程序集、版本、已有系统及接入动作。组件库通过资产、图标、本地化和状态接入；加载不代表全部写接口可控。需要桥接0.10.0。',annotations:readOnly,inputSchema:{}},async()=>{
    try{await requireVersion();return jsonResult(await bridgeJson('/mods/catalog'));}catch(e){return errorResult(e);}});
  server.registerTool('cs2_mod_inspect',{title:'模组实际状态与配置回读',description:'只读Anarchy独立开关/27项错误策略、RoadBuilder配置、NetworkTools参数/会话、MoveIt队列、NodeController节点端部、Traffic组件或TrafficLightManager相位/模板。可传完整实体index/version。信号配时单位是64模拟帧一次的更新计数。',annotations:readOnly,
    inputSchema:{mod:z.enum(mods),entity:entityIdentity.optional()}},async({mod,entity})=>{
    try{await requireVersion();return jsonResult(await bridgeJson(payloadPath('/mods/inspect',entity?{entity}:{},{mod})));}catch(e){return errorResult(e);}});
  server.registerTool('cs2_mod_operation',{title:'原生模组工具操作状态',description:'读取最近NetworkTools/Traffic工具会话的操作ID、状态、预览或结果；超时/失败后先回读，禁止盲目重试。',annotations:readOnly,inputSchema:{}},async()=>{
    try{await requireVersion();return jsonResult(await bridgeJson('/mods/operation'));}catch(e){return errorResult(e);}});
  server.registerTool('cs2_anarchy_helpers',{title:'设置Anarchy正常辅助偏好',description:'只开放已核实的高程显示、换资产重置、升级展示、防道具剔除与笔刷辅助。偏好不等于施工参数，不启用错误放行、重复独特建筑或扩展高程。',annotations:write,inputSchema:helperSchema.shape},async(args)=>{
    try{await requireVersion();const payload=helperSchema.parse(args);return jsonResult(await bridgeJson(payloadPath('/mods/anarchy/helpers/set',payload)));}catch(e){return errorResult(e);}});
  server.registerTool('cs2_anarchy_refresh_props',{title:'请求Anarchy刷新道具可见性',description:'调用Anarchy原生刷新调度；返回requested，不冒充完成，不更改网络碰撞。',annotations:write,inputSchema:{}},async()=>{
    try{await requireVersion();return jsonResult(await bridgeJson('/mods/anarchy/props/refresh'));}catch(e){return errorResult(e);}});
  server.registerTool('cs2_mod_action',{title:'执行明确模组原生动作',description:'道路独立模板、NetworkTools预览/应用/取消、NodeController过街、BetterBulldozer精确装饰删除/防重生恢复、Traffic原生清理/重置、TrafficLightManager相位/成员/完整放行掩码/模板。payload受桥接逐动作校验，默认dryRun=true（network会话操作除外）。先读docs/native-control.md中的模组契约。异步返回operationId后轮询cs2_mod_operation，不能以请求代替结果。需要桥接0.10.0。',annotations:write,
    inputSchema:{action:z.enum(modActions),payload:z.record(z.unknown())}},async({action,payload})=>{
    try{await requireVersion();return jsonResult(await bridgeJson(payloadPath('/build/mod/action',payload,{action}),30000));}catch(e){return errorResult(e);}});
}
