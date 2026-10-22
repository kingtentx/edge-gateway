# 第三方来源与许可说明

## 参考与迁移来源

参考基准为 [iioter/iotgateway](https://github.com/iioter/iotgateway) 的固定提交 [`8de442a7dec52d2993d1e9667d799846936d681b`](https://github.com/iioter/iotgateway/tree/8de442a7dec52d2993d1e9667d799846936d681b)。网关的 WinForms、调度、存储与北向接口按 .NET 8 重新实现；以下源驱动目录和必需的 `Plugins/PluginInterface` 已进行实质源码迁移：

`CNC.Fanuc`、`CNC.Fanuc.H`、`CNC.MTConnect`、`Mock.TcpClient`、`OPC.DaClient`、`OPC.UaClient`、`Other.Toledo`、`PLC.AllenBradley`、`PLC.MelsecMc`、`PLC.ModBusMaster`、`PLC.OmronFins`、`PLC.SiemensS7`。

上游 MIT 版权声明 `Copyright (c) 2021 dd` 及完整许可保存在 [IoTGateway-LICENSE.txt](LICENSES/IoTGateway-LICENSE.txt)。迁移保留源文件、驱动类、方法与参数契约，修改了目标框架、依赖版本、连接释放、超时、读取质量、TCP 帧处理和若干协议转换错误；原 Web/WTM 页面未迁入。完整源文件与接口清单见 [driver-migration-manifest.json](docs/driver-migration-manifest.json)。

内嵌 `PLC.ModBusMaster/NModbus4` 和 `PLC.SiemensS7/S7.Net` 分别保留 [NModbus4 MIT 许可](LICENSES/NModbus4-LICENSE.txt) 与 [S7NetPlus MIT 许可](LICENSES/S7NetPlus-LICENSE.txt)。FANUC 声明中的原厂版权、HslCommunication 授权条件和 OPC Automation/Server 的独立权利不被本项目 MIT 许可替代，详见下文。

## 运行依赖

运行包使用以下 NuGet 包及其传递依赖。完整许可与版权文本随代码和发布包保存在 `LICENSES/` 中。

| 依赖 | 版本 | 许可 | 随附文件 |
|---|---|---|---|
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 8.0.32 | MIT | [Microsoft.Data.Sqlite-LICENSE.txt](LICENSES/Microsoft.Data.Sqlite-LICENSE.txt) |
| MQTTnet | 4.3.7.1207 | MIT | [MQTTnet-LICENSE.txt](LICENSES/MQTTnet-LICENSE.txt) |
| System.IO.Ports | 8.0.0 | MIT | [System.IO.Ports-LICENSE.txt](LICENSES/System.IO.Ports-LICENSE.txt)、[System.IO.Ports-THIRD-PARTY-NOTICES.txt](LICENSES/System.IO.Ports-THIRD-PARTY-NOTICES.txt) |
| SQLitePCLRaw.bundle_e_sqlite3 / core / provider.e_sqlite3 / lib.e_sqlite3 | 2.1.13 | Apache-2.0 | [SQLitePCLRaw-LICENSE.txt](LICENSES/SQLitePCLRaw-LICENSE.txt)、[SQLitePCLRaw-ATTRIBUTION.txt](LICENSES/SQLitePCLRaw-ATTRIBUTION.txt) |

| 新迁移驱动依赖 | 版本 | 许可 / 归属 | 随附文件 |
|---|---|---|---|
| Newtonsoft.Json | 13.0.4 | MIT | [Newtonsoft.Json-LICENSE.txt](LICENSES/Newtonsoft.Json-LICENSE.txt) |
| IoTClient | 1.0.48 | MIT；农码一生 | [IoTClient-LICENSE.txt](LICENSES/IoTClient-LICENSE.txt) |
| SimpleTCP.Core | 1.0.4 | 包指定上游 SimpleTCP 为 Apache-2.0 | [SimpleTCP-LICENSE.txt](LICENSES/SimpleTCP-LICENSE.txt) |
| opennetcf-mtconnect-client | 1.0.17160 | MIT；OpenNETCF Consulting | [OpenNETCF.MTConnect-LICENSE.txt](LICENSES/OpenNETCF.MTConnect-LICENSE.txt) |
| opennetcf-extensions-standard / opennetcf-restconnector | 1.0.17160 | MIT；Chris Tacke | [OpenNETCF.Extensions-RestConnector-LICENSE.txt](LICENSES/OpenNETCF.Extensions-RestConnector-LICENSE.txt) |
| OPCFoundation.NetStandard.Opc.Ua.Client / Configuration / Core / Security.Certificates / Types | 1.5.378.182 | OPC Foundation MIT License 1.00 | [OPCFoundation-UA-LICENSE.txt](LICENSES/OPCFoundation-UA-LICENSE.txt) |
| BitFaster.Caching | 2.6.0 | MIT；Alex Peck | [BitFaster.Caching-LICENSE.txt](LICENSES/BitFaster.Caching-LICENSE.txt) |
| Microsoft.Extensions.*、System.Text.Json 等 UA 传递依赖 | 10.0.8 | MIT；Microsoft / .NET Foundation | [Microsoft.NET-LICENSE.txt](LICENSES/Microsoft.NET-LICENSE.txt)、[第三方声明](LICENSES/Microsoft.NET-THIRD-PARTY-NOTICES-10.0.8.txt) |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 直接引用，最终由 UA 统一解析至 10.0.8 | MIT | [Microsoft.Extensions-8-LICENSE.txt](LICENSES/Microsoft.Extensions-8-LICENSE.txt) 及上述 .NET 许可 |
| HslCommunication | 12.8.3 | 厂商专有授权；杭州胡工物联科技有限公司 | [HslCommunication-NOTICE.txt](LICENSES/HslCommunication-NOTICE.txt) |

已解析的直接与传递包版本、作者、版权、许可 URL 和源码提交完整记录在 [NuGet-runtime-attribution.json](LICENSES/NuGet-runtime-attribution.json)。`System.Memory` 传递依赖的附加声明保存在 [System.Memory-THIRD-PARTY-NOTICES.txt](LICENSES/System.Memory-THIRD-PARTY-NOTICES.txt)。发布包仅选用对应 Windows x64 的运行资产。

许可文本来源：

- MQTTnet：包内 `.nuspec` 指定的源码提交 [`740d605fd2c25922fad6ea60e78e8370356e95dd`](https://github.com/dotnet/MQTTnet/blob/740d605fd2c25922fad6ea60e78e8370356e95dd/LICENSE)。
- Microsoft.Data.Sqlite：包内 `.nuspec` 指定的源码提交 [`5e864fab7a450eedb39ddb931ba61759102c4e35`](https://github.com/dotnet/efcore/blob/5e864fab7a450eedb39ddb931ba61759102c4e35/LICENSE.txt)。
- System.IO.Ports：8.0.0 官方 NuGet 包自带的 `LICENSE.TXT` 与 `THIRD-PARTY-NOTICES.TXT`，按原文保留。
- SQLitePCLRaw：官方仓库 [LICENSE.TXT](https://github.com/ericsink/SQLitePCL.raw/blob/main/LICENSE.TXT)（文件 blob `d645695673349e3947e8e5ae42332d0ac3164cd7`）；2.1.13 包内 `.nuspec` 的版权声明为 `Copyright 2014-2024 SourceGear, LLC`，四个包均声明 Apache-2.0，已单独保留该归属声明。

测试工程使用 Microsoft.NET.Test.Sdk 17.11.1、xunit 2.9.2 和 xunit.runner.visualstudio 2.8.2；它们不随网关运行包发布。

自包含发布包还包含 .NET 8 / Windows Desktop Runtime，其许可和第三方声明由 Microsoft 提供。

### 新增许可文本的原始来源

- IoTClient：包内固定源码提交 [`a4dc22e4fc9b60647b24db0675a36dabc75066ef/LICENSE`](https://github.com/zhaopeiym/IoTClient/blob/a4dc22e4fc9b60647b24db0675a36dabc75066ef/LICENSE)。
- SimpleTCP.Core：包内 `projectUrl` 指向 [BrandonPotter/SimpleTCP](https://github.com/BrandonPotter/SimpleTCP)，采用该项目 [LICENSE](https://github.com/BrandonPotter/SimpleTCP/blob/master/LICENSE)，文件 blob `8dada3edaf50dbc082c9a125058f25def75e625a`。
- OpenNETCF MTConnect：原作者维护的 [EntityClient.cs 文件头](https://github.com/ctacke/mtconnect/blob/master/OpenNETCF.MTConnect.Client/Clients/EntityClient.cs)，blob `0bfbd43d99f75b04d1739f0677b0b33eb29b04a0`，包含完整 MIT 授权文本。Extensions 与 RestConnector 的同一许可文件 blob 为 `93fcce20c3453f2695dccd8027b034a1ba184f08`，分别见 [Extensions](https://github.com/ctacke/extensions/blob/master/LICENSE) 与 [RestConnector](https://github.com/ctacke/restconnector/blob/master/LICENSE)。
- NModbus4：[LICENSE.txt](https://github.com/NModbus4/NModbus4/blob/portable-3.0/LICENSE.txt)，保留 Scott Alexander 与 Dmitry Turin 的版权，blob `5edd65b2c049918a32151a69bf0e43bbead1eb52`。
- S7NetPlus：[License.txt](https://github.com/S7NetPlus/s7netplus/blob/main/License.txt)，保留 Jürgen Schildmann 的版权，blob `50d0fcdb4b4ee5bb46ab7143dd60abbf11f14d4f`。
- Newtonsoft.Json、OPC Foundation UA 与 BitFaster.Caching：使用对应版本官方 NuGet 包内的完整许可。各包版权和源码提交另见包归属清单。
- Microsoft 10.0.8 包：使用包内相同的第三方声明及包元数据指定提交 [`94ea82652cdd4e0f8046b5bd5becbd11461482ca/LICENSE.TXT`](https://github.com/dotnet/dotnet/blob/94ea82652cdd4e0f8046b5bd5becbd11461482ca/LICENSE.TXT)。

## 原厂组件与部署条件

`CNC.Fanuc.H` 调用 HslCommunication 的实际通信 API。其生产使用和再分发遵循[厂商授权条款](https://www.hsltechnology.cn/Home/Licence?area=HslCommunication)，本项目不提供授权码。`AuthorizationCodeEnvironmentVariable` 指定存放合法授权码的环境变量名，默认 `EDGE_HSL_AUTHORIZATION_CODE`；驱动调用厂商的授权验证 API，不将授权值写入设备配置或日志。

`CNC.Fanuc/fwlib64.cs` 原有 `Copyright (C) 2002-2014 by FANUC CORPORATION All rights reserved.` 保持原样，见 [FANUC-FOCAS-NOTICE.txt](LICENSES/FANUC-FOCAS-NOTICE.txt)。原生 `FWLIB64.dll` 和其官方依赖未随代码或运行包提供，须从厂商合法取得；当前实际 Focas1 调用针对 Windows x64。保留 `fwlib32.cs` 是源目录完整迁移的一部分，不代表发布包支持 x86。

`OPC.DaClient` 保留上游 `Interop.OPCAutomation.dll`，文件哈希和来源见 [OPC-Automation-NOTICE.txt](LICENSES/OPC-Automation-NOTICE.txt)。该文件只包含托管 COM 互操作定义；运行机器仍需安装并注册位数兼容的 OPC Automation 组件和 OPC DA Server，并配置 DCOM。上游未随该互操作二进制给出独立许可文件，不能将 OPC UA 的 MIT 文本作为 OPC DA 原厂组件授权。
