import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { bridgeJson, jsonResult, errorResult } from './bridge-client.js';

export function registerInterchangeTools(server: McpServer, requireVersion: () => Promise<void>) {
  server.registerTool('cs2_interchange_info', {
    title: '读取原生立交资产的逐段结构',
    description: '读取AssetStamp的真实道路预制体、宽度、三维贝塞尔曲线、共享节点、吸附、方向翻转和道路升级。局部坐标用于独立复刻与改造；分页完整读取后仍须实际生成车道与运营验收。需要桥接0.10.2。',
    annotations: {readOnlyHint:true,destructiveHint:false,openWorldHint:false},
    inputSchema: {name:z.string().min(1),offset:z.number().int().min(0).max(100000).default(0),limit:z.number().int().min(1).max(250).default(100)},
  }, async(args) => {
    try { await requireVersion(); return jsonResult(await bridgeJson(`/prefabs/interchange?${new URLSearchParams(Object.entries(args).map(([k,v])=>[k,String(v)]))}`)); }
    catch(error) { return errorResult(error); }
  });
  server.registerTool('cs2_place_interchange', {
    title: '正常预览或放置原生复合立交',
    description: '使用游戏AssetStamp/ObjectTool原生生成、Validation和正常收费，不绕过解锁、碰撞或用地。默认仅预览，显式dryRun=false才施工；返回应用不代表连接或通车完成。需要桥接0.10.2。',
    annotations: {readOnlyHint:false,destructiveHint:true,openWorldHint:false},
    inputSchema: {prefab:z.string().min(1),x:z.number().finite().min(-7168).max(7168),z:z.number().finite().min(-7168).max(7168),rotation:z.number().finite().default(0),elevation:z.number().finite().min(-30).max(60).default(0),dryRun:z.boolean().default(true)},
  }, async(args) => {
    try { await requireVersion(); return jsonResult(await bridgeJson(`/build/interchange/place?${new URLSearchParams(Object.entries(args).map(([k,v])=>[k,String(v)]))}`,30000,undefined,{method:"POST"})); }
    catch(error) { return errorResult(error); }
  });
}

const layoutPoint = z.object({x:z.number().finite().min(-10000).max(10000),y:z.number().finite(),z:z.number().finite().min(-10000).max(10000)}).strict();
export function registerRoadLayoutRemovalTool(server:McpServer, requireVersion:()=>Promise<void>) {
  server.registerTool('cs2_remove_road_layout',{
    title:'整组正常预览及拆除独立道路',
    description:'完整身份逐条验证后统一走原生拆除预览、校验、退款及应用，避免逐段拆除时自动合并导致后续编号过期。默认dryRun=true；仅独立可见道路，不能选择建筑内部网络或重复实体。需桥接0.10.2；操作后读实际道路与资金。',
    annotations:{readOnlyHint:false,destructiveHint:true,openWorldHint:false},
    inputSchema:{roads:z.array(z.object({index:z.number().int().positive(),version:z.number().int().positive()}).strict()).min(1).max(250),dryRun:z.boolean().default(true)},
  },async(args)=>{
    try {
      if(new Set(args.roads.map(r=>`${r.index}:${r.version}`)).size!==args.roads.length)throw new Error('重复道路，整批未发送。');
      await requireVersion();
      return jsonResult(await bridgeJson('/build/road-layout/remove',30000,undefined,{method:'POST',headers:{'Content-Type':'application/json; charset=utf-8'},body:JSON.stringify(args)}));
    }catch(error){return errorResult(error);}
  });
}
const layoutPart = z.object({prefab:z.string().min(1),curve:z.object({a:layoutPoint,b:layoutPoint,c:layoutPoint,d:layoutPoint}).strict(),
  nodes:z.object({start:z.number().int().min(-1).max(4096),end:z.number().int().min(-1).max(4096)}).strict(),
  parentMesh:z.object({start:z.number().int().min(-1).max(0),end:z.number().int().min(-1).max(0)}).strict().optional(),
  upgrades:z.object({general:z.string().max(256),left:z.string().max(256),right:z.string().max(256)}).strict().optional()}).strict();
export function registerRoadLayoutTool(server:McpServer, requireVersion:()=>Promise<void>, requireProfileVersion:()=>Promise<void>) {
  server.registerTool('cs2_build_road_layout',{
    title:'以原生道路、轨道或步道定义重建完整布局',
    description:'明确指定正常可见道路、轨道或步道、三维曲线、共享节点、高程固定方式和升级，走原生统一预览、Validation和正常收费。轨道/步道和明示纵断面需0.10.5；管线和航道不在此入口。曲线y相对原点，实际世界高程范围含共享节点。默认只预览；此贡献不含任意网络替换或replaceRoads参数，避免重复待审PR4职责。不改源资产、不依赖不可见母体。',
    annotations:{readOnlyHint:false,destructiveHint:true,openWorldHint:false},
    inputSchema:z.object({x:z.number().finite().min(-7168).max(7168),z:z.number().finite().min(-7168).max(7168),rotation:z.number().finite().default(0),
      elevation:z.number().finite().min(-30).max(60).default(0),dryRun:z.boolean().default(true),segments:z.array(layoutPart).min(1).max(250),
      preserveVerticalProfile:z.boolean().optional().describe('0.10.5：原生拆分后保留明示曲线纵断面；仅本次定义，保留正常碰撞/解锁/用地/收费；无法对应或既有节点高度不符即拒绝。')}).strict(),
  },async(args)=>{
    try {
      if(args.preserveVerticalProfile===true)await requireProfileVersion();
      else await requireVersion();
      const body=JSON.stringify(args);
      if(Buffer.byteLength(body,'utf8')>512000)throw new Error('布局参数超过512KB，未发送施工请求。');
      return jsonResult(await bridgeJson('/build/road-layout',30000,undefined,{method:'POST',headers:{'Content-Type':'application/json; charset=utf-8'},body}));
    }catch(error){return errorResult(error);}
  });
}
