# OPC UA 服务端移除记录

按用户要求，当前版本完整移除 OPC UA 对外服务端，不保留服务端配置字段、兼容层或迁移逻辑。网关继续负责设备采集、本地监控与历史、平台 MQTT/RPC 和持久化补传。

## 变更范围

| 范围 | 本次处理 |
| --- | --- |
| 服务端工程与生命周期 | 删除独立服务端工程及宿主的构造、启动、重配置和退出接线，不再提供北向 UA TCP 端点 |
| 配置与校验 | 删除服务端配置模型、网关成员和专用校验，不保留兼容字段或专用迁移逻辑 |
| 桌面界面 | 删除“OPC UA 服务”页面、服务状态、端点操作、证书管理窗口与专属烟测 |
| 项目依赖与发布 | 删除服务端工程引用及仅供服务端使用的包和打包门禁；继续保留南向客户端所需依赖和许可证 |
| 测试与证据 | 删除服务端专用测试及 UA 页面/证书指标，按当前页面与弹窗重新核对统一烟测；旧版本通过数量不转记为本版结果 |
| 文档与开发规范 | 删除服务端使用说明、两张专属截图及混合旧验收文件；更新 README、能力边界、架构和开发 Skill |

## 保留的采集能力

上游 `Plugins/Drivers` 的 **12 个迁移工程全部保留**，包括用于连接外部设备或服务器的南向 `OPC.UaClient` 和 `OPC.DaClient`。加上本项目模拟、Modbus TCP、Modbus RTU 三个驱动，仍有 15 个驱动入口。保留原方法、参数、驱动配置界面、采集质量与错误处理，南向证书验证和许可要求继续按[驱动迁移说明](driver-migration.md)执行。

通道与扫描、设备独立启停、运行中配置、真实连接诊断、一次写入与主动读回、表达式、点位 CSV、五平台 MQTT/RPC、历史和 outbox 不属于本次删除范围。操作与边界见 [Kepware 核心能力](kepware-core-capabilities.md)、[运行中配置](live-configuration.md)和[平台协议](platform-protocols.md)。

## 当前界面验收范围

以下范围已由本轮 Windows Server 2022 和 Windows Server 2025 作业分别检查通过；对应源码、作业和产物见下一节。

| 项目 | 已检查范围 |
| --- | --- |
| 主页面 | 7 页：总览、设备与点位、实时数据、历史趋势、平台与系统、日志、通信诊断 |
| 矩阵弹窗 | 5 类：设备配置、点位编辑、多行写入、点位导入预览、通道编辑；最小工作区的专用配置是额外场景，不重复计类 |
| 等效工作区 | 1366×768、1920×1080、2560×1440、3200×2000 与 100% / 125% / 150% / 175% / 200% 的 20 个组合 |
| PNG | 122 张：7 张主页面 + 45 张驱动视图 + 8 张业务场景 + 20×3 张矩阵图 + 2 张额外场景 |
| 完成标记与输入指标 | 12 个完成标记；通道输入 3 个、写入核对输入 4 个；保留真实焦点、祖先视口边界与 DPI 一致性断言 |

两套 CI 的实际控件 DPI 与原生窗口 DPI 均为 **96**。20 个组合是等效工作区检查，不代替真实高 DPI 初始启动和 100% ↔ 150% ↔ 200% 跨屏验收。

## 本轮执行结果

程序源码提交为 [`e3c5466798852bb2138b77ea4c2c7ad0d4aa00a5`](https://github.com/kingtentx/edge-gateway/commit/e3c5466798852bb2138b77ea4c2c7ad0d4aa00a5)。[GitHub Actions 运行 39977938638](https://github.com/kingtentx/edge-gateway/actions/runs/39977938638) 第 1 次执行于 2026-10-23 15:33:30 UTC 完成，结论为 `success`。本节使用该提交新生成的测试、截图与二进制，未沿用删除前版本的通过结果。

| 验证 | 实际结果 |
| --- | --- |
| 依赖还原 | 成功 |
| 完整 Release 构建 | 本地与两个 Windows 作业均成功，均为 419 个继承上游源码的警告、0 个错误 |
| 核心回归 | 本地与两个 Windows 作业分别为 319/319 通过，0 失败、0 跳过；不包含已删除的服务端专用测试 |
| 驱动迁移清单 | 12 个工程、65 个方法、74 个参数、176 个来源文件检查通过 |
| 依赖与许可清单 | 新依赖图不含 OPC UA Server 包，南向 Client 保留；第三方元数据与现有依赖匹配 |
| Windows 原生界面烟测 | 两个作业分别通过 7 页、5 类弹窗、20 组合及 122 PNG 检查；12 个完成标记齐全，标准错误为空，焦点恢复次数均为 0 |
| 新 Windows x64 运行包 | Server 2022 作业发布成功；下载后核对来源、CRC、SHA-256、12 个迁移驱动、28 个必需程序集以及服务端删除结果 |

### Windows 作业与实际环境

| 项目 | Windows Server 2022 | Windows Server 2025 |
| --- | --- | --- |
| 作业 | [120497354391](https://github.com/kingtentx/edge-gateway/actions/runs/39977938638/job/120497354391)，成功 | [120497354969](https://github.com/kingtentx/edge-gateway/actions/runs/39977938638/job/120497354969)，成功 |
| 实际系统版本 | Microsoft Windows 10.0.20348 | Microsoft Windows 10.0.26100 |
| 运行时 / WinForms | .NET 8.0.32 / 8.0.0.0 | .NET 8.0.32 / 8.0.0.0 |
| 实际显示器 / 工作区 | 1024×768 / 1024×728 | 1024×768 / 1024×720 |
| 实际控件 / 原生窗口 DPI | 96 / 96 | 96 / 96 |
| 矩阵 / 截图 | 20/20 通过，122 张 PNG | 20/20 通过，122 张 PNG |
| 测试 | 319/319 通过 | 319/319 通过 |
| 原始验证产物 | [verification-windows-2022](https://github.com/kingtentx/edge-gateway/actions/runs/39977938638/artifacts/12511868628) | [verification-windows-2025](https://github.com/kingtentx/edge-gateway/actions/runs/39977938638/artifacts/12512751509) |

下载后的两个原始验证产物均完成 ZIP CRC 与 GitHub 摘要核对，并重新执行仓库中的证据检查规则，确认原始 JSON 摘要、TRX 测试计数、完成标记、页面/弹窗集合、输入指标和完整截图清单一致。检查不改写下载的原始证据。界面截图显示 7 个导航项连续排列，无服务端菜单或空占位；南向 `OPC.UaClient` 的设备配置窗口仍可见。

### 运行包与内容核对

新包由同次运行的 Server 2022 作业生成，下载入口为 [EdgeGateway-win-x64](https://github.com/kingtentx/edge-gateway/actions/runs/39977938638/artifacts/12513230047)。Server 2025 负责独立构建、测试和原生界面检查，按现有工作流不重复发布运行包。

| 项目 | 实际结果 |
| --- | --- |
| 包内程序 ZIP | `EdgeGateway-win-x64.zip`，79,389,415 字节、610 个条目 |
| 程序 ZIP SHA-256 | `381a13fda3157b26beecbbbccd46b8db5bbf73ff51ec7275c71898caf8789ba8` |
| 发布方式 | `win-x64`，自包含；WinForms、`hostfxr.dll` 和 `coreclr.dll` 齐全 |
| 包内来源 | `build-info.json` 指向 `e3c5466798852bb2138b77ea4c2c7ad0d4aa00a5`、运行 `39977938638`、第 1 次执行 |
| 服务端程序集 | `EdgeGateway.OpcUa.dll` 和 `Opc.Ua.Server.dll` 均不存在，依赖清单不含已删除的服务端工程或 SDK |
| 客户端与其它驱动 | 12 个上游迁移驱动及全部必需程序集齐全；OPC UA Client、Configuration、Core、Types、Security.Certificates 依赖保留 |
| 文档与专属截图 | 删除的服务端说明、服务页面和证书窗口截图均不存在；抽查的 10 个说明、示例及 Skill 文件与程序源码提交一致 |
| 包内验证摘要 | 319 项测试、20 个工作区、122 张截图、7 页、5 类弹窗；没有服务端专属验证字段 |

程序包保持 CI 原始字节不变；本记录及 README 的最终验收结论作为后续仅文档提交补充。因此包内文档反映发布时的源码快照，最新执行结果以本记录和上述原始 CI 证据为准。

界面检查不代表现场 PLC/CNC/串口或南向 OPC UA 目标服务器已实联，也不覆盖真实高 DPI 跨屏与 24–72 小时长稳。这些边界继续按[验证说明](verification.md)执行。

## 历史追溯

删除前版本保留在 Git 提交 [`556653f670b5f361b4664b62be0ed7ad5bb18dd6`](https://github.com/kingtentx/edge-gateway/tree/556653f670b5f361b4664b62be0ed7ad5bb18dd6)。其中的[旧服务说明](https://github.com/kingtentx/edge-gateway/blob/556653f670b5f361b4664b62be0ed7ad5bb18dd6/docs/opc-ua-server.md)与[旧混合验收记录](https://github.com/kingtentx/edge-gateway/blob/556653f670b5f361b4664b62be0ed7ad5bb18dd6/docs/kepware-opcua-verification.md)仅描述删除前版本。

其它通用能力文档和各自历史验证记录继续保留原提交、运行包及实际结果，不将历史结论改写为当前版本已通过。
