#!/usr/bin/env node
/**
 * cs2-mcp - MCP server for Cities: Skylines II.
 *
 * Translates MCP tool calls into HTTP requests against the CS2MCP bridge mod
 * running inside the game (default http://127.0.0.1:8642, override with the
 * CS2_BRIDGE_URL environment variable).
 */
import { BRIDGE_URL, BridgeError, bridgeFetch, bridgeJson, jsonResult, errorResult } from "./bridge-client.js";
import { readSkyveStatus } from "./skyve-status.js";
import { registerModTools } from "./mod-tools.js";
import { registerInterchangeTools, registerRoadLayoutTool, registerRoadLayoutRemovalTool } from "./interchange-tools.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

async function requirePrecisionBridge(label = "精细施工") {
  const ping = await bridgeJson("/ping",3000) as {version?:string};
  const match = ping.version?.match(/^(\d+)\.(\d+)\.(\d+)/);
  const parts = match?.slice(1).map(Number);
  if (!parts || parts[0]*1000100+parts[1]*1000+parts[2] < 10002)
    throw new BridgeError(`${label}需要桥接0.10.2或更高；当前${ping.version ?? "未知"}，不能让旧桥接静默忽略新参数。`);
}

const roadPathSegment = z.object({
  x1:z.number(),z1:z.number(),x2:z.number(),z2:z.number(),cx:z.number().optional(),cz:z.number().optional(),
  c1x:z.number().optional(),c1z:z.number().optional(),c2x:z.number().optional(),c2z:z.number().optional(),
  c1e:z.number().finite().optional(),c2e:z.number().finite().optional(),
  e1:z.number().finite().default(0),e2:z.number().finite().default(0),
  snap:z.number().min(0).max(50).default(8),heightTolerance:z.number().min(0).max(30).default(3),
});

function validateRoadPath(segments:z.infer<typeof roadPathSegment>[]) {
  for(const [index,s] of segments.entries()) {
    const quadratic=[s.cx,s.cz],cubic=[s.c1x,s.c1z,s.c2x,s.c2z];
    const hasQuadratic=quadratic.some(v=>v!==undefined),hasCubic=cubic.some(v=>v!==undefined);
    if((hasQuadratic&&quadratic.some(v=>v===undefined)) || (hasCubic&&cubic.some(v=>v===undefined)) || (hasQuadratic&&hasCubic)
      ||(!hasCubic&&(s.c1e!==undefined||s.c2e!==undefined))) throw new Error(`第${index+1}段曲线控制参数不完整或冲突；整条路径尚未施工。`);
    if(Math.hypot(s.x2-s.x1,s.z2-s.z1)<4 || Math.hypot(s.x2-s.x1,s.z2-s.z1)>1500) throw new Error(`第${index+1}段端点距离须4..1500米；整条路径尚未施工。`);
  }
}

const server = new McpServer({ name: "cs2-mcp", version: "0.10.2" });

server.registerTool("cs2_facility_parking_fees", {
  title: "读取全部公共设施停车费",
  description: "读取原生允许付费停车的停车设施与公共服务建筑：当前启用、实际政策值、原生滑块上下限。手工道路停车使用整体市辖区收费。需要桥接0.10.2。",
  annotations: { readOnlyHint:true, destructiveHint:false, openWorldHint:false }, inputSchema: {},
}, async()=>{try {await requirePrecisionBridge("公共设施停车收费");return jsonResult(await bridgeJson("/city/facility-parking"));}catch(e){return errorResult(e);}});

server.registerTool("cs2_set_facility_parking_fee", {
  title: "设置公共设施原生停车费",
  description: "通过建筑面板 PaidParking 原生政策设置停车费，严格遵守当前原生上限与解锁，不修改泊位或政策预制体。入队后再次读取确认。需要桥接0.10.2。",
  annotations: { readOnlyHint:false, destructiveHint:false, openWorldHint:false },
  inputSchema: {index:z.number().int().nonnegative(),version:z.number().int().positive(),fee:z.number().finite().nonnegative()},
}, async args=>{try {await requirePrecisionBridge("公共设施停车收费");return jsonResult(await bridgeJson(`/building/parking-fee/set?${new URLSearchParams(Object.entries(args).map(([k,v])=>[k,String(v)]))}`));}catch(e){return errorResult(e);}});
registerModTools(server,()=>requirePrecisionBridge("模组原生接入"),()=>requirePrecisionBridge("原生对象预览"),()=>requirePrecisionBridge("已初始化端部的过街控制"));
registerInterchangeTools(server,()=>requirePrecisionBridge("原生立交资产"));
registerRoadLayoutTool(server,()=>requirePrecisionBridge("原生道路布局世界高程范围校验"));
registerRoadLayoutRemovalTool(server,()=>requirePrecisionBridge("原生批量拆路"));

server.registerTool("cs2_facility_catalog", {
  title: "完整原生公共设施与扩建盘点",
  description: "一次按原生服务和菜单归属读取非成长设施及标志性建筑（isSignature单列）：说明、费用、尺寸、锁定、原始容量/效果、正常升级及现场安装数量/位置。固定扩建随父设施列出；空升级数组表示当前原生菜单无手动升级。目录资格不代替正常放置与运营验收。需要桥接0.10.2。",
  annotations: {readOnlyHint:true, destructiveHint:false, openWorldHint:false}, inputSchema: {},
}, async()=>{try{await requirePrecisionBridge("公共设施完整盘点");return jsonResult(await bridgeJson('/prefabs/facilities'));}catch(e){return errorResult(e);}});

server.registerTool("cs2_signature_buildings", {
  title: "全部五类原生标志性建筑清单",
  description: "按原生SignatureBuildingData读取住宅、混合用途、商业、工业、办公标志性建筑，含所属分区、正常解锁/内容可用性、实际已建位置、占地和原生全城效果。名称相似的升级与普通成长建筑不混入。需要桥接0.10.2。",
  annotations: {readOnlyHint:true,destructiveHint:false,openWorldHint:false}, inputSchema: {},
}, async()=>{try{await requirePrecisionBridge("标志性建筑清单");return jsonResult(await bridgeJson('/prefabs/signatures'));}catch(e){return errorResult(e);}});

server.registerTool("cs2_move_building", {
  title: "正常搬迁已有建筑",
  description: "原生ObjectTool的Relocate预览、碰撞/资金校验、应用流程；只接受独立手动建筑/标志性建筑，不移动普通分区成长楼、专门产业占位母体或单独子物体。现有专门产业优先原位扩围，成套迁移未接入。普通建筑的扩建与内部结构仍须在应用后独立复核入口、水电、经营和升级；不支持force。dryRun仅位置计算。需要桥接0.10.2。",
  annotations:{readOnlyHint:false,destructiveHint:true,openWorldHint:false},
  inputSchema:{index:z.number().int(),version:z.number().int(),x:z.number().finite(),z:z.number().finite(),rotation:z.number().finite().optional(),dryRun:z.boolean().optional()},
}, async args=>{try{await requirePrecisionBridge("原生建筑搬迁");return jsonResult(await bridgeJson(`/build/building/move?${new URLSearchParams(Object.entries(args).map(([k,v])=>[k,String(v)]))}`,60000));}catch(e){return errorResult(e);}});

server.registerTool("cs2_natural_resources", {
  title: "原生资源图限定片区取样",
  description: "只读石油、矿石、肥沃土地、鱼类的原生格基础量/已使用量/差值，注明格号、尺度、时间和帧。不是吨、百分比或产出预测，不含城市加成/多边形积分，森林使用独立树木数据。粗网格可能漏小矿带；不写资源，不调用编辑器。需要桥接 0.10.2。",
  annotations: { readOnlyHint: true, destructiveHint: false, openWorldHint: false },
  inputSchema: { x:z.number().finite().min(-7168).max(7168), z:z.number().finite().min(-7168).max(7168), radius:z.number().finite().min(50).max(7168), resolution:z.number().int().min(4).max(64).default(16) },
}, async args=>{try {await requirePrecisionBridge("原生资源取样");return jsonResult(await bridgeJson(`/map/natural-resources?${new URLSearchParams(Object.entries(args).map(([k,v])=>[k,String(v)]))}`));}catch(e){return errorResult(e);}});

server.registerTool("cs2_extractor_areas", {
  title: "读取建筑自有采掘区域",
  description: "有界读取指定建筑的原生自有区域或指定区域：边界、入口、所属、原生编辑限制、资源与采掘计数。原始计数不是吨或月产量，区域存在不代表企业投产。需要桥接 0.10.2。",
  annotations: { readOnlyHint: true, destructiveHint: false, openWorldHint: false },
  inputSchema: { index: z.number().int(), version: z.number().int() },
}, async args => { try { await requirePrecisionBridge("采掘区域读取"); return jsonResult(await bridgeJson(`/city/extractor-areas?${new URLSearchParams(Object.entries(args).map(([k,v])=>[k,String(v)]))}`)); } catch(e) { return errorResult(e); } });

server.registerTool("cs2_edit_extractor_area", {
  title: "编辑已有原生采掘区域",
  description: "只编辑正常建筑自有且原生允许编辑的采掘 Lot，走原生预览/校验/应用。nodes 不重复闭合点，前两个入口节点须保持原坐标和顺序；保留所属与缓存，遵守原生半径、已购土地和碰撞。dryRun 只检查资格与多边形，不是碰撞/费用/资源/投产预览。不创建独立或隐形资产，不支持 force。需要桥接 0.10.2。",
  annotations: { readOnlyHint: false, destructiveHint: true, openWorldHint: false },
  inputSchema: { index: z.number().int(), version: z.number().int(), nodes: z.array(z.object({ x: z.number().finite(), z: z.number().finite() })).min(3).max(64), dryRun: z.boolean().optional() },
}, async ({index,version,nodes,dryRun}) => { try { await requirePrecisionBridge("采掘区域编辑"); const params=new URLSearchParams({index:String(index),version:String(version),nodes:JSON.stringify(nodes)}); if(dryRun!==undefined)params.set("dryRun",String(dryRun)); return jsonResult(await bridgeJson(`/build/extractor-area/edit?${params}`,30000)); } catch(e) { return errorResult(e); } });

server.registerTool("cs2_skyve_status", {
  title: "Skyve 模组、兼容性与日志诊断",
  description: "只读 Skyve 本地缓存和限定日志：原生启用配组、加载模块、兼容性审查版本、必需与可选依赖、脱敏警告。每个来源独立注明时间与读取失败，缓存不是实时兼容性保证。不登录、不修改订阅/配组、不调用会弹出窗口的 Skyve 命令管道。",
  annotations: { readOnlyHint: true, destructiveHint: false, openWorldHint: false },
  inputSchema: { includeLogs: z.boolean().default(true), logLimit: z.number().int().min(1).max(100).default(30) },
}, async ({includeLogs,logLimit}) => {
  try {
    const local = await readSkyveStatus(includeLogs, logLimit);
    let runtime: unknown;
    try {
      const snapshot = await bridgeJson("/tools/capabilities", 4000) as {capturedAtUtc?:string;bridgeVersion?:string;mods?:unknown};
      runtime = {available:true,capturedAtUtc:snapshot.capturedAtUtc,bridgeVersion:snapshot.bridgeVersion,mods:snapshot.mods};
    }
    catch (error) { runtime = { available: false, error: error instanceof Error ? error.message : String(error) }; }
    return jsonResult({ ...local, runtime });
  } catch(error) { return errorResult(error); }
});

server.registerTool(
  "cs2_ping",
  {
    title: "Ping the game bridge",
    description:
      "Check that Cities: Skylines II is running with the CS2MCP bridge mod loaded. " +
      "Returns mod version, current game mode (MainMenu / Game / Editor) and whether a save is loading. " +
      "Works even while a save is still loading; use this first to diagnose connection issues.",
    inputSchema: {},
  },
  async () => {
    try {
      return jsonResult(await bridgeJson("/ping", 3_000));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool("cs2_development", {
  title: "Read development tree", description: "Native development points, nodes, service and parent prerequisites, and actual purchase eligibility.", inputSchema: {},
}, async () => { try { return jsonResult(await bridgeJson("/city/development")); } catch (err) { return errorResult(err); } });
server.registerTool("cs2_purchase_development", {
  title: "Purchase development node", description: "Spend actual available development points via native DevTreeSystem.Purchase, respecting native prerequisites; never force unlocks.",
  inputSchema: { name: z.string().describe("Exact node name from cs2_development") },
}, async ({ name }) => { try { return jsonResult(await bridgeJson(`/city/development/purchase?${new URLSearchParams({ name })}`)); } catch (err) { return errorResult(err); } });
server.registerTool("cs2_events", {
  title: "Read city disasters and events", description: "Native event journal and all current burning objects, including forest fires that building notifications omit.", inputSchema: {},
}, async () => { try { return jsonResult(await bridgeJson("/city/events")); } catch (err) { return errorResult(err); } });

server.registerTool("cs2_facilities", {
  title: "设施容量与建筑等级", description: "学校学生/容量、医疗床位/病人、垃圾存储/处理、实际效率、按分区统计建筑等级。", inputSchema: {},
}, async () => { try { return jsonResult(await bridgeJson("/city/facilities")); } catch (err) { return errorResult(err); } });
server.registerTool("cs2_building_details", {
  title: "建筑经营与服务详情", description: "实际等级、效率、员工、车辆、辖区、扩建及企业经营；含原生建筑水电需求/满足量、发电/储能及流图连接。拆管线前同时核建筑端口，零瞬时流量不等于冗余；单位保持原生。",
  inputSchema: { index: z.number().int(), version: z.number().int() },
}, async (args) => { try { return jsonResult(await bridgeJson(`/city/building/details?${new URLSearchParams(Object.entries(args).map(([k,v])=>[k,String(v)]))}`)); } catch (err) { return errorResult(err); } });
server.registerTool("cs2_upgrade_building", {
  title: "原生服务建筑扩建", description: "使用 building_details 中正常解锁的扩建项目，经原生工具校验、收费及安装。固定扩建自动按建筑定位，独立扩建需 x/z；成功后复核安装与实际容量。",
  inputSchema: { index:z.number().int(), version:z.number().int(), prefab:z.string(), x:z.number().optional(), z:z.number().optional(), rotation:z.number().optional(), dryRun:z.boolean().optional() },
}, async (args) => { try { return jsonResult(await bridgeJson(`/build/building/upgrade?${new URLSearchParams(Object.entries(args).map(([k,v])=>[k,String(v)]))}`,60000)); } catch (err) { return errorResult(err); } });
server.registerTool("cs2_rebuild_service_building", {
  title: "原生损毁服务设施重建", description: "只对原生灾害救援清障已完成的损毁主体或服务附属原位重建。保留正常预览、碰撞与收费；dryRun默认true，实际应用后回读损毁、附属和端口。",
  inputSchema: { index:z.number().int().min(1), version:z.number().int().min(1), dryRun:z.boolean().default(true) },
}, async args => { try { await requirePrecisionBridge("正常灾害设施重建"); return jsonResult(await bridgeJson(`/build/building/rebuild?${new URLSearchParams(Object.entries(args).map(([k,v])=>[k,String(v)]))}`,60000)); } catch (err) { return errorResult(err); } });
server.registerTool("cs2_happiness", {
  title: "全城幸福度成因", description: "与原生幸福度悬浮面板同源的全部正负贡献。", inputSchema: {},
}, async () => { try { return jsonResult(await bridgeJson("/city/happiness")); } catch (err) { return errorResult(err); } });
server.registerTool("cs2_city_indicators", {
  title: "全城进阶面板指标", description: "原生邮政、教育层级、客货运输、旅游、人口迁移及治安统计；结合设施容量和幸福因素判断，不能把零值一概当故障。", inputSchema: {},
}, async () => { try { return jsonResult(await bridgeJson("/city/indicators")); } catch (err) { return errorResult(err); } });
server.registerTool("cs2_utilities", {
  title: "原生公用事业与外部端口",
  description: "与原生信息面板同源的电力、净水、污水供需与真实进出口；读取每个地图外公用事业端口及其所属节点的实际容量和流量。污水外送是付费处置，端口数量不等于全网有效吞吐。需要桥接0.10.2。",
  annotations: { readOnlyHint: true, destructiveHint: false, openWorldHint: false }, inputSchema: {},
}, async () => { try { await requirePrecisionBridge("公用事业外部连接"); return jsonResult(await bridgeJson("/city/utilities")); } catch (err) { return errorResult(err); } });
server.registerTool("cs2_outside_connections", {
  title: "全部原生对外交通和公用端口",
  description: "读取地图外原生连接的客货运输类型、名称、实际位置、所属节点及接入网络，包含海陆空和水电。端口存在不等于线路已运营。需要桥接0.10.2。",
  annotations: { readOnlyHint: true, destructiveHint: false, openWorldHint: false }, inputSchema: {},
}, async () => { try { await requirePrecisionBridge("全部对外连接"); return jsonResult(await bridgeJson("/city/outside-connections")); } catch (err) { return errorResult(err); } });
server.registerTool("cs2_set_name", {
  title: "原生中文命名", description: "重命名建筑、市辖区、公交等实体；路段自动定位整条街道的原生 Aggregate。",
  inputSchema: { index: z.number().int(), version: z.number().int(), name: z.string().min(1).max(64) },
}, async (args) => { try { return jsonResult(await bridgeJson(`/entity/name/set?${new URLSearchParams(Object.entries(args).map(([k,v])=>[k,String(v)]))}`,60000)); } catch (err) { return errorResult(err); } });
server.registerTool("cs2_set_service_districts", {
  title: "分配服务辖区", description: "原生 ServiceDistrict 范围限制；空列表恢复运行范围内全城服务。先核实其它片区仍有服务，不能随意限制唯一设施。",
  inputSchema: { index: z.number().int(), version: z.number().int(), districts: z.array(z.object({index:z.number().int(),version:z.number().int()})).max(50) },
}, async ({index,version,districts}) => { const params=new URLSearchParams({index:String(index),version:String(version),districts:districts.map(d=>`${d.index}:${d.version}`).join(';')}); try { return jsonResult(await bridgeJson(`/building/districts/set?${params}`)); } catch (err) { return errorResult(err); } });
server.registerTool("cs2_zone_polygon", {
  title: "按街坊边界完整分区", description: "覆盖多边形内实际可分区格，解决圆刷漏角；默认仅填未分区格，保留原有用途。原生解锁与 ToolUpdate 刷新。",
  inputSchema: { zone:z.string(), nodes:z.string(), onlyUnzoned:z.boolean().default(true) },
}, async ({zone,nodes,onlyUnzoned}) => { try { return jsonResult(await bridgeJson(`/build/zone?${new URLSearchParams({zone,nodes,onlyUnzoned:String(onlyUnzoned)})}`)); } catch (err) { return errorResult(err); } });
server.registerTool("cs2_zone_tool", {
  title: "原生填充、框选与细刷分区",
  description: "直接使用游戏 GenerateZones/ApplyZones。fill 从真实格填满原生连通区域，marquee 按种子格方向框选，paint 沿线细刷；默认保留已有用途。previewOnly 返回实际选中格，maxCells 防止意外扩散。混合用途或公共预留使用框选/多边形。应用后复核 changedCells/verified，不把没长楼当漏格。",
  inputSchema: { zone:z.string(), mode:z.enum(["fill","marquee","paint"]), x:z.number(), z:z.number(), x2:z.number().optional(), z2:z.number().optional(),
    overwrite:z.boolean().default(false), previewOnly:z.boolean().default(false), maxCells:z.number().int().min(1).max(20000).default(4096) },
}, async (args) => { const params=new URLSearchParams(); for(const [k,v] of Object.entries(args)) if(v!==undefined) params.set(k,String(v)); try { return jsonResult(await bridgeJson(`/build/zone/tool?${params}`,60000)); } catch(err) { return errorResult(err); } });
server.registerTool("cs2_terrain_brush", {
  title: "原生地形塑形与坡度衔接",
  description: "raise/lower 抬升或降低，level 按世界高度整平，soften 平滑，slope 从起点高度衔接终点目标高度。原生高度笔刷、纹理、强度、旋转和有限次笔触；检查整个笔刷范围已购地。dryRun 仅核对场址参数，不是假想地形预览。应用后地形/水体异步更新，必须用 terrain/environment_field 复核，岸线及排污不得只凭外观。",
  inputSchema: { mode:z.enum(["raise","lower","level","soften","slope"]), x:z.number(), z:z.number(), x2:z.number().optional(), z2:z.number().optional(),
    size:z.number().min(8).max(1000).default(64), strength:z.number().positive().max(1).default(.3), angle:z.number().optional(),
    targetHeight:z.number().min(-1024).max(4096).optional(), brush:z.string().optional(), passes:z.number().int().min(1).max(20).default(1), dryRun:z.boolean().default(false) },
}, async (args) => { const params=new URLSearchParams(); for(const [k,v] of Object.entries(args)) if(v!==undefined) params.set(k,String(v)); try { return jsonResult(await bridgeJson(`/build/terrain?${params}`,60000)); } catch(err) { return errorResult(err); } });
server.registerTool("cs2_tool_capabilities", {
  title: "原生精细工具能力与接口覆盖",
  description: "原生工具模式、吸附、笔刷和桥接缺口，以及已加载模组程序集、Anarchy真实状态/校验屏蔽、已接入与未接入的模组能力。只读审计，加载不等于功能全可控，UI偏好不是施工参数。",
  inputSchema: {},
}, async () => { try { return jsonResult(await bridgeJson("/tools/capabilities")); } catch(err) { return errorResult(err); } });
server.registerTool("cs2_zone_cells", {
  title: "复核真实分区格与漏涂", description: "读取多边形内可见有效分区格、各用途数量、未涂色坐标，排除原生遮挡格；有计划的公共预留不得误当漏涂。",
  inputSchema: { nodes:z.string() },
}, async ({nodes}) => { try { return jsonResult(await bridgeJson(`/zones/cells?${new URLSearchParams({nodes})}`)); } catch (err) { return errorResult(err); } });
server.registerTool("cs2_environment_field", {
  title: "片区风向、水流与污染", description: "批量读取实际空气污染搬运风场、水流向、水深及污染；向量为流往方向，干地水流为null。刚加载未模拟时明确提示初始化风险，不假定单位为米/秒。",
  inputSchema: { x:z.number().min(-7168).max(7168).optional(), z:z.number().min(-7168).max(7168).optional(), radius:z.number().min(50).max(7168).optional(), resolution:z.number().int().min(4).max(64).optional() },
}, async (args) => { const params=new URLSearchParams(); for (const [key,value] of Object.entries(args)) if (value!==undefined) params.set(key,String(value)); try { return jsonResult(await bridgeJson(`/map/environment?${params}`)); } catch(err) { return errorResult(err); } });
server.registerTool("cs2_content_status", {
  title: "读取成就资格与数据包授权", description: "只读原生成就启用标志、正常游戏选项、内容可用性和实际不可用引用；不修改成就或DLC。", inputSchema: {},
}, async () => { try { return jsonResult(await bridgeJson("/game/content-status")); } catch (err) { return errorResult(err); } });
server.registerTool("cs2_save_settings", {
  title: "读取原生自动存档设置", description: "读取真实秒数、原生轮换数量；原生自动存档与手动主档独立。", inputSchema: {},
}, async () => { try { return jsonResult(await bridgeJson("/settings/saves")); } catch (err) { return errorResult(err); } });
server.registerTool("cs2_set_save_settings", {
  title: "设置独立原生自动存档", description: "使用游戏原生菜单定时与轮换设置；当前1/2/5/10/30/60分钟，20分钟不是原生菜单选项，不定时覆盖手动主档。原生轮换总数为全本机自动档范围。",
  inputSchema: { enabled:z.boolean().optional(), intervalSeconds:z.union([z.literal(60),z.literal(120),z.literal(300),z.literal(600),z.literal(1800),z.literal(3600)]).optional(), retentionCount:z.union([z.literal(1),z.literal(2),z.literal(3),z.literal(10)]).optional() },
}, async (args) => { const params = new URLSearchParams(); for (const [key,value] of Object.entries(args)) if (value !== undefined) params.set(key,String(value)); try { return jsonResult(await bridgeJson(`/settings/saves/set?${params}`)); } catch(err) { return errorResult(err); } });
server.registerTool("cs2_graphics_settings", {
  title: "游戏帧率设置", description: "读取原生垂直同步、基础渲染帧率上限；显卡驱动插帧后的帧率不在游戏内可读。", inputSchema: {},
}, async () => { try { return jsonResult(await bridgeJson("/settings/graphics")); } catch (err) { return errorResult(err); } });
server.registerTool("cs2_resource_diagnostics", {
  title: "读取原生后台资源计数", description: "只读进程提交/物理内存、Unity原生与Mono分配、临时实体、焦点和两次读取间基础帧率；不启用Profiler、不强制GC或卸载资源。", inputSchema: {}, annotations: {readOnlyHint:true},
}, async () => { try { return jsonResult(await bridgeJson("/diagnostics/resources")); } catch (err) { return errorResult(err); } });
server.registerTool("cs2_set_graphics_settings", {
  title: "设置游戏基础帧率与垂直同步", description: "使用原生设置关闭/开启垂直同步，并保存本地基础帧率上限；不改变画质与驱动插帧。",
  inputSchema: { maxFps: z.number().int().min(10).max(240).optional(), vSync: z.boolean().optional() },
}, async ({ maxFps, vSync }) => {
  const params = new URLSearchParams();
  if (maxFps !== undefined) params.set("maxFps", String(maxFps));
  if (vSync !== undefined) params.set("vSync", String(vSync));
  try {
    await requirePrecisionBridge("图形参数校验与后台限帧恢复");
    return jsonResult(await bridgeJson(`/settings/graphics/set?${params}`));
  } catch (err) { return errorResult(err); }
});

server.registerTool(
  "cs2_game_state",
  {
    title: "Get game state",
    description:
      "Get the current game state: game mode, whether a city is loaded, city name, " +
      "simulation pause/speed and the in-game date/time.",
    inputSchema: {},
  },
  async () => {
    try {
      return jsonResult(await bridgeJson("/state"));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_city_overview",
  {
    title: "Get city overview",
    description:
      "Key statistics of the loaded city: population (plus citizens currently moving in), " +
      "average happiness and health, city treasury money, XP, in-game date and simulation speed. " +
      "Requires a loaded city.",
    inputSchema: {},
  },
  async () => {
    try {
      return jsonResult(await bridgeJson("/city/overview"));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_demand",
  {
    title: "Get RCI zoning demand",
    description:
      "Residential (low/medium/high density), commercial, industrial, office and storage demand " +
      "of the loaded city, including native scale metadata: residential and building-demand bars are 0-100; " +
      "industrial/office company demand and storage are raw native values, not percentages. Includes factors explaining WHY demand is high or low " +
      "(e.g. Taxes, Unemployment, EmptyZones, Homelessness). Positive factor values push demand up, " +
      "negative values push it down.",
    inputSchema: {},
  },
  async () => {
    try {
      return jsonResult(await bridgeJson("/city/demand"));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_set_simulation",
  {
    title: "Pause / set simulation speed",
    description:
      "Control the simulation clock: pause/unpause the game and/or set the simulation speed " +
      "(0 = paused, 1 = normal, 2 = double, 4 = fastest UI speed). " +
      "Returns the resulting state. backgroundSimulation explicitly enables this-load-only background running; native menu/save/error pause barriers remain active.",
    inputSchema: {
      paused: z.boolean().optional().describe("true to pause, false to resume"),
      speed: z.union([z.literal(0),z.literal(1),z.literal(2),z.literal(4)]).optional().describe("native paused/1x/2x/4x"),
      backgroundSimulation: z.boolean().optional().describe("Enable background simulation for this loaded save only; does not bypass native pause barriers"),
    },
  },
  async ({ paused, speed, backgroundSimulation }) => {
    if (paused === undefined && speed === undefined && backgroundSimulation === undefined) {
      return errorResult(new Error("provide at least one of: paused, speed, backgroundSimulation"));
    }
    const params = new URLSearchParams();
    if (speed !== undefined) params.set("speed", String(speed));
    if (paused !== undefined) params.set("paused", String(paused));
    if (backgroundSimulation !== undefined) params.set("backgroundSimulation", String(backgroundSimulation));
    try {
      if (backgroundSimulation !== undefined) await requirePrecisionBridge("本次加载的原生后台模拟控制");
      return jsonResult(await bridgeJson(`/sim/control?${params.toString()}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool("cs2_milestone_popup", {
  title: "读取或确认已达到的原生里程碑通知",
  description: "读取正常解锁后待确认的通知；dismiss=true调用原生继续动作。不能解锁、加钱、加点或消除错误/保存暂停屏障。需要桥接0.10.2。",
  annotations: {readOnlyHint:false,destructiveHint:false,openWorldHint:false},
  inputSchema: {dismiss:z.boolean().optional()},
}, async ({dismiss})=>{
  try {
    await requirePrecisionBridge("原生里程碑通知确认");
    return jsonResult(await bridgeJson(`/game/milestone-popup${dismiss===undefined?'':`?dismiss=${dismiss}`}`));
  } catch(err) {return errorResult(err);}
});

server.registerTool(
  "cs2_screenshot",
  {
    title: "Take a screenshot",
    description:
      "Capture the current game view as a PNG image. Useful for seeing the city layout, " +
      "checking what the player is looking at, or verifying the result of an action. " +
      "Returns the image directly.",
    inputSchema: {
      width: z
        .number()
        .int()
        .min(64)
        .max(3840)
        .optional()
        .describe("Downscale the image to this width in pixels (default 1280, keeps aspect ratio)"),
    },
  },
  async ({ width }) => {
    const w = width ?? 1280;
    try {
      const res = await bridgeFetch(`/screenshot?width=${w}`, 30_000);
      if (!res.ok) {
        const text = await res.text();
        let message = `bridge returned HTTP ${res.status}`;
        try {
          message = (JSON.parse(text) as { error?: string })?.error ?? message;
        } catch {
          // keep default message
        }
        return errorResult(new BridgeError(message));
      }
      const buffer = Buffer.from(await res.arrayBuffer());
      return {
        content: [
          { type: "image" as const, data: buffer.toString("base64"), mimeType: "image/png" },
        ],
      };
    } catch (err) {
      return errorResult(err);
    }
  },
);

/** Register a parameter-less tool that returns bridge JSON. */
function registerJsonTool(name: string, title: string, description: string, path: string) {
  server.registerTool(name, { title, description, inputSchema: {} }, async () => {
    try {
      return jsonResult(await bridgeJson(path));
    } catch (err) {
      return errorResult(err);
    }
  });
}

registerJsonTool(
  "cs2_budget",
  "Get budget breakdown",
  "Detailed city budget: total income/expenses, balance, and a per-source breakdown " +
    "(residential/commercial/industrial/office taxes, service fees, subsidies, service upkeep, " +
    "loan interest, electricity/water import-export, map tile upkeep). Values are monthly rates; " +
    "expenses are positive costs.",
  "/city/budget",
);

registerJsonTool(
  "cs2_city_services",
  "Get utility service status",
  "Electricity (production/consumption/battery/trade), water & sewage (capacity/consumption/trade) " +
    "and garbage accumulation of the loaded city. Compare production vs consumption to spot shortages.",
  "/city/services",
);

registerJsonTool(
  "cs2_labor",
  "Get labor market",
  "Employment data: employed citizens, unemployment rate, homelessness, total/free jobs broken down " +
    "by required education level, and the population age structure (children/teens/adults/seniors).",
  "/city/labor",
);

server.registerTool("cs2_pedestrians", {
  title: "读取实际行人导航与位置",
  description: "只读原生HumanCurrentLane与Transform，按区域统计当前行人及导航目标，保留目标归属链供天桥与穿越验收。瞬时占用不等于容量或完成出行。",
  inputSchema: {x:z.number().finite().optional(),z:z.number().finite().optional(),radius:z.number().min(1).max(2000).optional(),limit:z.number().int().min(1).max(100).optional()},
  annotations: {readOnlyHint:true},
}, async args=>{try {await requirePrecisionBridge("原生行人导航读取");return jsonResult(await bridgeJson(`/city/pedestrians?${new URLSearchParams(Object.entries(args).map(([k,v])=>[k,String(v)]))}`));}catch(e){return errorResult(e);}});

server.registerTool(
  "cs2_statistics",
  {
    title: "Get statistic history",
    description:
      "Time series of a city statistic (sampled 32x per in-game day). Useful types: Population, Money, " +
      "Income, Expense, HouseholdCount, WorkerCount, Unemployed, TouristCount, CrimeRate, BirthRate, " +
      "DeathRate, CitizensMovedIn, CitizensMovedAway, ResidentialTaxableIncome, TrafficFlow-style passenger " +
      "counts (PassengerCountBus/Subway/Train...). An invalid type returns the full list of valid names.",
    inputSchema: {
      type: z.string().describe("StatisticType enum name, e.g. 'Population' or 'Money'"),
      parameter: z.number().int().optional().describe("Sub-index for parameterized statistics (default 0)"),
      samples: z.number().int().min(1).max(512).optional().describe("How many recent samples to return (default 64)"),
    },
  },
  async ({ type, parameter, samples }) => {
    const params = new URLSearchParams({ type });
    if (parameter !== undefined) params.set("parameter", String(parameter));
    if (samples !== undefined) params.set("samples", String(samples));
    try {
      return jsonResult(await bridgeJson(`/city/statistics?${params.toString()}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

registerJsonTool("cs2_production", "原生资源与产业链", "各资源生产/消费/产能/库存、分类消费、原始进出口和分项企业/建筑/仓储需求，以及实际产业链。原生单位，产能不等于产量，暂停快照不等于即时变化。", "/city/production");

server.registerTool("cs2_anarchy_normal_construction", {
  title: "禁用Anarchy自动绕过入口",
  description: "只关闭Anarchy原生设置AnarchicBulldozer并保存，保留正常建造辅助；无启用绕过的参数。返回真实状态。若AnarchyEnabled或允许忽略错误仍存在，施工继续被拒绝。",
  inputSchema: {},
}, async () => { try { return jsonResult(await bridgeJson("/mods/anarchy/normal-construction")); } catch (err) { return errorResult(err); } });

registerJsonTool(
  "cs2_get_taxes",
  "Get tax rates",
  "四类税率、原生范围与教育层级/产品分项税率。分项修改可能归一化大类基准，应用后重读全部实际税率。",
  "/city/taxes",
);

server.registerTool(
  "cs2_set_tax",
  {
    title: "Set a tax rate",
    description:
      "Set the tax rate (integer percent) for one tax area. Values outside the game's allowed " +
      "range are rejected. Higher taxes raise income but lower demand and happiness.",
    inputSchema: {
      area: z.enum(["Residential", "Commercial", "Industrial", "Office"]).describe("Tax area to change"),
      rate: z.number().int().describe("New tax rate in percent"),
      educationLevel: z.number().int().min(0).max(4).optional().describe("住宅教育分项，0..4；与resource互斥"),
      resource: z.string().optional().describe("产品分项，必须是cs2_get_taxes中该类可税产品；与educationLevel互斥"),
    },
  },
  async ({ area, rate, educationLevel, resource }) => {
    try {
      if (educationLevel !== undefined && (resource !== undefined || area !== "Residential")) throw new Error("educationLevel requires Residential and excludes resource");
      if (resource !== undefined && area === "Residential") throw new Error("resource requires Commercial/Industrial/Office");
      const params = new URLSearchParams({ area, rate: String(rate) });
      if (educationLevel !== undefined) params.set("educationLevel", String(educationLevel));
      if (resource !== undefined) params.set("resource", resource);
      return jsonResult(await bridgeJson(`/city/taxes/set?${params}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

registerJsonTool(
  "cs2_policies",
  "List city policies",
  "All city-wide policies with their internal name, localized title, active state, locked state and " +
    "whether they take a slider adjustment value (e.g. Recycling, Education Subsidies, speed limits).",
  "/city/policies",
);

server.registerTool(
  "cs2_set_policy",
  {
    title: "Toggle a city policy",
    description:
      "Activate or deactivate a city-wide policy by its internal name (from cs2_policies). " +
      "Slider policies additionally accept an adjustment value. Locked policies cannot be set.",
    inputSchema: {
      name: z.string().describe("Policy internal name from cs2_policies"),
      active: z.boolean().describe("true to activate, false to deactivate"),
      adjustment: z.number().optional().describe("Slider value for slider policies (optional)"),
    },
  },
  async ({ name, active, adjustment }) => {
    const params = new URLSearchParams({ name, active: String(active) });
    if (adjustment !== undefined) params.set("adjustment", String(adjustment));
    try {
      return jsonResult(await bridgeJson(`/city/policies/set?${params.toString()}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

registerJsonTool(
  "cs2_service_budgets",
  "Get service budgets",
  "Per-service budget sliders (50-150%, 100 = default) with current efficiency, estimated upkeep cost " +
    "and building count for every city service (police, healthcare, education, transport, ...).",
  "/city/service-budgets",
);

server.registerTool(
  "cs2_set_service_budget",
  {
    title: "Set a service budget",
    description:
      "Set the budget percentage (50-150) for one city service by name (from cs2_service_budgets). " +
      "Lower budgets save money but reduce service efficiency; higher budgets do the opposite.",
    inputSchema: {
      service: z.string().describe("Service name from cs2_service_budgets"),
      percentage: z.number().int().min(50).max(150).describe("Budget percentage, 100 = default"),
    },
  },
  async ({ service, percentage }) => {
    const params = new URLSearchParams({ service, percentage: String(percentage) });
    try {
      return jsonResult(await bridgeJson(`/city/service-budgets/set?${params.toString()}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_find_prefabs",
  {
    title: "Search placeable prefabs",
    description:
      "Search the game's building or road prefabs by name substring. Returns exact prefab names " +
      "needed by cs2_place_building, plus their type and locked state. Example queries: 'school', " +
      "'FireHouse', 'WindTurbine', 'Highway'.",
    inputSchema: {
      category: z
        .enum(["building", "road", "net", "tree", "asset"])
        .optional()
        .describe("Prefab category (default building); net = all networks; asset = native composite AssetStamp including interchanges"),
      query: z.string().optional().describe("Case-insensitive name substring filter"),
      limit: z.number().int().min(1).max(200).optional().describe("Max results (default 50)"),
    },
  },
  async ({ category, query, limit }) => {
    const params = new URLSearchParams();
    if (category) params.set("category", category);
    if (query) params.set("query", query);
    if (limit) params.set("limit", String(limit));
    try {
      if (category === 'asset') await requirePrecisionBridge("复合资产搜索");
      return jsonResult(await bridgeJson(`/prefabs?${params.toString()}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_place_building",
  {
    title: "Place a building",
    description:
      "Place a building in the world at the given map coordinates (x, z in meters; the map is roughly " +
      "-7000 to +7000 on each axis, use cs2_list_buildings to see coordinates of existing buildings for " +
      "reference). Height is sampled from the terrain automatically. The game validates the placement " +
      "(collisions, terrain, water) and the call fails with an explanation if blocked. Costs city money " +
      "like a normal player action. dryRun=true uses native placement preview, clears it without construction or charges, and needs bridge 0.10.2. Verify the result with cs2_screenshot or cs2_list_buildings.",
    inputSchema: {
      prefab: z.string().describe("Exact prefab name from cs2_find_prefabs"),
      x: z.number().describe("World X coordinate (meters)"),
      z: z.number().describe("World Z coordinate (meters)"),
      rotation: z.number().optional().describe("Rotation around Y axis in degrees (default 0)"),
      force: z.literal(false).optional().describe("正常发展规则只接受 false；不能绕过解锁或碰撞"),
      dryRun: z.boolean().optional().describe("Native collision/cost preview only; no construction or charges"),
    },
  },
  async ({ prefab, x, z, rotation, force, dryRun }) => {
    const params = new URLSearchParams({ prefab, x: String(x), z: String(z) });
    if (rotation !== undefined) params.set("rotation", String(rotation));
    if (force) params.set("force", "true");
    if (dryRun !== undefined) params.set("dryRun", String(dryRun));
    try {
      if (dryRun !== undefined) await requirePrecisionBridge("正常建筑原生预览");
      return jsonResult(await bridgeJson(`/build/place?${params.toString()}`, 15_000));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_build_road",
  {
    title: "Build a road segment",
    description:
      "Build any network segment between two world coordinates (terrain-following): roads, train tracks, " +
      "pedestrian paths, power lines, pipes ;any prefab from cs2_find_prefabs category 'road' or 'net'. " +
      "Straight by default; pass cx/cz for a curved segment through that control point. Length 8-1500m. " +
      "Endpoints on existing nodes connect to them. Costs city money; fails with an explanation if blocked.",
    inputSchema: {
      prefab: z.string().describe("Exact prefab name from cs2_find_prefabs (category road or net)"),
      x1: z.number().describe("Start X (meters)"),
      z1: z.number().describe("Start Z (meters)"),
      x2: z.number().describe("End X (meters)"),
      z2: z.number().describe("End Z (meters)"),
      cx: z.number().optional().describe("Curve control point X (with cz: builds a curve through it)"),
      cz: z.number().optional().describe("Curve control point Z"),
      e1: z.number().finite().optional().describe("Elevation at start in meters (bridges/elevated; negative = tunnel-ish)"),
      e2: z.number().finite().optional().describe("Elevation at end in meters"),
      force: z.literal(false).optional().describe("正常发展规则只接受 false；不能绕过解锁或碰撞"),
    },
  },
  async ({ prefab, x1, z1, x2, z2, cx, cz, e1, e2, force }) => {
    const params = new URLSearchParams({
      prefab,
      x1: String(x1),
      z1: String(z1),
      x2: String(x2),
      z2: String(z2),
    });
    if (cx !== undefined) params.set("cx", String(cx));
    if (cz !== undefined) params.set("cz", String(cz));
    if (e1 !== undefined) params.set("e1", String(e1));
    if (e2 !== undefined) params.set("e2", String(e2));
    if (force) params.set("force", "true");
    try {
      return jsonResult(await bridgeJson(`/build/road?${params.toString()}`, 15_000));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_list_buildings",
  {
    title: "List placed buildings",
    description:
      "List buildings existing in the city with their prefab name, world position and entity id " +
      "(index+version, needed for cs2_demolish). Filter by name substring to find specific buildings.",
    inputSchema: {
      query: z.string().optional().describe("Case-insensitive prefab-name substring filter"),
      limit: z.number().int().min(1).max(500).optional().describe("Max results (default 100)"),
    },
  },
  async ({ query, limit }) => {
    const params = new URLSearchParams();
    if (query) params.set("query", query);
    if (limit) params.set("limit", String(limit));
    try {
      return jsonResult(await bridgeJson(`/city/buildings?${params.toString()}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

registerJsonTool(
  "cs2_list_zones",
  "List zone types",
  "All zone types (residential low/medium/high, commercial, industrial, office...) with their " +
    "internal name, area type and locked state. Use the exact name with cs2_zone_area.",
  "/zones",
);

server.registerTool(
  "cs2_zone_area",
  {
    title: "Zone an area",
    description:
      "Paint zoning on all zonable cells within a radius around a point. Zone cells only exist " +
      "along roads (build a road first). Pass zone='None' to remove zoning. Buildings grow on zoned " +
      "cells while the simulation runs, driven by RCI demand (check cs2_demand).",
    inputSchema: {
      zone: z.string().describe("Exact zone name from cs2_list_zones, or 'None' to dezone"),
      x: z.number().describe("Center X (meters)"),
      z: z.number().describe("Center Z (meters)"),
      radius: z.number().min(8).max(200).optional().describe("Radius in meters (default 32)"),
      force: z.literal(false).optional().describe("正常发展规则只接受 false；不能绕过解锁或碰撞"),
    },
  },
  async ({ zone, x, z: zCoord, radius, force }) => {
    const params = new URLSearchParams({ zone, x: String(x), z: String(zCoord) });
    if (radius !== undefined) params.set("radius", String(radius));
    if (force) params.set("force", "true");
    try {
      // Refresh belongs to the bridge's native ToolUpdate system. A zoning
      // request must never reverse nearby roads to compensate for an old bridge.
      return jsonResult(await bridgeJson(`/build/zone?${params.toString()}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool("cs2_road_upgrades", {
  title:"原生道路升级目录", description:"真实IsUpgrade预制件，包括可独立铺设的公交/电车升级、正常解锁与设置/取消标志；车道不按道路名称猜测。",
  annotations:{readOnlyHint:true}, inputSchema:{query:z.string().optional(),limit:z.number().int().min(1).max(200).optional()},
}, async args=>{try {await requirePrecisionBridge("道路升级目录");return jsonResult(await bridgeJson(`/prefabs/road-upgrades?${new URLSearchParams(Object.entries(args).map(([k,v])=>[k,String(v)]))}`));}catch(e){return errorResult(e);}});

server.registerTool("cs2_apply_lane_upgrade", {
  title:"原生道路车道升级", description:"使用目录中支持的正常车道工具，保留无关升级和禁转，经原生工具校验收费。invert翻转单侧，remove正常取消；dryRun仅解析标志。禁止force与隐形资产。",
  inputSchema:{index:z.number().int().min(1),version:z.number().int().min(1),prefab:z.string(),invert:z.boolean().optional(),remove:z.boolean().optional(),dryRun:z.boolean().optional()},
}, async args=>{try {await requirePrecisionBridge("道路车道升级实际回读");return jsonResult(await bridgeJson(`/build/lane-upgrade?${new URLSearchParams(Object.entries(args).map(([k,v])=>[k,String(v)]))}`,15000));}catch(e){return errorResult(e);}});

server.registerTool("cs2_apply_road_service", {
  title:"原生道路服务调整", description:"按实时目录serviceApplicationSupported应用正常边段升级，含挡土墙、堤岸、高架、隧道、景观和车道；保留无关设置，invert翻转单侧，remove取消。节点及禁转用各自工具，Ground/恒坡用connect_road显式参数。dryRun仅解析标志，正常原生碰撞与收费，不启用错误屏蔽。",
  inputSchema:{index:z.number().int().min(1),version:z.number().int().min(1),prefab:z.string(),invert:z.boolean().optional(),remove:z.boolean().optional(),dryRun:z.boolean().optional()},
}, async args=>{try {
  if(args.prefab==='Crosswalk') throw new BridgeError('过街必须指定节点和接邻道路，使用 nodeController.crosswalk；边段服务入口不接受 Crosswalk。');
  await requirePrecisionBridge("道路服务调整实际回读");return jsonResult(await bridgeJson(`/build/road-service?${new URLSearchParams(Object.entries(args).map(([k,v])=>[k,String(v)]))}`,15000));
}catch(e){return errorResult(e);}});

server.registerTool(
  "cs2_upgrade_road",
  {
    title: "Upgrade a road segment",
    description:
      "Apply upgrades to an existing road segment (from cs2_list_roads): grass, trees, wideSidewalk, " +
      "soundBarrier, parking, lighting, medianGrass, medianTrees. Combine multiple with commas. " +
      "Preserves existing lanes, crossings, structures and other services; removal uses the native service tool. " +
      "The segment is recreated with the new composition via the game's tool pipeline.",
    inputSchema: {
      index: z.number().int().describe("Road segment entity index"),
      version: z.number().int().describe("Road segment entity version"),
      upgrades: z.string().describe("Comma-separated upgrade names, e.g. 'grass,lighting'"),
      side: z.enum(["both", "left", "right"]).optional().describe("Which side for side upgrades (default both)"),
    },
  },
  async ({ index, version, upgrades, side }) => {
    const params = new URLSearchParams({ index: String(index), version: String(version), upgrades });
    if (side) params.set("side", side);
    try {
      await requirePrecisionBridge("道路美化保留已有服务并核验实际标志");
      return jsonResult(await bridgeJson(`/build/upgrade?${params.toString()}`, 15_000));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_list_roads",
  {
    title: "List road segments",
    description:
      "List road segments (edges) with entity id, prefab name, start/end coordinates and length. " +
      "Filter spatially with x/z/radius or by prefab-name substring. Use the entity id with cs2_demolish.",
    inputSchema: {
      query: z.string().optional().describe("Prefab-name substring filter"),
      x: z.number().optional().describe("Center X for spatial filter"),
      z: z.number().optional().describe("Center Z for spatial filter"),
      radius: z.number().optional().describe("Radius in meters for spatial filter (default 250)"),
      limit: z.number().int().min(1).max(500).optional().describe("Max results (default 100)"),
    },
  },
  async ({ query, x, z: zCoord, radius, limit }) => {
    const params = new URLSearchParams();
    if (query) params.set("query", query);
    if (x !== undefined) params.set("x", String(x));
    if (zCoord !== undefined) params.set("z", String(zCoord));
    if (radius !== undefined) params.set("radius", String(radius));
    if (limit) params.set("limit", String(limit));
    try {
      return jsonResult(await bridgeJson(`/city/roads?${params.toString()}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_demolish",
  {
    title: "Demolish a building or road segment",
    description:
      "Demolish (bulldoze) one building (from cs2_list_buildings) or road segment (from cs2_list_roads) " +
      "identified by its entity index and version. Irreversible ;double-check the target first.",
    inputSchema: {
      index: z.number().int().describe("Entity index from cs2_list_buildings"),
      version: z.number().int().describe("Entity version from cs2_list_buildings"),
    },
  },
  async ({ index, version }) => {
    try {
      return jsonResult(await bridgeJson(`/build/demolish?index=${index}&version=${version}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

registerJsonTool(
  "cs2_get_camera",
  "Get camera state",
  "Current gameplay camera: pivot (look-at point), position, compass/tilt angles and zoom distance.",
  "/camera",
);

server.registerTool(
  "cs2_set_camera",
  {
    title: "Move the camera",
    description:
      "Point the gameplay camera: set the pivot (look-at world coordinates; height auto-sampled from " +
      "terrain unless y given), compass rotation angleX (degrees), tilt angleY (0-89) and zoom distance. " +
      "Combine with cs2_screenshot to LOOK at any place in the city ;the AI's own eyes.",
    inputSchema: {
      x: z.number().min(-7168).max(7168).optional().describe("Pivot X (requires z)"),
      z: z.number().min(-7168).max(7168).optional().describe("Pivot Z (requires x)"),
      y: z.number().optional().describe("Pivot height (optional, terrain height used if omitted)"),
      angleX: z.number().optional().describe("Compass rotation in degrees"),
      angleY: z.number().min(0).max(89).optional().describe("Tilt in degrees (0 = horizontal, 89 = top-down)"),
      zoom: z.number().min(10).max(10000).optional().describe("Camera distance (10-10000, larger = further out)"),
      edgeScrolling: z.boolean().optional().describe("Native session camera edge scrolling; false prevents background cursor drift; persistent player settings are unchanged"),
      clearInfoView: z.boolean().optional().describe("Clear the current native information overlay for scene inspection; session only"),
    },
  },
  async ({ x, z: zCoord, y, angleX, angleY, zoom, edgeScrolling, clearInfoView }) => {
    const params = new URLSearchParams();
    if (x !== undefined) params.set("x", String(x));
    if (zCoord !== undefined) params.set("z", String(zCoord));
    if (y !== undefined) params.set("y", String(y));
    if (angleX !== undefined) params.set("angleX", String(angleX));
    if (angleY !== undefined) params.set("angleY", String(angleY));
    if (zoom !== undefined) params.set("zoom", String(zoom));
    if (edgeScrolling !== undefined) params.set("edgeScrolling", String(edgeScrolling));
    if (clearInfoView !== undefined) params.set("clearInfoView", String(clearInfoView));
    try {
      if (edgeScrolling !== undefined) await requirePrecisionBridge("相机边缘滚动设置");
      if (clearInfoView !== undefined) await requirePrecisionBridge("原生信息视图清除");
      return jsonResult(await bridgeJson(`/camera/set?${params.toString()}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_terrain",
  {
    title: "Get terrain & water map",
    description:
      "Sampled heightmap and water-depth grid of the whole map (14336x14336m), or a local x/z/radius region. Returns row-major arrays; local maps use bounds/cellSizeX/Z; " +
      "waterDepths > 0 marks rivers/lakes/sea. Use to understand geography before planning construction.",
    inputSchema: {
      resolution: z.number().int().min(16).max(256).optional().describe("Grid resolution per axis (default 64)"),
      x:z.number().min(-7168).max(7168).optional(), z:z.number().min(-7168).max(7168).optional(), radius:z.number().min(8).max(7168).optional(),
    },
  },
  async ({ resolution,x,z,radius }) => {
    const params = new URLSearchParams();
    if (resolution) params.set("resolution", String(resolution));
    for(const [key,value] of Object.entries({x,z,radius})) if(value!==undefined) params.set(key,String(value));
    try {
      if([x,z,radius].some(v=>v!==undefined)) {
        if([x,z,radius].some(v=>v===undefined)) throw new Error("局部地形必须一起提供x/z/radius。");
        await requirePrecisionBridge();
      }
      return jsonResult(await bridgeJson(`/city/terrain?${params.toString()}`, 30_000));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_gridmap",
  {
    title: "Get data-layer grid",
    description:
      "The game's native cell-map grids as row-major arrays: landValue, groundPollution, airPollution, " +
      "noisePollution, groundWater, groundWaterPollution. Use to pick good locations (cheap land, clean " +
      "air, water for pumps) like a player reading infoviews.",
    inputSchema: {
      layer: z
        .enum(["landValue", "groundPollution", "airPollution", "noisePollution", "groundWater", "groundWaterPollution"])
        .describe("Which data layer to export"),
    },
  },
  async ({ layer }) => {
    try {
      return jsonResult(await bridgeJson(`/city/gridmap?layer=${layer}`, 30_000));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_zoning",
  {
    title: "Read current zoning",
    description:
      "Summary of painted zones: cells per zone type with occupied/empty split, whole-city or within a " +
      "radius. Empty zoned cells are where buildings will grow.",
    inputSchema: {
      x: z.number().optional().describe("Center X for area filter"),
      z: z.number().optional().describe("Center Z for area filter"),
      radius: z.number().optional().describe("Radius in meters (with x/z)"),
    },
  },
  async ({ x, z: zCoord, radius }) => {
    const params = new URLSearchParams();
    if (x !== undefined) params.set("x", String(x));
    if (zCoord !== undefined) params.set("z", String(zCoord));
    if (radius !== undefined) params.set("radius", String(radius));
    try {
      return jsonResult(await bridgeJson(`/city/zoning?${params.toString()}`, 20_000));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_notifications",
  {
    title: "List warning notifications",
    description:
      "All active in-world warning icons (no electricity, no water, garbage piling up, abandoned buildings, " +
      "high rent...) with type counts, locations and target entities. The primary way to discover problems.",
    inputSchema: {
      limit: z.number().int().min(1).max(500).optional().describe("Max detailed items (default 100)"),
    },
  },
  async ({ limit }) => {
    const params = new URLSearchParams();
    if (limit) params.set("limit", String(limit));
    try {
      return jsonResult(await bridgeJson(`/city/notifications?${params.toString()}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_inspect",
  {
    title: "Inspect an entity",
    description:
      "Detail view of one entity (building/road) by index+version: prefab, position, status flags " +
      "(abandoned/condemned/destroyed), renters, native utility flow/capacity/attached ports and road composition, " +
      "plus read-only Traffic mod components/buffers. Lane details are opt-in and paginated; default only returns lane counts. Zero instantaneous flow is not proof of redundancy.",
    inputSchema: {
      index: z.number().int().describe("Entity index"),
      version: z.number().int().describe("Entity version"),
      laneLimit: z.number().int().min(0).max(256).optional().describe("Lane details page size; default 0 (summary only)"),
      laneOffset: z.number().int().min(0).max(65535).optional().describe("Lane details offset; default 0"),
    },
  },
  async ({ index, version, laneLimit, laneOffset }) => {
    try {
      const params = new URLSearchParams({index:String(index),version:String(version)});
      if(laneLimit !== undefined || laneOffset !== undefined) {
        await requirePrecisionBridge("车道分页");
        if(laneLimit !== undefined) params.set("laneLimit",String(laneLimit));
        if(laneOffset !== undefined) params.set("laneOffset",String(laneOffset));
      }
      return jsonResult(await bridgeJson(`/entity/inspect?${params}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

registerJsonTool(
  "cs2_get_loan",
  "Get city loan state",
  "Current loan principal, daily interest rate, daily payment and the city's creditworthiness (max borrowable).",
  "/city/loan",
);

server.registerTool(
  "cs2_set_loan",
  {
    title: "Borrow / repay loan",
    description:
      "Set the city's loan principal: higher than current = borrow more (cash added to treasury), " +
      "lower = repay, 0 = repay fully. Values above native creditworthiness are rejected. Interest accrues daily.",
    inputSchema: {
      amount: z.number().int().min(0).describe("New total loan principal"),
    },
  },
  async ({ amount }) => {
    try {
      return jsonResult(await bridgeJson(`/city/loan/set?amount=${amount}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

registerJsonTool(
  "cs2_get_fees",
  "Get service fees",
  "Current price the city charges per service (electricity, water, healthcare, education levels, garbage, " +
    "parking, public transport...) with slider ranges and estimated monthly income per fee.",
  "/city/fees",
);

server.registerTool(
  "cs2_set_fee",
  {
    title: "Set a service fee",
    description:
      "Set the fee/price for one service resource (name from cs2_get_fees). Higher fees raise income " +
      "but reduce usage and citizen happiness.",
    inputSchema: {
      resource: z.string().describe("Resource name from cs2_get_fees, e.g. 'Electricity'"),
      fee: z.number().finite().nonnegative().describe("New fee within native sliderRange, adjustable fees only"),
    },
  },
  async ({ resource, fee }) => {
    const params = new URLSearchParams({ resource, fee: String(fee) });
    try {
      return jsonResult(await bridgeJson(`/city/fees/set?${params.toString()}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_list_objects",
  {
    title: "List standalone trees/plants",
    description:
      "List standalone trees and plants (not building sub-objects) with entity ids and positions. " +
      "Filter by name or spatially. Use the entity id with cs2_demolish to remove them.",
    inputSchema: {
      query: z.string().optional().describe("Prefab-name substring filter"),
      x: z.number().optional().describe("Center X for spatial filter"),
      z: z.number().optional().describe("Center Z for spatial filter"),
      radius: z.number().optional().describe("Radius meters (default 250 with x/z)"),
      limit: z.number().int().min(1).max(500).optional().describe("Max results (default 100)"),
    },
  },
  async ({ query, x, z: zCoord, radius, limit }) => {
    const params = new URLSearchParams();
    if (query) params.set("query", query);
    if (x !== undefined) params.set("x", String(x));
    if (zCoord !== undefined) params.set("z", String(zCoord));
    if (radius !== undefined) params.set("radius", String(radius));
    if (limit) params.set("limit", String(limit));
    try {
      return jsonResult(await bridgeJson(`/city/objects?${params.toString()}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_run_simulation",
  {
    title: "Run simulation for N in-game hours",
    description:
      "Unpause and run the simulation at the given speed, auto-pausing after the requested number of " +
      "in-game hours. Returns immediately with the target frame; poll cs2_game_state (frameIndex) to " +
      "track progress. Use cancel=true to stop early. The core loop for autonomous mayoring: " +
      "act, run time forward, observe results.",
    inputSchema: {
      hours: z.number().min(0.1).max(96).optional().describe("In-game hours to run (required unless cancel)"),
      speed: z.union([z.literal(1),z.literal(2),z.literal(4)]).optional().describe("Native simulation speed while running (default 4)"),
      cancel: z.boolean().optional().describe("true to cancel a timed run and pause now"),
    },
  },
  async ({ hours, speed, cancel }) => {
    const params = new URLSearchParams();
    if (cancel) params.set("cancel", "true");
    if (hours !== undefined) params.set("hours", String(hours));
    if (speed !== undefined) params.set("speed", String(speed));
    try {
      return jsonResult(await bridgeJson(`/sim/run?${params.toString()}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_save_game",
  {
    title: "Save the game",
    description:
      "经原生保存流程完成后返回；后台隐藏时includePreview=false省略缩略图，不额外渲染或恢复前台。调用后仍须核文件落盘、内层GUID和备份。 " +
      "Default name is timestamped 'CS2MCP ...'.",
    inputSchema: {
      name: z.string().optional().describe("Save name (default: timestamped)"),
      includePreview: z.boolean().optional().describe("后台隐藏保存设false；原生保存不生成缩略图，城市数据正常保存"),
    },
  },
  async ({ name, includePreview }) => {
    const params = new URLSearchParams();
    if (name) params.set("name", name);
    if (includePreview !== undefined) params.set("includePreview", String(includePreview));
    try {
      if (includePreview !== undefined) await requirePrecisionBridge("后台原生保存与预览选项");
      return jsonResult(await bridgeJson(`/game/save?${params.toString()}`, 60_000));
    } catch (err) {
      return errorResult(err);
    }
  },
);

registerJsonTool(
  "cs2_tiles_info",
  "Get map tile info",
  "Owned/total map tiles, tiles available to purchase and upkeep settings. (Purchasing via API arrives in v0.9.)",
  "/city/tiles",
);

registerJsonTool(
  "cs2_list_districts",
  "List districts",
  "All districts with entity id, center position, polygon size and active policy count.",
  "/districts",
);

server.registerTool(
  "cs2_create_district",
  {
    title: "Create a district",
    description:
      "Draw a district over an area by polygon corners (3-32 points, world meters). Buildings and roads " +
      "inside get assigned to it; district policies can then be applied to just that area.",
    inputSchema: {
      nodes: z.string().describe("Polygon corners 'x1,z1;x2,z2;x3,z3;...' (counter-clockwise)"),
      prefab: z.string().optional().describe("District prefab name (default: the standard district)"),
    },
  },
  async ({ nodes, prefab }) => {
    const params = new URLSearchParams({ nodes });
    if (prefab) params.set("prefab", prefab);
    try {
      return jsonResult(await bridgeJson(`/build/district?${params.toString()}`, 15_000));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_district_policies",
  {
    title: "List district policies",
    description:
      "Policies available for one district (speed limits, parking fees, combustion ban...) with " +
      "active/locked state. District from cs2_list_districts.",
    inputSchema: {
      index: z.number().int().describe("District entity index"),
      version: z.number().int().describe("District entity version"),
    },
  },
  async ({ index, version }) => {
    try {
      return jsonResult(await bridgeJson(`/district/policies?index=${index}&version=${version}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_set_district_policy",
  {
    title: "Toggle a district policy",
    description: "Activate/deactivate a policy on one district (policy name from cs2_district_policies).",
    inputSchema: {
      index: z.number().int().describe("District entity index"),
      version: z.number().int().describe("District entity version"),
      name: z.string().describe("Policy internal name"),
      active: z.boolean().describe("true to activate"),
      adjustment: z.number().optional().describe("Slider value for slider policies"),
    },
  },
  async ({ index, version, name, active, adjustment }) => {
    const params = new URLSearchParams({
      index: String(index),
      version: String(version),
      name,
      active: String(active),
    });
    if (adjustment !== undefined) params.set("adjustment", String(adjustment));
    try {
      return jsonResult(await bridgeJson(`/district/policies/set?${params.toString()}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

const transitType = z.enum(["bus", "tram", "metro", "train", "ship", "ferry"]);

server.registerTool("cs2_transport_overview", {
  title: "全部客运与货运统计类型",
  description: "原生交通信息页同源配置、显示／解锁、线路计数和居民／游客／货运统计，覆盖出租车、公交、铁路、电车、地铁、渡轮、客船、航空和公路货运。横线与零线路分别返回；原始货运单位不冒充人数或吨。需要桥接0.10.2。",
  inputSchema: {},
}, async () => {
  try { await requirePrecisionBridge("全部交通统计"); return jsonResult(await bridgeJson("/transit/overview")); }
  catch (err) { return errorResult(err); }
});

/** An entity id (index+version) or a world point; the bridge takes "i:v" or "x,z" items joined by ';'. */
const entityOrPoint = z.union([
  z.object({ index: z.number().int(), version: z.number().int() }),
  z.object({ x: z.number(), z: z.number() }),
]);

function joinEntityOrPoints(items: z.infer<typeof entityOrPoint>[]): string {
  return items.map((item) => ("index" in item ? `${item.index}:${item.version}` : `${item.x},${item.z}`)).join(";");
}

/**
 * The bridge decodes query values with Uri.UnescapeDataString, which keeps '+'
 * literal, so spaces must go out as %20 (URLSearchParams writes '+'; a real '+'
 * is already %2B).
 */
function bridgeQueryString(params: URLSearchParams): string {
  return params.toString().replace(/\+/g, "%20");
}

server.registerTool(
  "cs2_list_transit_stops",
  {
    title: "List public transport stops",
    description:
      "List existing transit stops (bus/tram roadside stops, train/metro station platforms) with entity id, type, " +
      "name, position, owning station building, attached road and the lines already serving each stop. Filter by " +
      "type and/or spatially (sorted by distance when x/z given). prefabs=true also lists placeable roadside stop " +
      "prefabs for cs2_place_transit_stop.",
    inputSchema: {
      type: z.enum(["bus", "tram", "metro", "train", "ship", "ferry", "airplane"]).optional().describe("Only stops of this transport type; ferry is independent of ship; airplane lists airport gates and air outside connections"),
      x: z.number().optional().describe("Center X for spatial filter"),
      z: z.number().optional().describe("Center Z for spatial filter"),
      radius: z.number().optional().describe("Radius in meters for spatial filter (default 500)"),
      limit: z.number().int().min(1).max(500).optional().describe("Max results (default 100)"),
      prefabs: z.boolean().optional().describe("Also list placeable roadside stop prefabs"),
    },
  },
  async ({ type, x, z: zCoord, radius, limit, prefabs }) => {
    const params = new URLSearchParams();
    if (type) params.set("type", type);
    if (x !== undefined) params.set("x", String(x));
    if (zCoord !== undefined) params.set("z", String(zCoord));
    if (radius !== undefined) params.set("radius", String(radius));
    if (limit) params.set("limit", String(limit));
    if (prefabs) params.set("prefabs", "true");
    try {
      return jsonResult(await bridgeJson(`/transit/stops?${bridgeQueryString(params)}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_place_transit_stop",
  {
    title: "Place a bus/tram stop on a road",
    description:
      "Place a roadside bus or tram stop at the road nearest to (x, z), on the side of the road where the point is " +
      "(or on a specific road segment via road). Runs through the game's object tool pipeline: the game attaches " +
      "the stop to the road, snaps it to the curb, validates it (sidewalk, lanes, overlap, money) and charges the " +
      "normal cost. Train/metro stops are station buildings: use cs2_place_building for those. Returns the new " +
      "stop's entity id for cs2_create_transit_line. The road segment is regenerated, so its id may change.",
    inputSchema: {
      type: z.enum(["bus", "tram"]).describe("Stop type"),
      x: z.number().describe("World X of a point beside the road (meters)"),
      z: z.number().describe("World Z of a point beside the road (meters)"),
      road: z
        .object({ index: z.number().int(), version: z.number().int() })
        .optional()
        .describe("Force a specific road segment (from cs2_list_roads) instead of the nearest suitable one"),
      radius: z.number().min(4).max(200).optional().describe("Road search radius in meters (default 40)"),
      prefab: z.string().optional().describe("Exact stop prefab name (see cs2_list_transit_stops prefabs=true)"),
      force: z.literal(false).optional().describe("正常发展规则只接受 false；不能绕过解锁或碰撞"),
    },
  },
  async ({ type, x, z: zCoord, road, radius, prefab, force }) => {
    const params = new URLSearchParams({ type, x: String(x), z: String(zCoord) });
    if (road) params.set("road", `${road.index}:${road.version}`);
    if (radius !== undefined) params.set("radius", String(radius));
    if (prefab) params.set("prefab", prefab);
    if (force) params.set("force", "true");
    try {
      return jsonResult(await bridgeJson(`/transit/stops/place?${bridgeQueryString(params)}`, 15_000));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_create_transit_line",
  {
    title: "创建客运或货运线路",
    description:
      "Create a bus/tram/metro/train/ship/ferry line through existing stops, in order; the line automatically loops back to " +
      "the first stop. Each stop is a stop entity id, a station building id (its matching platform is used) or an " +
      "{x, z} point (snaps to the nearest existing stop of that type within snapRadius). Uses the game's own route " +
      "pipeline: it waits for the game to pathfind every segment, and fails with an explanation (e.g. 'no bus path " +
      "from stop #2 to stop #3') instead of creating a broken line. Needs a depot of the same type for vehicles.",
    inputSchema: {
      type: transitType.describe("Transport type"),
      cargo: z.boolean().optional().describe("明确货运模式，仅train/ship；默认false保留客运行为"),
      stops: z.array(entityOrPoint).min(2).max(100).describe("Ordered stops (at least 2 different ones)"),
      name: z.string().max(64).optional().describe("Custom line name (default: the game's 'Bus Line N')"),
      color: z
        .string()
        .regex(/^#?[0-9a-fA-F]{6}$/)
        .optional()
        .describe("Line color as #RRGGBB (default: the game's color for this line type)"),
      snapRadius: z.number().min(5).max(300).optional().describe("Max distance for {x, z} stops to snap (default 60m)"),
      force: z.literal(false).optional().describe("正常发展规则只接受 false；不能绕过解锁或碰撞"),
    },
  },
  async ({ type, cargo, stops, name, color, snapRadius, force }) => {
    const params = new URLSearchParams({ type, stops: joinEntityOrPoints(stops) });
    if (name) params.set("name", name);
    if (color) params.set("color", color.startsWith("#") ? color : `#${color}`);
    if (snapRadius !== undefined) params.set("snapRadius", String(snapRadius));
    if (force) params.set("force", "true");
    try {
      if (cargo && type !== "train" && type !== "ship") throw new BridgeError("货运线路仅支持铁路和航运");
      if (type === "ferry") await requirePrecisionBridge("原生渡轮线路");
      if (cargo !== undefined || type === "ship") await requirePrecisionBridge("货运或航运线路");
      if (cargo !== undefined) params.set("cargo", String(cargo));
      return jsonResult(await bridgeJson(`/transit/lines/create?${bridgeQueryString(params)}`, 15_000));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_list_transit_lines",
  {
    title: "List public transport lines",
    description:
      "List transit lines with the same numbers the game's transportation panel shows: name, number, type, color, " +
      "active/visible, day/night schedule, stop count, vehicles, passengerCount or cargoAmountRaw, usage (occupancy) and length. " +
      "Cargo raw units are not people or verified tonnes; legacy passengersOnBoard is ambiguous for cargo lines. " +
      "includeStops=true adds each stop in order with its waiting passengers. The game keeps no per-line ridership " +
      "history; city-wide passenger series are in cs2_statistics (PassengerCountBus, ...).",
    inputSchema: {
      type: transitType.optional().describe("Only lines of this transport type"),
      query: z.string().optional().describe("Case-insensitive line name substring"),
      includeStops: z.boolean().optional().describe("Include the ordered stop list with waiting passengers"),
      limit: z.number().int().min(1).max(200).optional().describe("Max results (default 50)"),
    },
  },
  async ({ type, query, includeStops, limit }) => {
    const params = new URLSearchParams();
    if (type) params.set("type", type);
    if (query) params.set("query", query);
    if (includeStops) params.set("includeStops", "true");
    if (limit) params.set("limit", String(limit));
    try {
      return jsonResult(await bridgeJson(`/transit/lines?${bridgeQueryString(params)}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_delete_transit_line",
  {
    title: "Delete a public transport line",
    description:
      "Delete one transit line (from cs2_list_transit_lines) exactly like the game's own 'delete line' button. " +
      "Its stops stay in place. Irreversible: double-check the line first.",
    inputSchema: {
      index: z.number().int().describe("Line entity index"),
      version: z.number().int().describe("Line entity version"),
    },
  },
  async ({ index, version }) => {
    try {
      return jsonResult(await bridgeJson(`/transit/lines/delete?index=${index}&version=${version}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_list_map_tiles",
  {
    title: "List map tiles",
    description:
      "List map tiles with entity id, center, bounds, owned flag and natural features (buildable land, fertile " +
      "land, forest, oil, ore, water, fish), plus owned count, remaining tile permits and treasury. Filter by " +
      "ownership and spatially (sorted by distance when x/z given).",
    inputSchema: {
      owned: z.enum(["all", "owned", "unowned"]).optional().describe("Ownership filter (default all)"),
      x: z.number().optional().describe("Center X for spatial filter"),
      z: z.number().optional().describe("Center Z for spatial filter"),
      radius: z.number().optional().describe("Radius in meters for spatial filter (default 3000)"),
      features: z.boolean().optional().describe("Include natural features per tile (default true)"),
      limit: z.number().int().min(1).max(1000).optional().describe("Max results (default 100)"),
    },
  },
  async ({ owned, x, z: zCoord, radius, features, limit }) => {
    const params = new URLSearchParams();
    if (owned) params.set("owned", owned);
    if (x !== undefined) params.set("x", String(x));
    if (zCoord !== undefined) params.set("z", String(zCoord));
    if (radius !== undefined) params.set("radius", String(radius));
    if (features !== undefined) params.set("features", String(features));
    if (limit) params.set("limit", String(limit));
    try {
      return jsonResult(await bridgeJson(`/city/tiles/list?${bridgeQueryString(params)}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_buy_map_tiles",
  {
    title: "Buy map tiles",
    description:
      "Buy one or more unowned map tiles, by tile id (cs2_list_map_tiles) or by a point inside the tile. Uses the " +
      "game's own purchase logic (the Map Tiles panel's Purchase button): the game prices the tiles from their " +
      "features (price rises with tiles owned), requires enough tile permits (from milestones) and money, then " +
      "charges the treasury. Irreversible; returns the actual cost charged.",
    inputSchema: {
      tiles: z.array(entityOrPoint).min(1).max(50).describe("Tiles to buy: {index, version} or {x, z} inside the tile"),
    },
  },
  async ({ tiles }) => {
    const params = new URLSearchParams({ tiles: joinEntityOrPoints(tiles) });
    try {
      return jsonResult(await bridgeJson(`/city/tiles/buy?${bridgeQueryString(params)}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_traffic",
  {
    title: "Traffic report",
    description:
      "Traffic diagnostics with the game's own numbers: the city-wide traffic flow (as the traffic info view " +
      "shows it), the roads with the lowest flow (0-100% per road like the road info panel, with volume and the " +
      "four daily periods) and every lane the game flags as a traffic bottleneck, grouped by road or intersection. " +
      "Filter spatially with x/z/radius. Use before and after road or transit changes to measure their effect; " +
      "values update 32 times per in-game day, so let the simulation run between checks.",
    inputSchema: {
      x: z.number().optional().describe("Center X for spatial filter"),
      z: z.number().optional().describe("Center Z for spatial filter"),
      radius: z.number().optional().describe("Radius in meters for spatial filter (default 1000)"),
      limit: z.number().int().min(1).max(200).optional().describe("How many worst roads to list (default 25)"),
      minVolume: z.number().min(0).optional().describe("Ignore roads with less volume than this when ranking (default 1)"),
    },
  },
  async ({ x, z: zCoord, radius, limit, minVolume }) => {
    const params = new URLSearchParams();
    if (x !== undefined) params.set("x", String(x));
    if (zCoord !== undefined) params.set("z", String(zCoord));
    if (radius !== undefined) params.set("radius", String(radius));
    if (limit) params.set("limit", String(limit));
    if (minVolume !== undefined) params.set("minVolume", String(minVolume));
    try {
      return jsonResult(await bridgeJson(`/city/traffic?${bridgeQueryString(params)}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_vehicles",
  {
    title: "Vehicle census and queue heads",
    description:
      "Every driving road vehicle (parked cars excluded) by type (personal car, taxi, bus, delivery/cargo truck, " +
      "service...) with how many are stopped, and for an area: their destinations (outside the city, residential, " +
      "commercial...) and what stopped vehicles wait for (vehicle ahead, red light, crossing or oncoming traffic). " +
      "queueHeads follows each stopped vehicle's blockers to the head of its queue, so it names the junctions or " +
      "roads actually holding traffic up and how many vehicles are stuck behind each; deadlocks lists loops of " +
      "vehicles blocking each other (gridlock). A live snapshot: repeat it to see what persists.",
    inputSchema: {
      x: z.number().optional().describe("Center X for the area breakdown"),
      z: z.number().optional().describe("Center Z for the area breakdown"),
      radius: z.number().optional().describe("Area radius in meters (default 500)"),
      limit: z.number().int().min(1).max(100).optional().describe("How many queue heads / deadlocks to list (default 15)"),
    },
  },
  async ({ x, z: zCoord, radius, limit }) => {
    const params = new URLSearchParams();
    if (x !== undefined) params.set("x", String(x));
    if (zCoord !== undefined) params.set("z", String(zCoord));
    if (radius !== undefined) params.set("radius", String(radius));
    if (limit) params.set("limit", String(limit));
    try {
      return jsonResult(await bridgeJson(`/city/vehicles?${bridgeQueryString(params)}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_road_graph",
  {
    title: "Road graph of an area",
    description:
      "Road segments in an area with their junction ids (so you can see what connects to what), direction " +
      "(one-way roads run start to end), flow and volume, plus each junction's position, number of roads and " +
      "whether it has traffic lights or is a roundabout. Use it to plan road changes before cs2_replace_road, " +
      "cs2_build_road or cs2_demolish.",
    inputSchema: {
      x: z.number().describe("Center X"),
      z: z.number().describe("Center Z"),
      radius: z.number().min(1).max(2000).optional().describe("Radius in meters (default 300)"),
      limit: z.number().int().min(1).max(3000).optional().describe("Max road segments (default 400)"),
      query: z.string().optional().describe("Only prefabs whose name contains this"),
      allNets: z
        .boolean()
        .optional()
        .describe("Include every network, not just roads: train/subway/tram tracks (incl. stations' own tracks), paths..."),
    },
  },
  async ({ x, z: zCoord, radius, limit, query, allNets }) => {
    const params = new URLSearchParams({ x: String(x), z: String(zCoord) });
    if (radius !== undefined) params.set("radius", String(radius));
    if (limit) params.set("limit", String(limit));
    if (query) params.set("query", query);
    if (allNets) params.set("nets", "all");
    try {
      return jsonResult(await bridgeJson(`/city/road-graph?${bridgeQueryString(params)}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_connect_road",
  {
    title: "Build a connected road or track",
    description:
      "Build a road, track or other network segment whose ends join the existing network, like drawing with the " +
      "road tool's snapping: each end snaps to the nearest junction of a compatible network within `snap` meters, " +
      "otherwise splits the nearest compatible segment at that point (a track never joins a road). Ends with " +
      "nothing in range start freely on the terrain. Straight, quadratic with cx/cz, or cubic with both c1x/c1z and c2x/c2z control handles (curves need not pass through handles). Explicit end elevations filter snapping by height. " +
      "Use matching elevations at stacked roads to avoid ground/flyover confusion (needed where tracks must " +
      "leave a junction smoothly). Use for new road links and ramps, and for track spurs to station tracks (find " +
      "those with cs2_road_graph allNets). Validated by the game; costs money like a player build.",
    inputSchema: {
      prefab: z.string().describe("Exact network prefab name (cs2_find_prefabs category road or net)"),
      x1: z.number().describe("Start X"),
      z1: z.number().describe("Start Z"),
      x2: z.number().describe("End X"),
      z2: z.number().describe("End Z"),
      cx: z.number().optional().describe("Curve control point X (with cz)"),
      cz: z.number().optional().describe("Curve control point Z"),
      snap: z.number().min(0).max(50).optional().describe("Snap distance in meters (default 8; 0 = never snap)"),
      e1: z.number().finite().optional().describe("Elevation of a free (unsnapped) start in meters: >0 bridge/flyover, <0 tunnel"),
      e2: z.number().finite().optional().describe("Elevation of a free (unsnapped) end in meters"),
      c1x: z.number().optional().describe("First cubic control handle X; supply all four cubic coordinates"),
      c1z: z.number().optional(), c2x: z.number().optional(), c2z: z.number().optional(),
      c1e: z.number().finite().optional().describe("First cubic handle elevation above terrain"),
      c2e: z.number().finite().optional().describe("Second cubic handle elevation above terrain"),
      heightTolerance: z.number().min(0).max(30).optional().describe("Vertical snapping tolerance, default 3m when e1/e2 given; explicit setting enables both-end height filtering"),
      composition: z.enum(["auto","ground"]).optional().describe("正常结构方式：ground保留世界几何但将定义Elevation归零，可能挖填地形；不绕过校验。需要桥接0.10.2"),
      constantSlope: z.boolean().optional().describe("直线按两端世界高度形成恒定坡度；不支持与曲线控制柄组合。需要桥接0.10.2"),
      dryRun: z.boolean().optional().describe("Resolve geometry and endpoints without building; not native collision/cost preview"),
      force: z.literal(false).optional().describe("正常发展规则只接受 false；不能绕过解锁或碰撞"),
    },
  },
  async ({ prefab, x1, z1, x2, z2, cx, cz, snap, e1, e2, c1x, c1z, c2x, c2z, c1e, c2e, heightTolerance, composition, constantSlope, dryRun, force }) => {
    const params = new URLSearchParams({
      prefab,
      x1: String(x1),
      z1: String(z1),
      x2: String(x2),
      z2: String(z2),
    });
    if (cx !== undefined) params.set("cx", String(cx));
    if (cz !== undefined) params.set("cz", String(cz));
    if (snap !== undefined) params.set("snap", String(snap));
    if (e1 !== undefined) params.set("e1", String(e1));
    if (e2 !== undefined) params.set("e2", String(e2));
    for (const [key,value] of Object.entries({c1x,c1z,c2x,c2z,c1e,c2e,heightTolerance,composition,constantSlope,dryRun})) if(value!==undefined) params.set(key,String(value));
    if (force) params.set("force", "true");
    try {
      await requirePrecisionBridge("网络端点实际高程校验");
      if (composition !== undefined || constantSlope !== undefined) await requirePrecisionBridge("道路结构与恒坡");
      return jsonResult(await bridgeJson(`/build/road/connect?${bridgeQueryString(params)}`, 15_000));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool("cs2_road_path", {
  title:"连续道路与曲线匝道批量施工",
  description:"一个调用内按顺序施工多段直线/二次/三次道路，完整参数先核对，每段沿原生工具连接、校验和收费。端点默认明确地形+0米及3米高度吸附；高架/隧道须显式e1/e2。失败停止并返回已成功段，不能整批盲重放。dryRun 只检查当前现场几何，尚未施工的前段不会出现在后段吸附预览中；不得把独立段清单叫连续通路。",
  inputSchema:{prefab:z.string(),segments:z.array(roadPathSegment).min(1).max(32),requireContinuity:z.boolean().default(true),dryRun:z.boolean().default(false)},
}, async ({prefab,segments,requireContinuity,dryRun})=>{
  const results:unknown[]=[];
  let phase:"preflight"|"execute"="preflight";
  try {
    validateRoadPath(segments);
    if(requireContinuity) for(let i=1;i<segments.length;i++) {
      const previous=segments[i-1],next=segments[i];
      if(Math.hypot(previous.x2-next.x1,previous.z2-next.z1)>.01 || Math.abs(previous.e2-next.e1)>.01)
        throw new Error(`第${i+1}段与前段端点/高度不连续；整条路径尚未施工。`);
    }
    await requirePrecisionBridge();
    phase="execute";
    for(const segment of segments) {
      const params=new URLSearchParams({prefab,dryRun:String(dryRun)});
      for(const [k,v] of Object.entries(segment)) if(v!==undefined) params.set(k,String(v));
      results.push(await bridgeJson(`/build/road/connect?${params}`,15000));
    }
    return jsonResult({completed:results.length,total:segments.length,dryRun,results,note:"批次返回后按实际road_graph核验端点连接和高度； native成本/碰撞校验逐段进行，不具备整体回滚"});
  } catch(err) {
    const failedSegmentMayHaveApplied = phase === "execute" && !dryRun
      && (err instanceof BridgeError ? err.mayHaveApplied : true);
    const partialChangesRemain = !dryRun && (results.length > 0 || failedSegmentMayHaveApplied);
    return {...jsonResult({completed:results.length,total:segments.length,phase,failedAt:phase==="execute"?results.length:null,results,dryRun,
      partialChangesRemain,failedSegmentMayHaveApplied,mayHaveApplied:partialChangesRemain,safeToRetry:false,
      error:err instanceof Error?err.message:String(err),note:"失败段可能已经应用；先回读现场，禁止盲目重放整条路径。"}),isError:true};
  }
});

server.registerTool(
  "cs2_prefab_info",
  {
    title: "Prefab dimensions and placement rules",
    description:
      "Footprint (lot in 8 m cells and meters), size, placement flags (e.g. Shoreline, RoadSide), placement " +
      "offset and cost of a building prefab, or the width and layers of a network prefab. Use before placing " +
      "large buildings such as harbors, stations and cargo terminals. Includes native utility capacity/voltage/water data " +
      "and prefab assembly; actual generated road utilities must also be checked through inspect.",
    inputSchema: {
      name: z.string().describe("Exact prefab name (cs2_find_prefabs)"),
    },
  },
  async ({ name }) => {
    const params = new URLSearchParams({ name });
    try {
      return jsonResult(await bridgeJson(`/prefabs/info?${bridgeQueryString(params)}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_buildings_near",
  {
    title: "Buildings near a point",
    description:
      "Buildings around a point, nearest first, with kind (residential/commercial/office/industrial/park/service), " +
      "footprint, position and the direction their front faces. Use to find free spots or the building to clear " +
      "before placing a station or depot.",
    inputSchema: {
      x: z.number().describe("Center X"),
      z: z.number().describe("Center Z"),
      radius: z.number().min(1).max(1000).optional().describe("Radius in meters (default 100)"),
      limit: z.number().int().min(1).max(300).optional().describe("Max results (default 40)"),
      query: z.string().optional().describe("Only prefabs whose name contains this"),
    },
  },
  async ({ x, z: zCoord, radius, limit, query }) => {
    const params = new URLSearchParams({ x: String(x), z: String(zCoord) });
    if (radius !== undefined) params.set("radius", String(radius));
    if (limit) params.set("limit", String(limit));
    if (query) params.set("query", query);
    try {
      return jsonResult(await bridgeJson(`/city/buildings/near?${bridgeQueryString(params)}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_place_roadside",
  {
    title: "Place a building against a road",
    description:
      "Place a road-side building (station, depot, service building...) flush against a road segment, facing it: " +
      "at curve position t (0-1) along the segment, on its left or right side as seen driving from the segment's " +
      "start to its end. Position and rotation come from the road's geometry and the building's lot, so the front " +
      "touches the road edge. The game validates the placement; dryRun=true only reports it.",
    inputSchema: {
      prefab: z.string().describe("Exact building prefab name"),
      road: z.object({ index: z.number().int(), version: z.number().int() }).describe("Road segment (cs2_road_graph)"),
      side: z.enum(["left", "right"]).describe("Side of the road, as seen from the segment's start"),
      t: z.number().min(0).max(1).optional().describe("Position along the segment (default 0.5)"),
      gap: z.number().min(0).max(50).optional().describe("Extra distance from the road edge in meters (default 0)"),
      dryRun: z.boolean().optional().describe("Only compute the position and rotation"),
      force: z.literal(false).optional().describe("正常发展规则只接受 false；不能绕过解锁或碰撞"),
    },
  },
  async ({ prefab, road, side, t, gap, dryRun, force }) => {
    const params = new URLSearchParams({ prefab, road: `${road.index}:${road.version}`, side });
    if (t !== undefined) params.set("t", String(t));
    if (gap !== undefined) params.set("gap", String(gap));
    if (dryRun) params.set("dryRun", "true");
    if (force) params.set("force", "true");
    try {
      return jsonResult(await bridgeJson(`/build/place/roadside?${bridgeQueryString(params)}`, 15_000));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_place_shoreline",
  {
    title: "Place a building on the shore",
    description:
      "Place a building that must stand on the water's edge (cargo/passenger harbors, water pumps, outlets...) at " +
      "the shoreline nearest a point, positioned and turned to face the land exactly like the game's placement " +
      "tool snaps it (the game then validates it). dryRun=true only reports the computed position and rotation. " +
      "Costs money like a player build.",
    inputSchema: {
      prefab: z.string().describe("Exact building prefab name"),
      x: z.number().describe("X of a point on the water's edge"),
      z: z.number().describe("Z of a point on the water's edge"),
      dryRun: z.boolean().optional().describe("Only compute the position and rotation"),
      force: z.literal(false).optional().describe("正常发展规则只接受 false；不能绕过解锁或碰撞"),
    },
  },
  async ({ prefab, x, z: zCoord, dryRun, force }) => {
    const params = new URLSearchParams({ prefab, x: String(x), z: String(zCoord) });
    if (dryRun) params.set("dryRun", "true");
    if (force) params.set("force", "true");
    try {
      return jsonResult(await bridgeJson(`/build/place/shoreline?${bridgeQueryString(params)}`, 15_000));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_replace_road",
  {
    title: "Replace road type",
    description:
      "Change the road type of existing segments in place, like the road tool's Replace mode: e.g. widen a " +
      "highway (Highway Oneway - 2 lanes -> 3 lanes) or turn a street into a one-way one. The game regenerates " +
      "the segments, lanes and zone blocks and validates the result (a wider road can remove buildings it " +
      "overlaps). invert=true flips the drawing direction, which sets the direction of one-way roads. The game " +
      "updates segments in place, so they normally keep their ids; the replaced segments are returned by position.",
    inputSchema: {
      roads: z
        .array(z.object({ index: z.number().int(), version: z.number().int() }))
        .min(1)
        .max(50)
        .describe("Road segments to replace (from cs2_list_roads or cs2_traffic)"),
      prefab: z.string().describe("New road prefab name (cs2_find_prefabs category road)"),
      invert: z.boolean().optional().describe("Flip the drawing direction (one-way roads then run the other way)"),
      force: z.literal(false).optional().describe("正常发展规则只接受 false；不能绕过解锁或碰撞"),
    },
  },
  async ({ roads, prefab, invert, force }) => {
    const params = new URLSearchParams({
      roads: roads.map((r) => `${r.index}:${r.version}`).join(";"),
      prefab,
    });
    if (invert !== undefined) params.set("invert", String(invert));
    if (force) params.set("force", "true");
    try {
      return jsonResult(await bridgeJson(`/build/road/replace?${bridgeQueryString(params)}`, 15_000));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_line_policies",
  {
    title: "Transit line policies",
    description:
      "Policies available for one transit line (ticket price, vehicle count and similar options from the line " +
      "panel) with active state, current adjustment and slider range.",
    inputSchema: {
      index: z.number().int().describe("Line entity index (cs2_list_transit_lines)"),
      version: z.number().int().describe("Line entity version"),
    },
  },
  async ({ index, version }) => {
    const params = new URLSearchParams({ index: String(index), version: String(version) });
    try {
      return jsonResult(await bridgeJson(`/transit/lines/policies?${bridgeQueryString(params)}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_set_line_policy",
  {
    title: "Set transit line policy",
    description:
      "Activate/deactivate a policy on one transit line (name from cs2_line_policies), e.g. the ticket price or " +
      "vehicle count slider, the same way the line panel does.",
    inputSchema: {
      index: z.number().int().describe("Line entity index"),
      version: z.number().int().describe("Line entity version"),
      name: z.string().describe("Policy internal name"),
      active: z.boolean().describe("true to activate"),
      adjustment: z.number().optional().describe("Slider value for slider policies"),
    },
  },
  async ({ index, version, name, active, adjustment }) => {
    const params = new URLSearchParams({
      index: String(index),
      version: String(version),
      name,
      active: String(active),
    });
    if (adjustment !== undefined) params.set("adjustment", String(adjustment));
    try {
      return jsonResult(await bridgeJson(`/transit/lines/policies/set?${bridgeQueryString(params)}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_set_turn_restrictions",
  {
    title: "Set native approach turn signs",
    description: "Set native turn restrictions for one incoming road approach at its endpoint junction. " +
      "Preserves other upgrades and the opposite endpoint. Save first; uses normal validated tool pipeline. " +
      "Inspect junction car lanes/path owners afterward; native signs may retain Unsafe emergency paths. " +
      "For right-in/right-out, assess the branch and relevant arterial approaches together, not only traffic lights.",
    inputSchema: {
      road: z.string().regex(/^\d+:\d+$/).describe("Road edge index:version"),
      node: z.string().regex(/^\d+:\d+$/).describe("Connected junction node index:version"),
      forbidLeft: z.boolean(), forbidRight: z.boolean(), forbidStraight: z.boolean(),
    },
  },
  async (args) => {
    if (args.forbidLeft && args.forbidRight && args.forbidStraight)
      return errorResult(new Error("Cannot forbid all approach movements"));
    const params = new URLSearchParams(Object.entries(args).map(([key, value]) => [key, String(value)]));
    try { return jsonResult(await bridgeJson(`/build/turn-restrictions?${bridgeQueryString(params)}`, 15_000)); }
    catch (err) { return errorResult(err); }
  },
);

server.registerTool(
  "cs2_set_junction_control",
  {
    title: "Set junction traffic control",
    description:
      "Set how one road junction (node id from cs2_road_graph) is controlled, like the game's intersection " +
      "tools: lights = traffic lights, nolights = remove traffic lights, stop = all-way stop, default = the " +
      "game's automatic choice. Needs a junction of 3 or more roads. Save first: this recreates the junction.",
    inputSchema: {
      index: z.number().int().describe("Junction (node) entity index"),
      version: z.number().int().describe("Junction (node) entity version"),
      mode: z.enum(["lights", "nolights", "stop", "default"]).describe("Control to apply"),
    },
  },
  async ({ index, version, mode }) => {
    const params = new URLSearchParams({ index: String(index), version: String(version), mode });
    try {
      return jsonResult(await bridgeJson(`/build/junction?${bridgeQueryString(params)}`, 15_000));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_line_vehicles",
  {
    title: "Transit line vehicle models",
    description:
      "Vehicle models a transit line can use (the line panel's vehicle selection): the models currently chosen " +
      "and every candidate for the line's transport type with passenger capacity, role (engine, carriage, " +
      "multipleUnit or vehicle), max speed and the carriages an engine pulls.",
    inputSchema: {
      index: z.number().int().describe("Line entity index (cs2_list_transit_lines)"),
      version: z.number().int().describe("Line entity version"),
    },
  },
  async ({ index, version }) => {
    const params = new URLSearchParams({ index: String(index), version: String(version) });
    try {
      return jsonResult(await bridgeJson(`/transit/lines/vehicles?${bridgeQueryString(params)}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

server.registerTool(
  "cs2_set_line_vehicles",
  {
    title: "Set transit line vehicle model",
    description:
      "Choose the vehicle model new vehicles on a transit line use, as the line panel's vehicle selection does: " +
      "primary = engine or single vehicle, secondary = carriage (names from cs2_line_vehicles). clear=true goes " +
      "back to a random model per vehicle. Vehicles already running keep their model.",
    inputSchema: {
      index: z.number().int().describe("Line entity index"),
      version: z.number().int().describe("Line entity version"),
      primary: z.string().optional().describe("Engine or single-vehicle model name"),
      secondary: z.string().optional().describe("Carriage model name (trains)"),
      clear: z.boolean().optional().describe("Remove the choice (random model per vehicle)"),
      force: z.literal(false).optional().describe("正常发展规则只接受 false；不能绕过解锁或碰撞"),
    },
  },
  async ({ index, version, primary, secondary, clear, force }) => {
    const params = new URLSearchParams({ index: String(index), version: String(version) });
    if (primary !== undefined) params.set("primary", primary);
    if (secondary !== undefined) params.set("secondary", secondary);
    if (clear !== undefined) params.set("clear", String(clear));
    if (force !== undefined) params.set("force", String(force));
    try {
      return jsonResult(await bridgeJson(`/transit/lines/vehicles/set?${bridgeQueryString(params)}`));
    } catch (err) {
      return errorResult(err);
    }
  },
);

const transport = new StdioServerTransport();
await server.connect(transport);
console.error(`cs2-mcp 0.10.2 running on stdio (bridge: ${BRIDGE_URL})`);
