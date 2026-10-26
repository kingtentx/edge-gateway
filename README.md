# Edge Gateway · .NET 8 WinForms 采集网关

**2026-10-26 后台代理启动修复：** 修复 `GatewayWorkerClient.Invoke` 泛型异步调用查找基类私有方法时发生的空引用。Debug/Release 构建、5 项真实命名管道代理测试及架构检查通过；未执行界面烟测或全量回归。[原因与验证](docs/worker-proxy-startup-fix.md)。

**2026-10-26 卸载与初始化修复：** 卸载驱动只移除注册记录，保留 DLL 和停用清单；新库在驱动加载后仅初始化可用驱动对应的默认设备，校验后再保存，避免重新引用已卸载的模拟驱动导致启动失败。Debug / Release 构建、22 项定向测试及架构检查通过，未执行全量回归或界面烟测。[说明与证据](docs/driver-plugin-management.md#卸载保留文件与新库初始化2026-10-26)。

**2026-10-26 SQLite 分表：** 网关、上报、MQTT/RPC、日志、设备连接、点位和数据库映射按功能与类型保存，移除整包 JSON 配置表，合并重复驱动元数据。当前与上一份配置保持事务一致；旧库明确拒绝，不自动清空或迁移。Release 构建、50 项定向测试及架构检查通过，未运行全量回归。[表结构与验证记录](docs/sqlite-configuration-schema.md)。

**2026-10-26 映射手动常量：** 字段映射取消“使用采样”后仍写入手动常量，整数列可填写 `192` 并按元数据解析；错误质量类型和无效常量在保存前拒绝。值来源显示 `raw_value [原始值]` 等字段名与中文说明。经确认，最终 Release 构建、17 项定向测试、架构检查及隔离后台弹窗检查通过；未连接业务数据库或运行全量回归。[操作与验证说明](docs/data-recording.md#手动常量与来源名称2026-10-26)。

**2026-10-26 服务器资源可视化：** 首页 CPU 在左、内存在右，后台每秒持续采样，切页返回补全最近 60 秒真实曲线；较窄窗口降为单列，真实失败保留缺失提示。Debug/Release 构建、16 项资源采样与 IPC 定向测试、架构检查和隔离后台宽窄检查通过，保留 4 张截图；未执行全量回归或完整 DPI 矩阵。[说明与验证状态](docs/overview-dashboard.md#服务器资源图表2026-10-26)。

**2026-10-26 映射保存反馈修复：** 新增写入组自动避开已有名称；手动重名时定位到“写入设置 → 写入组名称”，保留字段映射输入。编辑已有组按稳定 ID 排除自身。3 项核心定向测试通过；首轮 WinForms 编译发现新增弹窗检查代码变量重名，已修正，按用户要求未重编译或执行弹窗检查。详见 [写入组名称](docs/data-recording.md#写入组名称与保存失败2026-10-26)。

**日志目录：** 程序运行日志保存到程序根目录 `logs`，点位采样日志保存到 `logs/data`，分别轮转和保留；不再创建 `AppData/logs`。详见 [运行目录约定](docs/runtime-directory-layout.md#日志分目录2026-10-25)。

**2026-10-26 日志精简：** 主文件名改为 `yyyyMMdd.log`，大小轮转使用数字序号；采样只保留一层业务字段，空单位/错误不输出，失败质量和错误仍记录。查询与导出同步支持新格式；WinForms Release 编译通过，26 项日志定向测试通过。详见 [日志格式及验证](docs/data-recording.md#日志配置)。

**2026-10-25 驱动插件管理：** 内置与上传驱动均支持查看详情、更新 DLL 与卸载，也可上传新增驱动；同 DLL 的驱动整组处理。更新需停止引用设备并通过现有配置校验，卸载需解除所有设备引用，移除注册记录但保留 DLL，重启不自动加载；新库默认配置避开已卸载驱动。实现和验证状态见 [驱动插件管理](docs/driver-plugin-management.md)。

**2026-10-25 输入区优化：** 各页面和弹窗的单行输入区按普通按钮统一高度，覆盖文本、密码、下拉、数值和日期字段，并保持高 DPI 居中及键盘操作。[实现与验证](docs/input-height.md)。

参考 [iioter/iotgateway](https://github.com/iioter/iotgateway) 的分层与驱动扩展思路，使用 **.NET 8 + 原生 Windows Forms** 实现的桌面工业数据采集网关。网关主体独立实现，并将源项目 **`Plugins/Drivers` 下全部 12 个驱动工程**及其驱动接口迁入本仓库，接入统一采集、配置与监控界面。围绕 Kepware 的采集侧工作流程完善通道、设备、点位管理、诊断与读写核对，并提供实时监控、历史趋势、多平台 MQTT 与持久化补传。

新版数据存储：[SQLite 仅配置、采集日志与外部数据库](docs/data-recording.md)。默认采集只写滚动日志；按需启用 MySQL / PostgreSQL / Oracle，支持每点一行和自定义字段映射。仅支持 schema 3，不保留旧历史或提供迁移。

**2026-10-25 凭据直存：** 数据库/MQTT 密码、平台 Token 和驱动授权码改为直接输入并保存，界面掩码显示，移除对应环境变量字段。[字段与验证说明](docs/direct-credentials.md)。

**2026-10-25 配置保存修复：** 修正启动刷新驱动清单时提前释放共用 SQLite 连接的问题，恢复刷新后的配置保存与插件登记。[连接生命周期说明](docs/runtime-directory-layout.md#sqlite-的职责) · [本次验证记录](docs/data-recording.md#sqlite-连接生命周期修复2026-10-25)。

开发规范入口：[项目架构与依赖](docs/architecture.md) · [开发 SKILL](.agents/skills/develop-edge-gateway/SKILL.md) · [全部驱动迁移清单](docs/driver-migration.md) · [上游结构参考](.agents/skills/develop-edge-gateway/references/architecture.md) · [驱动扩展](.agents/skills/develop-edge-gateway/references/driver-development.md) · [历史验证记录](docs/verification.md)

功能闭环说明：[运行中配置、点表交换与通信诊断](docs/live-configuration.md) · [Kepware 核心能力实现](docs/kepware-core-capabilities.md) · [平台接入与 RPC](docs/platform-protocols.md) · [源项目与主流网关能力对照](docs/gateway-capability-review.md)

界面与显示适配：[本轮操作改造与验收](docs/ui-usability.md) · [DPI 规范与历史说明](docs/ui-dpi.md) · [Windows 构建与运行包](https://github.com/kingtentx/edge-gateway/actions/workflows/build.yml) · [源码 ZIP](https://github.com/kingtentx/edge-gateway/archive/refs/heads/main.zip)。采用统一现代工业界面、FontAwesome 图标和 PerMonitorV2；区分真实 Windows DPI 运行检查与 20 种等效工作区布局验证。

**2026-10-24 数据库菜单修复：** 修正高 DPI 下首次点击“数据库”时的窗口句柄创建异常，共用页签的修复同时覆盖平台与日志页面；本机 192 DPI 导航及 418 项核心测试通过。完整布局矩阵仍存在数据库写入组弹窗焦点检查失败，详见 [本次验证与限制](docs/ui-usability.md#数据库菜单句柄修复2026-10-24)。

**本轮数据记录改造：** SQLite 仅保存 schema 3 网关配置；采集默认输出可配置的滚动日志，外部 MySQL / PostgreSQL / Oracle 支持标准点行和自定义字段映射。旧 SQLite 历史和旧配置不迁移、不兼容。核心回归已通过 388 项，真实 PostgreSQL / MySQL 集成分别通过 22 / 24 项，详细证据与边界见 [数据记录](docs/data-recording.md)。Windows 原生界面和发布包以 [PR #1 的实际检查](https://github.com/kingtentx/edge-gateway/pull/1/checks) 为准，操作规则与证据入口见 [操作改造与验收](docs/ui-usability.md)。

**已验证的上一版：** OPC UA 对外服务端及其配置字段、页面、专用依赖和验收入口已完整移除，不保留兼容层；本轮继续保持这一范围。全部 12 个迁移驱动（含南向 `OPC.UaClient`）、核心采集和 MQTT/RPC 保留。程序提交 `e3c5466` 通过 [Windows Server 2022 / 2025 CI](https://github.com/kingtentx/edge-gateway/actions/runs/39977938638)：当时两个作业各 319 项测试通过，分别完成 7 页、5 类弹窗、20 个等效工作区及 122 张截图检查，实际 DPI 为 96。[该版 Windows x64 运行包](https://github.com/kingtentx/edge-gateway/actions/runs/39977938638/artifacts/12513230047)及完整证据见 [服务端移除与验证记录](docs/opc-ua-service-removal.md)，不代表本轮改造或真实高 DPI 跨屏已验证。

**历史发布基线：** 程序源码 [`f1d398b`](https://github.com/kingtentx/edge-gateway/commit/f1d398ba27728e919c3fd56f60cbf510312267d8)、[Windows x64 运行包](https://github.com/kingtentx/edge-gateway/actions/runs/39923092261/artifacts/12487349660)、[CI 39923092261](https://github.com/kingtentx/edge-gateway/actions/runs/39923092261) 对应上一轮运行中配置功能。Windows Server 2022 / 2025 当时各通过 **163 / 163 项测试、20 / 20 个等效工作区布局**，覆盖七个主页面及四类矩阵编辑器，各保存 102 张实际 PNG。Server 2025 在 attempt 1 通过；Server 2022 首次焦点异常后，在同一提交、未改代码或断言的情况下仅重跑失败作业，于 attempt 2 通过。详情保留在 [历史验证记录](docs/live-configuration-verification.md) 和 [机器可读证据](docs/live-configuration-verification.json)。

## 当前功能

软件中的容量单位统一显示为 `M`，按 1 M = 1024 × 1024 字节计算。

标准采集表默认名为 `gateway_point`。连接测试成功后检查默认 Schema 下该表是否可访问；缺表须在标准点行写入组点击“创建数据表”并确认，采集写入不自动建表，已有表只校验、不改结构。保留事件、设备/点位 ID 与名称、地址、原始值、工程值、UTC 时间和质量共 10 列。名称、地址和两个值字段均为 varchar(128)，超长写入明确失败，详见 [字段约定](docs/data-recording.md#方式一每个点位一行)。旧标准表和旧连接配置不迁移。

| 模块 | 已实现内容 |
|---|---|
| 桌面管理 | 深色导航、FontAwesome 图标、圆角按钮与卡片、统一表格；运行总览、设备与点位、实时监控、历史与趋势、平台与系统、运行日志、数据库、通信诊断八个页面 |
| DPI 与分辨率 | PerMonitorV2、统一 96 DPI 基准、当前屏幕工作区约束、跨 DPI 后重排；小屏卡片换列、滚动表单与固定操作栏 |
| 设备配置 | 添加、编辑、行内配置及双击共用一个窗体；运行中可维护设备与点位，保存后仅重启发生变更的运行设备，其他会话保持；失败保留配置及弹窗输入 |
| 设备维护 | 通道/分组筛选与关键字查找；复制设备生成新的设备、点位 ID，副本默认停用；通道调度、设备连接与点表分别配置 |
| 点位模型 | 数据类型、寄存区、字节序、倍率/偏移、受限数值表达式及试算、点位扫描周期、变化死区、单位、上下限、读写及上传开关 |
| 设备与点位交换 | 设备、点位均支持 Excel (.xlsx) 模板/导入/导出；设备含通道和驱动参数，支持按标识更新；点位支持按名称更新，保留 CSV 入口；完整预览校验 |
| 上游驱动迁移 | 全部 12 个原驱动工程；保留 65 个原读取方法、74 个原配置参数及协议辅助源码 |
| 驱动专用配置 | 根据驱动元数据生成参数编辑器；点位可选择原读取方法、原数据类型及字节序 |
| 内置模拟 | sine、ramp、random、constant:数值、bool；开箱可演示；明确标记为模拟设备 |
| Modbus TCP | FC1/2/3/4 连续地址自动批量读取；FC5/6/16 写入；完整 MBAP、字节数、功能码和写入回显验证 |
| Modbus RTU | 共用连续地址批量读取；真实 SerialPort 通信、CRC16、帧间隔、超时与取消；当前每个串口一个启用设备 |
| 采集引擎 | 每设备独立启停；同设备读写互斥、同通道串行和请求间隔、不同通道并行；设备/点位周期扫描、退避重连与可选故障降速；最后一台停止后关闭共享采集输出/MQTT任务 |
| 诊断与写入 | 停止设备短连接诊断、运行设备复用会话单点读取；写入仅下发一次，可显式读回核对，区分提交、一致、不一致、超时和取消 |
| 告警与审计 | 点位高低阈值状态变化日志、通信统计、手动写入确认与结果；远程写命令持久占位、结果与重复请求保护 |
| 本地配置 | SQLite 结构版本 4，17 张关系表按功能存储强类型配置；WAL / FULL 事务更新当前和上一份；模型/JSON 交换保持 schema 3，旧库不迁移 |
| 采集记录 | 默认写可配置的滚动 JSONL 日志；可选 MySQL / PostgreSQL / Oracle，标准每点一行或已有业务表字段映射 |
| 数据库写入 | 每组独立后台批处理、连接共享行数/字节配额、有限重试与显式丢弃计数；外库故障不等待采集循环 |
| 协议状态 | 独立原子文件保存 MQTT outbox 和 RPC 写入占位/结果；正常重启后恢复 |
| 云端通信 | MQTT 3.1.1、QoS1、TLS、直接保存密码及 Token；Generic、ThingsBoard、ThingsCloud、ThingsPanel、IoTSharp 专用格式；周期、变化、变化加心跳三种策略 |
| 远程 RPC | 真实订阅与请求/响应，仅允许 read/write；默认关闭远程写，校验目标、权限、容量和超时，复用设备读写服务并防止重复下发 |
| 离线补传 | 持久化 outbox、重启重发、稳定消息 ID、队列容量保护；ThingsCloud 等待属性应用响应，其余模式等待 Broker QoS 1 确认 |
| 通信诊断 | 设备读写批次、失败、耗时、降速及单次操作结果；MQTT 入队、连接、发送确认、积压和重试；RPC 订阅、拒绝、重放和错误 |
| 查询与导出 | 按设备/点位/时间查询历史、数值曲线、CSV 导出、配置 JSON 导入导出、日志导出 |
| 扩展机制 | 独立驱动工厂与契约，管理窗口展示全部可用驱动、来源与版本；扫描 `drivers` 下可信程序集，上传安装后立即注册 |
| 工程交付 | 标准 `.sln`、自动测试、Windows Server 2022 / 2025 CI、窗体启动及工作区矩阵烟测、Windows x64 自包含发布脚本 |

运行总览以设备总数、在线、异常和停止数量为主，显示采样质量、连接/重连及上报输出状态，不展示设备列表。服务器 CPU、物理内存和本地固定磁盘容量在底部辅助显示，详见 [首页指标与验证](docs/overview-dashboard.md)。

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

首次启动且尚无已保存配置时，包含一台启用的“演示机组”、一台**未启用**的 Modbus TCP 示例，以及一台**未启用**的“Modbus RTU 从站 1（COM11）”，后者预置从站 1、9600 / 8N1、保持寄存器地址 0–9。进入“设备与点位”，点击“演示机组”所在行的“启动”，即可看到该设备的温度、频率、运行状态和目标温度；该行“停止”只停止演示机组。默认自动启动与 MQTT 均关闭。已有配置继续原样加载，可通过“更多操作 → 追加导入设备…”追加 [RTU 示例](docs/modbus-rtu-com11.md)。

### 发布可直接运行的 Windows 包

```powershell
powershell -ExecutionPolicy Bypass -File scripts/publish-win-x64.ps1
```

输出为 `artifacts/EdgeGateway-win-x64.zip`。完整解压后双击 `EdgeGateway.WinForms.exe`。自包含发布包自带 .NET 运行时；若自行选择依赖框架的发布方式，则需安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)。

运行包中的 `build-info.json` 用于核对源码提交、CI run/attempt 与打包作业的实际测试、布局和 DPI 摘要；手工本地发布的 `verification` 为 null，不冒充 CI 验证结果。

GitHub Actions 工作流会在提交后构建、测试、启动窗体并生成同类运行包，见 [Actions](https://github.com/kingtentx/edge-gateway/actions)。工作流是否实际通过以该次执行结果为准。

## 连接现场设备

在“设备与点位”点击 **添加设备**，或使用设备行 **配置**、双击普通数据列，均使用同一个配置窗和相同的字段布局。弹框默认显示“采集设置”，首行 **启用设备** 复选框下依次提供设备名称、分组、驱动下拉框、采集周期和通信超时。切换到连接或驱动参数页后，内置 Modbus TCP 显示网络字段，RTU 显示串口字段；迁移驱动按源项目的参数、类型与枚举生成编辑器。在同一弹框中来回切换驱动会保留各驱动的参数草稿。

点击保存按钮（停止设备时为“保存配置”，运行设备时为“保存并应用”）后，弹窗等待完整候选配置校验、原子保存和运行时应用全部成功才关闭；失败时显示原因并保留输入，原配置与运行会话不因校验或保存失败而变化。新建设备生成所选驱动的默认点位，保存后保持停止；编辑已有设备保留设备 ID 和已有点位，换驱动不会自动转换或清空点位，不兼容的点位会使完整校验失败。

设备 A 运行时，可以新增、编辑、删除或追加导入设备 B，也可以维护 B 的点位。若 B 原本停止，保存后仍停止；若 B 正在运行，保存变更后只重启 B，禁用或删除 B 则只停止 B。A 的连接、最新值与共享采集输出/MQTT 任务保持运行。连接失败会显示错误并进入原有重连流程，已经保存的参数不会自动回滚。完整流程见 [运行中配置与数据闭环](docs/live-configuration.md)、[参数说明](docs/device-configuration.md) 与 [单设备启停及自动保存](docs/device-control-autosave.md)。

按 Modbus Slave 截图连接 COM10 / COM11 虚拟串口对，参见 [Modbus RTU 从站 1（COM11）配置与导入步骤](docs/modbus-rtu-com11.md)；已有配置可用“更多操作 → 追加导入设备…”追加 [单设备示例 JSON](examples/modbus-rtu-com11.json)，保留当前网关、MQTT 和已有设备配置。

迁移驱动：在“设备与点位”选择上述原目录名对应驱动，填写驱动专用参数；添加点位时选择具体读取方法。OPC 默认选择 `ReadNode`，源 Modbus 默认选择 `HoldingRegisters` / `InputRegisters` / `Coil` / `Input` 等方法；原有演示 `Read` 入口已改为真实读取的兼容别名。Fanuc 依具体采集项选择相应方法。参数与方法说明见 [驱动清单](docs/driver-migration.md)，可导入 [12 个驱动的未启用示例](examples/migrated-drivers.json) 查看配置。

以下步骤适用于本项目原有的 `modbus-tcp` / `modbus-rtu`：

1. 在“设备与点位”点击“添加设备”，其他设备可以继续采集。在默认打开的“采集设置”页填写名称、分组，选择 `modbus-tcp` 或 `modbus-rtu`，勾选“启用设备”并配置周期、超时。
2. TCP 填写 IP/域名、端口、站号；RTU 填写 COM 端口、波特率、数据位、校验位、停止位与站号。
3. 点击“保存配置”，生成所选驱动的默认点位，经完整配置校验和自动保存后添加设备；新增设备保持停止。
4. 在下方点位表按实际点表修改默认点位或添加点位，填写**从 0 开始的地址**、寄存区、数据类型、字节序和工程量换算。每次确认后自动校验并保存。
5. 确认设备已启用，再点击该设备行的“启动”。在“实时监控”核对原始值、工程值、质量和时间，配置外部标准点表后在“历史与趋势”查询采样。每行“停止”仅停止对应设备。

超时覆盖一次设备采集循环，驱动也对单次交换限时；点位多或串口低速时应按实际通信时间配置。

原生 `modbus-tcp` / `modbus-rtu` 自动将本轮到期点位中同寄存区的连续或重叠地址合并读取，不跨地址空洞；每批最多 125 个寄存器或 2000 位。100 个连续 UInt16 点可由 100 次请求降为 1 次，1000 个降为 8 次，已用真实 TCP 回环核对请求数。实际周期和吞吐仍取决于设备响应、链路及输出处理，详见 [Modbus 批量读取与验证](docs/modbus-batch-reading.md)。迁移驱动 `PLC.ModBusMaster` 继续使用其原有批量/缓存方法。

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

实时页“写入选中点位”默认启用写后读回核对，可配置超时和数值容差。写操作只下发一次，核对阶段按限定次数主动读取；结果区分 `Submitted`、`Matched`、`Mismatched`、`TimedOut`、`Failed`、`Cancelled`。超时可能表示设备已执行但确认未到达，不能自动重写。输入寄存器与离散输入不允许写入，表达式点位也只读。操作规则见 [真实诊断与写入核对](docs/kepware-core-capabilities.md#3-真实诊断与写入核对)。

### 批量维护点位

在 **设备导入导出** 菜单下载 Excel 模板、导入设备、导出所选或全部设备。设备工作簿仅保留“设备 / 点位”两页，通道和驱动参数并入设备行，设备导出包含全部关联点位。新增模板的键和标识留空，点位按设备名称关联，系统自动生成标识；已有数据导出保留标识供更新。导入支持追加和按标识更新，先预览校验再一次保存；文件未列出的设备、点位和参数保留。同标识通道参数冲突会拒绝，新通道需停止全部设备后导入。

选中设备后，在 **点位操作** 中下载 Excel 模板、导入或导出当前设备点位，默认格式为 `.xlsx`，文件对话框仍可选择 CSV。点位导入可追加或按名称更新，更新保留原标识及未列出的点位。模板提供可修改的示例行与中文表头；真实地址、类型及方法须符合驱动。文件最多 8 MiB，每台设备最多 2000 点。详见 [Excel 工作簿使用说明](docs/excel-configuration-exchange.md)；原 [CSV 字段说明](docs/live-configuration.md#点位-csv-导入导出) 继续适用。


设备页可按通道、分组及设备/点位关键字筛选，通过“更多操作 → 复制所选设备”创建独立副本。“通道管理”维护通信调度域；“更多操作 → 替换整份配置…”要求全停并确认，不能当作追加导入。副本的设备和点位均分配新 ID，默认停用，确认前可调整连接参数。通道将采集、诊断、写入纳入同一串行调度；它不共享驱动连接，也不放开同一串口只能有一个启用设备的限制。扫描模式、故障降速和表达式示例见 [Kepware 核心能力实现](docs/kepware-core-capabilities.md)。

## MQTT 上报

可以随时在“平台与系统”编辑 Broker、端口、ClientId、主题和上报周期，或点击“重置修改”恢复已保存值。平台输入先作为独立草稿，未应用时设备仍可按已保存的设置启动；普通设备保存不会覆盖这份草稿。**应用平台全局设置和“导入配置”整份替换仍要求所有设备停止**，避免切换共享服务的连接配置。默认主题：`edge/{gatewayId}/telemetry`。启用 TLS 时通常使用 Broker 配置的 TLS 端口（例如 8883），证书必须通过系统验证；程序不会跳过证书错误。

直接填写用户名和“密码”；平台将 Token 用作用户名时，填写“平台 Token”，其值优先于普通用户名。无密码时留空。密码、Token 与驱动授权码按输入原值保存到配置 SQLite，并包含在配置 JSON 导出中；输入框掩码显示，日志和诊断继续脱敏。真实凭据与现场配置不提交到 Git。

“平台与系统”选择 Generic、ThingsBoard、ThingsCloud、ThingsPanel 或 IoTSharp，并选择周期全量、仅变化、变化加心跳。旧配置默认周期全量；变化判断包含工程值死区、质量和错误，只有完整快照成功持久化后推进比较与心跳基线。仅纳入启用设备的启用且允许上传点位；关闭上传后本地采集、实时值与已配置的数据输出保持，旧队列继续补传。详见 [遥测与数据语义](docs/telemetry.md)。

`messageId` 在补传中保持不变，拆分设备消息时以设备标识与消息 ID 联合去重。发送成功后才移除 outbox：ThingsCloud 还须收到 `gateway/attributes/response` 的 `result=1`，其余模式等待 Broker QoS 1 PUBACK。确认后删除前中断仍可能重投，因此保持至少一次语义；这些确认都不等于下游业务已完成去重、处理或入库。

队列满时保留旧消息并暂停生成后续遥测快照，采集与已配置的数据输出继续，暂停期间的中间变化不会逐条还原。切换平台类型前必须排空旧 outbox，否则在配置保存前拒绝。同平台修改 Broker、连接或账号标识且存在待传消息时，界面会要求确认是否迁移；取消不会保存。确认后旧记录以原主题和载荷发往新连接，程序不会改写旧报文，建议先使用原连接完成补传。

RPC 与“允许远程写入”分别默认关闭。启用后真实订阅对应平台命令主题，只有 `read`、`write` 两种点位方法；写入还须设备运行、点位启用可写，并经过统一读回核对和 文件请求去重。无结果的已占位写请求返回 `Indeterminate`，不会自动重复执行。主题、认证、平台连接通知、容量限制及示例见 [平台接入与 RPC](docs/platform-protocols.md)。

“通信诊断”页显示最近入队、发送、发送确认、累计确认、待补传数量与最早时间、重试、设备通信批次和 RPC 状态；确认次数可能包含重复投递。设备表同时显示最近成功采集、连续失败和错误，帮助区分现场、存储与平台链路。

## 配置、日志与数据库

数据目录路径显示在“平台与系统 → 网关设置”页，可按显示的路径管理文件。

| 路径 | 用途 |
|---|---|
| `gateway.config.db` | 仅网关、设备、点位、日志及数据库映射等配置；事务内保存当前/上一份配置 |
| `gateway.config.db-wal` / `gateway.config.db-shm` | 配置库的 SQLite 运行文件 |
| `logs/yyyyMMdd.log` | 程序运行事件 JSONL；按大小、份数和天数轮转 |
| `logs/data/yyyyMMdd.log` | 点位采样 JSONL；独立轮转及保留 |
| `messaging/outbox/` | 未确认 MQTT 报文 |
| `messaging/rpc/` | RPC 写入占位与结果 |

“运行日志 → 日志设置”可配置等级、采样日志、单文件大小、保留份数与天数，支持运行中保存；默认 Information、采样开启、10 MiB / 文件、30 份、30 天，文件数与天数同时约束。

“数据库”提供连接测试、读取表/字段、显式创建标准点表和写入状态。标准模式一条采样一行，保留原始值、工程值、质量和源时间；自定义逐点模式让多个选中点位共用字段映射、各写一行；自定义整行模式把足够新鲜且完整的固定关联点位组合成业务行，支持字段来源、类型和手动常量。映射页可直接读取字段，自增列自动排除；[配置示例](docs/data-recording.md#自定义逐点多个点位共用一套映射)。连接与写入组保存要求全部设备停止；密码直接输入并随连接配置保存。

采集线程与数据库 I/O 分离。队列满、重试耗尽、超大采样或停止时未排空的记录会明确计数；外库队列不写本地 SQLite，不承诺断电无损。标准点表使用稳定 `event_id` 对同批重试去重；自定义业务表的唯一键及重复处理由业务表定义。历史趋势只查询外部标准点表，默认无数据库时显示空状态。

设备、点位与配置通过应用层校验、SQLite 原子保存后同步受影响会话。日志可热更新，其他全局配置仍需全停；保存失败保留原会话和输入。仅接受 schema 3，旧 `gateway.json` / `gateway.db` 不再读取；开发环境使用新空数据目录，不迁移或保留旧 SQLite 历史。备份当前配置应在正常停止后复制完整数据目录。

详细操作、字段表、快照时机、配额与可靠性边界见 [配置、采集日志与外部数据库](docs/data-recording.md)。

## 解决方案结构

| 路径 | 内容 |
|---|---|
| `src/EdgeGateway.Model` | 配置和运行数据模型 |
| `src/EdgeGateway.Abstractions` | 驱动、存储、日志、发布器接口 |
| `src/EdgeGateway.DataAccess` | SQLite 配置、JSON 交换、文件协议状态、MySQL / PostgreSQL / Oracle 连接器 |
| `src/EdgeGateway.Service` | 采集运行时、插件、校验、纯平台报文规则、RPC 与日志 |
| `src/EdgeGateway.Application` | 桌面用例入口、配置命令和预览确认、查询、快照与编辑器元数据 |
| `src/EdgeGateway.Hosting` | 数据目录占用、配置/存储/驱动/MQTT 组装与资源释放 |
| `src/EdgeGateway.Infrastructure.Mqtt` | MQTTnet、TLS、连接、订阅与 QoS 1 传输确认 |
| `src/EdgeGateway.IotGateway` | 全部源驱动的工厂、参数/方法元数据、线程与接口适配 |
| `src/EdgeGateway.WinForms` | Windows 桌面交互、独立草稿、选择与展示；通过应用层执行业务 |
| `Plugins/PluginInterface` | 迁移后的原驱动接口、属性与数据契约 |
| `Plugins/Drivers` | 12 个原驱动工程及本项目模拟 / Modbus 工程 |
| `tests/EdgeGateway.Tests` | 驱动、存储、运行时、应用/宿主边界与 MQTT 回环测试 |
| `.agents/skills/develop-edge-gateway` | 可复用开发 skill 及架构、驱动、验收参考 |
| `examples/` | 不含凭据的示例配置 |
| `scripts/` | Windows x64 构建和打包 |
| `.github/workflows/` | Windows 构建、测试、启动与发布 |

依赖方向、资源所有权及新增功能的放置位置见 [项目架构](docs/architecture.md)。Hosting 是桌面进程的组合边界，不表示已提供 Windows Service。

## 验证和边界

按开发规范，执行测试前先说明建议范围、目的和预计耗时，询问并获得用户同意；不因每次功能修改自动执行全部测试。以下命令仅供确认后按需选择，界面测试在隔离后台桌面运行。未执行的测试如实标明，不使用历史结果代替本次验证。

```powershell
dotnet test tests/EdgeGateway.Tests/EdgeGateway.Tests.csproj -c Release
# Windows 上执行；检查界面与隔离配置中的本地模拟采集，不连接现场设备
./scripts/smoke-test-winforms-background.ps1
```

核心自动测试包含实际 TCP 回环与 TLS 握手，覆盖采集策略、表达式、读写核对、五平台 JSON、MQTT 订阅/回复、ThingsCloud 应用响应和持久请求去重。本轮 Windows 脚本必须实际验证 8 个主页面、7 类矩阵弹窗、20 个等效工作区、149 张 PNG 和 14 个完成标记，包含真实菜单/页签、选择保持、查询条件与反馈检查，并保留真实焦点与 DPI 边界断言；发布前再次核对同一源码的测试、截图和矩阵证据。实际运行结果及产物见 [PR 检查](https://github.com/kingtentx/edge-gateway/pull/1/checks) 和 [本轮验收入口](docs/ui-usability.md#执行证据)。真实 PLC、串口、南向 OPC UA 目标服务器和用户现场平台租户仍需对应环境验收，自动测试不等于现场验收；现场还需核对点表、字节序、串口硬件、证书信任与 24–72 小时长稳运行。

历史交付另行保留：[驱动迁移基线](docs/verification.md)、[单设备启停](docs/device-control-autosave.md#此前版本验证与运行包)、[配置窗合并版本](docs/device-configuration-unification.md#实际执行结果)。这些文档中的运行包、截图和通过数量对应各自源码提交。

本项目原生 Modbus 驱动按点位请求；源 Modbus / MC 驱动保留原有批量和缓存方法，需按其说明配置。通道提供通信串行调度，串口仍按设备独占，不提供共享串口多从站连接。当前是桌面程序，关闭后停止采集；未包含 Windows Service、桌面多角色权限管理、OTA、VPN、跨点位表达式、所有驱动逐帧抓包或完整 Kepware 产品能力。已实现的平台协议仍需真实租户配置验收。配置数量限制不代表同规模性能已测；源驱动未实现的写入方法继续明确拒绝。

## 许可与来源

本项目原创部分采用 [MIT License](LICENSE)。迁入源码保留上游与各第三方的原版权和许可证，详见 [第三方说明](THIRD-PARTY-NOTICES.md)；FANUC 绑定、厂商原生库和商业组件不因本仓库的 MIT 许可而改变其授权条件。实际 NuGet 依赖及其许可证见包元数据，发布包包含相应许可说明。
