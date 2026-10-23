# 采集网关能力对照与本轮改进范围

更新日期：2026-10-23。上游固定为 [iioter/iotgateway `8de442a7dec52d2993d1e9667d799846936d681b`](https://github.com/iioter/iotgateway/tree/8de442a7dec52d2993d1e9667d799846936d681b)。本页最初相对 `83743cf0870649d9bbf7bd147df17dfb1fbcc158` 审查运行中配置，历史发布证据对应 `f1d398b`。当前继续保留六项差距与核心采集能力，按用户要求完整移除 OPC UA 对外服务端。程序提交 `e3c5466` 的本地与两个 Windows 作业各 319 项测试、两套原生界面证据和新发布包已核验，详见 [服务端移除记录](opc-ua-service-removal.md)；布局矩阵是在实际 96 DPI 下检查等效工作区。旧数量只对应其历史提交。

运行中局部配置、点表批量提交与点位上传开关继续保留。此前列出的停止设备诊断、筛选复制、写后读回、单点表达式、变化/心跳上报、平台适配/RPC 六项差距均已补充代码；同时保留通道调度、点位周期和故障降速。具体操作与边界见 [Kepware 核心能力实现](kepware-core-capabilities.md)和[平台协议](platform-protocols.md)，不声明完整复刻 Kepware。

## 1. 上游已经具备哪些业务能力

上游不只是驱动目录。完整的设备生命周期、变量计算与平台适配还分布在 `Plugins/Plugin`、模型和 ViewModel 中。迁入全部 12 个驱动工程并接通读取方法，不能自动获得这些外围业务能力。

| 范围 | 固定版本的源码证据 | 对当前项目的含义 |
| --- | --- | --- |
| 单设备配置生效 | [`DeviceService.UpdateDevice/UpdateDevices`][up-device-service] 删除并重建设备线程；[`UpdateDevices`][up-update-devices] 从设备、变量及参数变更解析受影响设备 | 配置应按受影响设备应用。其他设备正常运行时，全局禁止新增或修改设备是需要修复的限制 |
| 分组、采集与上报周期 | [`Device`][up-device] 包含树状设备结构、自动启动、变化上传、强制上传周期和命令间隔 | 已有分组/通道筛选、设备/点位周期、通道间隔和三种上报策略；树状分组与组级周期尚未复制 |
| 点位配置 | [`DeviceVariable`][up-variable] 包含表达式、触发标记、上传开关、别名与读写保护 | 已有独立采集/上传开关、线性换算及受限单点表达式；不包含全部触发和别名能力 |
| 点位与设备批量配置 | [`DeviceVariableImportVM`][up-tag-import]、[`ExportDevicesSetting`][up-export]、[`ImportExcelVM`][up-import] 提供 Excel 导入或导出流程 | 本轮新增当前设备的 CSV 点表交换；这与上游 Excel 文件格式兼容是两项不同工作 |
| 计算、触发、别名和写入 | [`Expression`][up-expression]、[`DeviceThread`][up-thread] 处理原值及历史值引用、跨点位引用、触发采集、别名分组和 RPC 写请求 | 单点计算、白名单 RPC 与写后读回已独立实现；历史引用、跨点位依赖和触发采集仍不在当前范围 |
| 变化上报与平台适配 | [`MessageService`][up-message] 使用 `CgUpload/EnforcePeriod`；[`PlatformHandlerFactory`][up-platform-factory] 明确选择 ThingsBoard、ThingsCloud、ThingsPanel、IoTSharp 处理器 | 当前五模式均有明确主题、载荷及 RPC 请求/回复；新增实际订阅和协议回环，真实租户验收另行记录 |

上游平台工厂只有上述四个明确实现分支，其余值进入默认 IoTSharp 分支。不能仅根据 [`SystemConfig`][up-system-config] 中的多个平台名称，声称它们都具有完整、独立的平台实现。ThingsBoard 的专用行为可直接查看 [`ThingsBoardHandler`][up-thingsboard]。

上游的 WTM 用户、角色、菜单、路由和网页框架不是本桌面程序的迁移目标。本轮保留原生 .NET 8 WinForms，借鉴设备管理与运行反馈的业务行为。

## 2. 主流产品官方资料给出的操作原则

以下均为产品官方文档，按问题取用，没有照搬页面布局，也不据此承诺与商业产品功能等同。

| 官方参考 | 已核实的操作行为 | 本项目采用的方向 |
| --- | --- | --- |
| [EMQX Neuron：南向驱动][emqx-device] | 节点运行状态与连接状态分别展示；节点提供自身配置、日志和统计入口 | 区分启用、已启动、连接成功、读值成功；修改一个设备不阻断同级设备 |
| [EMQX Neuron：批量配置][emqx-bulk] | 点表批量导入、复制驱动节点、整体设备导入导出是不同操作 | 明确点表导入的目标设备和冲突规则；与整份网关配置替换分开 |
| [ThingsBoard IoT Gateway：远程配置][tb-config] | 连接器有启停、配置同步状态、工作状态与日志；存储统计展示积压消息和传输过程 | “保存成功”和“设备可通信”分别反馈；提供积压、发送和错误状态。该页面不构成本项目所有配置都能无重启更新的依据 |
| [ThingsBoard IoT Gateway：上报策略][tb-report] | 区分收到即上报、变化上报、周期上报及变化或周期上报 | 当前实现周期全量、仅变化、变化加心跳；按成功入队推进基线，保留源采样时间 |
| [Kepware：点位管理][kep-tags]、[通信诊断][kep-diagnostics] | 点位支持 CSV 交换；诊断可观察读写成功或失败，以及协议收发 | 当前加入通道串行、扫描策略、故障降速、单次诊断与批次统计；仍不提供所有驱动的逐帧 TX/RX 追踪 |

这些资料共同支持一个设计判断：配置、连接、读写和上报应分别给出可验证的结果。连接状态为绿色，不能替代点位地址、类型、字节序及平台接收结果的检查。

## 3. 本轮实际实现

| 优先级 | 改进前的具体问题 | 本轮源码行为 | 保留的边界 |
| --- | --- | --- | --- |
| P0 | 任意设备运行时，其他设备的编辑与新增也被锁定 | 新增 `ApplyConfigurationAsync`：完整校验并原子保存候选配置后，只停止、替换或重启受影响设备；未变化设备继续采集 | 全局 MQTT、网关设置和整份配置替换仍要求停止全部设备 |
| P0 | 编辑运行中设备时，缺少“保存后会影响谁”的明确反馈 | 编辑窗显示针对当前设备的应用影响；保存失败保留输入。成功后只有原先运行、修改后仍启用的受影响设备重连 | 保存不是连通性承诺；错误地址仍会保存，随后由正常连接重试与采集诊断显示失败 |
| P1 | 大量点位只能逐项维护 | 20 列 CSV 模板、预览、导出；兼容原 17 列，支持追加和按名称更新并追加，整批校验后一次保存 | 缺失扩展列保留匹配点原值，显式空表达式清空；不导入 `Id`，不兼容上游 Excel 格式 |
| P1 | 本地需要保留但不希望上传的点位，缺少独立开关 | `TagConfiguration.UploadEnabled` 默认 `true`，编辑器、点位列表和 CSV 均携带该字段；新快照按当前配置筛选 | 关闭上传不停止采集，不清历史，不删除已持久化补传消息 |
| P1 | MQTT 仅连接状态不足以判断数据是否在积压、失败或已确认 | “通信诊断”显示发送确认、重试、积压、持久化、设备批次与 RPC 状态，可导出诊断 JSON | ThingsCloud 确认含属性应用响应，其余为 PUBACK；均不等于最终业务入库 |
| P1 | 设备连接失败原因需要翻找日志 | 设备行状态与错误保留，并提供停止设备临时连接、运行设备会话复用的真实单点诊断 | 仅连接成功单独标示；未增加所有驱动的原始帧抓取 |
| P1 | 设备维护与通信策略缺少统一入口 | 通道/分组/关键字筛选、独立副本、通道互斥与间隔、点位周期、故障降速 | 通道不共享物理驱动连接；仍限制每串口一个启用设备 |
| P1 | 写入提交与现场值核对混在一起 | 写入只下发一次，按命令 ID 主动读回并比较，返回状态、原值、工程值、次数和耗时 | 超时或取消不能证明现场未执行，核对失败不自动重写 |
| P1 | 线性换算与周期全量不能覆盖常用数据处理 | 受限单点表达式和预览；变化死区、质量变化、周期心跳；成功持久化后推进基线 | 表达式只读；不保存检查周期内每一次短暂变化 |
| P1 | 专用平台协议与远程操作缺少闭环 | 五平台主题/载荷、实际订阅、请求响应、权限校验、持久写命令去重与结果 | 远程写默认关闭，租户物模型和现场设备需要部署验收 |

相关实现入口：

- [配置事务及设备会话更新](../src/EdgeGateway.Service/GatewayRuntime.Configuration.cs)、[在线配置界面](../src/EdgeGateway.WinForms/MainForm.Configuration.cs)。
- [CSV 交换服务](../src/EdgeGateway.Service/TagCsvExchange.cs)、[导入预览](../src/EdgeGateway.WinForms/TagImportDialog.cs)、[模板文件](../examples/tag-template.csv)。
- [上报诊断状态模型](../src/EdgeGateway.Model/TelemetryPipelineStatus.cs)、[上报流水线](../src/EdgeGateway.Service/GatewayRuntime.Telemetry.cs)、[通信诊断界面](../src/EdgeGateway.WinForms/MainForm.Diagnostics.cs)。
- [设备操作与读回](../src/EdgeGateway.Service/GatewayRuntime.Operations.cs)、[扫描与通道调度](../src/EdgeGateway.Service/GatewayRuntime.Scheduling.cs)、[表达式](../src/EdgeGateway.Service/NumericExpression.cs)、[变化上报](../src/EdgeGateway.Service/TelemetryReportPlanner.cs)。
- [平台协议适配](../src/EdgeGateway.Service/PlatformMqttAdapter.cs)、[RPC 运行时](../src/EdgeGateway.Service/GatewayRuntime.Rpc.cs)。

此前已经实现的 12 个上游驱动工程、统一驱动参数编辑、原生模拟与 Modbus 驱动、单设备启停、原值和工程值、数据质量、读写互斥、退避重连、SQLite 历史、MQTT QoS 1 补传和本地手工写入继续保留。本轮没有将这些已有能力计为新增功能；驱动范围和依赖见[迁移清单](driver-migration.md)。

## 4. 配置应用应如何理解

### A 运行时维护 B 或新增 C

可以打开 B 的配置、修改点位、导入点表，也可以新增 C。整份候选配置通过校验并保存后，更新相应设备；A 的驱动实例、采集任务和共享历史、MQTT 任务保持运行。新增设备及原先已经停止的设备，保存后不会自动启动。

如果 B 本身正在运行，保存会终止 B 的旧会话和与其关联的旧写入操作，再以新配置启动 B；关闭 B 的“启用设备”则停止 B。删除或禁用最后一个运行设备后，共享任务关闭。仅重启唯一运行设备时，共享任务继续保留。

校验失败、保存失败或提交前取消，不应停止旧设备或替换旧配置。原子保存完成后，运行时会完成与磁盘配置的同步；此时不再因调用方取消而留下“磁盘已新、运行时仍旧”的状态。详细操作规则见[在线配置](live-configuration.md)。

### 点表的两种导入方式

| 方式 | 匹配与身份规则 | 原有点位处理 |
| --- | --- | --- |
| 追加点位 | 所有导入行生成新的点位标识；与当前设备重名会报错 | 全部保留 |
| 按名称更新并追加 | 名称不区分大小写；匹配项保留原 `Id`，未匹配项生成新 `Id` | 文件中未出现的点位仍保留 |

导入先生成独立候选点表，再执行完整网关和驱动校验。名称冲突、非法类型、无效地址或不支持的方法等错误不会产生半批导入。20 列 CSV 使用 UTF-8 BOM，除原有字段外增加表达式、点位扫描周期和上报死区。旧 17 列仍可导入，按名称更新时缺失的扩展列保留原字段；显式空表达式清空，显式空数值扩展字段拒绝。数值使用固定小数点，不随 Windows 区域改变。

## 5. 上报诊断的准确语义

### 四个不同结果

| 阶段 | 确认了什么 | 失败或恢复时的行为 |
| --- | --- | --- |
| 最新快照入队 | 当前快照已经持久化到本地补传队列 | 队列满时暂停生成新快照；存储失败时报告错误并重试。本地采集与历史任务独立继续 |
| MQTT 发送尝试 | 已开始连接或发送这一条待补传消息 | 尚未代表 Broker 已接收；失败保留原消息，按退避策略重试 |
| 发送确认 | ThingsCloud 收到属性应用 `result=1`；其余模式收到 Broker QoS 1 PUBACK | 更新最近确认时间及本进程累计确认次数；仍不代表所有业务规则与入库完成 |
| 本地队列确认移除 | 已确认消息从本地队列删除 | 删除失败仍保留消息，明确显示可能重复发送；后续仍需重试 |

`GetTelemetryStatus()` 返回不可变快照，分别提供最近入队尝试、成功入队、发送尝试、发送确认、发送失败、持久化失败、容量阻塞的时间，以及当前错误、连续失败和下次重试时间。正常取消不会计为失败；恢复后清除当前相应错误，保留最近事件时间。入队、读取队列、删除已确认行的存储错误分别清理，避免一次读取恢复掩盖尚未恢复的删除故障。

累计确认次数是本进程观察到的发送确认次数。同一消息在确认丢失或删除失败后可能重发并再次计数；分设备报文应按设备标识与 `messageId` 联合去重。该计数不是平台业务记录数。ThingsCloud 的属性应用响应比 PUBACK 更进一步，仍须在平台核对下游规则及入库。报文与补传规则见 [遥测协议](telemetry.md)。

### 上传开关的生效边界

新生成快照只包含当前配置中同时满足以下条件的缓存点位：设备 `Enabled`、点位 `Enabled`、点位 `UploadEnabled`。点位关闭上传后，本地最新值和历史仍按采集配置保留。

队列阻塞时尚未入队的快照会在检测到配置更新后重新生成；**已经开始执行入队的单个快照可能仍带旧选择，已持久化的所有消息继续按原主题和原载荷补传。** 删除设备、禁用点位或关闭上传都不会清除历史待传数据，也不会将旧消息改写成新的载荷。

仅手动停止一台仍为 `Enabled` 的设备时，缓存中的最新值会标为不确定；只要其他设备使共享上报继续运行，这些不确定样本仍可能出现在快照中。上传过滤依据配置开关，不把“停止一台设备”解释为“删除此前对外状态”。最后一台设备停止后，上报工作任务随共享任务关闭。

## 6. 原六项差距的当前状态

此前的六项待办已形成代码、操作入口和相应回归用例。下表区分当前实现与仍未覆盖的扩展；代码完成与真实现场验收分别记录。

| 顺序 | 能力 | 当前实现与验证边界 |
| --- | --- | --- |
| 1 | 停止设备的连接诊断 | `ProbeDeviceAsync` 创建临时驱动并真实连接/读取；运行时复用当前会话，返回操作 ID、样本、质量、耗时与错误；没有启用点位时只报告连接成功 |
| 2 | 分组筛选、查找和设备复制 | 通道、分组、设备/驱动/点位/地址关键字筛选；复制深拷贝参数并更新全部身份，默认停用；树状分组和组级周期未包含 |
| 3 | 写入后的读回核对 | `WriteVerifiedAsync` 在同设备及通道锁内写一次并主动读回；区分 Submitted/Matched/Mismatched/TimedOut/Failed/Cancelled，保留类型精度和容差 |
| 4 | 单点计算表达式 | `x` 线性换算后受限 decimal 表达式、保存校验与试算；动态错误标 Bad；表达式点只读，不包含跨点依赖或历史函数 |
| 5 | 变化上报和周期心跳 | Periodic/OnChange/OnChangeWithHeartbeat；绝对工程量死区、质量/错误变化、首次值和心跳，完整入队后推进基线；不是全量采样事件流 |
| 6 | 平台专用适配与远程写控制 | Generic、ThingsBoard、ThingsCloud、ThingsPanel、IoTSharp 明确格式与路由；真实订阅/回复、默认拒写、持久防重复调度和写后读回；真实租户物模型仍待部署联调 |

这些实现继续以 [Kepware 核心能力说明](kepware-core-capabilities.md) 中的通道、扫描与诊断规则为主。OPC UA 对外服务端已按用户要求删除，南向 `OPC.UaClient` 继续保留。Windows Service、桌面多角色、跨点位计算、共享串口多从站、全驱动逐帧追踪及商业插件仍不属于完成声明。

现场依赖也不能通过界面功能代替：Fanuc 仍需合法取得的 FWLIB64 及官方依赖；采用 Hsl 的驱动可能需要配置许可环境变量；OPC DA 依赖匹配位数的 COM/Automation、服务器注册及 DCOM；南向 OPC UA 采集依赖目标服务器与正确的证书信任。当前串口限制是一条串口只配置一个启用设备；共享串口多从站需要专门的总线调度，不能只删除冲突校验。具体说明见[驱动依赖](driver-migration.md)与[第三方声明](../THIRD-PARTY-NOTICES.md)。

## 7. 本轮验证范围与历史证据

本轮服务端删除后的当前范围和实际执行结果见 [服务端移除记录](opc-ua-service-removal.md)。本地、Windows Server 2022 / 2025 CI 与新发布包均已核验；使用本轮源码对应的原始测试和界面证据，历史成功不自动适用于本次修改。

以下已执行数据属于**历史基线**：源码 [`f1d398b`](https://github.com/kingtentx/edge-gateway/commit/f1d398ba27728e919c3fd56f60cbf510312267d8) 在 Windows Server 2022 / 2025 完成核心测试、原生窗体烟测与布局矩阵，[CI 39923092261](https://github.com/kingtentx/edge-gateway/actions/runs/39923092261) 最终 attempt 2 成功。当时两系统各通过 **163 / 163 项测试、20 / 20 个等效工作区场景**，覆盖七个主页面和四类编辑器，各保存 102 张 PNG；成功作业 stderr 为空，失败截图为 0，`focusRecoveryCount=0`，矩阵期间采集已停止。

历史作业中 Server 2025 在 attempt 1 首次通过；Server 2022 首次焦点异常，只重跑失败作业后在 attempt 2 通过，同一提交源码和断言未更改。最终成功不代表首次全部通过或异常根因已解决。原始详情见 [历史验证说明](live-configuration-verification.md) 和 [机器可读证据](live-configuration-verification.json)。

两套成功环境均为 .NET 8.0.32、WinForms 8.0.0.0，实际/原生窗口 DPI 为 96，屏幕为 1024×768；Server 2022 工作区为 1024×728，Server 2025 为 1024×720。布局矩阵使用等效工作区模拟，不代表真实系统多档 DPI 或多显示器切换已经验收。运行中配置、失败保留输入、CSV 和诊断的 [操作说明与实际截图](live-configuration.md#实际-winforms-截图) 可与源码逐项对照。

| 用例入口 | 要证明的行为 |
| --- | --- |
| [RuntimeConfigurationTests](../tests/EdgeGateway.Tests/RuntimeStorage/RuntimeConfigurationTests.cs) | A 运行时维护 B、新增 C；只重启受影响设备；保存失败保留旧会话；配置提交与启动停止串行；错误地址能够再次修正 |
| [TagCsvExchangeTests](../tests/EdgeGateway.Tests/RuntimeStorage/TagCsvExchangeTests.cs) | 全字段往返、追加与更新身份、重复和类型错误、行号、编码、区域设置、容量限制及整批拒绝 |
| [TelemetryPipelineTests](../tests/EdgeGateway.Tests/RuntimeStorage/TelemetryPipelineTests.cs) | 发送确认边界、密码脱敏、容量与持久化恢复、正常取消、上传筛选、旧消息补传、配置变化、分阶段存储错误和真实 SQLite 最老待传时间 |
| [AcquisitionPoliciesTests](../tests/EdgeGateway.Tests/RuntimeStorage/AcquisitionPoliciesTests.cs)、[ExpressionTests](../tests/EdgeGateway.Tests/RuntimeStorage/ExpressionTests.cs) | 通道、周期、降速、复制/筛选、报告基线、精度、表达式限制及错误质量 |
| [DeviceOperationTests](../tests/EdgeGateway.Tests/RuntimeStorage/DeviceOperationTests.cs) | 临时连接、会话复用、主动读回、取消/超时、写入一次和数值精度 |
| [PlatformAdapterTests](../tests/EdgeGateway.Tests/RuntimeStorage/PlatformAdapterTests.cs)、[MqttRpcTransportTests](../tests/EdgeGateway.Tests/Integration/MqttRpcTransportTests.cs) | 五平台独立协议向量、真实 TCP 订阅/回复、SUBACK 拒绝、默认拒写、ThingsCloud 注册及应用响应 |
| [RpcRuntimeTests](../tests/EdgeGateway.Tests/RuntimeStorage/RpcRuntimeTests.cs)、[PlatformSwitchTests](../tests/EdgeGateway.Tests/RuntimeStorage/PlatformSwitchTests.cs) | 权限、持久去重、重启/身份/方法冲突、令牌轮换、待确定写入与平台切换阻止 |
| [在线配置界面烟测](../src/EdgeGateway.WinForms/MainForm.LiveConfigurationSmoke.cs) | 使用本地模拟设备验证在线编辑、失败保留输入、点表导入和上传设置等界面路径 |
| [新增功能界面烟测](../src/EdgeGateway.WinForms/MainForm.CapabilitiesSmoke.cs) | 设备维护、表达式、平台、通道和核对选项操作与布局；具体执行结果单独记录 |

控制发布结果的伪实现用于验证确认先后、失败和恢复，不代表已完成某个业务平台的接收测试；本地模拟设备与协议回环也不替代现场 PLC、CNC、串口及 COM 验收。

历史交付链接：[Windows x64 自包含运行包](https://github.com/kingtentx/edge-gateway/actions/runs/39923092261/artifacts/12487349660) · [Server 2022 证据](https://github.com/kingtentx/edge-gateway/actions/runs/39923092261/artifacts/12488666311) · [Server 2025 证据](https://github.com/kingtentx/edge-gateway/actions/runs/39923092261/artifacts/12487239139)。这些包不包含本轮后续新增功能，版本、校验值与测试数量仅适用于原提交。

[up-device-service]: https://github.com/iioter/iotgateway/blob/8de442a7dec52d2993d1e9667d799846936d681b/Plugins/Plugin/DeviceService.cs
[up-update-devices]: https://github.com/iioter/iotgateway/blob/8de442a7dec52d2993d1e9667d799846936d681b/IoTGateway.ViewModel/BasicData/UpdateDevices.cs
[up-device]: https://github.com/iioter/iotgateway/blob/8de442a7dec52d2993d1e9667d799846936d681b/IoTGateway.Model/Device.cs
[up-variable]: https://github.com/iioter/iotgateway/blob/8de442a7dec52d2993d1e9667d799846936d681b/IoTGateway.Model/DeviceVariable.cs
[up-tag-import]: https://github.com/iioter/iotgateway/blob/8de442a7dec52d2993d1e9667d799846936d681b/IoTGateway.ViewModel/BasicData/DeviceVariableVMs/DeviceVariableImportVM.cs
[up-export]: https://github.com/iioter/iotgateway/blob/8de442a7dec52d2993d1e9667d799846936d681b/IoTGateway.ViewModel/BasicData/ExportDevicesSetting.cs
[up-import]: https://github.com/iioter/iotgateway/blob/8de442a7dec52d2993d1e9667d799846936d681b/IoTGateway.ViewModel/BasicData/ImportExcelVM.cs
[up-expression]: https://github.com/iioter/iotgateway/blob/8de442a7dec52d2993d1e9667d799846936d681b/Plugins/Plugin/Expression.cs
[up-thread]: https://github.com/iioter/iotgateway/blob/8de442a7dec52d2993d1e9667d799846936d681b/Plugins/Plugin/DeviceThread.cs
[up-message]: https://github.com/iioter/iotgateway/blob/8de442a7dec52d2993d1e9667d799846936d681b/Plugins/Plugin/MessageService.cs
[up-platform-factory]: https://github.com/iioter/iotgateway/blob/8de442a7dec52d2993d1e9667d799846936d681b/Plugins/Plugin/PlatformHandler/PlatformHandlerFactory.cs
[up-system-config]: https://github.com/iioter/iotgateway/blob/8de442a7dec52d2993d1e9667d799846936d681b/IoTGateway.Model/SystemConfig.cs
[up-thingsboard]: https://github.com/iioter/iotgateway/blob/8de442a7dec52d2993d1e9667d799846936d681b/Plugins/Plugin/PlatformHandler/ThingsBoardHandler.cs
[emqx-device]: https://docs.emqx.com/en/neuronex/latest/configuration/south-devices/south-devices.html
[emqx-bulk]: https://docs.emqx.com/en/neuronex/latest/configuration/bulk-config.html
[tb-config]: https://thingsboard.io/docs/iot-gateway/features/remote-configuration/
[tb-report]: https://thingsboard.io/docs/iot-gateway/features/report-strategy/
[tb-rpc]: https://thingsboard.io/docs/iot-gateway/features/reserved-rpc/
[kep-tags]: https://support.ptc.com/help/kepware/kepware_server/en/kepware/server/tag-management.html
[kep-diagnostics]: https://support.ptc.com/help/kepware/kepware_server/en/kepware/server/channel-diagnostics.html
