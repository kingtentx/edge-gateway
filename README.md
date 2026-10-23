# Edge Gateway · .NET 8 WinForms 采集网关

参考 [iioter/iotgateway](https://github.com/iioter/iotgateway) 的分层与驱动扩展思路，使用 **.NET 8 + 原生 Windows Forms** 实现的桌面工业数据采集网关。网关主体独立实现，并将源项目 **`Plugins/Drivers` 下全部 12 个驱动工程**及其驱动接口迁入本仓库，接入统一采集、配置与监控界面。围绕 Kepware 的采集侧工作流程完善通道、设备、点位管理、诊断与读写核对，并提供实时监控、历史趋势、多平台 MQTT、持久化补传及 OPC UA 对外服务。

开发规范入口：[开发 SKILL](.agents/skills/develop-edge-gateway/SKILL.md) · [全部驱动迁移清单](docs/driver-migration.md) · [结构参考](.agents/skills/develop-edge-gateway/references/architecture.md) · [驱动扩展](.agents/skills/develop-edge-gateway/references/driver-development.md) · [验证记录](docs/verification.md)

功能闭环说明：[运行中配置、点表交换与通信诊断](docs/live-configuration.md) · [Kepware 核心能力实现](docs/kepware-core-capabilities.md) · [平台接入与 RPC](docs/platform-protocols.md) · [OPC UA 对外服务](docs/opc-ua-server.md) · [源项目与主流网关能力对照](docs/gateway-capability-review.md)

界面与显示适配：[DPI 与界面升级说明](docs/ui-dpi.md) · [Windows 构建与运行包](https://github.com/kingtentx/edge-gateway/actions/workflows/build.yml) · [源码 ZIP](https://github.com/kingtentx/edge-gateway/archive/refs/heads/main.zip)。采用统一现代工业界面、FontAwesome 图标和 PerMonitorV2；区分真实 Windows DPI 运行检查与 20 种等效工作区布局验证。

**2026-10-23 当前状态：本轮功能集成中，新的源码提交、运行包和完整验证结果待统一 CI 确认。** 下方功能说明对应当前开发源码；此前列出的六项差距已补充代码，OPC UA 对外服务正在同轮集成。不能用旧版本的测试数、页面数或截图数证明新增功能已经验收。

**历史发布基线：** 程序源码 [`f1d398b`](https://github.com/kingtentx/edge-gateway/commit/f1d398ba27728e919c3fd56f60cbf510312267d8)、[Windows x64 运行包](https://github.com/kingtentx/edge-gateway/actions/runs/39923092261/artifacts/12487349660)、[CI 39923092261](https://github.com/kingtentx/edge-gateway/actions/runs/39923092261) 对应上一轮运行中配置功能。Windows Server 2022 / 2025 当时各通过 **163 / 163 项测试、20 / 20 个等效工作区布局**，覆盖七个主页面及四类矩阵编辑器，各保存 102 张实际 PNG。Server 2025 在 attempt 1 通过；Server 2022 首次焦点异常后，在同一提交、未改代码或断言的情况下仅重跑失败作业，于 attempt 2 通过。详情保留在 [历史验证记录](docs/live-configuration-verification.md) 和 [机器可读证据](docs/live-configuration-verification.json)。

![运行设备配置提交失败后保留输入](docs/images/live-configuration-device.png)

上图来自历史基线 `f1d398b` 的实际 WinForms 检查，展示运行设备编辑失败后保留草稿。两套成功环境均为 .NET 8.0.32、实际/原生窗口 DPI 96、屏幕 1024×768；20 种矩阵为等效工作区模拟，不替代真实多 DPI 切换、跨显示器或现场设备通信验收。

## 当前功能

| 模块 | 已实现内容 |
|---|---|
| 桌面管理 | 深色导航、FontAwesome 图标、圆角按钮与卡片、统一表格；运行总览、设备与点位、实时监控、历史与趋势、平台与系统、运行日志、通信诊断、OPC UA 服务八个页面；本轮布局矩阵结果待 CI |
| DPI 与分辨率 | PerMonitorV2、统一 96 DPI 基准、当前屏幕工作区约束、跨 DPI 后重排；小屏卡片换列、滚动表单与固定操作栏 |
| 设备配置 | 添加、编辑、行内配置及双击共用一个窗体；运行中可维护设备与点位，保存后仅重启发生变更的运行设备，其他会话保持；失败保留配置及弹窗输入 |
| 设备维护 | 通道/分组筛选与关键字查找；复制设备生成新的设备、点位 ID，副本默认停用；通道调度、设备连接与点表分别配置 |
| 点位模型 | 数据类型、寄存区、字节序、倍率/偏移、受限数值表达式及试算、点位扫描周期、变化死区、单位、上下限、读写及上传开关 |
| 点表交换 | UTF-8 BOM、20 列 CSV 模板与导出；旧 17 列兼容；追加或按名称更新，完整候选预览校验，8 MiB / 2000 点限制 |
| 上游驱动迁移 | 全部 12 个原驱动工程；保留 65 个原读取方法、74 个原配置参数及协议辅助源码 |
| 驱动专用配置 | 根据驱动元数据生成参数编辑器；点位可选择原读取方法、原数据类型及字节序 |
| 内置模拟 | sine、ramp、random、constant:数值、bool；开箱可演示；明确标记为模拟设备 |
| Modbus TCP | FC1/2/3/4 读取；FC5/6/16 写入；完整 MBAP、字节数、功能码和写入回显验证 |
| Modbus RTU | 真实 SerialPort 通信、CRC16、帧间隔、超时与取消；当前每个串口一个启用设备 |
| 采集引擎 | 每设备独立启停；同设备读写互斥、同通道串行和请求间隔、不同通道并行；设备/点位周期扫描、退避重连与可选故障降速；最后一台停止后关闭共享历史/MQTT任务 |
| 诊断与写入 | 停止设备短连接诊断、运行设备复用会话单点读取；写入仅下发一次，可显式读回核对，区分提交、一致、不一致、超时和取消 |
| 告警与审计 | 点位高低阈值状态变化日志、通信统计、手动写入确认与结果；远程写命令持久占位、结果与重复请求保护 |
| 本地数据 | JSON schema 2 原子配置与上一版备份；SQLite WAL 历史及 outbox，数据库 schema 2 增加 RPC 审计表 |
| 云端通信 | MQTT 3.1.1、QoS1、TLS、环境变量凭据；Generic、ThingsBoard、ThingsCloud、ThingsPanel、IoTSharp 专用格式；周期、变化、变化加心跳三种策略 |
| 远程 RPC | 真实订阅与请求/响应，仅允许 read/write；默认关闭远程写，校验目标、权限、容量和超时，复用设备读写服务并防止重复下发 |
| OPC UA 服务 | 独立北向 UA TCP 端点，浏览/读取/订阅，证书信任和用户认证，默认远程写关闭；独立启停，不因最后一台设备停止而关闭 |
| 离线补传 | 持久化 outbox、重启重发、稳定消息 ID、队列容量保护；ThingsCloud 等待属性应用响应，其余模式等待 Broker QoS 1 确认 |
| 通信诊断 | 设备读写批次、失败、耗时、降速及单次操作结果；MQTT 入队、连接、发送确认、积压和重试；RPC 订阅、拒绝、重放和错误 |
| 查询与导出 | 按设备/点位/时间查询历史、数值曲线、CSV 导出、配置 JSON 导入导出、日志导出 |
| 扩展机制 | 独立驱动工厂与契约，扫描 `plugins` 下可信驱动程序集，重启加载 |
| 工程交付 | 标准 `.sln`、自动测试、Windows Server 2022 / 2025 CI、窗体启动及工作区矩阵烟测、Windows x64 自包含发布脚本 |

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

首次启动且尚无已保存配置时，包含一台启用的“演示机组”、一台**未启用**的 Modbus TCP 示例，以及一台**未启用**的“Modbus RTU 从站 1（COM11）”，后者预置从站 1、9600 / 8N1、保持寄存器地址 0–9。进入“设备与点位”，点击“演示机组”所在行的“启动”，即可看到该设备的温度、频率、运行状态和目标温度；该行“停止”只停止演示机组。默认自动启动与 MQTT 均关闭。已有配置继续原样加载，可通过“导入设备”追加 [RTU 示例](docs/modbus-rtu-com11.md)。

### 发布可直接运行的 Windows 包

```powershell
powershell -ExecutionPolicy Bypass -File scripts/publish-win-x64.ps1
```

输出为 `artifacts/EdgeGateway-win-x64.zip`。完整解压后双击 `EdgeGateway.WinForms.exe`。自包含发布包自带 .NET 运行时；若自行选择依赖框架的发布方式，则需安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)。

运行包中的 `build-info.json` 用于核对源码提交、CI run/attempt 与打包作业的实际测试、布局和 DPI 摘要；手工本地发布的 `verification` 为 null，不冒充 CI 验证结果。

GitHub Actions 工作流会在提交后构建、测试、启动窗体并生成同类运行包，见 [Actions](https://github.com/kingtentx/edge-gateway/actions)。工作流是否实际通过以该次执行结果为准。

## 连接现场设备

在“设备与点位”点击工具栏 **添加设备**、**编辑设备**，或点击设备行 **配置**、双击普通数据列，均使用同一个配置窗和相同的字段布局。弹框默认显示“采集设置”，首行 **启用设备** 复选框下依次提供设备名称、分组、驱动下拉框、采集周期和通信超时。切换到连接或驱动参数页后，内置 Modbus TCP 显示网络字段，RTU 显示串口字段；迁移驱动按源项目的参数、类型与枚举生成编辑器。在同一弹框中来回切换驱动会保留各驱动的参数草稿。

点击“确定”后，弹窗等待完整候选配置校验、原子保存和运行时应用全部成功才关闭；失败时显示原因并保留输入，原配置与运行会话不因校验或保存失败而变化。新建设备生成所选驱动的默认点位，保存后保持停止；编辑已有设备保留设备 ID 和已有点位，换驱动不会自动转换或清空点位，不兼容的点位会使完整校验失败。

设备 A 运行时，可以新增、编辑、删除或追加导入设备 B，也可以维护 B 的点位。若 B 原本停止，保存后仍停止；若 B 正在运行，保存变更后只重启 B，禁用或删除 B 则只停止 B。A 的连接、最新值与共享历史/MQTT 任务保持运行。连接失败会显示错误并进入原有重连流程，已经保存的参数不会自动回滚。完整流程见 [运行中配置与数据闭环](docs/live-configuration.md)、[参数说明](docs/device-configuration.md) 与 [单设备启停及自动保存](docs/device-control-autosave.md)。

按 Modbus Slave 截图连接 COM10 / COM11 虚拟串口对，参见 [Modbus RTU 从站 1（COM11）配置与导入步骤](docs/modbus-rtu-com11.md)；已有配置可用“导入设备”追加 [单设备示例 JSON](examples/modbus-rtu-com11.json)，保留当前网关、MQTT 和已有设备配置。

迁移驱动：在“设备与点位”选择上述原目录名对应驱动，填写驱动专用参数；添加点位时选择具体读取方法。OPC 默认选择 `ReadNode`，源 Modbus 默认选择 `HoldingRegisters` / `InputRegisters` / `Coil` / `Input` 等方法；原有演示 `Read` 入口已改为真实读取的兼容别名。Fanuc 依具体采集项选择相应方法。参数与方法说明见 [驱动清单](docs/driver-migration.md)，可导入 [12 个驱动的未启用示例](examples/migrated-drivers.json) 查看配置。

以下步骤适用于本项目原有的 `modbus-tcp` / `modbus-rtu`：

1. 在“设备与点位”点击“添加设备”，其他设备可以继续采集。在默认打开的“采集设置”页填写名称、分组，选择 `modbus-tcp` 或 `modbus-rtu`，勾选“启用设备”并配置周期、超时。
2. TCP 填写 IP/域名、端口、站号；RTU 填写 COM 端口、波特率、数据位、校验位、停止位与站号。
3. 点击“确定”，生成所选驱动的默认点位，经完整配置校验和自动保存后添加设备；新增设备保持停止。
4. 在下方点位表按实际点表修改默认点位或添加点位，填写**从 0 开始的地址**、寄存区、数据类型、字节序和工程量换算。每次确认后自动校验并保存。
5. 确认设备已启用，再点击该设备行的“启动”。在“实时监控”核对原始值、工程值、质量和时间，在“历史与趋势”查询采样。每行“停止”仅停止对应设备。

超时覆盖一次设备采集循环，驱动也对单次交换限时；点位多或串口低速时应按实际通信时间配置。

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

选中设备后使用“点位模板”“导出点位”和“导入点位”维护 CSV 点表；仓库提供 [空白模板](examples/tag-template.csv)。模板包含 20 个业务字段，不含 `Id`；新增 `Expression`、`ScanIntervalMs`、`Deadband`。旧 17 列文件仍可导入，按名称更新时缺失的扩展列保留现有值；显式空 `Expression` 清除表达式，显式数值扩展列不能留空。导出为 UTF-8 BOM；导入可选“追加”或“按名称更新”，后者保留匹配点位 ID 和文件中未出现的点位。文件最多 8 MiB，单次最多 2000 条数据，合并后每台设备最多 2000 点；预览最多 500 条，校验和保存覆盖完整点表。任意校验失败时整批不保存。运行设备导入后仅重启该设备，详见 [CSV 字段与操作说明](docs/live-configuration.md#点位-csv-导入导出)。

设备页还可按通道、分组及设备/点位关键字筛选，使用“复制设备”创建独立副本。副本的设备和点位均分配新 ID，默认停用，确认前可调整连接参数。通道将采集、诊断、写入纳入同一串行调度；它不共享驱动连接，也不放开同一串口只能有一个启用设备的限制。扫描模式、故障降速和表达式示例见 [Kepware 核心能力实现](docs/kepware-core-capabilities.md)。

## MQTT 上报

可以随时在“平台与系统”编辑 Broker、端口、ClientId、主题和上报周期，或点击“重置修改”恢复已保存值。平台输入先作为独立草稿，未应用时设备仍可按已保存的设置启动；普通设备保存不会覆盖这份草稿。**应用平台全局设置和“导入配置”整份替换仍要求所有设备停止**，避免切换共享服务的连接配置。默认主题：`edge/{gatewayId}/telemetry`。启用 TLS 时通常使用 Broker 配置的 TLS 端口（例如 8883），证书必须通过系统验证；程序不会跳过证书错误。

配置文件只存密码环境变量名称，默认 `EDGE_MQTT_PASSWORD`。平台 Token 用作用户名时，填写“用户名/令牌环境变量”，环境变量内容覆盖普通用户名；平台允许空密码时将密码环境变量名留空。启动前设置环境变量，再启动程序。不要把密码或 Token 填入变量名输入框，也不要提交真实凭据或现场配置到 Git。

“平台与系统”选择 Generic、ThingsBoard、ThingsCloud、ThingsPanel 或 IoTSharp，并选择周期全量、仅变化、变化加心跳。旧配置默认周期全量；变化判断包含工程值死区、质量和错误，只有完整快照成功持久化后推进比较与心跳基线。仅纳入启用设备的启用且允许上传点位；关闭上传后本地采集、实时值与历史保持，旧队列继续补传。详见 [遥测与数据语义](docs/telemetry.md)。

`messageId` 在补传中保持不变，拆分设备消息时以设备标识与消息 ID 联合去重。发送成功后才移除 outbox：ThingsCloud 还须收到 `gateway/attributes/response` 的 `result=1`，其余模式等待 Broker QoS 1 PUBACK。确认后删除前中断仍可能重投，因此保持至少一次语义；这些确认都不等于下游业务已完成去重、处理或入库。

队列满时保留旧消息并暂停生成后续遥测快照，采集与历史继续，暂停期间的中间变化不会逐条还原。切换平台类型前必须排空旧 outbox，否则在配置保存前拒绝。同平台修改 Broker、连接或账号标识且存在待传消息时，界面会要求确认是否迁移；取消不会保存。确认后旧记录以原主题和载荷发往新连接，程序不会改写旧报文，建议先使用原连接完成补传。

RPC 与“允许远程写入”分别默认关闭。启用后真实订阅对应平台命令主题，只有 `read`、`write` 两种点位方法；写入还须设备运行、点位启用可写，并经过统一读回核对和 SQLite 请求去重。无结果的已占位写请求返回 `Indeterminate`，不会自动重复执行。主题、认证、平台连接通知、容量限制及示例见 [平台接入与 RPC](docs/platform-protocols.md)。

“通信诊断”页显示最近入队、发送、发送确认、累计确认、待补传数量与最早时间、重试、设备通信批次和 RPC 状态；确认次数可能包含重复投递。设备表同时显示最近成功采集、连续失败和错误，帮助区分现场、存储与平台链路。

## OPC UA 对外服务

新增“OPC UA 服务”页提供北向服务的配置、独立启停和证书/认证入口。默认端点为 `opc.tcp://localhost:4840/EdgeGateway`，服务默认关闭；标准客户端可浏览设备/点位、读取和订阅。仅启用 `Basic256Sha256` 的 `SignAndEncrypt`，需要双方证书信任。匿名访问默认只读；用户认证和远程写分别配置，密码仅从环境变量读取。授权写入复用设备写入与读回核对，只有匹配成功返回 Good。

UA 服务独立于采集会话：停止最后一台设备后端点仍可在线，但相应值的质量不能冒充新成功采样；普通设备配置更新不会重新启动手动停止的 UA 服务。它与南向 `OPC.UaClient` 驱动各自承担不同方向的连接。部署和客户端操作见 [OPC UA 服务说明](docs/opc-ua-server.md)；本轮标准客户端回环与 Windows 页面验收结果待统一记录。

## 本地数据与恢复

默认数据目录：`%LOCALAPPDATA%\EdgeGateway`，可在界面中打开。

| 文件 | 用途 |
|---|---|
| `gateway.json` | 已保存的设备、点位与系统配置 |
| `gateway.json.bak` | 上一份成功替换前的配置 |
| `gateway.db` | SQLite 历史、待发送消息和 RPC 命令审计 |
| `gateway.db-wal` / `gateway.db-shm` | SQLite 运行时文件，运行中不要单独删除或拷贝数据库 |
| `logs/` | 按 UTC 日期记录并轮转的运行日志 |

设备、点位与导入配置通过同一个运行时事务入口提交：在生命周期锁内校验完整候选、原子保存，再同步受影响设备；顶部不再提供“保存配置”。保存失败不停止原会话，弹窗保留输入。保存提交后完成运行时同步，调用方随后取消不撤回已保存配置；物理连接结果由设备状态和日志反映。平台输入通过“应用设置”单独提交。正常修改会保留上一版 `.bak`。配置损坏会报错并保留原文件，不会静默重置。恢复前停止程序并备份数据目录，再用确认无误的 `.bak` 恢复。备份数据库时先正常关闭程序后复制整个数据目录。

JSON 配置仍为 schema 2；旧版本 1 在加载时内存迁移，保存前不改写原文件，设备和点位 ID 保持不变。新增字段均提供兼容缺省：上传开启、通道为空、设备周期扫描、降速关闭、空表达式、点位周期/死区为 0、周期上报、Generic、RPC/远程写及 UA 服务关闭。SQLite 自身独立升级到数据库 schema 2，仅新增 RPC 审计表，保留历史和 outbox。未知版本拒绝，破坏兼容的变更仍须显式迁移，见 [配置兼容规则](docs/live-configuration.md#配置兼容规则)。

默认保留 30 天、最多 1,000,000 条历史，定期清理；数据库文件空间可复用，删除记录不会立即缩小物理文件。内存日志最多 2,000 条；文件日志保留最近 30 个 UTC 日期，每日最多当前文件及 2 个轮转文件，每个约 10 MiB。

历史采样先进入有界内存缓冲，再批量写入 SQLite；正常停止等待缓冲处理完。突然断电可能丢失尚未落盘的历史和尚未生成的快照。已成功持久化的 outbox 在重启后仍然可发送。

## 解决方案结构

| 路径 | 内容 |
|---|---|
| `src/EdgeGateway.Model` | 配置和运行数据模型 |
| `src/EdgeGateway.Abstractions` | 驱动、存储、日志、发布器接口 |
| `src/EdgeGateway.DataAccess` | JSON 配置与 SQLite |
| `src/EdgeGateway.Service` | 采集运行时、插件、校验、MQTT、日志 |
| `src/EdgeGateway.OpcUa` | 北向 OPC UA Server、地址空间、证书与受控读写 |
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
# Windows 上执行；检查界面与隔离配置中的本地模拟采集，不连接现场设备
dotnet run --project src/EdgeGateway.WinForms -c Release -- --smoke-test --screenshot artifacts/ui-smoke.png
```

核心自动测试包含实际 TCP 回环与 TLS 握手；本轮新增采集策略、表达式、读写核对、五平台 JSON、MQTT 订阅/回复、ThingsCloud 应用响应和持久请求去重用例，UA 服务另补标准客户端回环。新的完整通过数量、八页布局矩阵和截图数量待统一 CI，见 [验证记录](docs/verification.md)。历史 `f1d398b` 的 163 项结果只见证其原有功能，保留在 [历史验证](docs/live-configuration-verification.md)。**尚未连接真实 PLC、串口设备或用户现场平台租户，自动测试不等于现场验收。** 现场仍需核对点表、字节序、串口硬件、证书与 24–72 小时长稳运行。

历史交付另行保留：[驱动迁移基线](docs/verification.md)、[单设备启停](docs/device-control-autosave.md#此前版本验证与运行包)、[配置窗合并版本](docs/device-configuration-unification.md#实际执行结果)。这些文档中的运行包、截图和通过数量对应各自源码提交。

本项目原生 Modbus 驱动按点位请求；源 Modbus / MC 驱动保留原有批量和缓存方法，需按其说明配置。通道提供通信串行调度，串口仍按设备独占，不提供共享串口多从站连接。当前是桌面程序，关闭后停止采集与对外服务；未包含 Windows Service、桌面多角色权限管理、OTA、VPN、跨点位表达式、所有驱动逐帧抓包或完整 Kepware 产品能力。已实现的平台协议仍需真实租户配置验收。配置数量限制不代表同规模性能已测；源驱动未实现的写入方法继续明确拒绝。

## 许可与来源

本项目原创部分采用 [MIT License](LICENSE)。迁入源码保留上游与各第三方的原版权和许可证，详见 [第三方说明](THIRD-PARTY-NOTICES.md)；FANUC 绑定、厂商原生库和商业组件不因本仓库的 MIT 许可而改变其授权条件。实际 NuGet 依赖及其许可证见包元数据，发布包包含相应许可说明。
