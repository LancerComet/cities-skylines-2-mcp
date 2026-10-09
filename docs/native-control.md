# 原生控制与验收

桥接和 MCP 服务统一版本为 0.10.0。必须成套构建、安装和运行；新增参数在发送前核桥接最低版本，防止旧桥接忽略参数后执行不同动作。完整入口见[工具目录](tool-reference.md)。

## 正常游戏边界

建设使用当前存档的资金、解锁、内容资格、可购土地和原生校验。`force=true`、独立不可见资产、成就授予、编辑器资源写入和任意反射调用不开放。既有 `force` 参数只接受 false，这改变了旧版允许绕过部分解锁的行为。Anarchy 的辅助显示与建造规则分开；启用错误放行、重复唯一建筑或扩展高程时，正常施工拒绝继续。

道路、建筑、扩建、搬迁、分区、地形与复合资产沿各自原生工具定义、预览、校验和应用流程操作。公共服务升级按原生 `m_ForbidMultiple` 及建筑类型判断是否允许重复，固定扩建不凭名称推断重复资格。目录中的容量、效率、员工、水电、维护和效果须与实际安装数量一起解释。

## 预览与真实结果

`cs2_place_prop`、`cs2_place_interchange`、`cs2_build_road_layout`、`cs2_remove_road_layout` 默认 `dryRun=true`；这些入口生成原生预览，返回能否应用、原生费用与错误后清除预览，不收费。

部分较早接口的 `dryRun` 只计算位置或参数，包括建筑搬迁、建筑升级、路边/岸线放置、连接道路、地形笔刷、采掘区域编辑和道路服务标志解析。它们的说明明确写出限制；不能把这类结果当作碰撞、收费、完整路径或运营验收。实际执行仍经过原生校验。

`cs2_road_path` 预先验证全部段的参数及要求的端点连续性，再逐段施工，没有整体事务或回滚。失败响应保留成功前缀、失败位置和未知结果标记。预览时前段没有施工，后段只能吸附当前已有现场。

原生应用接受后，回读实际实体、曲线、生成车道、端口、资金、已安装升级和处理结果。工具返回 accepted/requested 不证明交通通畅、车辆出车、乘客到达或服务容量已经满足。地形、水流和经营统计可能要等模拟推进后更新；原始计数、速度和费用单位按入口说明保留。

## 实体、加载与失败恢复

实体需要完整 `index` / `version`。同一存档重载也可能重新分配编号；保存不等于重载，`cs2_game_state.loadedSaveGuid` 记录当前实际加载身份。切城、加载中或重新加载后必须重新读取实体与场址，不重放旧施工参数。

MCP 传输错误返回 `mayHaveApplied` 和 `safeToRetry`。网络中断、超时、无效成功 JSON、服务端异常，以及地形多笔触部分应用都不能当作“尚未施工”。只有明确尚未入队的短暂 busy 响应允许重试；道路路径整批错误始终禁止自动重放。先检查现场，再决定新的动作。

施工路由在入队前读取 Windows 原生进程内存与提交预算：保留至少 2 GiB 或提交上限的 15%，且进程私有内存低于提交上限的 65%。计数器未知也拒绝施工，ping 仍提供诊断；保存和正常退出不受该门槛阻断。这是保守资源保护，不能证明内存泄漏已修复。

## 可选模组

通过已经加载的程序集和既有系统访问固定接口，不加载指定 DLL、不修改其他模组源码、不接受远程类型/方法/字段名。此版本按 Anarchy 1.8、Traffic 0.2.13、Traffic Light Manager 1.7.6、Road Builder 0.7、Network Tools 1.5.8、Node Controller 1.5.2 和 Better Bulldozer 1.3.19 的契约实现；未来接口或 UI 范围变化须重新验证。

Network Tools 与 Traffic 同时只保留一个原生会话。prepare 返回 operationId；读取 `cs2_mod_operation` 到预览就绪，再带相同 operationId 明确 apply 或 cancel。会话过期、被其他工具打断、重载或失败后先回读；Traffic 连接是原生 toggle，重放可能反转结果。结束时恢复原工具及临时校验选项。Move It 撤销历史及 Node Controller 原始几何写入不开放。

Traffic Light Manager 数值范围从原生 ModManager 登记的实际加载资产旁 UI 文件读取；文件/格式未知时拒绝数值写入。模板、部分配时变更及完整放行掩码先验证后调用原模组回调。Road Builder 只生成独立模板。Better Bulldozer 永久记录按所属建筑内预制体匹配，预览列出全部受影响装饰，经营、升级、道路和交通子元素受保护。

Skyve 诊断只读取标准用户数据目录或显式 `CS2_GAME_USER_DATA` 指定目录中的有限缓存/日志，敏感内容脱敏；缓存时间、缺失审查、读取失败与运行中程序集分别报告。没有订阅、登录、配组或缓存写入口。

## 构建与验证

默认构建不部署 DLL；游戏正常退出、备份旧版本后，才使用 `-p:DeployToLocalMods=true`。`-p:SkipDeploy=true` 仍可显式阻止部署。

```powershell
dotnet build CS2MCP.Bridge/CS2MCP.Bridge.csproj -p:SkipDeploy=true -p:GamePath="<game-install-directory>"
cd mcp-server
npm ci
npm test
```

无游戏依赖的 C# 诊断：

```powershell
dotnet run --project tests/Bridge.Diagnostics/Bridge.Diagnostics.csproj
```

诊断链接实际 HTTP、数值校验、内存预算及信号范围源码；游戏类型替身仅用于 HTTP ping，不模拟游戏行为。TypeScript 测试通过真实 MCP stdio 握手及独立 loopback 模拟桥接验证参数、版本拒绝、分页、预览和失败语义。测试均使用合成数据，不携带玩家存档或验收现场。

截图和保存预览仅读取 Unity 已渲染帧，不主动重复渲染相机。当前要求游戏有可核验的前台帧；隐藏/失焦时可能拒绝或等待，不能声称支持后台保存。保存响应只确认原生异步管线结束，调用方仍须核文件新鲜、完整性和备份。新增修正没有在此贡献分支上重启游戏验收；完整异步会话、搬迁、地形、多种资产布局和模组版本组合仍须实机验证。

HTTP 客户端保持无凭据 loopback HTTP origin、禁止重定向、变更/建造预览使用 POST，并支持可选 `CS2MCP_TOKEN`。本分支没有合入另行评审的服务端 Host/Origin/token 防护；上述客户端支持不能当作服务端已具备认证。
