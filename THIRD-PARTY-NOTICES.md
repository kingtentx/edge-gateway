# 第三方来源与许可说明

## 结构参考

本项目根据用户指定的 [iioter/iotgateway](https://github.com/iioter/iotgateway) 项目结构独立编写，参考基准为提交 `8de442a7dec52d2993d1e9667d799846936d681b`。参考范围为模型、数据访问、驱动契约、协议驱动、采集调度与平台通信的分层职责；未将上游 Web/WTM 代码、协议实现或数据库直接复制到本项目。

上游采用 MIT License，版权声明为 `Copyright (c) 2021 dd`。其完整许可证以 [固定提交中的 LICENSE](https://github.com/iioter/iotgateway/blob/8de442a7dec52d2993d1e9667d799846936d681b/LICENSE) 为准。未来如复制上游实质代码，必须同时保留其原版权和许可文本。

## 运行依赖

运行包使用以下 NuGet 包及其传递依赖。完整许可与版权文本随代码和发布包保存在 `LICENSES/` 中。

| 依赖 | 版本 | 许可 | 随附文件 |
|---|---|---|---|
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 8.0.32 | MIT | [Microsoft.Data.Sqlite-LICENSE.txt](LICENSES/Microsoft.Data.Sqlite-LICENSE.txt) |
| MQTTnet | 4.3.7.1207 | MIT | [MQTTnet-LICENSE.txt](LICENSES/MQTTnet-LICENSE.txt) |
| System.IO.Ports | 8.0.0 | MIT | [System.IO.Ports-LICENSE.txt](LICENSES/System.IO.Ports-LICENSE.txt)、[System.IO.Ports-THIRD-PARTY-NOTICES.txt](LICENSES/System.IO.Ports-THIRD-PARTY-NOTICES.txt) |
| SQLitePCLRaw.bundle_e_sqlite3 / core / provider.e_sqlite3 / lib.e_sqlite3 | 2.1.13 | Apache-2.0 | [SQLitePCLRaw-LICENSE.txt](LICENSES/SQLitePCLRaw-LICENSE.txt)、[SQLitePCLRaw-ATTRIBUTION.txt](LICENSES/SQLitePCLRaw-ATTRIBUTION.txt) |

许可文本来源：

- MQTTnet：包内 `.nuspec` 指定的源码提交 [`740d605fd2c25922fad6ea60e78e8370356e95dd`](https://github.com/dotnet/MQTTnet/blob/740d605fd2c25922fad6ea60e78e8370356e95dd/LICENSE)。
- Microsoft.Data.Sqlite：包内 `.nuspec` 指定的源码提交 [`5e864fab7a450eedb39ddb931ba61759102c4e35`](https://github.com/dotnet/efcore/blob/5e864fab7a450eedb39ddb931ba61759102c4e35/LICENSE.txt)。
- System.IO.Ports：8.0.0 官方 NuGet 包自带的 `LICENSE.TXT` 与 `THIRD-PARTY-NOTICES.TXT`，按原文保留。
- SQLitePCLRaw：官方仓库 [LICENSE.TXT](https://github.com/ericsink/SQLitePCL.raw/blob/main/LICENSE.TXT)（文件 blob `d645695673349e3947e8e5ae42332d0ac3164cd7`）；2.1.13 包内 `.nuspec` 的版权声明为 `Copyright 2014-2024 SourceGear, LLC`，四个包均声明 Apache-2.0，已单独保留该归属声明。

测试工程使用 Microsoft.NET.Test.Sdk 17.11.1、xunit 2.9.2 和 xunit.runner.visualstudio 2.8.2；它们不随网关运行包发布。

自包含发布包还包含 .NET 8 / Windows Desktop Runtime，其许可和第三方声明由 Microsoft 提供。未引入 HslCommunication、OPC Foundation 或其他上游专用协议库；不能把上游仓库 MIT 许可视为其他协议库的统一授权。
