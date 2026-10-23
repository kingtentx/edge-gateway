# 运行中配置与数据闭环：实际验证记录

验证日期：2026-10-23。程序源码 [`f1d398ba27728e919c3fd56f60cbf510312267d8`](https://github.com/kingtentx/edge-gateway/commit/f1d398ba27728e919c3fd56f60cbf510312267d8)，源码树 `0422ddea89864bdd8c7a6c7730edc9a3ebff1103`。[Windows CI 39923092261](https://github.com/kingtentx/edge-gateway/actions/runs/39923092261) 最终 attempt 2 为 **success**。

**两个 Windows 系统各通过 163 项测试、20 个等效工作区布局场景，成功证据各有 102 张实际截图。** Server 2025 在 attempt 1 首次通过；Server 2022 首次遇到焦点异常，同一提交仅重跑失败作业后于 attempt 2 通过。重跑没有修改源码或断言，本页保留首次失败，不把最终成功写成首次全部通过。

交付与使用：[Windows x64 自包含运行包](https://github.com/kingtentx/edge-gateway/actions/runs/39923092261/artifacts/12487349660) · [机器可读验证记录](live-configuration-verification.json) · [运行中配置、点表和诊断操作说明](live-configuration.md) · [能力对照与剩余范围](gateway-capability-review.md)。

## 执行环境与实际结果

下表描述两个最终成功的实际执行作业，不把重跑 API 中沿用的成功结果视为再次执行。

| 项目 | Windows Server 2022 | Windows Server 2025 |
|---|---|---|
| 实际执行 attempt | 2，仅重跑失败作业 | 1，首次通过 |
| 实际执行作业 | [120314742298](https://github.com/kingtentx/edge-gateway/actions/runs/39923092261/job/120314742298) | [120312791815](https://github.com/kingtentx/edge-gateway/actions/runs/39923092261/job/120312791815) |
| OS 版本 | Microsoft Windows 10.0.20348 | Microsoft Windows 10.0.26100 |
| .NET / WinForms | .NET 8.0.32 / 8.0.0.0，X64 | .NET 8.0.32 / 8.0.0.0，X64 |
| Release 构建 | 0 错误，419 项既有上游警告 | 0 错误，419 项既有上游警告 |
| 核心自动测试 | 163 / 163 通过，0 失败、0 跳过 | 163 / 163 通过，0 失败、0 跳过 |
| 全部驱动编辑器 | 15 个驱动入口，65 个方法，77 个含适配扩展的反射参数 | 同左 |
| 运行中配置烟测 | 通过，A 持续采集的成功标记已核对 | 同左 |
| 等效工作区矩阵 | 20 / 20 | 20 / 20 |
| 矩阵主页面 | 七页 | 七页 |
| 矩阵编辑器 | 四类 | 四类 |
| 实际 DeviceDpi / 原生窗口 DPI | 96 / 96 | 96 / 96 |
| 实际屏幕 | 1024×768 | 1024×768 |
| 实际工作区 | 1024×728 | 1024×720 |
| 矩阵实际窗口范围 | 宽 683–3200，高 336–1952 像素 | 同左 |
| 实际 PNG 数量 | 102 | 102 |
| 成功作业 stderr | 0 字节 | 0 字节 |
| 成功作业失败截图 | 0 张 | 0 张 |
| 成功矩阵 `focusRecoveryCount` | 0 | 0 |
| 矩阵期间 `collectionRunning` | false | false |

GitHub 最新 attempt 的 API 将已成功的 Server 2025 作业镜像为 `120314743961`，但开始和结束时间仍属于 attempt 1；本页及 JSON 使用真实执行作业 `120312791815`，保留镜像 ID 供追溯，不声称该系统在 attempt 2 又执行了一次。

驱动完整性清单仍为 **12 个原驱动工程、65 个原读取方法、74 个原配置参数、176 个来源文件**。界面记录的 77 个反射参数包括适配层扩展，不能与 74 个上游原参数混为同一统计。419 个警告属于保留的迁入源码，未用增量构建结果替代完整构建结果，也未声称零警告。

## 本轮功能覆盖

核心测试包含配置事务、每设备生命周期隔离、CSV 交换、上传过滤与上报诊断，以及已有协议回环、持久化、写入和重连用例。成功 TRX 中 163 项全部执行，无失败或跳过；测试入口见 [运行时配置](../tests/EdgeGateway.Tests/RuntimeStorage/RuntimeConfigurationTests.cs)、[CSV](../tests/EdgeGateway.Tests/RuntimeStorage/TagCsvExchangeTests.cs) 与 [上报诊断](../tests/EdgeGateway.Tests/RuntimeStorage/TelemetryPipelineTests.cs)。

两个系统的 stdout 均包含以下实际界面成功标记，完整原文和文件哈希保存在 JSON 与证据制品中：

| 检查 | 实际覆盖的操作 |
|---|---|
| 全驱动与动态切换 | 15 个驱动的新建/已有配置、动态字段布局、切回恢复各驱动草稿；最终驱动决定新设备默认点位 |
| 已有设备换驱动 | 保留设备/点位 ID 与点表，不兼容地址经完整配置校验拒绝，磁盘保持原值 |
| 设备四入口 | 添加、工具栏编辑、行内配置和普通数据列双击共用配置窗；先选中 A，再操作 B 行仍定位 B |
| 启用与独立启停 | 配置入口启用、双击禁用；保存配置与行启动可用状态一致，停止设备的质量与写入保护生效 |
| 自动保存与导入 | 真实保存失败保持旧配置；平台草稿不被普通设备提交覆盖；重复 ID 和启用串口冲突拒绝 |
| 运行中配置 | A 持续采集期间新增 C，编辑停止/运行 B，模态保存失败后重试，导入 B 的 CSV 点表和新设备 D，平台草稿下启动，以及 B 禁用/删除 |

Windows 功能烟测只运行本地模拟设备，MQTT 保持未启用，不连接现场设备。模拟采集完成并停止后才执行布局矩阵，因此 `collectionRunning=false` 描述矩阵阶段，不表示本轮没有验证采集过程。

### 布局矩阵的实际含义

矩阵覆盖 1366×768、1920×1080、2560×1440、3200×2000 四种名义分辨率，各使用 100% / 125% / 150% / 175% / 200% 五档名义缩放，共 20 个等效工作区场景。报告标记 `viewportSimulation=true`，预留 48 个逻辑像素任务栏空间；实际窗口 Bounds 与请求 Bounds 均一致，实际 DPI 始终为 96。

每个场景检查七个主页面：运行总览、设备与点位、实时监控、历史与趋势、平台与系统、运行日志、通信诊断。四类矩阵编辑器为源 Modbus 设备配置、源 Modbus 点位配置、CSV 导入预览和多行写入弹窗；全 15 个驱动入口另有编辑器烟测覆盖。

`focusRecoveryCount=0` 只描述两份成功矩阵，没有覆盖或抹去首次焦点失败。等效工作区截图证明对应布局和滚动路径可操作，不能作为真实系统 DPI 切换、多显示器往返或全部 Windows 版本已通过的证据。

## 失败与修正经过

### 早期提交的设备行裁剪

源码 [`e2efd791a2c431ca32bc02e69b1b236377278d9a`](https://github.com/kingtentx/edge-gateway/commit/e2efd791a2c431ca32bc02e69b1b236377278d9a) 的 [CI 39922374791](https://github.com/kingtentx/edge-gateway/actions/runs/39922374791) 在两个系统的 **1366×768 / 200% 等效工作区**均失败：设备行操作单元格需要 40 像素高度，只显示 34 像素。

最终提交 `f1d398b` 修正了真实工具栏换行高度和滚动条占宽对可用内容区域的影响，再次执行完整检查。保留原有完整单元格可见性断言，未扩大测试允许的可见边界。最终两个成功矩阵中该场景均通过。

### 最终提交的首次焦点异常与重跑

`f1d398b` 的 CI `39923092261` 在 attempt 1 中，Server 2025 通过，Server 2022 出现焦点异常。该异常的根因尚未确证。随后**仅重跑失败作业**，源码、断言及提交保持不变，Server 2022 在 attempt 2 通过，工作流最终成功。

此次同源重跑证明该次执行通过，不证明焦点异常根因已经定位或修复。两次失败经过分别记录：早期提交的确定性裁剪问题有代码修正；最终提交首次焦点失败只有未改代码的成功重跑，不能混写成同一个已修复问题，也不能宣称本轮首次全部通过。

## 证据下载与校验

- [Server 2022 成功证据，artifact 12488666311](https://github.com/kingtentx/edge-gateway/actions/runs/39923092261/artifacts/12488666311)：实际 attempt 2 作业。
- [Server 2025 成功证据，artifact 12487239139](https://github.com/kingtentx/edge-gateway/actions/runs/39923092261/artifacts/12487239139)：实际 attempt 1 作业。
- [机器可读汇总](live-configuration-verification.json)：保留实际作业与镜像 ID、环境、全部测试计数、成功标记、矩阵场景、证据文件哈希、失败历史、运行包与所选截图信息。

已直接核对两份 `tests.trx`、stdout 成功标记、零字节 stderr、完整 `layout-matrix.json`、截图数量及失败截图缺席；矩阵报告 SHA-256 与已核验摘要一致。

| 文件 | Server 2022 SHA-256 | Server 2025 SHA-256 |
|---|---|---|
| 布局报告 `layout-matrix.json` | `678f798b8be3299d945bc31dd6e1fde551f005cb4540e43f86bbf263ec3109cc` | `e456ba836d0aaf70eb514425d2f48d7e3b792559b71c2c8ffa1df9948c79e478` |
| 完整证据制品 | `c8a1b6b7c502407fc7918a4a3f0e74182727cbf6230d01b2b730997271f17aee` | `39ead26b0d2db418991c630659e80657ac8581f9a0f45fba99770ebf61211784` |

## Windows 运行包核验

[下载 artifact 12487349660](https://github.com/kingtentx/edge-gateway/actions/runs/39923092261/artifacts/12487349660)，解出其中的 `EdgeGateway-win-x64.zip`，再完整解压，运行 `EdgeGateway.WinForms.exe`。这是 `win-x64` 自包含包，附带 .NET Desktop 运行时。

| 核对项 | 实测结果 |
|---|---|
| 内层运行包大小 | **79,126,231 字节** |
| 内层 ZIP 条目 | 602 |
| 外层制品 ZIP / 内层运行包 CRC | 均通过 |
| 必需网关及依赖程序集 | 23 个 |
| 全部迁移驱动程序集 | 12 个 |
| 源文件核对 | 下列七份文件与 `f1d398b` 源码逐项匹配 |
| 点位 CSV 模板 | UTF-8 BOM、CRLF 换行 |

运行包 SHA-256：

```text
efde36d11e4c1e00c493bf761c1858efa3af0b04b4bf255e8840eed69d77dbf5
```

七份已核对文件为 `README.md`、`examples/modbus-rtu-com11.json`、`examples/tag-template.csv`、`docs/live-configuration.md`、`docs/gateway-capability-review.md`、`docs/device-configuration.md` 和 `.agents/skills/develop-edge-gateway/SKILL.md`。

包内 `build-info.json` 实际记录源码 `f1d398b`、工作流 `39923092261`、attempt 2，构建时间 `2026-10-23T08:22:23.3127079+00:00`；其验证摘要为 163 项测试通过、0 失败、20 / 20 布局，实际 DPI 96、OS 10.0.20348、`focusRecoveryCount=0`。这反映打包的 Server 2022 作业，不能用来覆盖 Server 2025 的实际 attempt 1 身份。

**保持 GitHub 原始 CI ZIP，未二次重打包。** 包内 README、Skill 和操作说明对应程序源码 `f1d398b` 当时的内容，可能仍写有等待最终验证的说明；本次验收图片、MD 与 JSON 是后续仅文档提交，不冒充已经包含在原始运行包内。核对包版本使用其实际 `build-info.json`，最终执行结果由本页和机器可读记录补充。

## 实际截图的来源

下列三张图片均来自 Server 2022 attempt 2 的证据制品，已按字节计算 SHA-256 并确认与原图一致；未生成示意图或修改截图内容。具体操作画面见 [运行中配置截图](live-configuration.md#实际-winforms-截图)。

| 仓库图片 | 原证据文件 | 尺寸 / 字节 |
|---|---|---|
| [运行配置失败保留草稿](images/live-configuration-device.png) | `ui-smoke-live-configuration-retained-draft.png` | 740×650 / 31,675 |
| [CSV 点表预览](images/live-configuration-tags.png) | `ui-smoke-tag-import-preview.png` | 740×650 / 28,822 |
| [通信诊断](images/live-configuration-diagnostics.png) | `ui-smoke-live-configuration-diagnostics.png` | 1024×728 / 57,899 |

三张图片的完整 SHA-256、来源作业与制品 ID 均已写入 [JSON](live-configuration-verification.json)。通信诊断截图展示界面字段与状态，不表示已对现场 Broker 或业务平台完成接收、去重和入库验收。

## 尚未完成的现场范围

本轮没有执行用户 COM10 ↔ COM11 虚拟串口、真实 PLC/CNC、厂商本机库或用户现场 MQTT Broker 联调，也没有完成现场 24–72 小时长稳测试。真实 Windows 多档 DPI、跨显示器往返与远程桌面重连仍需对应环境执行。

自动化中的发布接口故障控制和本地协议回环证明各自的取消、确认、补传和失败处理语义，不等同于某个业务平台入库成功。Broker QoS 1 确认与平台业务处理是不同阶段。本轮仍保留 [能力对照](gateway-capability-review.md#6-后续工作按影响排序) 中的后续工作，不以全部源驱动已迁入替代现场设备验收。
