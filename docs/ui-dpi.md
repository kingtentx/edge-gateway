# Windows DPI 与现代界面升级

日期：2026-10-23（Asia/Shanghai）。适用项目：.NET 8 Windows Forms。

## 实现范围

界面采用深色导航、明亮内容区、带强调色的状态卡、圆角图标按钮、统一表格和弹窗。图标来自 FontAwesome.Sharp 6.6.0，按当前控件 DPI 绘制和缓存，替换及窗体关闭时释放图像。组件和字体图标许可证随代码与运行包保留，见 [第三方说明](../THIRD-PARTY-NOTICES.md)。

| 模块 | 实际处理 |
|---|---|
| 应用入口 | `ApplicationHighDpiMode=PerMonitorV2`，由 `ApplicationConfiguration.Initialize()` 在创建窗口前初始化；统一 Microsoft YaHei UI 10 pt 基础字体 |
| 所有主窗体与弹窗 | `DpiAwareForm` 统一 Dpi 模式和 96×96 基线；构造全过程暂停布局，完整控件树构造后执行框架自动缩放 |
| 防止重复缩放 | 首次框架缩放和 DPI 变更期间禁止响应布局预先写入物理尺寸；完成后触发 `DpiLayoutCompleted` 重排，不重置 96 基线或额外递归 Scale |
| 窗口范围 | 根据当前屏幕 WorkingArea 与 DPI 重算最小尺寸并限位，处理任务栏、负坐标副屏、分辨率或工作区改变；标准窗口边框保留系统移动/最大化功能 |
| 主页面 | 窄窗口顶部状态和命令分行；启动/停止/保存维持可达；导航可滚动；总览卡按宽度切换四列、两列、一列 |
| 小工作区 | 页面及表格内容保留可用高度并提供滚动，不把字体缩小以强行塞入；平台设置标签按实际列宽换行 |
| 设备/点位弹窗 | 参数滚动、底部操作固定；短窗将详细说明放到可键盘操作的“查看说明”入口；多行输入按可视区调整高度并保留内部滚动 |
| 动态驱动配置 | 每批新字段及可移除页签使用独立、显式 96 DPI/10 pt 基线的内容容器，只缩放新子树；显示后切换驱动不重缩放旧宿主 |
| 表格 | 从当前控件字体计算正文、加粗表头、行高、单元格内边距与列最小宽度；不保留错误的固定小字号 |
| 页签与下拉 | 页签 Point Padding、ComboBox 下拉宽高从逻辑常量与当前工作区重新计算 |
| 趋势绘制 | 坐标间距、文字区域、网格线和数据线宽按当前 DPI 重绘 |

详细开发规则见 [WinForms DPI Skill 参考](../.agents/skills/develop-edge-gateway/references/winforms-dpi.md)。

## 验证范围与记录方式

| 分辨率 | 名义 Windows 缩放 |
|---|---|
| 1366×768 | 100%、125%、150%、175%、200% |
| 1920×1080 | 100%、125%、150%、175%、200% |
| 2560×1440 | 100%、125%、150%、175%、200% |
| 3200×2000 | 100%、125%、150%、175%、200% |

`--smoke-test --layout-matrix` 在 Windows 原生窗口中执行完整 15 个驱动设备/点位编辑器检查、显示后驱动切换，以及上述 20 种**等效工作区**。每个矩阵场景检查六个主页面、导航与固定按钮、卡片、平台字段、原 Modbus 最长参数页、点位页及多行写入弹窗。可滚动字段按焦点和祖先滚动路径逐项检查，不放宽裁剪断言。

**等效工作区不是修改操作系统 DPI。** 脚本根据目标分辨率/名义缩放推算逻辑空间，扣除 48 逻辑像素任务栏预留，再换算成构建机实际 DPI 下的窗口尺寸。仅在测试模式下使用 `SetWindowPos` 绕过宿主桌面最大拖动尺寸限制，并严格比对实际 Bounds；截图可能在超出宿主显示范围的测试窗口上通过 DrawToBitmap 获取。报告包含 `viewportSimulation=true`、名义 DPI、实际 `DeviceDpi`、原生 `GetDpiForWindow`、OS、请求/实际窗口范围以及按钮/字体/参数控件尺寸。

真实不同 DPI 的首次启动、设置中切换缩放、100%↔150%↔200% 多屏往返、远程桌面重连需在相应 Windows 桌面环境验证。不能把等效工作区截图或几何测试写成这些场景已通过。

## 本次执行结果

当前已完成本地编译、迁移清单和开发 Skill 校验；Windows Server 2022 / 2025 CI 执行后在此补充实际运行记录、测试数量、截图与下载链接。运行包中的本页可能反映打包时状态，最终以仓库本页和对应 CI 运行记录为准。

## 复核命令

在 Windows 桌面会话中执行：

```powershell
dotnet restore EdgeGateway.sln
dotnet build EdgeGateway.sln -c Release --no-restore
dotnet test tests/EdgeGateway.Tests/EdgeGateway.Tests.csproj -c Release --no-build
dotnet run --project src/EdgeGateway.WinForms -c Release --no-build -- --smoke-test --layout-matrix --screenshot artifacts/ui-smoke.png
```

矩阵报告：`artifacts/ui-smoke-layout-matrix/layout-matrix.json`。Windows CI 分别保存两个系统的证据，运行包在 Windows Server 2022 作业发布。烟测使用独立临时数据目录，不连接现场设备、不修改用户配置。

## 系统与现场边界

目标为符合 .NET 8 支持矩阵的 Windows 桌面系统，包括 Windows 11、仍受 .NET 8 支持的 Windows 10 版本，以及带桌面环境的 Windows Server。Windows 7 / 8.1 不在 .NET 8 支持范围内。操作系统实机、不同显卡/显示器、Windows 辅助功能“文本大小”和长时间跨屏操作须按现场环境验收。

微软参考：[.NET 8 高 DPI 改进](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/whats-new/net80#high-dpi-improvements)、[自动缩放](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/forms/autoscale)、[Windows .NET 支持矩阵](https://learn.microsoft.com/en-us/dotnet/core/install/windows)。
