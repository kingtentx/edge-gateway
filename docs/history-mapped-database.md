# 自定义逐点映射的历史查询

2026-10-31。历史页此前只筛选 PointRows，Application 和连接器也拒绝其他模式。因此已启用的 PointMapping 连接/写入组不会出现，页面误导为未配置数据库。

## 本次修复

- 下拉框列出所有已配置写入组，可查询项优先；保留稳定 ID 选择。连接未启用、写入组未启用、映射不完整分别说明，查询按钮按实际条件启用。
- PointRows 保持现有标准列和 JSON 值还原。
- PointMapping 依据值来源映射到实际列名，支持更名后的业务表列；必须保存设备标识、点位标识、采样时间、工程值和质量。没有工程值时可以使用数值来源。不能用字段名猜测来源，也不能把常量当作身份或时间。
- 设备/点位标识、质量必须是文本字段；采样时间必须是支持的时间字段。PostgreSQL 支持 timestamp with/without time zone，MySQL 支持 datetime，Oracle 支持 timestamp/date。MySQL timestamp 和 Oracle 带时区类型暂不开放此自定义查询，避免错误还原时区；标准表路径保持原有行为。
- 查询前只读检查表字段元数据，随后按实际列名引用并参数化筛选。最多 10000 条，时间边界包含起止，不创建或修改表。
- 自定义映射的工程值/原始值保留数据库读取类型，文本不当 JSON 解析；文本数值可以显示趋势。未映射的名称、地址、单位、错误保持空，原始值保持 null；无效身份、质量或时间明确拒绝，不伪造 Good 数据。
- 整行多点映射仍不支持单点历史，界面明确说明。查询失败不切换数据源；已导出/已显示快照不会因条件切换重标。

## 数据边界

只读确认当前本地配置的连接和写入组均启用，模式为 PointMapping，10 个标准名称字段的来源已正确设置。未修改用户配置、实际外库数据和缓冲。修复不会把此前错误写入的记录自动改正；外库写入失败期间可查询仍保留的采样日志。

## 验证

用户明确授权本次范围及同范围修复重跑。2026-11-01 执行结果：

- Release 构建通过，0 错误、420 项现有驱动/Worker 警告：`artifacts/history-mapping-build.log`，输出 `artifacts/history-mapping-runtime`。
- 20 项定向测试通过，0 失败/跳过：DatabaseHistoryMappingTests 与 ExternalDatabaseConnectorTests 中 History/MappedHistory 用例。覆盖标准 JSON 还原、更名字段查询、参数化筛选和条数、PostgreSQL 有/无时区绑定、缺列/错误类型、无效身份/质量/时间和普通文本/null。使用 DbConnection 测试替身，没有真实数据库连接。TRX：`artifacts/history-mapping-tests/history-mapping.trx`。
- 架构检查通过：8 个层间边界、24 个无环工程，记录 `artifacts/history-mapping-architecture.log`。
- 隔离后台 HistorySources 通过，实际 DeviceDpi=192，退出码 0，stderr 为空。覆盖本地日志趋势、坏质量断线、分页/CSV 快照、来源切换、逐点映射/标准组、缺少质量/未启用原因及选择保持。截图和隔离桌面记录位于 `artifacts/history-mapping-ui`；人工查看可用映射与缺少质量两张截图，控件和原因显示完整。
- 未执行全量、真实外库查询/写入或真实设备测试。未修改用户配置、数据表、记录和缓冲；未声明真实 PostgreSQL 端到端验收完成。
