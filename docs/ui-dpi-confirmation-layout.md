# DPI 布局与确认按钮顺序（2026-10-29）

## 本轮修改

- 设备、实时、历史与两类日志查询的 22 个筛选字段使用 `FilterFieldPanel` 原生布局宿主。保留 Designer 中创建的标签、输入及明确列宽，只在缩放时按原声明宽度计算绝对列，避免使用临时挤压后的末列宽度作为下一次缩放基准。列宽保留小数，避免 125%/150% 多次往返时反复取整而逐渐变窄。
- 原因依据：[.NET 8 TableLayoutPanel.ScaleAbsoluteStyles](https://github.com/dotnet/winforms/blob/v8.0.32/src/System.Windows.Forms/src/System/Windows/Forms/TableLayoutPanel.cs)。框架对最后一个绝对列使用已测量的单元格宽度；AutoSize 与换行中的中间布局可能与设定宽度不同。
- 公共 `GatewayButton` 在框架缩放尺寸、Padding、MinimumSize 的过程中暂停 AntdUI 自动尺寸，完成后恢复原自动尺寸模式，避免中间状态参与重算。
- `DpiAwareForm` 显式关闭 AntdUI 手动缩放，继续由 WinForms `AutoScaleMode.Dpi` 管理窗体。AntdUI 默认模式原本会跳过 Dpi 自动缩放窗体，此设置明确单一责任，不将其称为已证实的重复缩放根因。
- 统一 AntdUI Modal 的实际按钮排列为左“是”右“否”、左“确定”右“取消”；三按钮为“是 / 否 / 取消”。按官方按钮首选尺寸和 80 逻辑像素的最小宽度完成布局，关闭仅宽度自动尺寸，避免右停靠按钮在父容器定位后再变宽而越界。保留原事件、返回值、危险操作默认否、Esc 取消语义；按排列设置 Tab 顺序，并在官方排序方向键导航之前完成布局。
- 公共配置弹窗与写入弹窗在 Designer 内调整底部按钮顺序，确认在左、取消在右，仍整体靠右。

## 当前验证状态

用户已授权 Release 编译及本轮隔离后台定向检查，允许同范围修复重跑。产物保留于 `artifacts/ui-dpi-confirmation`。

- `build-1.log` 编译 0 错误、420 条既有警告；后续增量构建均 0 错误、1 条既有 `GatewayWorkerClient.cs:283` CS8602 警告。
- `run-1` 在 420 秒上限超时，保留已完成的页面及驱动编辑器截图，不计作通过。
- `run-2` 捕获官方 Modal 最小宽度与右停靠自动尺寸冲突造成的裁剪，已修复。
- `run-3` 至 `run-5` 捕获连续模态窗的隔离桌面激活时序问题。检查等待 Shown 的焦点工作完成，先断言 ActiveControl 是原默认按钮，再使用已有后台焦点恢复流程，并继续断言真实焦点、Tab/方向键及 Enter/Esc，未切换输入桌面。
- 可控通知向隔离测试窗发送 WM_DPICHANGED 及子控件前后通知，记录窗体目标 DPI、原生窗口 DPI、子控件 DPI、字体与列宽。实际系统 HWND 与子控件仍报告 192 DPI，因此这部分仅用于框架缩放路径和列宽往返检查，不能作为 96/120/144 DPI 的原生渲染或真实跨屏验收。它发现并促成了列宽小数保留修复。

- `run-6` 完整定向检查通过，83 张 PNG，退出码 0、stderr 为空。覆盖实际 192 DPI 的八页面、全部页签、宽窄往返，15 类驱动编辑器、77 个参数、65 个方法及动态参数切换，设备/点位/导入/通道/数据库/分组/写入弹窗的焦点和边界，配置与写入页脚确认在左；9 种确认动作覆盖左右顺序、Tab/方向键、默认否的 Enter 结果与 Esc 取消。人工查看设备页、设备编辑器和“是/否”弹框截图。
- 最终 `build-7.log` 编译 0 错误、1 条既有警告。相对 `run-6` 唯一后续生产代码变动为筛选列宽保留小数；针对这一变动的 `run-7` 通过，15 张 PNG，退出码 0、stderr 为空。重新核对上述 9 种确认动作，并执行 10 次 DPI 通知（96/120/144/192 往返），列宽误差小于 0.1 像素，最终设备筛选宽度为 480 / 250 / 290.00006 像素，无逐步缩窄。没有在最终取整修改后重复整套编辑器巡检。
- 两次通过的运行均记录 `ProcessWindowVerified=true`、`InputDesktopSwitch=false`。未执行全量核心、真实设备、系统缩放修改、真实多显示器往返或 Visual Studio 设计器逐窗往返验收。

可复用定向入口：`smoke-test-winforms-background.ps1 -Scenario DpiConfirmation`（含页面/编辑器巡检）与 `-Scenario DpiRoundTrip`（仅确认弹框及可控列宽往返）。后续运行仍须遵守仓库测试授权约定。
