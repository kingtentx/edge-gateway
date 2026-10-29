# 工具栏搜索框与按钮对齐

2026-10-29：点位管理工具栏的按钮按字体和 Padding 自动增高，InputFrame 仍按基础输入高度计算，导致搜索框边框及文字中心线高于按钮。

公共 InputFrame 仅在直属 FlowLayoutPanel 工具栏中参考可见 GatewayButton 的首选高度，统一搜索框与按钮高度，随当前字体和 DPI 重新计算。保留官方输入焦点样式、按钮样式、换行和固定表单的独立居中规则；不修改搜索/点位操作逻辑。

在列表弹窗定向检查中补充点位工具栏实际 ReadRectangle 的高度及同排顶部对齐断言，覆盖宽窄窗口和搜索输入焦点。输入区检查同步区分工具栏高度与普通表单高度。

沿用用户已授权的表单对齐及同范围修复重跑，执行 Release 编译和隔离后台 SurfaceLists：

- `artifacts/toolbar-search-alignment/build-3.log`：0 错误、1 个既有 GatewayWorkerClient CS8602 警告；首轮完整编译为 420 个既有驱动/代理警告。
- `ui-1` / `ui-2`：搜索框聚焦后，分页检查回退为聚焦 Form，导致焦点断言失败；第二轮宽窗口的搜索框实际边框对齐断言已通过。测试调整为使用点位窗口中真实可聚焦的“添加点位”按钮，保留焦点和边界断言，失败日志保留。
- `ui-3`：通过，退出码 0，stderr 为空，12 张截图。点位搜索框聚焦、宽窄窗口下与四个按钮的实际 ReadRectangle 高度和同排位置一致；人工查看宽窄截图确认。列表弹窗的分页末行及取消等既有检查同时通过。
- 隔离桌面 `ProcessWindowVerified=true`、`InputDesktopSwitch=false`，未占用用户桌面。未执行全量核心、真实设备或真实跨屏 DPI 测试。
