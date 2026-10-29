# HTTP桥接与原生生命周期

0.9.1继承官方`4b6ea95`的Host、Origin、POST和可选token防护。HTTP只监听IPv4 loopback；Host必须为实际端口的127.0.0.1或localhost。带Origin的请求在入队前拒绝，包括空Origin。`/ping`保留加载中的离线程读取，其他路径在配置非空白`CS2MCP_TOKEN`时要求匹配的`X-CS2MCP-Token`。服务端与SDK各自从已有进程环境读取同一配置；本改动不设置系统环境或写出秘密。

共享客户端`bridge-client.ts`承担上游的传输选择与token头。全部build/sim路径、设置/购买/保存/控制/创建/删除/放置及本地模组写路径使用POST，保留既有body、Content-Type、超时和禁止重定向。C#的`IsMutatingPath`与TS的`requiresPost`使用同一组行为样例验收。正常预览也走POST；实际应用风险仍由独立的`isMutation`及结果阶段判断，不能因为POST便把预览说成已施工。

合并保留本贡献的45秒原生保存等待、普通请求10秒预算、停止同步门、尚未开始请求的结构化失败，以及原生取景结果领取的token保护。超时不取消已开始的原生保存或施工，可能已应用时禁止自动重发。单次构建和隔离HTTP测试不等于已部署，也不等于真实城市施工/保存验收。
