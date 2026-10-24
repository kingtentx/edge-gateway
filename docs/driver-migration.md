# 源项目全部驱动迁移

源项目：[iioter/iotgateway](https://github.com/iioter/iotgateway)。固定提交：[`8de442a7dec52d2993d1e9667d799846936d681b`](https://github.com/iioter/iotgateway/commit/8de442a7dec52d2993d1e9667d799846936d681b)。目标框架：`.NET 8`；桌面宿主：原生 Windows Forms、Windows x64。

## 范围和核对依据

源 `Plugins/Drivers` 下 **12 个驱动工程全部迁入相同目录**，并保留其 S7.Net、NModbus4、OPC 客户端和 FOCAS 绑定等辅助源码。`Plugins/PluginInterface` 同时迁入并重新编译。IDE 缓存和构建产物不迁移。

源清单共记录 **176 个文件、65 个带 `[Method]` 的读取方法、74 个带 `[ConfigParameter]` 的参数**。原始文件 SHA256、完整方法名和参数名见 [机器可读清单](driver-migration-manifest.json)。SHA256 用于记录来源；修改过的 .NET 8 目标文件不要求与原始哈希一致。

执行以下命令可检查目录、文件、原方法/参数、目标框架与解决方案引用完整性：

```powershell
python scripts/verify-driver-migration.py
```

CI 另外编译全部项目、运行适配与协议测试、实例化 WinForms 驱动编辑界面；发布脚本在每个源驱动程序集缺失时直接失败。测试结果和未执行的现场验收见 [验证记录](verification.md)。

## 逐项清单

“读取方法数”指迁移前带 `[Method]` 的方法数。原 OPC UA/DA 与 Modbus 的演示 `Read` 方法名保留，但迁移版将其改为实际 `ReadNode` / `HoldingRegisters` 的兼容入口，避免返回本机时间或固定示例值。界面默认仍选择明确的实际读取方法。

| 驱动目录 / 配置中的 DriverId | 读取方法数 | 配置与用法 |
|---|---:|---|
| `PLC.SiemensS7` | 2 | PLC 类型、IP、端口、Rack、Slot；`Read` 使用 S7 标准地址，`ReadByteString` 使用源方法的地址和长度格式 |
| `PLC.MelsecMc` | 3 | A_1E / Qna_3E、IP、端口；单点 `Read`、批量 `ReadMultiple`、后续 `ReadFromCache` |
| `PLC.OmronFins` | 1 | IP、端口；沿用源 FINS 地址与类型规则 |
| `PLC.AllenBradley` | 1 | IP、端口；沿用源 EtherNet/IP 标签地址与类型规则 |
| `PLC.ModBusMaster` | 7 | 选择 MasterType、网络或串口参数、从站号；`HoldingRegisters`、`InputRegisters`、`Coil`、`Input`；批量与缓存方法按原说明配置 |
| `OPC.UaClient` | 2 | OPC UA 端点；默认使用 `ReadNode`；`Read` 为其兼容入口 |
| `OPC.DaClient` | 2 | IP、OpcServerName；默认使用 `ReadNode`；`Read` 为其兼容入口 |
| `CNC.Fanuc` | 21 | IP、端口；按设备类型、状态、模式、宏变量、寄存器、速度、报警、程序号、计数、刀具、时间或负载选择具体方法 |
| `CNC.Fanuc.H` | 23 | IP、端口；按系统状态、报警、坐标、程序、速度、轴负载、刀具、时间、宏变量等选择具体方法 |
| `CNC.MTConnect` | 1 | HTTP Agent URI；`ReadById` 按数据项 ID 读取 |
| `Other.Toledo` | 1 | 源称重设备连接参数；`Read` 使用源设备报文解析规则 |
| `Mock.TcpClient` | 1 | IP、端口、超时；源 TCP 示例协议，须有对应测试端，并非内置随机数据发生器 |

`DeviceId` 绑定网关的稳定设备 ID，`Timeout` 和 `MinPeriod` 绑定设备的通用超时与采集周期；其余驱动参数保存在 `DeviceConfiguration.Properties`。驱动参数编辑器按属性元数据生成，枚举提供选择项，保存前执行类型转换和参数校验。原目录名直接作为 DriverId，避免与原有 `simulator` / `modbus-tcp` / `modbus-rtu` 冲突。

[全部驱动示例配置](../examples/migrated-drivers.json) 提供 12 台**未启用**的设备及各自一个点位，可从界面导入查看参数。IP、URI、地址与服务名都是本机占位示例，须按现场修改后逐台启用。首次安装仍默认加载原来的内置模拟演示。

## 读取方法、数据与写入

每个点位保存原驱动读取方法、数据类型和字节序。适配器只调用签名符合原接口且标记为读取方法的成员，不通过任意反射调用用户填写的方法名。原数据类型全部保留，但各协议和方法实际支持的类型仍以该驱动实现为准；不支持的组合必须报错或返回坏质量，不能自动编造有效值。

字符串、64 位整数、时间与结构值需要保留原始内容。数值工程量继续由服务层统一处理，不能在适配层和服务层重复缩放。读取返回空值、连接失败、方法异常或非 Good 状态时，不得当作成功采样。

源工程中的 `WriteAsync` 并不都具有实际写入实现。只有驱动与具体方法同时支持写入、点位被设为可写并经本地确认时才执行。上游明确返回“未实现写入”的驱动继续拒绝写入，不通过迁移适配器制造虚假的成功结果。写入失败不自动重复发送。

批量/缓存读取的方法仍保持源驱动语义：先执行填充缓存的批量点位，再执行依赖该缓存的读取点位，并按实际顺序组织点表。当前没有替用户自动生成批量点表或自动分析任意 PLC 地址。

源 Modbus `ReadMultiple` 直接返回原始位或寄存器数组；需要浮点数时由 `ReadFromCache` 按点位类型和字节序解码。例如批读 `3,0,4,cache1`，再以 `Float32` 读取 `cache1,0` 与 `cache1,2`。源 MC 批量方法的数据类型用于选择报文解码，结果仍保留字典/数组形状，不当作一个标量执行倍率换算。

## 同步驱动与桌面运行时的边界

原接口是同步 `Connect` / `Read` / `Close`。迁移适配器将一个设备的构造、连接、调用和释放放在专属工作线程中，避免 UI 阻塞和跨线程访问 OPC DA COM 对象；Windows 上按 STA 要求处理。上层仍保持同设备读写互斥。

同步厂商库不提供统一的可取消接口。超时后必须让调用方返回失败，并隔离尚未结束的原调用，禁止立即在同一实例或同一设备上启动新的并发调用。不能用 `Task.Run` 后仅放弃等待的方式无限创建后台请求，也不能声称取消令牌能强制中断任意原生库。长期不返回的本机调用需要现场检查驱动环境，必要时重启程序。

## 外部运行依赖

| 驱动 | 现场必须准备的条件 |
|---|---|
| `CNC.Fanuc` | 有效授权下取得的 FANUC FOCAS **x64** 原生运行库及其依赖。源实现使用 `FWLIB64.dll`；仓库仅包含源项目原有的 C# 绑定，不包含厂商原生运行库。缺失时应明确报错 |
| `CNC.Fanuc.H` | HslCommunication 按其许可要求提供有效授权；授权码通过设备参数直接填写并随配置保存，不提交真实授权码到源码 |
| `OPC.DaClient` | Windows，匹配 x64 宿主的已注册 OPC Automation / OPC DA Server，正确的 COM/DCOM 身份和连接配置。仓库保留原 `Interop.OPCAutomation.dll`，它不能替代实际 COM 服务的安装注册 |
| `OPC.UaClient` | 可访问的 OPC UA 端点、正确的安全模式与证书信任配置；不默认忽略任意证书错误 |
| 串口模式 | 实际串口驱动、正确的 COM 参数和接线；同一串口按设备独占，未新增共享多站总线调度 |
| `CNC.MTConnect` | 可访问且返回对应 MTConnect 数据项的 HTTP Agent |

这些运行条件不影响源码迁移清单的完整性，但会影响现场连接是否成功。缺少依赖、授权或现场设备时，保持明确错误与坏质量，不切换为模拟数据。

`CNC.Fanuc.H` 的 `AuthorizationCode` 参数默认留空；直接填写有效授权码并保存，输入框使用掩码。`OPC.UaClient` 增加 `UseSecurity`（默认启用）和 `CertificateStorePath`，证书库根目录留空时使用当前用户目录；设备服务器与客户端应用证书需要按实际 PKI 配置信任。

## 配置升级

当前配置格式为 `SchemaVersion: 2`。已有版本 1 配置在读取时只做内存升级，保持设备/点位 ID、原驱动标识和地址；用户保存时才原子写入版本 2，同时保留上一份 `.bak`。未知版本报错并保留原文件。新增点位字段为读取方法、写入方法和原协议字节序；原有三种驱动仍使用原参数和字节序语义。

后续新增的点位 `UploadEnabled` 是 schema 2 的可选字段，缺省为 true，旧文件保持原上传行为，因此没有再次升级版本。它控制新 MQTT 快照，不改变驱动读取方法或本地历史。兼容字段与不兼容版本迁移的判定规则见 [配置兼容规则](live-configuration.md#配置兼容规则)。

## 继续维护

新增协议优先实现本项目的异步 `IDeviceDriver` / `IDriverFactory`。维护这 12 个迁移驱动时，保留原方法名和参数名；若升级源版本，先更新来源提交与清单，再执行完整性、编译、适配器、协议和 Windows 界面验证。

授权与来源见 [第三方说明](../THIRD-PARTY-NOTICES.md)。本项目原创代码的 MIT 许可不覆盖或替代 FANUC 绑定、商业库及其他第三方已有许可。
