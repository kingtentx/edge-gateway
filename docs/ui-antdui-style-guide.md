# AntdUI-Demo 界面样式规范

更新日期：2026-10-29。普通按钮增加官方边框；按用户本次明确要求，设备启动使用官方绿色 Success、停止使用官方红色 Error。其余按钮继续采用 Default / Primary。

## 样式依据

界面采用 [AntdUI-Demo](https://github.com/AntdUI/AntdUI-Demo/tree/0a66f848f1e218ca975c69c3b9a4312c6316db6f) 的规范样式，按钮以该版本 [ButtonDemo.Designer.cs](https://github.com/AntdUI/AntdUI-Demo/blob/0a66f848f1e218ca975c69c3b9a4312c6316db6f/Views/ButtonDemo.Designer.cs) 中基础 Default / Primary 示例为准，不采用其中展示自定义配色的示例。依赖保持 AntdUI 2.4.16、.NET 8；库行为参考 [v2.4.16 Button.cs](https://github.com/AntdUI/AntdUI/blob/3167b26361e161cb26d628be5bd0532b9e6e92ce/src/AntdUI/Controls/Button.cs)。后续升级须重新核对参考与实际依赖，不能只凭截图近似绘制。

## 按钮

- 所有页面、弹窗、工具栏、菜单触发按钮、导航折叠按钮及行内操作统一遵循此规范。
- 普通操作使用 `Type=Default`；新增、保存、应用、查询等已有主操作使用 `Type=Primary`。删除保留明确文案和确认流程，不通过匹配“删除”字符串自动染红；新增危险样式须有明确产品需求并使用官方类型。
- 普通控件使用 `GatewayButton : AntdUI.Button`，保留官方圆角 6、边框宽度 1、非 Ghost；`BorderWidth=1` 与基础 Demo 的 `WaveSize=0` 在共享基类中统一，普通白底按钮也能区分可点击轮廓。不要自行设置 Back、Fore、DefaultBack、DefaultBorderColor、BackHover、BackActive、ForeHover、ForeActive，也不要通过 WinForms BackColor / ForeColor 叠加按钮皮肤。
- 2026-10-29 用户指定例外：侧栏折叠按钮使用无边框官方方向图标，`BorderWidth=0`；普通业务按钮仍保留边框。展开侧栏保持 184 逻辑像素并增加菜单项高度及左侧留白，收缩为 64，详见 [导航与筛选布局](ui-compact-layout.md)。
- 默认、悬停、按下、禁用及焦点状态交由官方按钮处理，不通过鼠标事件切换自定义配色或模拟普通按钮。保留 Enter / Space、Tab、默认按钮、取消和无障碍语义。图标使用官方主题色，保持现有 DPI 与资源释放规则。
- 全部业务表格使用 `GatewayTable : AntdUI.Table`，设备启停直接使用官方 `CellButton` 的 Success / Error；配置与查看点位为 `CellLink`，字段映射开关为 `CellSwitch`。不再保留原生 DataGridView 或自行绘制行按钮。绑定适配层保留稳定 ID、选择、分页、滚动、键盘及无障碍行/单元格树。
- 38 逻辑像素操作高度、中文 10 pt 正文、工具栏换行和可点击面积继续按项目 DPI 规范维护；不为照搬 Demo 示例窗口尺寸缩小字号或裁切文字。

## 下拉操作与对齐（2026-10-28）

2026-10-29 性能与尺寸补充：白色卡片内的大型嵌套布局表使用同色不透明背景，避免 WinForms 透明背景在缩放/折叠时反复绘制整张父卡片。这不改变 AntdUI 外部圆角或控件状态。Switch 的 AutoSize 必须保留开关实际点击范围，不能按空文本宽度收缩；动态磁盘条按当前 DPI 计算固定行高，百分比只显示一次。详见 [首页性能修复](ui-overview-performance.md)。

菜单触发按钮使用官方 `ShowArrow=true` / `IsLink=true` 的下拉箭头；操作项改用真正的 `AntdUI.ContextMenuStrip`，保留默认圆角、留白、分隔和悬停，快捷键单独在右侧显示。禁用项显示简短原因，无障碍描述保留完整原因。方向键、Enter、Escape 和打开时捕获的设备身份继续有效。启停单元格由官方 CellButton 负责文字居中和全部状态绘制。

普通单行表单统一 240 逻辑像素上限，长字段 400；Select 弹出列表跟随输入宽度。实现及本轮验证见 [菜单和表单对齐](ui-menu-form-alignment.md)。

页面筛选项在 Designer 中以标签在左、输入在右排列，宽度不足时整项换行。表格启用官方表头拖动，填充列使用官方百分比宽度和最小宽度约束；刷新、分页保留用户拖动结果，详见 [紧凑布局](ui-compact-layout.md)。

## 其他组件与设计器

### 列表分页（2026-10-28）

列表使用真正的 `AntdUI.Pagination`，参考同一固定 Demo 版本的 `Views/PaginationDemo.Designer.cs` 和用户指定的 [Pagination 截图](https://github.com/AntdUI/AntdUI-Demo/blob/main/assets/screenshots/Pagination.png)。保留官方默认主题、圆角 6、间距 8、边框及悬停/禁用状态；不仿制页码按钮或设置自定义颜色。按用户参考图从左到右排列“共 N 条 → 每页条数下拉 → 上一页/页码/下一页”，整组靠右对齐。控件高 38 逻辑像素，底栏上下各留 4 像素；默认每页 10 条，可选 10/20/50/100 条。总数标签、独立 AntdUI Select 和 Pagination 都在各自 Designer 中创建，关闭 Pagination 内置右侧条数选择器。所有列表设置 `MaxPageTotal=8`，第一页显示 `1 2 3 4 5 6 … 末页`；宽窗口不铺满整行，窄窗口由官方控件继续减少可见页码。

设备、点位、实时数据、历史、运行/采样日志、数据库连接/写入组、分组/通道、驱动列表及导入/数据库预览在各自 Designer 中创建分页条和表格宿主。编辑用字段映射表及通信诊断固定指标表保留原有编辑/展示方式。`GridPagination` 只负责绑定已有分页控件和切片数据，不创建固定控件。

分页作用于已加载的列表或查询快照，不改变后台查询上限和导入校验范围。空结果清空当前页，删除末页记录后收敛至有效页；明确新增/编辑目标按稳定 ID 切到所在页，实时刷新保持页码和现有行。历史趋势及 CSV、日志导出继续使用完整查询快照，不仅导出当前页。点位 CSV 导入预览保留原有 500 条上限。

本轮编译、分页定向检查与未执行事项见 `docs/ui-pagination.md`，不沿用下方 2026-10-27 的验证结果。

全部界面开发以官方 Demo 为统一样式依据，不再以“类似 Demo”的自定义皮肤作为新页面标准。覆盖主窗体、八个页面、全部配置/详情弹窗、固定字段和动态生成的内容。对应组件采用官方基础示例，保持官方主题、状态、边框、圆角和交互；业务布局仍按实际可用空间设计。

| 界面元素 | 规范 |
|---|---|
| 按钮与操作区 | 官方 Button 的基础 Default / Primary、统一 1 逻辑像素边框；设备启停为 Success / Error |
| 文本、密码、数值、下拉、日期 | 对应 Input、InputNumber、Select、DatePicker；容器不叠加第二层底色或边框 |
| 布尔表单 | Switch；沿用官方开关形态，保留字段标签、键盘与无障碍语义 |
| 导航与页签 | Menu 与 Tabs 基础样式；侧栏收缩保持图标坐标，页签保持统一的选中和焦点状态 |
| 表格与动态行 | 真实 AntdUI.Table / CellButton / CellLink / CellSwitch；输入编辑使用真实 Select / Input，禁止自绘单元格皮肤 |
| 分组、详情与弹窗 | 官方 Demo 的基础表单布局与配色，统一标题层级、间距、底部确认/取消区；不将 Demo 中自定义效果展示作为默认样式 |

窗口采用 AntdUI.Window 和 Designer 中的 PageHeader，确认/提示采用 AntdUI.Modal；标签、卡片、曲线、容量条、Logo 宿主、提示和托盘菜单分别使用 Label、Panel、Chart、Progress、Avatar、TooltipComponent 和 ContextMenuStrip。原生 UserControl / Panel / TableLayoutPanel / FlowLayoutPanel 仅作为布局与滚动宿主；系统文件对话框和 NotifyIcon 保留 Windows 集成。品牌图标是资源图形，非控件皮肤。清单及本次验证见 [全页面控件迁移](ui-antdui-migration.md)。

导航、输入、下拉、数值、开关、日期与页签使用 AntdUI 对应组件和 Demo 的基础样式，不在原生控件外套仿制边框。页面使用浅灰工作区、白色内容区、官方蓝色选中态；输入容器只负责尺寸，背景透明，由输入控件绘制输入区。开关旁不重复显示说明文字，保留字段标签及无障碍名称；数字输入和界面数值文本不使用千分位。

固定结构、按钮、字段和列在对应 Designer 中真实创建并挂入控件树，代码只绑定事件/状态。公共样式适配器不能用来绕过此要求。动态驱动参数、数据库 schema 映射行、设备数据与曲线仍可由代码生成。详见 [设计器边界](ui-redesign-architecture.md) 和 [DPI 规范](../.agents/skills/develop-edge-gateway/references/winforms-dpi.md)。

## 表单宽度

2026-10-28：页面和弹窗的单行输入改为按字段类型限制宽度，并为地址、主题及表达式显式放宽；保留 AntdUI 默认样式、左侧标签和输入高度。尺寸规则、覆盖范围及本轮验证见 [表单输入宽度](ui-form-widths.md)。

## 按钮轮廓与日志标题（2026-10-28）

日志页面在三个页签上方统一显示“运行日志”标题与简短说明，固定控件在 LoggingPageView.Designer.cs 中创建。按钮轮廓、启停颜色及本轮验证记录见 [按钮与日志标题调整](ui-button-appearance.md)。

## 历史验证（2026-10-27）

用户已授权 Release 编译及隔离后台八页面、配置弹窗、设备行按钮定向检查，允许在该范围修复重跑。产物目录为 `artifacts/antd-default-buttons-20261027`。

- Release 编译通过。首轮 `build-1.log` 为 0 错误、420 条驱动及代理代码警告；最终 `build-2.log` 为 0 错误、1 条既有 `GatewayWorkerClient.cs:283` CS8602 警告。未修改依赖或核心业务。
- 首次后台检查 `ui-1` 在悬停检查失败：发送鼠标移动消息没有维持后台控件的悬停状态。已改为调用官方控件 MouseEnter 事件，检查该事件触发后的状态与渲染；保留失败日志。这是隔离桌面的事件级验证，不宣称完成物理鼠标操作验收。按下态截图使用官方 ExtraMouseDown 渲染，避免触发新增设备弹窗。
- 最终 `ui-2` 八页面及配置弹窗检查通过，应用和脚本退出码均为 0、stderr 为空，共 86 张 PNG。覆盖默认/主按钮、MouseEnter 悬停、按下渲染、禁用命令拒绝、真实键盘消息触发一次 Space 操作、设备行焦点和状态、菜单键盘路径、固定按钮可达性与保存失败保留输入。人工查看默认、悬停、禁用以及点位弹窗截图。
- 实际 192 DPI，隔离后台桌面 `ProcessWindowVerified=true`、`InputDesktopSwitch=false`；只运行模拟设备，MQTT 关闭，数据库为 UI 元数据夹具，未联系现场设备和外部数据库。
- 未执行全量核心回归、架构静态门禁、完整 DPI 矩阵、跨屏往返、现场联调或 Visual Studio 设计器逐窗打开/编辑/保存验收。固定控件源码仍在 Designer，不把源码审查与编译通过等同于设计器往返验收。
