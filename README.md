# Edge Gateway · .NET 8 WinForms 采集网关

参考 [iioter/iotgateway](https://github.com/iioter/iotgateway) 的分层与驱动扩展思路，使用 **.NET 8 + 原生 Windows Forms** 实现的桌面工业数据采集网关。网关主体独立实现，并将源项目 **`Plugins/Drivers` 下全部 12 个驱动工程**及其驱动接口迁入本仓库，接入统一采集、配置与监控界面。包含设备与点位管理、实时监控、历史趋势、MQTT 上报与持久化补传。

开发规范入口：[开发 SKILL](.agents/skills/develop-edge-gateway/SKILL.md) · [全部驱动迁移清单](docs/driver-migration.md) · [结构参考](.agents/skills/develop-edge-gateway/references/architecture.md) · [驱动扩展](.agents/skills/develop-edge-gateway/references/driver-development.md) · [验证记录](docs/verification.md)

已验证交付：[Windows x64 运行包](https://github.com/kingtentx/edge-gateway/actions/runs/39818478291/artifacts/12443481330) · [Windows 构建与 93 项测试结果](https://github.com/kingtentx/edge-gateway/actions/runs/39818478291) · [源码 ZIP](https://github.com/kingtentx/edge-gateway/archive/refs/heads/main.zip)。完整驱动界面烟测生成 36 张截图，证据与构建警告说明见验证记录。

## 当前功能

| 模块 | 已实现内容 |
|---|---|
| 桌面管理 | 中文界面，运行总览、设备与点位、实时监控、历史与趋势、平台与系统、运行日志六页 |
| 设备配置 | 设备分组、连接参数、启停、点位新增/编辑/删除；运行时禁止修改配置 |
| 点位模型 | 数据类型、寄存区、字节序、倍率/偏移、单位、上下限、只读/可写、启用状态 |
| 上游驱动迁移 | 全部 12 个原驱动工程；保留 65 个原读取方法、74 个原配置参数及协议辅助源码 |
| 驱动专用配置 | 根据驱动元数据生成参数编辑器；点位可选择原读取方法、原数据类型及字节序 |
| 内置模拟 | sine、ramp、random、constant:数值、bool；开箱可演示；明确标记为模拟设备 |
| Modbus TCP | FC1/2/3/4 读取；FC5/6/16 写入；完整 MBAP、字节数、功能码和写入回显验证 |
| Modbus RTU | 真实 SerialPort 通信、CRC16、帧间隔、超时与取消；当前每个串口一个启用设备 |
| 采集引擎 | 每设备独立任务、同设备读写互斥、取消/停止、退避重连、原始值与工程值、数据质量 |
| 告警与审计 | 点位高低阈值状态变化日志、通信故障、手动写入确认与结果日志 |
| 本地数据 | JSON 原子配置与上一版备份；SQLite WAL 历史，按天数/行数清理 |
| 云端通信 | MQTT 3.1.1、QoS1、用户名/环境变量密码、TLS 系统证书验证、通用 JSON 遥测 |
| 离线补传 | 遥测持久化 outbox、收到确认后删除、重启重发、消息 ID 去重依据、队列满告警 |
| 查询与导出 | 按设备/点位/时间查询历史、数值曲线、CSV 导出、配置 JSON 导入导出、日志导出 |
| 扩展机制 | 独立驱动工厂与契约，扫描 `plugins` 下可信驱动程序集，重启加载 |
| 工程交付 | 标准 `.sln`、自动测试、Windows CI、窗体启动烟测、Windows x64 自包含发布脚本 |

上游结构与迁移基准固定在 [`8de442a7dec52d2993d1e9667d799846936d681b`](https://github.com/iioter/iotgateway/commit/8de442a7dec52d2993d1e9667d799846936d681b)。上游该版本目标为 .NET 10；本项目将驱动重新编译为 .NET 8，保留原目录名与方法，通过适配层接入桌面程序。迁移范围包含所有源驱动，不包含上游 Web/WTM 界面与数据库；不承诺直接加载上游 .NET 10 生成的 DLL。

## 全部迁移驱动

| 原目录 / 稳定驱动标识 | 协议或设备 |
|---|---|
| `PLC.SiemensS7` | Siemens S7，含源驱动标准地址及字节字符串方法 |
| `PLC.MelsecMc` | Mitsubishi MC，含源驱动单点、批量与缓存读取 |
| `PLC.OmronFins` | Omron FINS |
| `PLC.AllenBradley` | Allen-Bradley EtherNet/IP |
| `PLC.ModBusMaster` | 源 Modbus 主站，TCP/UDP、RTU/ASCII 及网络封装模式 |
| `OPC.UaClient` | OPC UA 节点读取 |
| `OPC.DaClient` | OPC DA，依赖本机 COM/DCOM 环境 |
| `CNC.Fanuc` | Fanuc FOCAS，依赖厂商 x64 原生运行库 |
| `CNC.Fanuc.H` | HslCommunication Fanuc，按库要求提供有效授权 |
| `CNC.MTConnect` | MTConnect HTTP 数据项读取 |
| `Other.Toledo` | 源 Toledo 称重设备驱动 |
| `Mock.TcpClient` | 源 TCP 示例驱动，连接实际 TCP 测试端 |

原有 `simulator`、`modbus-tcp`、`modbus-rtu` 同时保留，共 15 个驱动入口。迁移驱动的参数、读取方法和外部依赖详见 [迁移说明](docs/driver-migration.md)。源码迁移与编译通过不代表已对每种现场设备完成联调。

## 快速启动

### 从源码运行

在 Windows 10/11 或具备桌面环境的 Windows Server 上安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。Visual Studio 用户可使用支持 .NET 8 的 VS 2022，并安装“.NET 桌面开发”工作负载。

```powershell
git clone https://github.com/kingtentx/edge-gateway.git
cd edge-gateway
dotnet restore EdgeGateway.sln
dotnet build EdgeGateway.sln -c Release
dotnet run --project src/EdgeGateway.WinForms -c Release --no-build
```

也可以直接打开 `EdgeGateway.sln`，把 `EdgeGateway.WinForms` 设为启动项目。

首次启动包含一台启用的“演示机组”和一台**未启用**的 Modbus TCP 示例。点击“启动采集”，即可看到温度、频率、运行状态和目标温度。默认 MQTT 关闭，程序不会自动连接现场设备或云端平台。

### 发布可直接运行的 Windows 包

```powershell
powershell -ExecutionPolicy Bypass -File scripts/publish-win-x64.ps1
```

输出为 `artifacts/EdgeGateway-win-x64.zip`。完整解压后双击 `EdgeGateway.WinForms.exe`。自包含发布包自带 .NET 运行时；若自行选择依赖框架的发布方式，则需安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)。

GitHub Actions 工作流会在提交后构建、测试、启动窗体并生成同类运行包，见 [Actions](https://github.com/kingtentx/edge-gateway/actions)。工作流是否实际通过以该次执行结果为准。

## 连接现场设备

迁移驱动：在“设备与点位”选择上述原目录名对应驱动，填写驱动专用参数；添加点位时选择具体读取方法。OPC 默认选择 `ReadNode`，源 Modbus 默认选择 `HoldingRegisters` / `InputRegisters` / `Coil` / `Input` 等方法；原有演示 `Read` 入口已改为真实读取的兼容别名。Fanuc 依具体采集项选择相应方法。参数与方法说明见 [驱动清单](docs/driver-migration.md)，可导入 [12 个驱动的未启用示例](examples/migrated-drivers.json) 查看配置。

以下步骤适用于本项目原有的 `modbus-tcp` / `modbus-rtu`：

1. 停止采集，在“设备与点位”添加设备，选择 `modbus-tcp` 或 `modbus-rtu`。
2. TCP 填写 IP/域名、端口、站号；RTU 填写 COM 端口、波特率、数据位、校验位、停止位与站号。
3. 配置采集周期和超时。超时覆盖一次设备采集循环，驱动也对单次交换限时；点位多或串口低速时应按实际通信时间配置。
4. 按设备点表添加点位，填写**从 0 开始的地址**、寄存区、数据类型、字节序和工程量换算。
5. 保存后启动，在“实时监控”核对原始值、工程值、质量和时间；在“历史与趋势”查询采样。

例如：厂家“40001”若确认为首个保持寄存器，对应本项目 `HoldingRegister` / 地址 `0`；厂家若已经给出 0 基偏移则直接使用。不要把参考地址 40001 直接当作偏移。

| 参数 | 支持范围 |
|---|---|
| 站号 | 1–247；不支持广播写入 |
| 位区 | Coil、DiscreteInput，仅 Boolean |
| 寄存器类型 | Boolean、Int16、UInt16、Int32、UInt32、Float32、Float64 |
| 32 位字节序 | ABCD、BADC、CDAB、DCBA |
| 64 位字节序 | ABCDEFGH、BADCFEHG、GHEFCDAB、HGFEDCBA，分别对应上述四种枚举 |
| 工程量 | `工程值 = 原始值 × Scale + Offset` |
| 写入 | 默认只读；允许写入后，通过实时页确认写入工程值，服务反算原始值并检查范围 |

写操作不会因超时自动重试。超时可能表示设备已执行但确认未到达，应先读回确认。输入寄存器与离散输入不允许写入。

## MQTT 上报

在“平台与系统”配置 Broker、端口、ClientId、主题和上报周期。默认主题：`edge/{gatewayId}/telemetry`。启用 TLS 时通常使用 Broker 配置的 TLS 端口（例如 8883），证书必须通过系统验证；程序不会跳过证书错误。

配置文件只存密码环境变量名称，默认 `EDGE_MQTT_PASSWORD`。使用用户名认证时，启动程序前在 Windows 用户环境变量中设置该变量，然后重新启动程序。不要把密码填到“密码环境变量名”输入框，也不要提交密码或真实现场配置到 Git。

遥测采用周期最新值快照，格式见 [遥测与数据语义](docs/telemetry.md)。`messageId` 在补传中保持不变，接收端按此去重。已确认消息才从 SQLite outbox 删除；确认到达与删除之间崩溃可能重复投递，语义为至少一次。

队列满时保留旧消息并暂停生成后续遥测快照，采集与历史继续。恢复后继续补传，再生成新的最新值快照；暂停期间未生成的中间快照不会逐条还原。改变 Broker 等连接目标前，应先完成旧队列补传，或明确确认将待发送消息迁移到新连接。

## 本地数据与恢复

默认数据目录：`%LOCALAPPDATA%\EdgeGateway`，可在界面中打开。

| 文件 | 用途 |
|---|---|
| `gateway.json` | 已保存的设备、点位与系统配置 |
| `gateway.json.bak` | 上一份成功替换前的配置 |
| `gateway.db` | SQLite 历史数据和待发送消息 |
| `gateway.db-wal` / `gateway.db-shm` | SQLite 运行时文件，运行中不要单独删除或拷贝数据库 |
| `logs/` | 按 UTC 日期记录并轮转的运行日志 |

配置损坏会报错并保留原文件，不会静默重置。恢复前停止程序并备份数据目录，再用确认无误的 `.bak` 恢复。备份数据库时先正常关闭程序后复制整个数据目录。

迁移版本使用配置格式 2；旧版本 1 配置在加载时内存升级，保存前不改写原文件，保存时保留 `.bak`。设备与点位 ID 保持不变；未知配置版本会被拒绝。

默认保留 30 天、最多 1,000,000 条历史，定期清理；数据库文件空间可复用，删除记录不会立即缩小物理文件。内存日志最多 2,000 条；文件日志保留最近 30 个 UTC 日期，每日最多当前文件及 2 个轮转文件，每个约 10 MiB。

历史采样先进入有界内存缓冲，再批量写入 SQLite；正常停止等待缓冲处理完。突然断电可能丢失尚未落盘的历史和尚未生成的快照。已成功持久化的 outbox 在重启后仍然可发送。

## 解决方案结构

| 路径 | 内容 |
|---|---|
| `src/EdgeGateway.Model` | 配置和运行数据模型 |
| `src/EdgeGateway.Abstractions` | 驱动、存储、日志、发布器接口 |
| `src/EdgeGateway.DataAccess` | JSON 配置与 SQLite |
| `src/EdgeGateway.Service` | 采集运行时、插件、校验、MQTT、日志 |
| `src/EdgeGateway.IotGateway` | 全部源驱动的工厂、参数/方法元数据、线程与接口适配 |
| `src/EdgeGateway.WinForms` | Windows 桌面界面与组合根 |
| `Plugins/PluginInterface` | 迁移后的原驱动接口、属性与数据契约 |
| `Plugins/Drivers` | 12 个原驱动工程及本项目模拟 / Modbus 工程 |
| `tests/EdgeGateway.Tests` | 驱动、存储、运行时与 MQTT 回环测试 |
| `.agents/skills/develop-edge-gateway` | 可复用开发 skill 及架构、驱动、验收参考 |
| `examples/` | 不含凭据的示例配置 |
| `scripts/` | Windows x64 构建和打包 |
| `.github/workflows/` | Windows 构建、测试、启动与发布 |

## 验证和边界

```powershell
dotnet test tests/EdgeGateway.Tests/EdgeGateway.Tests.csproj -c Release
# Windows 上执行；只做界面与存储初始化，不连接现场设备
dotnet run --project src/EdgeGateway.WinForms -c Release -- --smoke-test --screenshot artifacts/ui-smoke.png
```

当前核心自动测试包含实际 TCP 回环与 TLS 握手；具体结果见 [验证记录](docs/verification.md)。**尚未连接真实 PLC、串口设备或用户现场 Broker，不应将自动测试等同于现场验收。** 现场需核对点表、字节序、串口硬件、TLS 证书和 24–72 小时长稳运行。

本项目原生 Modbus 驱动按点位请求；源 Modbus / MC 驱动保留原有批量和缓存方法，需按其方法说明配置。串口仍按设备独占，不提供多个设备共享一条串行总线的调度。当前是桌面程序，关闭后停止采集；未包含 Windows Service、远程 RPC、用户角色权限、OTA、VPN 或完整平台专用消息适配。配置数量限制是输入保护，不代表已完成相同规模性能验收。源驱动中未实现的写入方法仍明确拒绝，不因迁移而宣称获得写入能力。

## 许可与来源

本项目原创部分采用 [MIT License](LICENSE)。迁入源码保留上游与各第三方的原版权和许可证，详见 [第三方说明](THIRD-PARTY-NOTICES.md)；FANUC 绑定、厂商原生库和商业组件不因本仓库的 MIT 许可而改变其授权条件。实际 NuGet 依赖及其许可证见包元数据，发布包包含相应许可说明。
