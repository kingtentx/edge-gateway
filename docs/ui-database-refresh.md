# 数据库页面定时刷新抖动修复

日期：2026-10-27。

## 原因与修复

数据库页每秒刷新时，先由 `BindDatabaseViews` 把状态说明设置为一行，再等待缓冲统计并追加第二行。状态标签位于 AutoSize 行，导致页签和表格先上移、后下移。两个数据库表格同时无条件重新指定 DataSource，即使空列表或内容完全相同，也会重新绑定和绘制。

- 先取得缓冲统计，再组装说明、统计和异常信息，一次更新完整文本；保存最新统计供配置变更后的页面重绑复用。内容相同时不触发 TextChanged。
- 数据库连接和写入组各使用 BindingList。稳定 ID 和顺序未变时，仅替换值实际变化的行；完全相同的快照不发绑定通知。新增、删除或顺序变化时才替换数据源，并通过既有 GridViewState 保持选择、当前列及滚动位置。
- 固定界面仍由 Designer 维护，本次没有改变表单结构、应用契约或数据库写入逻辑。

## 本轮验证

沿用用户授权的 Release 编译及隔离后台定向 UI 修复验证范围，只执行数据库页专项，不执行全量核心回归、八页面全套重跑、完整 DPI 矩阵或外库连接。

新增 `DatabaseRefresh` 入口复用 `scripts/smoke-test-winforms-background.ps1` 的隔离桌面。它实际执行页面刷新和一秒定时器，观察状态文字、页签/表格边界及数据源变化，核对选择和滚动位置；数据夹具为禁用的数据库连接和写入组，不访问外部数据库。

- 修复前：Release 编译通过；专项在空页面复现失败。5 次刷新均发生两个表格重绑、状态文字两次变化和中间布局移动，原始失败保留在 `artifacts/database-refresh-20261027/before`。
- 修复后：Release 编译通过，0 错误、1 条既有 `GatewayWorkerClient.cs:283` CS8602 警告。日志 `artifacts/database-refresh-20261027/build-1.log`。首次新输出目录构建另有迁移驱动的既有告警，完整日志为 `build-before.log`。
- `DatabaseRefresh` 通过，应用及脚本退出码均为 0，stderr 为空。产物 `artifacts/database-refresh-20261027/after-1`，包含空页面、40 条连接和 40 条写入组的三张截图。实际 192 DPI，`ProcessWindowVerified=true`、`InputDesktopSwitch=false`。
- 无变化刷新不再触发状态文字变化、表格重绑或边界移动；有变化的行可在原数据源中更新，删除后两个表格正确显示 39 行；保持当前行、列及水平/垂直阅读位置。
- 本轮没有重新验收现场数据库、完整 DPI 矩阵或 Visual Studio 设计器往返；不沿用历史验收结果作为本次通过证据。截图与事件/边界观察属于隔离后台运行验证，不能替代用户实际桌面的长时间使用体验。

本机修复版程序：`artifacts/database-refresh-20261027/app/EdgeGateway.WinForms.exe`。
