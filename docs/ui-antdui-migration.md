# 全页面 AntdUI 控件迁移

日期：2026-10-29。依据用户“有现成 AntdUI 控件就使用，不另行绘制”的要求，在现有 main 分支修改。版本保持 AntdUI 2.4.16、.NET 8，不改变采集、存储或设备协议。固定 Demo 参考与主题规则见 [样式规范](ui-antdui-style-guide.md)。

## 清单与实现

| 迁移前 | 当前实现 |
|---|---|
| 14 个原生表格定义；日志视图复用后共 15 个业务表格实例 | GatewayTable 继承真正的 AntdUI.Table；固定表头、列仍在对应 Designer 中创建 |
| 手绘设备启停、字段映射开关/输入单元格 | 官方 CellButton、CellLink、CellSwitch；编辑时使用 Select / Input，无原生 DataGridView 或自绘 Paint |
| 293 处既有 Label 构造及公共标签工厂 | 统一绑定 GatewayLabel : AntdUI.Label；只适配 WinForms 布局测量，不绘制文字 |
| 28 处消息框调用 | GatewayMessageBox 调用 AntdUI.Modal；保留确认、取消、是/否、Escape 和安全默认按钮语义 |
| 9 处手绘概览卡片 | AntdUI.Panel，内部 TableLayoutPanel 仅管理布局 |
| 手绘历史趋势、CPU/内存曲线及磁盘容量条 | AntdUI.Chart 和 Progress；缺失/错误数据断线，容量异常不显示旧数值 |
| 3 个 LinkLabel、1 个 PictureBox、2 个 ToolTip 组件 | Button、Avatar、TooltipComponent |
| 原生窗口与托盘右键菜单 | 公共 AntdUI.Window + Designer PageHeader；托盘菜单调用官方 ContextMenuStrip |

先前已迁移的 Button、Input、Select、InputNumber、DatePicker、Switch、Menu、Tabs、Pagination 继续使用官方组件。普通输入上限 240 逻辑像素，地址/主题/表达式等长字段 400；窄窗口可收缩。分页“总数 → 每页条数 → 页码”整体靠右，总数与控件垂直对齐。启动绿色、停止红色，菜单按钮显示下拉箭头。

绑定层保留稳定设备/点位 ID、现有 DTO、分页完整快照、选择列及像素滚动位置；同一刷新批次只更新一次 Table 渲染数据。字段映射仍保留自动生成列只读、手动常量草稿、类型校验和失败不关闭。表格向辅助技术提供行/单元格树、角色、禁用状态和默认操作。适配层中保留的 DataGridView 枚举、样式值对象及事件参数只是现有绑定契约，不是原生控件实例。

固定控件和列保留 Designer；新增公共卡片、磁盘条、窗口标题也有 Designer 源码。动态设备行、驱动参数、schema 映射行和曲线点仍由代码绑定。源码迁移、编译通过与 Visual Studio 设计器实际打开/编辑/保存是三种不同验收，不相互替代。

## 保留的 Windows 部分

- UserControl、Panel、TableLayoutPanel、FlowLayoutPanel 及 AutoScroll 作为布局/滚动宿主；不在其 Paint 中画业务控件皮肤。AntdUI 不提供可直接替换现有 WinForms TableLayoutPanel 布局契约的组件。
- OpenFileDialog、SaveFileDialog 和 NotifyIcon 负责系统文件选择与托盘集成。托盘弹出菜单已用 AntdUI。
- TrayIconFactory 生成产品品牌图标，供系统托盘/窗口使用；它是图形资源，不是按钮、表格或表单控件的自绘实现。

因此不把“业务控件迁移完成”表述为删除所有 System.Windows.Forms 引用。

## 本轮验证

用户已授权 Release 编译、架构检查及隔离后台定向 UI 回归，允许同范围修复重跑，不执行全量核心或真实设备测试。证据保存在 `artifacts/antd-migration/`；失败及中断目录保留。

| 验证 | 结果与证据 |
|---|---|
| Release 编译 | `build-release.log`：0 错误，1 条既有 GatewayWorkerClient.cs:283 CS8602 告警 |
| 架构检查 | `architecture-final.log`：8 个层边界、24 个无环工程，UI/Application 隔离和 MQTT 归属通过 |
| 八页面、按钮、菜单、模拟启停、Modal | `ui-release` 通过，48 张 PNG；普通按钮轮廓、菜单键盘/禁用/目标保持、模拟启停、Modal 确认/取消/Escape/安全默认 |
| 表单与配置弹窗 | `forms5` 通过：八页面、页签、宽窄往返、驱动动态字段及数据库/分组/通道/写入弹窗 |
| 字段映射 | `final-DatabaseMapping` 通过：开关鼠标/Space、实际编辑器、常量/类型校验、草稿刷新重开、目标变化失效 |
| 分页 | `final-Pagination` 通过：翻页、条数、末页删除、空结果、实时保持及完整 CSV/JSONL 导出 |
| 八页面刷新 | `final-PageRefresh` 通过：行对象、数据源、稳定 ID、列、像素滚动和所有页签布局保持 |
| 数据库页刷新 | `dbrefresh1` 通过 |
| 日志历史与趋势 | `final-HistorySources` 通过：无数据库查询、两段实际 Chart 数据集、坏质量断线、分页和查询快照导出 |
| 资源图表 | `resources2` 通过：真实资源接线、后台持续采样、60 秒数据集、缺失/长间隔断线、磁盘增删/故障、宽窄布局 |

所有上述 UI 运行均为实际 192 DPI 隔离后台桌面，进程窗口验证成功、InputDesktopSwitch=false；未占用用户前台窗口。只运行模拟设备，MQTT 关闭，数据库编辑使用元数据夹具，没有联系现场设备或外部数据库。

迁移期间发现并修复：绑定行 Add 重载递归；隐藏列导致的编辑焦点错位；库 CellRectangle 返回的滚动偏移；无滚动条时赋零造成负偏移；刷新恢复位置逐行偏移；映射开关的实际点击区域与 Space 路径；Modal 的 No 与 Cancel 返回值区别；标签自动尺寸重复测量。旧检查将说明/错误定位链接改成 Button 后误要求隐藏状态也必须可见，已按原有显隐规则调整；资源卡从 TableLayoutPanel 换成 Panel 后，检查更新为沿实际内容容器定位卡片。字段、焦点和边界断言保留。中间编译失败、旧截图的虚假标题栏、超时和主动中止的旧构建检查均保留原始日志，不能作为通过证据。

截图使用隔离后台窗口消息渲染。AntdUI 无边框窗口扩展了客户区，截图仅请求客户区和子控件，避免 WM_PRINT 额外绘制系统标题栏覆盖官方标题；不切换用户输入桌面。检查中的窗口尺寸变化发生在主机实际 DPI 下，不等同于真实多 DPI / 跨屏测试。

未执行全量核心/驱动测试、真实设备及外部数据库联调、完整 DPI 矩阵、跨屏往返或 Visual Studio 设计器往返验收。源码迁移和上述定向回归通过，不代表这些未执行项目也已验收。
