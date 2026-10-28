# 按钮轮廓、设备启停颜色与日志页标题

更新日期：2026-10-28。

## 修改范围

- 普通按钮此前继承 AntdUI 的零宽度边框，在白色工具栏上不易与文字区分；其他页面共用该基类，也存在相同表现。`GatewayButton` 统一设置 `BorderWidth=1`，页面、弹窗和菜单触发按钮继承官方边框，仍由 AntdUI 处理主题与交互状态。
- 按用户本次明确要求，设备行内启动采用官方 Success 绿色，停止采用 Error 红色，悬停与按下采用相应 Hover / Active 令牌；禁用仍为灰色。保留 DataGridView 原生键盘、无障碍、稳定设备 ID 与启停权限判断。
- 日志页面由 `LoggingPageView.Designer.cs` 创建顶部“运行日志”标题及说明，位于日志设置、运行日志、采样数据三个页签上方；与其他页面统一标题字号、留白和层级。
- 不修改数据契约、驱动或采集行为。样式规范与开发 Skill 同步更新。

## 本轮验证

用户明确授权 Release 编译、隔离后台八页面按钮状态与日志标题定向检查，以及同范围修复重跑。结果来自本轮产物 `artifacts/button-appearance-20261028`：

- `dotnet build src/EdgeGateway.WinForms/EdgeGateway.WinForms.csproj -c Release --no-restore` 通过，0 错误、420 条既有驱动及代理代码警告，日志为 `build-1.log`。
- `scripts/smoke-test-winforms-background.ps1 -Scenario ButtonAppearance -OutputDirectory artifacts/button-appearance-20261028/ui-1 -TimeoutSeconds 600` 首轮通过，应用及脚本退出码 0，stderr 为空，40 张 PNG。
- 实际 `DeviceDpi=192`，窗口截图为 2826×1791 与 1946×1791；八页面及全部页面页签检查按钮公共边框、可达性、焦点和裁剪。日志三页签检查标题可见且不与页签重叠。
- 官方 MouseEnter 事件悬停、ExtraMouseDown 按下态渲染、禁用命令拒绝、真实 Space 键消息单次调用及设备行键盘/无障碍检查通过。隔离后台没有物理鼠标，事件级悬停不等同于人工鼠标验收。
- 两台隔离模拟设备分别保持停止、运行状态，检查按行启动和停止以及禁用状态。人工查看设备页绿色启动、红色停止、工具栏轮廓，以及日志设置、窄窗口采样页和平台页截图。
- `desktop.txt` 记录 `ProcessWindowVerified=true`、`InputDesktopSwitch=false`；使用隔离配置，仅 simulator 采集、MQTT 关闭，没有访问现场设备或外部数据库。

未执行全量核心测试、架构门禁、完整 DPI/跨屏矩阵、现场联调或 Visual Studio 设计器往返验收。源码中固定标题仍在 Designer 内，编译与运行通过不替代设计器打开/编辑/保存验收。

## 截图

- `ui-1/ui-smoke-buttons-default.png`：普通按钮边框和同时可见的绿色启动、红色停止。
- `ui-1/ui-smoke-buttons-hover.png`、`ui-smoke-buttons-pressed.png`、`ui-smoke-buttons-disabled.png`：官方按钮交互状态。
- `ui-1/ui-smoke-button-appearance-logs-LoggingSettingsTab-wide.png`：日志设置标题与固定底部按钮。
- `ui-1/ui-smoke-button-appearance-logs-SampleArchive-narrow.png`：窄窗口日志标题、页签和筛选工具栏。
