# AntdUI 官方控件与交互修复

后续开发以 [AntdUI-Demo 样式规范](ui-antdui-style-guide.md) 为准；以下为当时实现与验证记录，不代表最新按钮样式的验收结果。

日期：2026-10-27。对应用户反馈：首页首次打开无法滚动、原生下拉样式、菜单收缩和页签样式。

## 实现

- 首页 `ResponsiveViewport` 避免刷新时重复设置相同内容边界，滚轮从图表/文字区域路由到指针所在视口。仅首页在 Designer 中启用滚轮转发，只处理同窗体、可见区域的滚轮消息，其他页面的表格继续自行处理滚轮；刷新保留位置，滚动条仍由 WinForms 管理。
- 固定导航替换为 `AntdUI.Menu`，八个 `MenuItem` 和底部收缩按钮在 MainForm Designer 创建。展开显示图标和文字，收缩为 56 逻辑像素的图标列；内容区同步扩展，切换页面保留当前收缩状态。
- 下拉、数值、日期、页签分别继承真正的 `AntdUI.Select`、`InputNumber`、`DatePicker`、`Tabs`，继续使用官方绘制、交互和弹层。文本、密码和复选框继续使用 AntdUI。不是原生 ComboBox/TabControl 外套皮肤。
- `GlobalUsings.cs` 将既有字段类型映射到薄适配类，保持 Designer 中固定字段及控件树。适配器保留原始枚举/对象选项、格式化名称、数值范围和日期值。缺失日期会拒绝提交，不生成替代时间。
- 输入容器只负责尺寸，官方输入控件填满容器并绘制边框。页签使用官方 Line 风格，增加 Ctrl+Tab、Ctrl+Shift+Tab 及聚焦页签头后的方向键/Home/End 切换。错误跨页定位等待 AntdUI 的异步页面激活，再聚焦字段。
- DataGridView 保留现有数据表结构和稳定身份逻辑。数据库映射下拉/常量单元格使用 AntdUI 编辑器，调用 WinForms 基类初始化以完成编辑面板挂载；编辑值仍按枚举、Guid 等原始类型保存。动态点位选择列表按可见行复用 AntdUI Checkbox，不为全部点位创建控件。
- 原生窗口标题栏、滚动条、文件对话框和 DataGridView 表格宿主仍保留。设计器源码迁移、设计器往返和 Windows 运行验收分别记录。

参考源码固定到 [AntdUI-Demo](https://github.com/AntdUI/AntdUI-Demo/tree/0a66f848f1e218ca975c69c3b9a4312c6316db6f)；实际依赖保持 AntdUI 2.4.16，其源码为 [v2.4.16](https://github.com/AntdUI/AntdUI/tree/3167b26361e161cb26d628be5bd0532b9e6e92ce)。本次未升级 .NET 或包版本。

## 验证记录

沿用用户明确授权的 Release 编译、隔离后台八页面/配置弹窗定向烟测及同范围修复重跑。产物目录 `artifacts/antd-controls-20261027`，不运行全量核心回归、完整 DPI 矩阵或现场联调。

过程中发现并修复：适配类缺少绑定成员、初始滚动覆盖影响显式定位、AntdUI 页签延迟激活与旧同步测试不符、映射表编辑器未初始化父面板。对应失败日志保留在该目录，不记为通过。

下拉专项的前三次边界检查失败源于测试仍使用 Form 占位尺寸，以及错误假设自适应弹层不能窄于输入框。现检查实际 HWND 边界、官方 TargetRect、最长选项文字宽度和工作区边界；截图使用官方 PrintBit 绘制结果，避免 DrawToBitmap 只取得占位图。该截图是控件绘制产物，不冒充前台桌面捕获。

第七轮应用内检查退出码为 0，但脚本因旧页签完成标记未同步而失败；已同步标记并新增首页/控件专项完成检查。第八轮新增点位勾选检查取消唯一选项，触发应有的“至少一个点位”校验；第九轮进一步核对错误定位目标为“全部启用点位”复选框而非列表。测试已明确验证这一拒绝分支，并验证恢复勾选后草稿不变。字段映射补充检查使用同一隔离后台脚本的 DatabaseMapping 入口，覆盖 AntdUI 常量编辑器的实际编辑/提交及跨页校验焦点；初轮同步焦点断言未等待 AntdUI 页面激活，已改为等待下一次 UI 消息后断言。

最终结果：

- Release 编译通过，0 错误、1 条既有 `GatewayWorkerClient.cs:283` CS8602 警告。日志 `build-14.log`。
- 八页面/配置弹窗 `UiRedesign` 通过，应用和脚本退出码均为 0，stderr 为空，72 张截图；产物 `smoke-10`。覆盖首页首次滚轮与滚动条、刷新保留位置、菜单两种宽度下图标和文字边界、八路导航、实际下拉弹层、Ctrl+Tab 双向切页、可调整列表及短窗口按钮可达性；并通过 15 个驱动编辑器、77 个参数、65 个方法、设备配置/导入、运行中配置、保存失败重试、数据库与日志配置检查。
- 字段映射补充 `DatabaseMapping` 通过，应用和脚本退出码均为 0、stderr 为空；产物 `mapping-3`。真实进入 AntdUI 单元格编辑器，验证下拉的枚举/Guid 值、常量 `192` 输入、提交、刷新和重开，以及跨页错误焦点。焦点检查等待实际消息循环完成，限时一秒，只观察状态，不替控件主动聚焦。`mapping-1` 和 `mapping-2` 的同步/单次 yield 观察均失败并保留记录。
- 两项均在实际 192 DPI 隔离后台桌面运行，`ProcessWindowVerified=true`，`InputDesktopSwitch=false`。模拟设备隔离运行、MQTT 关闭；数据库为元数据夹具，没有联系外部数据库或现场设备。
- 八页面通过后，改动仅涉及映射补充检查的异步观察方式及记录；生产代码未再修改，因此只重跑失败的映射专项，不重复全部页面。
- 未执行全量核心回归、完整 DPI 矩阵、跨屏往返、现场硬件联调或 Visual Studio 设计器逐窗打开/编辑/保存验收。Designer 源码和固定控件树已保留，不把源码迁移或编译通过当作设计器往返通过。

产物根目录：`artifacts/antd-controls-20261027`。预览使用 `smoke-10/ui-smoke-antd-menu-collapsed.png`、`smoke-10/ui-smoke-database.png`、`smoke-10/ui-smoke-tag-simulator.png` 和 `smoke-10/ui-smoke-antd-select-popup.png`。本机 Release 程序位于 `app/EdgeGateway.WinForms.exe`。
