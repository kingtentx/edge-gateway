# AntdUI / Designer 本轮验证记录

日期：2026-10-27。分支：`main`。源码基线：`2e3e95c`。

**最终结果：** Release 编译通过（0 错误、1 条已有警告）；架构静态检查通过；第六轮隔离后台烟测通过，应用退出码和脚本退出码均为 0，15 个完成标记全部满足，67 张截图，stderr 为 0 字节。主窗口截图为 2826 × 1791，实际 DeviceDpi 192。此次不包含 Visual Studio 设计器往返或完整 DPI 矩阵。

用户授权范围：WinForms Release 编译、架构静态检查、隔离后台桌面的八页面及配置弹窗定向烟测；不含全量核心回归或完整 DPI 矩阵。构建与界面运行串行，未占用输入桌面。构建输出及验证数据位于被 Git 忽略的 `artifacts/antd-ui-20261027/`，未提交运行库、配置数据库、日志或凭据。

## 编译与静态检查

- `python scripts/verify-architecture.py`：通过，8 个层边界、24 个无环工程，UI/Application 隔离与 MQTT 包归属检查通过。此检查执行后仅调整 UI 源码及 Compile/Designer 元数据，未改变工程依赖。
- 初次 Release 编译：失败，`SurfaceCard.Accent` 为 init-only，Designer 不能赋值；已修复为可设置属性。
- 获准的第 1 次编译重跑：失败，公共编辑器基类的受保护方法使用内部 `GatewayInput` 类型；已调整可访问性。
- 获准的第 2 次编译重跑：失败，设备启动按钮列初始化存在语法错误；已修复。
- 获准的第 3 次编译重跑：通过，0 错误、1 条已有 `GatewayWorkerClient.cs:283` 空引用警告，见 `build-round4.log`。

用户追加授权最多 3 轮修复验证后：

- `build-round5.log`：失败，AntdUI.Panel 的 BorderStyle 属性遮蔽 WinForms 同名枚举；将原生编辑器的枚举引用改为完整类型名。
- `build-round6.log`：通过，0 错误、1 条同一已有警告；用于第二轮 Windows 烟测。
- `build-round7.log`：通过，0 错误、1 条同一已有警告；另修复设备页工具栏测量导致的异常留白，用于第三轮 Windows 烟测。
## Windows 界面运行

命令：

```powershell
./scripts/smoke-test-winforms-background.ps1 `
  -Executable artifacts/antd-ui-20261027/app/EdgeGateway.WinForms.exe `
  -OutputDirectory artifacts/antd-ui-20261027/ui-round1 `
  -Scenario UiRedesign -TimeoutSeconds 900
```

`UiRedesign` 使用现有 `--smoke-test` 入口，覆盖八页面、驱动/点位编辑器、动态参数、配置保存失败保留输入与相关菜单可达性，不启用 `--layout-matrix`。脚本核对既有完成标记、八页面截图及标准错误输出。

首轮：失败。隔离桌面 `EdgeGateway.Validation.145bd4ffbc0a4fbbae402f93adccfdc2`，进程 38516，`ProcessWindowVerified=true`、`InputDesktopSwitch=false`。实际 DeviceDpi 为 192。总览健康检查和数据库/平台/日志延迟导航检查通过；八页面截图已生成。首个 CNC.Fanuc 设备弹窗的确认按钮超出底部操作区，后续编辑器与交互尚未完成，不能推断通过。

人工截图复核另发现导航按钮自动尺寸收缩、输入边框兄弟控件遮盖内容。源码已增加按钮尺寸约束适配，将输入边框改为父容器，并修复导航背景与 Dock/AutoSize 配置。后续轮次记录修复后的结果，不以修复前的成功编译覆盖后续修改。

第二轮（`ui-round2`）：失败。隔离桌面 `EdgeGateway.Validation.a95eb2a9aae74ffc87c5900b41838a45`，实际 DPI 192。15 个驱动编辑器、77 个反射参数、65 个方法、动态切换草稿、15 类设备配置、3 类编辑后重开、取消/只读隔离、设备行入口及导入检查通过。后续行内操作检查发现设备表格高度不足，启动按钮单元格被裁切；未到达的检查不记为通过。截图确认上轮按钮、导航及输入遮挡已修复。

设备页嵌套自动布局曾以过窄的临时宽度测量点位工具栏，留出多行空白并压缩表格。现按实际可用宽度测量工具栏的已有控件、更新固定操作行高度；未缩小字号或放宽边界断言。第三轮（`ui-round3`）：失败。设备表格留白修复后，三行设备与四行点位均完整显示，设备行内操作及自动保存检查通过。运行中配置检查随后发现旧断言将“无日志草稿时禁用应用/重置”误判为错误；修改前的同一断言和禁用规则均已存在。现已明确无草稿时必须禁用，同时增强后续日志热应用检查：有草稿时应用/重置可用，成功后两按钮恢复禁用；未降低焦点、边界或失败保留输入断言。另在历史页截图中发现自动布局缩短日期字段，已在 Designer 保留筛选组的设计最小宽度，工具栏通过换行适应窄窗口。用户随后明确允许在原范围内修复验证至完成，不扩大到全量回归、完整 DPI 矩阵或现场联调。

第四轮（`build-round8.log` / `ui-round4`）：编译通过，0 错误、1 条同一已有警告。Windows 11 专业版 10.0.26300，实际 DPI 192；隔离桌面 `EdgeGateway.Validation.5cf0315f0f01489085df70014ff47cb9`，进程 36236，未切换输入桌面。日期时间完整显示，筛选组在宽度不足时换行。15 个完成标记中的前 13 个通过，包含运行中配置、模态保存失败与重试、读写核对和平台字段保存。可用性检查因共享错误反馈文本的原有名称在迁移中丢失而失败，未完成后续数据记录检查。

恢复 `EditorValidationMessage` / `WriteValidationMessage` 及对应定位链接名称，保留固定控件的 Designer 创建方式，未更改错误反馈或输入保留断言。`build-round9.log` 编译通过，0 错误、1 条同一已有警告；第五轮后台烟测 `ui-round5` 的应用内 15 个完成标记全部通过，生成 67 张截图，stderr 为空；外层 PowerShell 结果读取遇到空值方法调用异常，脚本最终返回失败，因此不能将该轮外层脚本记为成功。已改为直接读取日志文件文本并持久记录进程退出码，使用相同 `build-round9` 程序执行第六轮 `ui-round6`，未重新编译或扩大测试范围。

## 最终 Windows 结果

第六轮（`ui-round6`）通过，使用 `build-round9.log` 对应的程序；此后仅修改脚本、文档和两处文件末尾空行，未改变 C# 运行逻辑。Windows 11 专业版 10.0.26300，实际 DPI 192。隔离桌面 `EdgeGateway.Validation.071e63ad4d0c4ac5aa823885e8a34cef`，进程 39432，`ProcessWindowVerified=true`、`InputDesktopSwitch=false`，`exit-code.txt` 为 0。外层脚本输出 `Background UI redesign baseline passed (eight pages and editor behavior; no DPI matrix).` 并返回 0。

- 八页导航、总览、固定工具栏/筛选、控件边界与输入高度检查通过。
- 15 个驱动、77 个反射参数、65 个方法的编辑器，以及动态驱动切换、草稿恢复和 3 类编辑后重开通过；不代表现场协议验收。
- 设备/点位身份与选择、菜单键盘打开、行内操作、独立采集、自动保存、运行中配置和模态保存失败后重试通过。
- 错误提示定位、失败保留输入、写值选项修正后提交、平台四页与草稿重置通过。
- 数据库连接 16 项输入、密码掩码与原值保存、连接保存失败/重试、点位选择、schema 字段映射、必填/自动列和常量绑定通过。
- 数据库全停限制、日志运行中应用、失败保留日志草稿、成功后应用/重置按钮禁用，以及采集继续运行检查通过。

本轮只运行模拟设备；MQTT 保持禁用，数据库元数据使用界面夹具，未连接外部数据库。人工复核总览、设备、历史、平台、日志、数据库页，以及设备和数据库配置截图；未把截图外的分辨率或 DPI 组合推断为通过。所有历次失败记录保留在同级 artifacts 目录。

复现最终执行可使用上述命令，将输出目录改为新的空路径（如 `artifacts/ui-redesign-new`）；执行仍需遵守 AGENTS.md 的授权约定。
## 尚未验证

- Visual Studio 设计器打开、修改、保存并重新打开的完整往返。
- 全量核心/驱动回归、完整分辨率与 DPI 矩阵、实际跨屏缩放。
- 现场设备、真实外部数据库和平台联调。

源码迁移和上述定向 Windows 运行检查已完成；“设计器可编辑”尚无 Visual Studio 往返证据，上述未执行项不标记为验收通过。提交使用 `[skip ci]`，不通过 CI 间接扩大测试范围。
