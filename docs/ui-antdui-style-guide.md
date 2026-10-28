# AntdUI-Demo 界面样式规范

更新日期：2026-10-27。此规范按用户最新要求，替代此前增强按钮悬停色、红色停止按钮等自定义皮肤约定。

## 样式依据

界面采用 [AntdUI-Demo](https://github.com/AntdUI/AntdUI-Demo/tree/0a66f848f1e218ca975c69c3b9a4312c6316db6f) 的规范样式，按钮以该版本 [ButtonDemo.Designer.cs](https://github.com/AntdUI/AntdUI-Demo/blob/0a66f848f1e218ca975c69c3b9a4312c6316db6f/Views/ButtonDemo.Designer.cs) 中基础 Default / Primary 示例为准，不采用其中展示自定义配色的示例。依赖保持 AntdUI 2.4.16、.NET 8；库行为参考 [v2.4.16 Button.cs](https://github.com/AntdUI/AntdUI/blob/3167b26361e161cb26d628be5bd0532b9e6e92ce/src/AntdUI/Controls/Button.cs)。后续升级须重新核对参考与实际依赖，不能只凭截图近似绘制。

## 按钮

- 所有页面、弹窗、工具栏、菜单触发按钮、导航折叠按钮及行内操作统一遵循此规范。
- 普通操作使用 `Type=Default`；新增、保存、应用、查询、启动等已有主操作使用 `Type=Primary`。删除保留明确文案和确认流程，不通过匹配“删除”字符串自动染红；新增危险样式须有明确产品需求并使用官方类型。
- 普通控件使用 `GatewayButton : AntdUI.Button`，保留官方圆角 6、边框宽度 0、非 Ghost；基础 Demo 的 `WaveSize=0` 在共享基类中统一。不要自行设置 Back、Fore、DefaultBack、DefaultBorderColor、BackHover、BackActive、ForeHover、ForeActive，也不要通过 WinForms BackColor / ForeColor 叠加按钮皮肤。
- 默认、悬停、按下、禁用及焦点状态交由官方按钮处理，不通过鼠标事件切换自定义配色或模拟普通按钮。保留 Enter / Space、Tab、默认按钮、取消和无障碍语义。图标使用官方主题色，保持现有 DPI 与资源释放规则。
- 设备表保留 DataGridView 的稳定 ID、行命令、键盘与无障碍合同。其按钮单元格使用 AntdUI 官方 Default / Primary 主题令牌绘制，默认按钮不增加描边，停止为 Default；禁用前景保留透明度，默认悬停/按下色叠加于 DefaultBg。禁止为行按钮单独写一套 RGB 色板。该适配器不是独立 AntdUI 控件，保留原生表格键盘焦点框。
- 38 逻辑像素操作高度、中文 10 pt 正文、工具栏换行和可点击面积继续按项目 DPI 规范维护；不为照搬 Demo 示例窗口尺寸缩小字号或裁切文字。

## 其他组件与设计器

### 列表分页（2026-10-28）

列表使用真正的 `AntdUI.Pagination`，参考同一固定 Demo 版本的 `Views/PaginationDemo.Designer.cs` 和用户指定的 [Pagination 截图](https://github.com/AntdUI/AntdUI-Demo/blob/main/assets/screenshots/Pagination.png)。保留官方默认主题、圆角 6、间距 8、边框及悬停/禁用状态；不仿制页码按钮或设置自定义颜色。按用户参考图从左到右排列“共 N 条 → 每页条数下拉 → 上一页/页码/下一页”，整组靠右对齐。控件高 38 逻辑像素，底栏上下各留 4 像素；默认每页 10 条，可选 10/20/50/100 条。总数标签、独立 AntdUI Select 和 Pagination 都在各自 Designer 中创建，关闭 Pagination 内置右侧条数选择器。所有列表设置 `MaxPageTotal=8`，第一页显示 `1 2 3 4 5 6 … 末页`；宽窗口不铺满整行，窄窗口由官方控件继续减少可见页码。

设备、点位、实时数据、历史、运行/采样日志、数据库连接/写入组、分组/通道、驱动列表及导入/数据库预览在各自 Designer 中创建分页条和表格宿主。编辑用字段映射表及通信诊断固定指标表保留原有编辑/展示方式。`GridPagination` 只负责绑定已有分页控件和切片数据，不创建固定控件。

分页作用于已加载的列表或查询快照，不改变后台查询上限和导入校验范围。空结果清空当前页，删除末页记录后收敛至有效页；明确新增/编辑目标按稳定 ID 切到所在页，实时刷新保持页码和现有行。历史趋势及 CSV、日志导出继续使用完整查询快照，不仅导出当前页。点位 CSV 导入预览保留原有 500 条上限。

本轮编译、分页定向检查与未执行事项见 `docs/ui-pagination.md`，不沿用下方 2026-10-27 的验证结果。

全部界面开发以官方 Demo 为统一样式依据，不再以“类似 Demo”的自定义皮肤作为新页面标准。覆盖主窗体、八个页面、全部配置/详情弹窗、固定字段和动态生成的内容。对应组件采用官方基础示例，保持官方主题、状态、边框、圆角和交互；业务布局仍按实际可用空间设计。

| 界面元素 | 规范 |
|---|---|
| 按钮与操作区 | 官方 Button 的基础 Default / Primary；同类操作状态一致 |
| 文本、密码、数值、下拉、日期 | 对应 Input、InputNumber、Select、DatePicker；容器不叠加第二层底色或边框 |
| 布尔表单 | Switch；沿用官方开关形态，保留字段标签、键盘与无障碍语义 |
| 导航与页签 | Menu 与 Tabs 基础样式；侧栏收缩保持图标坐标，页签保持统一的选中和焦点状态 |
| 表格与动态行 | 对齐官方 Table 的表头、选择、禁用与行操作风格；现有 DataGridView 宿主保留稳定 ID、选择、滚动及键盘合同，不新增独立色板 |
| 分组、详情与弹窗 | 官方 Demo 的基础表单布局与配色，统一标题层级、间距、底部确认/取消区；不将 Demo 中自定义效果展示作为默认样式 |

现有原生窗口标题栏、滚动条、文件选择器和 DataGridView 等宿主不是 AntdUI 控件，不宣称它们已全部替换。后续迁移控件须单独保持交互、设计器和 DPI 边界；不得为了统一样式移除业务功能或把固定控件改为运行时创建。

导航、输入、下拉、数值、开关、日期与页签使用 AntdUI 对应组件和 Demo 的基础样式，不在原生控件外套仿制边框。页面使用浅灰工作区、白色内容区、官方蓝色选中态；输入容器只负责尺寸，背景透明，由输入控件绘制输入区。开关旁不重复显示说明文字，保留字段标签及无障碍名称；数字输入和界面数值文本不使用千分位。

固定结构、按钮、字段和列在对应 Designer 中真实创建并挂入控件树，代码只绑定事件/状态。公共样式适配器不能用来绕过此要求。动态驱动参数、数据库 schema 映射行、设备数据与曲线仍可由代码生成。详见 [设计器边界](ui-redesign-architecture.md) 和 [DPI 规范](../.agents/skills/develop-edge-gateway/references/winforms-dpi.md)。

## 表单宽度

2026-10-28：页面和弹窗的单行输入改为按字段类型限制宽度，并为地址、主题及表达式显式放宽；保留 AntdUI 默认样式、左侧标签和输入高度。尺寸规则、覆盖范围及本轮验证见 [表单输入宽度](ui-form-widths.md)。

## 本轮验证

用户已授权 Release 编译及隔离后台八页面、配置弹窗、设备行按钮定向检查，允许在该范围修复重跑。产物目录为 `artifacts/antd-default-buttons-20261027`。

- Release 编译通过。首轮 `build-1.log` 为 0 错误、420 条驱动及代理代码警告；最终 `build-2.log` 为 0 错误、1 条既有 `GatewayWorkerClient.cs:283` CS8602 警告。未修改依赖或核心业务。
- 首次后台检查 `ui-1` 在悬停检查失败：发送鼠标移动消息没有维持后台控件的悬停状态。已改为调用官方控件 MouseEnter 事件，检查该事件触发后的状态与渲染；保留失败日志。这是隔离桌面的事件级验证，不宣称完成物理鼠标操作验收。按下态截图使用官方 ExtraMouseDown 渲染，避免触发新增设备弹窗。
- 最终 `ui-2` 八页面及配置弹窗检查通过，应用和脚本退出码均为 0、stderr 为空，共 86 张 PNG。覆盖默认/主按钮、MouseEnter 悬停、按下渲染、禁用命令拒绝、真实键盘消息触发一次 Space 操作、设备行焦点和状态、菜单键盘路径、固定按钮可达性与保存失败保留输入。人工查看默认、悬停、禁用以及点位弹窗截图。
- 实际 192 DPI，隔离后台桌面 `ProcessWindowVerified=true`、`InputDesktopSwitch=false`；只运行模拟设备，MQTT 关闭，数据库为 UI 元数据夹具，未联系现场设备和外部数据库。
- 未执行全量核心回归、架构静态门禁、完整 DPI 矩阵、跨屏往返、现场联调或 Visual Studio 设计器逐窗打开/编辑/保存验收。固定控件源码仍在 Designer，不把源码审查与编译通过等同于设计器往返验收。
