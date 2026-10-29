# 原生员工与到达证据

桥接0.9.1起，`cs2_inspect(index, version, employeeLimit, employeeOffset)`可选读取该雇主的原生`Game.Companies.Employee`缓冲。默认不展开员工，原有调用保持原状；新参数在旧桥接上明确拒绝，不静默省略。

- `employeeLimit`：整数0—64；0只读元数据。
- `employeeOffset`：整数0—65535；大于0时必须同时提供大于0的`employeeLimit`。
- `employeeRoster.available=false, count=null`：不存在原生员工缓冲，不表示零员工。
- 有缓冲时返回原生总数、当前页、原始偏移、`complete`和`nextOffset`。非零偏移只证明当前页，不声称完整名册。
- 每条保留员工序号、市民引用、存活资格、`Worker.m_Workplace`是否匹配被查询雇主，以及`CurrentBuilding`是否为雇主实际楼。公司楼由正常`PropertyRenter`关联读取；缺组件或楼身份未知返回null。
- 市民住处、工作及原始通勤计数复用既有市民只读信息。`TravelPurpose`、`CurrentTransport`及`TripNeeded`为原生状态；每个出行缓冲最多128条并保留完整性，不猜原始计数的时间单位。

本接口只读该页关联，不扫描全城市民，不产生路径、不改变工作/出行状态，也不调用任何游戏建造工具。调用前仍须核当前存档/GUID和完整实体身份。

`atEmployerBuilding=true`仅证明该次读取时市民位于雇主实际楼；员工数量、`lastCommuteTimeRaw`、待出行和当前交通对象都不能证明完成了指定天桥或公交路线。完整通勤验收仍需实际路径、交通方式、上下客/换乘和返回的独立证据。
