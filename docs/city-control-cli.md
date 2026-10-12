# 显式城市上下文与批次控制

`mcp-server/agent-control.mjs`（包命令 `cs2-control`）通过同一 MCP SDK 操作游戏，提供工具目录、单次调用、健康快照、显式监控及批次执行。它不操作桌面，不猜默认城市、存档或记录目录。`--help` 不连接游戏，`--list` 只读取 MCP 工具目录。

复制 [example-context.json](../mcp-server/examples/example-context.json) 后填写当前城市资料。`cityId` 是自己的稳定记录标识，`cityName` 仅作显示核验；`manualSavePath` 和 `journalDirectory` 相对上下文文件解析。`loadedSaveGuid` 必须来自当前 `cs2_game_state` 的真实 LoadGame 身份，示例 GUID 不可直接使用；保存文件的新 GUID 不替代仍未重载的运行身份。只读调用可省略 `manualSaveName`，实际保存必须明确填写原生手动主档名且参数 `name` 完全相同，不能从文件名猜原生名称。

```powershell
node mcp-server/agent-control.mjs --help
node mcp-server/agent-control.mjs --list
node mcp-server/agent-control.mjs --context example-context.json --health
node mcp-server/agent-control.mjs --context example-context.json cs2_city_overview
node mcp-server/agent-control.mjs --context example-context.json --batch operations.json
```

工具参数必须是 JSON 文件；批次是非空 `{tool, arguments}` 数组。先验证整批形状、当前工具目录和完整参数 schema，之后每步重新核加载 GUID；同名城市、加载中、无身份或切城均停止。记录目录首次独占写入所有者，同一代号换主档或其他城复用目录被拒绝。

批次结果写在输入文件旁的 `.results.json`，已有结果绝不覆盖或重放。失败保留成功前缀并标明当前步骤是否未发送或结果未知；突然进程中断留下占位时，还须结合城市操作日志和实际现场核验。短暂 busy 只有桥接明确证明尚未入队才可重试，重试前重新核身份。超时、断线、部分应用、日志失败或未知结果不能重放整批。

单次调用收到工具回执后，若图片或日志写入失败，返回 `local_result_persistence`，保留工具文本结果及图片元数据，标明不可自动重发；不将本地IO失败解释为游戏未应用，不输出图片base64。

`--watch` 只有明确调用才持续运行，每60秒只读健康并写日志，失败逐步退避至最多300秒。没有自动预算、暂停、施工、税率或手动主档保存，也不安装定时任务；手动工具调用仍可按正常游戏前提执行。生产环境的经营干预阈值属于玩家策略，不作为公共CLI功能搬入；旧 `watchPolicy` 字段会明确拒绝，避免静默改变其原先含义。

监控开始和采集结束均核本次加载身份，切城立即停止。健康快照注明采集时间、模拟推进和结果完整性；不同加载 GUID 的快照不拼成同一模拟区间。幸福数据保留原生数值，不加个人报警阈值。派车、峰值、交通与设施运营仍需按现场核验。
