# CS2MCP — Cities: Skylines II 的 MCP 服务器

[English](README.md) | **简体中文**

让 Claude 等 MCP 客户端**完全操作**正在运行的《城市：天际线 2》：读取城市数据、控制相机与截图、建设道路建筑、划区、管理财政政策、控制模拟时间。

## 架构

```
Claude Code / Claude Desktop（任意 MCP 客户端）
      │  MCP (stdio)
      ▼
cs2-mcp  (mcp-server/，Node.js 进程)
      │  HTTP，仅本机 127.0.0.1:8642
      ▼
CS2MCP 桥接 Mod  (CS2MCP.Bridge/，游戏进程内)
      │  ECS 查询与写入均在模拟主线程执行
      ▼
Cities: Skylines II
```

- **游戏内 Mod**（C#）：在游戏进程里用 `TcpListener` 起一个仅监听 127.0.0.1 的极简 HTTP 服务。请求由监听线程排队，在注册于 `SystemUpdatePhase.UIUpdate` 的 ECS System 中于模拟主线程执行（暂停时也可用）。建设操作通过自定义 `ToolBaseSystem` 走游戏原生的定义/校验/提交管线，拆除走推土机管线——不做任何绕过游戏校验的直接实体修改。
- **MCP Server**（TypeScript）：把 MCP 工具调用翻译为对桥接 Mod 的 HTTP 请求，stdio 传输。

## 环境要求

- Windows + Steam 版《城市：天际线 2》
- .NET SDK 8.0+（编译 Mod）
- Node.js 18+（运行 MCP server）

## 构建与安装

```powershell
# 1. 编译游戏内 Mod（构建后自动部署到游戏的本地 Mods 目录）
dotnet build CS2MCP.Bridge\CS2MCP.Bridge.csproj

# 2. 构建 MCP server
cd mcp-server
npm install
npm run build
```

Mod 会部署到 `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\CS2MCP\`，游戏启动时自动加载，无需发布到 Paradox Mods。想临时禁用：把该文件夹改名为 `.CS2MCP`。

### 游戏路径配置

编译 Mod 需要引用游戏目录下的程序集。查找顺序（先命中先用）：

1. 命令行参数：`dotnet build -p:GamePath="X:\...\Cities Skylines II"`
2. 环境变量 **`CS2_PATH`**
3. 环境变量 `CSII_INSTALLATIONPATH`（官方 Modding 工具链设置）
4. 常见 Steam 库位置自动探测（`C:\Program Files (x86)\Steam`、各盘符 `Steam` / `SteamLibrary`）

找不到游戏时构建会报出明确错误。设置环境变量示例：

```powershell
setx CS2_PATH "D:\Steam\steamapps\common\Cities Skylines II"
```

### 运行时环境变量（支持 .env）

MCP server 通过 [dotenv](https://github.com/motdotla/dotenv) 读取 `mcp-server/.env`（参考 `.env.example`）：

| 变量 | 作用域 | 默认值 | 说明 |
|---|---|---|---|
| `CS2_BRIDGE_URL` | MCP server | `http://127.0.0.1:8642` | 桥接 Mod 的地址 |
| `CS2MCP_PORT` | 游戏进程 | `8642` | 桥接 Mod 的监听端口（改它需同步改 `CS2_BRIDGE_URL`） |
| `CS2MCP_TOKEN` | 游戏进程**与** MCP server | *（未设置）* | 可选共享密钥，以 `X-CS2MCP-Token` 请求头发送；给游戏进程设置后，除 `/ping` 外所有路由都要求携带。此外无论是否设置，带 `Origin` 头或 `Host` 非回环地址的请求一律拒绝，改变状态的路由必须用 POST |
| `CS2_PATH` | 构建期 | 自动探测 | 游戏安装目录 |

## 在 Claude 中使用

**Claude Code**：仓库根目录的 [.mcp.json](.mcp.json) 已注册 `cs2` 服务器，在项目目录里打开 Claude Code 即可（首次会询问是否信任）。在其他目录手动注册：

```powershell
claude mcp add cs2 -- node <仓库路径>\mcp-server\dist\index.js
```

**Claude Desktop**（`claude_desktop_config.json`）：

```json
{
  "mcpServers": {
    "cs2": {
      "command": "node",
      "args": ["<仓库路径>\\mcp-server\\dist\\index.js"]
    }
  }
}
```

启动游戏、读取存档，然后问 Claude："看看我的城市财政状况"、"在河边划一片住宅区"、"修一条路把工业区接上高速"。

## 工具总览（65 个）

**状态与画面**

| 工具 | 说明 |
|---|---|
| `cs2_ping` | 桥存活、Mod 版本、游戏模式（读档中也能响应） |
| `cs2_game_state` | 模式、城市名、暂停/速度、游戏内日期时间 |
| `cs2_city_overview` | 人口、幸福度、健康度、资金、XP、日期 |
| `cs2_screenshot` | 当前画面 PNG（帧末采集，默认缩至 1280 宽） |
| `cs2_get_camera` / `cs2_set_camera` | 相机读写（观察点/角度/距离），配合截图 = AI 自主取景 |

**城市数据**

| 工具 | 说明 |
|---|---|
| `cs2_demand` | RCI 需求 + 需求因子（内部 0-255 刻度，暂停时不刷新） |
| `cs2_budget` | 收支明细：14 项收入 / 15 项支出来源 |
| `cs2_city_services` | 电力、水务、垃圾状态 |
| `cs2_labor` | 就业、失业率、按教育等级的岗位供需、年龄结构 |
| `cs2_statistics` | 60+ 种统计项历史曲线（每游戏日 32 采样） |
| `cs2_terrain` | 全图高度 + 水体栅格 |
| `cs2_gridmap` | 地价/地面污染/空气污染/噪音/地下水等 6 层原生栅格 |
| `cs2_zoning` | 区划现状汇总（占用/空置） |
| `cs2_notifications` | 全城警告图标（缺电缺水/弃置等）+ 目标实体 |
| `cs2_traffic` | 全城交通流量、最堵道路（流量百分比、车流量、每日四个时段）与交通瓶颈，数字取自游戏本身 |
| `cs2_vehicles` | 行驶中车辆按类型与目的地统计、停下的车在等什么、堵住车流的队首车辆及死锁环 |
| `cs2_road_graph` | 区域内路段及其路口、方向、流量与红绿灯（可选包含所有网络，含车站轨道） |
| `cs2_inspect` | 单实体详情（住户/雇员/状态标志） |

**建设**

| 工具 | 说明 |
|---|---|
| `cs2_find_prefabs` | 按名称搜索建筑/道路/网络/树木预设 |
| `cs2_place_building` | 放置建筑/树木（地形高度自动采样、旋转可调、原生校验） |
| `cs2_build_road` | 任意网络段：直线 / `cx,cz` 曲线 / `e1,e2` 高程（高架桥、匝道） |
| `cs2_upgrade_road` | 道路升级：草地/树/宽人行道/隔音屏/停车位/路灯/中央绿化 |
| `cs2_set_junction_control` | 把路口设为红绿灯、无灯、全向停车或游戏默认（即路口工具的节点升级） |
| `cs2_replace_road` | 原地更换已有路段的道路类型（道路工具的替换模式）：拓宽高速、改为单行道 |
| `cs2_connect_road` | 修建两端吸附到已有路口或切分已有路段的道路/轨道（同道路工具的吸附），可抬高或下沉 |
| `cs2_prefab_info` | 建筑的占地、尺寸、放置规则与造价，或网络的宽度与层级 |
| `cs2_place_shoreline` | 在水岸放置港口、水泵等临水建筑，按游戏放置工具的方式吸附 |
| `cs2_place_roadside` | 把车站、车库或服务建筑紧贴路段放置并朝向道路 |
| `cs2_buildings_near` | 某点周围的建筑（类型、占地、朝向），用于找空位或确定要清理的对象 |
| `cs2_zone_area` / `cs2_list_zones` | 划区与区划类型（`None` 清除） |
| `cs2_demolish` | 拆除建筑/路段/树木/地区（推土机管线） |
| `cs2_list_buildings` / `cs2_list_roads` / `cs2_list_objects` | 实体清单（ID、坐标） |

**财政与政策**

| 工具 | 说明 |
|---|---|
| `cs2_get_taxes` / `cs2_set_tax` | 四大区类税率（钳制在游戏允许范围） |
| `cs2_policies` / `cs2_set_policy` | 城市政策（含本地化名称） |
| `cs2_service_budgets` / `cs2_set_service_budget` | 服务预算滑条 50-150% |
| `cs2_get_fees` / `cs2_set_fee` | 水电/医疗/教育等服务收费 |
| `cs2_get_loan` / `cs2_set_loan` | 贷款借还 |
| `cs2_list_districts` / `cs2_create_district` | 地区列表 / 多边形画地区 |
| `cs2_district_policies` / `cs2_set_district_policy` | 地区政策 |
| `cs2_tiles_info` | 地块持有/维护费信息 |
| `cs2_list_map_tiles` / `cs2_buy_map_tiles` | 列出地块（归属、自然资源、剩余许可数）/ 通过游戏自身购买逻辑买地（价格、许可与资金由游戏校验） |

**公共交通**

| 工具 | 说明 |
|---|---|
| `cs2_list_transit_stops` | 站点的类型、名称、位置、所属车站、所依附道路及经停线路；`airplane` 列出机场登机口（客运或货运）与地图的航空外部连接 |
| `cs2_place_transit_stop` | 通过物件工具管线放置路边公交/有轨电车站（由游戏依附、吸附并校验） |
| `cs2_create_transit_line` | 经由有序站点（站点 ID、车站建筑或吸附到站点的坐标）通过路线工具管线建线；等待游戏寻路完成，不可达的路段会被拒绝 |
| `cs2_list_transit_lines` | 线路及交通面板上的数字（站数、车辆数、车上乘客、使用率、长度、时刻），可选每站候车人数 |
| `cs2_delete_transit_line` | 与游戏自带删除按钮完全一致地删除线路 |
| `cs2_line_policies` / `cs2_set_line_policy` | 线路政策（票价、车辆数等），按线路面板的方式设置 |
| `cs2_line_vehicles` / `cs2_set_line_vehicles` | 线路可用车型及载客量，并像线路面板的车辆选择那样指定新车所用车型 |

**时间与元操作**

| 工具 | 说明 |
|---|---|
| `cs2_set_simulation` | 暂停/调速（0-8） |
| `cs2_run_simulation` | 定时快进：跑 N 游戏小时后自动暂停 |
| `cs2_save_game` | 触发存档（建议 AI 大规模操作前调用） |

## 排障

- **`cs2_ping` 连不上**：Mod 未加载。查 `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\CS2MCP.log`（正常应有 `bridge listening on ...`）；无此文件则查同目录 `Player.log`。
- **409 no city loaded**：还在主菜单，先读档。
- **锁定状态显示异常**：读档后先短暂解除暂停一次（相关接口在未跑过模拟时会返回 `stalenessWarning`）。
- **端口冲突**：给游戏进程设 `CS2MCP_PORT`，并同步修改 `.env` 中的 `CS2_BRIDGE_URL`。

## 已知限制 / 路线图

- 地形改造尚未实现
- 公共交通：可放置路边公交/有轨电车站；火车/地铁站是车站建筑（用 `cs2_place_building` 放置），须用轨道连通。只有存在同类型车库且模拟在运行时，线路才会有车辆
- 匝道只能接在路段端点（节点），尚不支持路中段平滑汇入
- 截图为游戏当前渲染画面：道路类工具面板开启时游戏会以白色轮廓模式渲染

## 免责与致谢

- 本项目为非官方社区 Mod，与 Colossal Order / Paradox Interactive 无关。
- `CS2MCP.Bridge/CreateDefinitions.cs` 移植自 [LineTool-CS2](https://github.com/algernon-A/LineTool-CS2)（Apache-2.0，© algernon），其中含有源自游戏反编译代码的部分，适用 Paradox 用户协议。
- 感谢 CS2 modding 社区的先行者们：LineTool、InfoLoom、Traffic、unity-mcp 等项目的公开代码是本项目的重要参考。

## License

[Apache-2.0](LICENSE)
