# 左侧菜单收缩与展开修复

日期：2026-10-27。

## 原因与修改

收缩按钮原有两个 Click 处理器，先修改菜单/品牌显示并立即布局，再调整侧栏列宽。隐藏 AutoSize 品牌区会同步进入嵌套布局，列宽样式虽然已变化，TableLayoutPanel 缓存的实际列宽却可能仍是展开宽度，出现只剩图标但左侧空白没有收起的情况。AntdUI 2.4.16 的 `Collapsed` 属性仅写状态；官方 `SetCollapsed` 才会同时重算菜单项布局。

现将收缩操作合并为一个处理器：暂停主布局和侧栏布局，调用官方 `SetCollapsed`，同步品牌、图标、无障碍名称与实际列宽，最后恢复布局。Designer 显式关闭 AntdUI 自动折叠，状态由收缩按钮统一维护。固定 Menu、MenuItem 和按钮继续在 Designer 中创建，没有改动业务配置。

## 本轮验证

沿用已授权的 Release 编译、隔离后台 UI 定向修复验证范围，新增 `Navigation` 专项；不执行全量核心回归或完整 DPI 矩阵。

- 修复前：自然点击流程在窄窗口复现。收缩状态为 true、目标列宽 112 像素，实际侧栏和内容容器仍为 344 像素，内容区起点也仍为 344。失败证据保留在 `artifacts/navigation-collapse-20261027/before`。
- 旧导航烟测在点击后手动调用递归布局，可能掩盖该问题。新专项只等待正常 UI 消息处理，不通过 PerformLayout/递归布局修正被测状态。
- 修复后 Release 编译通过，0 错误、1 条既有 `GatewayWorkerClient.cs:283` CS8602 警告，日志 `build-1.log`。首次新输出目录构建另有迁移驱动既有告警，保留 `build-before.log`。
- `Navigation` 通过，应用和脚本退出码均为 0，stderr 为空。覆盖普通宽窗口、980 逻辑像素窄窗口和实际最大化；每种窗口反复收缩/展开 3 轮、连续点击 11 次、收缩后切换八页面及一秒定时刷新。
- 核对菜单状态、品牌显示、列宽样式、实际侧栏/内容容器宽度及主内容起点一致；收缩后宽 56 逻辑像素且图标居中，展开宽度按当前窗口恢复为 172/192 逻辑像素；菜单文字、图标及按钮可达，设备搜索条件保留。
- 实际 192 DPI，`ProcessWindowVerified=true`、`InputDesktopSwitch=false`；6 张宽/窄/最大化的收缩与展开截图在 `after-1`。没有关闭或覆盖用户正在运行的 Debug 程序，应用修复需从托盘退出旧程序后重新编译启动。
- 未执行完整 DPI 矩阵、跨屏往返、现场设备联调或 Visual Studio 设计器往返，不沿用历史通过结果。

产物根目录：`artifacts/navigation-collapse-20261027`。隔离 Release 程序位于 `app/EdgeGateway.WinForms.exe`。
