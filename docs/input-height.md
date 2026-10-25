# 单行输入区与按钮高度统一

日期：2026-10-25。

设备筛选与点位搜索使用原生字体高度的控件，明显低于工具栏按钮。本轮在公共主题和表单入口统一输入区域，让各页面和弹窗保持同一尺度。

- 普通操作按钮和单行输入区共享 38 个 96 DPI 逻辑像素高度；字体需要更多空间时使用相同的文本与留白测量规则。
- `InputFrame` 提供圆角背景、边框、聚焦提示和整块区域点击聚焦；内部仍是原生 TextBox、ComboBox、NumericUpDown 或 DateTimePicker，保持密码掩码、数据绑定、校验和键盘操作。点击下拉输入区的留白也可展开选项。
- `UiTheme.FilterField`、`Toolbar`、`EditorDialog.Field` 共用输入入口；数据库点位选择的独立搜索框同样接入。动态驱动参数沿用现有 FieldViewport 的 DPI 生命周期。
- 字段标题垂直居中，工具栏单行输入取消旧的额外顶部偏移。多行文本、复选框、数据表格单元格和侧边导航沿用自身布局。
- 界面回归检查完整输入区和原生编辑器均可见、可聚焦，输入区高度符合按钮基准，内部编辑器垂直居中；保持原有菜单、页签、保存、筛选和设备操作检查。

用户允许选用成熟 WinForms UI 组件库。本轮通过现有主题层完成，不新增第三方运行依赖。

## 验证记录

证据目录：`artifacts/input-height-20261025/`。

- 架构：8 层、24 个无环工程通过。
- 驱动清单：12 项目、65 方法、74 原参数、176 源文件通过。
- 核心测试：448/448 通过，见 `core-tests.trx`。
- 首次 UI 回归因输入容器沿用了业务字段名称前缀而失败，已改用独立 `InputSurface_` 前缀；原始失败保留在 `ui/`。
- `ui2/` 为进一步复核原生输入布局而中止的中间运行，不能作为通过证据。
- `ui3/` 已通过功能回归，但在布局矩阵第一组发现输入容器重复设置原生边界导致检查耗时增加，主动中止后修正：边界未变化不再调用 SetBounds，字体测量按 Font/DPI 缓存。该次运行不作为完整通过证据。
- 完整解决方案构建曾与 UI 回归同时运行，因测试进程占用 DLL 导致复制失败，保留在 `build.log`；串行重建的 `build-final.log` 已通过，0 警告、0 错误。
- 最终后台 UI：`ui-background4/ui-smoke-verification.json` 验证通过，8 页面、7 类弹窗、20/20 等效工作区、149 张 PNG、16 个完成标记，实际 DPI 为 192。完整功能、输入区高度与居中、原生焦点及裁剪断言均保留。
- `ui-final/` 前台运行在用户要求后台验证后立即停止，当时已有 7 个等效工作区通过，整体仍是不完整运行。隔离桌面启动阶段的 `ui-background/`、`ui-background2/`、`ui-background3/` 因跨桌面线程查询权限而终止；改用对私有桌面的窗口枚举核对测试进程后，正式运行位于 `ui-background4/`。

## 后台验证

本机使用 `./scripts/smoke-test-winforms-background.ps1 -OutputDirectory artifacts/<新的目录>`。脚本创建独立 Windows 桌面，通过 STARTUPINFO 指定测试进程桌面并仅继承日志句柄，枚举私有桌面的窗口核对进程归属后记录 `desktop.txt`。不切换当前输入桌面，窗口和原生焦点操作均留在隔离桌面；测试退出后复用原 `smoke-test-winforms.ps1 -VerifyOnly` 检查相同的断言、标记和截图数量。每次使用新输出目录，失败或超时保留原始日志。实现依据为 Microsoft 的 [STARTUPINFO 桌面字段](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-startupinfow) 和 [桌面继承规则](https://learn.microsoft.com/en-us/windows/win32/winstation/thread-connection-to-a-desktop)。

布局矩阵使用本机真实 DPI 下的 20 种等效工作区，不代表在每档系统 DPI、不同显示器或每个 Windows 版本上实测。未进行现场服务器跨屏验收。
