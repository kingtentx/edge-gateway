# OPC UA 对外服务

`EdgeGateway.OpcUa` 是独立的 `.NET 8` OPC UA TCP 服务端工程，使用 OPC Foundation 官方 `OPCFoundation.NetStandard.Opc.Ua.Server 1.5.378.182`，与迁移的南向 `OPC.UaClient` 驱动保持同一 SDK 版本。第三方客户端可以通过标准发现、浏览、读取、订阅和受控写入服务访问网关设备。服务端读取网关现有采样，不另建一套设备轮询连接；实际写入进入网关的设备锁、通道锁和主动读回核对流程。

这部分实现面向 Kepware 的核心数据服务能力。当前不提供 UA Historical Access、Alarms & Conditions、冗余服务器、多用户角色库或协议一致性认证声明；本地 SQLite 历史查询仍由网关界面提供。南向协议自动测试、北向 UA 回环测试和现场客户端验收分别记录，不能相互替代。

## 1. 配置和生命周期

在“OPC UA 服务”页面编辑独立草稿，选择“应用设置”后才改变已保存配置。未应用草稿不改变正在运行的端点。UA 设置属于全局配置，应用时要求设备全部停止；设备和点位的局部编辑仍可按[运行中配置流程](live-configuration.md)执行。

| 配置字段 | 默认值 | 约束与含义 |
|---|---|---|
| `Enabled` | `false` | 允许启动服务；启用并应用后尝试启动，程序启动时按已保存值启动 |
| `EndpointUrl` | `opc.tcp://localhost:4840/EdgeGateway` | `opc.tcp`、明确有效端口，不含账号、查询或片段 |
| `AllowAnonymous` | `true` | 允许匿名只读，仍要求客户端应用证书受信任 |
| `EnableUserAuthentication` | `false` | 启用一个配置的用户名；与匿名至少启用一种 |
| `Username` | `operator` | 1–128 个字符，不能含控制字符 |
| `PasswordEnvironmentVariable` | `EDGE_OPCUA_PASSWORD` | 只保存环境变量名称，不保存密码；启用用户验证时启动前必须能读到非空值 |
| `AllowRemoteWrite` | `false` | 必须同时启用用户名验证；匿名始终不能写 |
| `UpdateIntervalMs` | `250` | 50–60000 ms，地址空间与订阅缓存刷新周期；不改变设备采集周期 |
| `MaxSessions` | `20` | 1–200，活动 UA 会话上限 |
| `OperationTimeoutMs` | `5000` | 100–60000 ms，UA 通信/写入核对预算 |

这些字段是 schema 2 的可选扩展。旧配置缺少 `OpcUa` 时使用上述默认值，不自动开启网络服务。

**服务端启停与设备启停分开。** 设备全停后，UA 端点仍能接受连接和浏览，点位质量反映停机。用户手动停止 UA 后，普通设备编辑和点表导入不会把它自动启动。重新应用实际变化的 UA 设置会重建端点；重复应用同一组选项不重启服务。端口占用、证书不匹配、密码环境变量缺失等错误进入可见状态和日志，不阻止设备按原配置采集。处理错误后可以再次点击“启动服务”。

证书信任、撤销、导出和服务端证书重建可以在采集期间执行。它们不重建设备会话；可能重启 UA 端点的操作会断开 UA 客户端，客户端需要重新建立会话和订阅。

### 监听范围与主机名

官方 SDK 对 DNS 主机名，包括 `localhost`，可能在所有网络接口绑定监听；不能把默认 URL 解释为操作系统层面的仅本机绑定。需要只允许本机时使用 `opc.tcp://127.0.0.1:4840/EdgeGateway`，并按实际部署设置防火墙。局域网使用客户端真正访问的主机名或固定 IP，公共证书的 SAN 必须包含对应名称。首次生成和明确重建会同时加入实际端点地址及本机主机名/FQDN，以满足官方客户端对回环地址的名称归一化规则；仍执行严格的域名验证。不能以跳过 hostname 检查解决端点名称不匹配。

`GetEndpoints` 等发现操作可能在标准未加密发现通道上返回端点元数据；客户端建立可读写数据的会话必须选择 **Basic256Sha256 / SignAndEncrypt**。程序不提供 `SecurityPolicy None` 数据端点。

## 2. 第一次连接

以下流程以两端使用自签名应用证书为基线；客户端证书应使用至少 2048 位 RSA 公钥和 SHA-256 或更强签名。企业 CA 签发的客户端证书还需要完整发行链与符合 SDK 策略的吊销资料，不能只导入叶证书就视为配置完成，详见第 6 节。

1. 停止全部设备，在“OPC UA 服务”页勾选“允许并自动启动服务”，填写客户端实际访问的端点并点击“应用设置”。默认 `Enabled=false`，未启用时不能启动。若启用用户名验证，先让启动网关的进程能读取指定密码环境变量；关闭并重新打开程序可以让新进程获得用户或系统环境变量。
2. 确认服务状态为运行；若已手动停止或修正过启动错误，点击“启动服务”。首次启动在当前数据目录的 `opcua/pki/own` 生成本应用 RSA 服务端证书和私钥，界面显示公共证书指纹和目录。
3. 点击“OPC UA 服务”页工具栏的“导出公共证书”，将这一张证书加入客户端应用的受信任服务器列表。导出结果只有 DER 公共证书；该按钮在主页面，不在“证书管理”窗口内。
4. 获取客户端自己的应用公共证书，核对指纹和来源后，在网关证书窗口导入信任。也可以先让客户端尝试连接，再从“已拒绝”列表核对并信任指定证书。
5. 客户端选择配置的端点、`Basic256Sha256` 和 `SignAndEncrypt`。选择匿名只读，或使用配置的用户名及环境变量对应密码。
6. 按需启动设备，再浏览 `Objects → EdgeGateway → 设备 → 点位`，检查实际数据类型、质量和源时间戳，再建立订阅。设备未运行时仍可浏览节点，质量应为 `BadOutOfService`。

服务器与客户端是两个独立的应用证书信任方向，必须分别配置。用户名正确不能代替应用证书受信任。程序没有接受全部证书、自动信任未识别证书或降级到无加密连接的分支。

## 3. 地址空间和稳定身份

命名空间 URI 固定为：

```text
urn:kingtentx:edge-gateway:opcua
```

| 节点 | 字符串 NodeId 标识符 | 内容 |
|---|---|---|
| 根文件夹 | `gateway` | 显示名 `EdgeGateway` |
| 设备文件夹 | `device/{deviceId:N}` | 当前设备名称；`DeviceId`、`Driver` 元数据 |
| 点位变量 | `device/{deviceId:N}/tag/{tagId:N}` | 当前点位名称、标量工程值和质量 |
| 点位属性 | 点位标识符加 `/TagId`、`/Address`、`/Unit`、`/Description` | 稳定 ID、原地址、单位和说明，均只读字符串 |

`N` 表示没有连字符的 32 位 Guid。客户端应先根据 Namespace URI 解析当前 namespace index，不把某次连接的 `ns=2` 写死为永久身份。设备或点位重命名保留 Guid，因此保留 NodeId。复制或重新创建产生新 Guid，得到新 NodeId。

服务端监视运行时 `ConfigurationRevision`。设备/点位增加、删除、重命名及属性变更在刷新周期内更新地址空间，不重启 UA 端点。删除节点后，读取返回 `BadNodeIdUnknown`，原监视项收到节点删除状态。新增设备按网关规则保持停止，其点位可浏览但不能显示 Good。界面 `NodeCount` 包含文件夹、点位和元数据属性，不等于采集点数。

## 4. 值、类型、时间戳和质量

数据读取直接查询运行时当前样本；订阅更新使用同一份样本和类型转换。`SourceTimestamp` 为真实采样或实际主动读回的 UTC 时间，不使用界面刷新时刻重新包装旧值。`ServerTimestamp` 由 SDK 按响应生成。尚无样本时的坏质量节点没有可代表成功采样的源值。

| 网关数据类型/变换 | UA 类型或表示 |
|---|---|
| Boolean | Boolean |
| Int8 / UInt8 | SByte / Byte |
| Int16 / UInt16 / Bcd16 | Int16 / UInt16 / UInt16 |
| Bit / Int32 | Int32 |
| UInt32 / Bcd32 | UInt32 |
| Int64 / TimeStampMs / TimeStampS | Int64 |
| UInt64 | UInt64 |
| Float32 / Float64 | Float / Double |
| 日期时间 | DateTime，统一 UTC |
| 字符串 | String，保持文字和空白 |
| 有倍率、偏移或表达式的数值 | Double，数值须有限 |
| 结构类型或迁移驱动 `ReadMultiple` | String 中的 JSON；不开放 UA 结构写入 |

**未经数值变换的 Int64 / UInt64 不经过 Double，保留 64 位整数全部精度。** 一旦使用倍率、偏移或表达式，UA 对外声明为 Double；超过约 53 位整数精度的值不能承诺逐整数区分。需要精确计数或标识时，保留无变换整数点，另设工程量点。非法类型、非有限浮点或整数小数不能静默截断为 Good。

| 当前情况 | UA 质量 | 对外值 |
|---|---|---|
| 真实采样成功 | `Good` | 本次工程值 |
| 运行中尚无首次样本 | `BadWaitingForInitialData` | 空 |
| 设备停止、设备或点位禁用 | `BadOutOfService` | 空 |
| 本次读取失败或没有可用值 | `BadCommunicationError` | 空 |
| 有实际保留值但网关质量为 Uncertain | `UncertainDataSubNormal` | 保留值及原采样时间 |
| 与声明类型不符、转换失败 | `BadTypeMismatch` | 空 |
| 节点已删除 | `BadNodeIdUnknown` | 无节点 |

停止后重新启动设备时，运行时清除旧会话最新值；首次新采样完成前不恢复 Good。服务端没有独立的“最后写成功值”覆盖缓存，所以本地/RPC 的后续未核对写入、故障和设备重启都会及时反映到 UA 读取。

## 5. 写入权限和实际读回核对

UA 只对标量值属性开放受控写入，不接受修改节点属性、写入索引范围，或客户端附带自定义质量/时间戳。字符串、Boolean、有限数值按节点声明类型严格检查；结构、日期时间、表达式点只读。

一次 UA 写入必须同时满足：已应用 UA 配置允许远程写入、用户验证启用、当前会话确为配置的用户名、设备正在运行且启用、点位启用并可写、当前驱动支持写入。匿名会话始终拒绝，不能因全局许可打开而获得写权限。

标准 UA `Cancel` 请求按 SDK 分配的唯一请求 ID 关联取消令牌；不同会话中重复的客户端 `RequestHandle` 不会互相取消。取消发生在设备锁/通道锁排队期间时不下发写入，批量请求后续点也不继续写；已经提交的物理动作不能因请求取消而宣称已撤销。取消应答保留 `BadRequestCancelledByRequest`，不冒充超时。

满足前置条件后，服务端调用：

```csharp
await runtime.WriteVerifiedAsync(deviceId, tagId, engineeringValue,
    new WriteVerificationOptions
    {
        RequireOpcUaWritePermission = true,
        VerifyReadBack = true,
        TimeoutMs = settings.OperationTimeoutMs,
        MaxAttempts = 5,
        RetryIntervalMs = 100,
        Source = $"opcua:{settings.Username}"
    }, requestToken);
```

运行时在取得同一设备锁后再次检查权限和会话，按“设备锁 → 通道锁”进入实际通信。物理写入只执行一次，其后最多主动读取 5 次，每次间隔 100 ms，并受总预算和设备通信超时约束。UA 请求的取消预算包含等待锁和通道的时间，避免服务请求无限排队。

只有本次 `Matched` 且实际读回为 Good，UA 写结果才返回 `Good`。设备返回不同值时命令失败，但读取服务仍可以显示实际读到的不同值和 Good 采样质量：**通信成功读到当前值**与**请求值得到确认**是两件分别记录的事实。超时、取消、失败不代表物理写入一定没有发生，客户端不能自动假定可安全重复执行。

实际读回样本在设备锁内发布到共享最新值，不以客户端提交值回填。未核对的兼容 `WriteAsync` 仍只表示提交，最新值先成为 Uncertain；新的真实读回或采样成功后才恢复 Good。读回取消或过期后不能发布迟到成功。主动读回不额外插入历史记录，历史仍按采集扫描链路持久化。

日志包含独立命令 ID、`opcua:用户名` 来源、是否实际尝试/提交、核对次数和结果。该接口不提供跨会话重复命令 ID 去重，客户端应根据明确结果安排重试；平台 RPC 的持久化去重是独立功能。

## 6. 证书管理与恢复

PKI 位于当前数据目录的 `opcua/pki`：`own` 存本应用证书与私钥，`trusted` 存明确允许的客户端公共证书，`rejected` 存被拒绝证书，`issuers` 是 SDK 发行者存储。界面管理入口只导入单张客户端应用公共证书，不导入 CA 来扩大信任范围。

信任入口接受完整单张 DER/CER，或只有一块 `CERTIFICATE` 的 PEM；拒绝 PFX/PKCS#12、私钥、证书集合、DER 尾随内容、CA、SHA-1 签名、过期/尚未生效证书以及不足 2048 位的 RSA 公钥。信任新增后更新 SDK 校验缓存，新连接按更新后的列表验证。

上述入口面向客户端应用叶证书，不代办企业 CA 发行链和 CRL 管理。CA 签发的应用证书即使已经加入 `trusted`，缺少发行者仍会被 SDK 以 `BadCertificateChainIncomplete` 拒绝；本服务也启用 `RejectUnknownRevocationStatus`。这类部署需要按官方 PKI 规则配置完整发行链和所需吊销资料，再进行现场客户端验收。本轮自动测试覆盖自签名应用证书的精确信任，没有把企业 CA/CRL 部署记为已验证能力。

移除指定客户端证书后，正在运行的 UA 端点先停止再启动，撤销已有安全通道，不能让已建立会话永久保留被撤销证书的访问权。若重启遇到新错误，端点保持停止并显示错误，删除的信任不会自动恢复；设备采集继续。客户端若再次连接会被拒绝并进入拒绝列表。

### 端点名称或服务端证书需要变更

修改主机名或 IP 后，既有服务端证书若不包含该名称，启动会失败。处理步骤为：

1. 应用新的端点设置并确认其为客户端实际访问地址。
2. 在证书管理窗口选择“重建服务端证书”，确认断开客户端及更换信任关系。
3. 程序先校验完整配置、必要的密码环境变量，以及 `own/certs` 和 `own/private` 可写；预检失败时保留原证书和仍在运行的端点。
4. 停止 UA，删除本应用原证书和私钥，按最新端点名称生成新证书。保留所有可信/拒绝客户端条目和设备会话。
5. 若操作前 UA 正在运行则尝试恢复；若原来已停止，包括前一次名称不匹配导致启动失败，则保持停止，需显式启动。
6. 导出新公共证书，在每个客户端核对新指纹并替换信任，再重建连接/订阅。

删除旧证书后的生成或落盘失败会记录错误并清除旧证书状态缓存，不把已失效的旧公钥当作当前有效证书导出。目录可写预检不能保证后续磁盘空间充足，因此仍需处理生成失败。程序不保存或自动回滚旧私钥。只记录旧/新公共指纹，不向日志或配置输出私钥。

身份验证读取的是网关进程可见的密码环境变量。在 Windows 用户或系统设置中修改变量后，必须重启网关，让新进程取得新值；仅停止并启动 UA 服务不会刷新进程环境。已验证会话不会因环境变量在进程外被修改而自动注销；显式停止 UA 或退出网关会断开这些会话。

## 7. 服务 API 与验证

`GatewayOpcUaServer(GatewayRuntime, IGatewayLog, dataDirectory)` 构造时不启动监听，实现 `IAsyncDisposable`。公开 `StartAsync`、`StopAsync`、`ReconfigureAsync`、`GetStatus` 和上述公共证书管理方法。`GetStatus` 返回当前配置启用状态、实际运行状态、端点、Namespace URI、节点/会话数量、公共证书指纹/目录、启动时间及最后错误。正常退出先停止 UA，再关闭设备和持久化服务。

真实协议测试位于 [`OpcUaServerTests.cs`](../tests/EdgeGateway.Tests/Integration/OpcUaServerTests.cs)，使用官方客户端、独立临时 PKI、精确信任的测试证书和实际加密回环 TCP 连接；测试不会打开 AcceptAll 或 None 数据端点。南向设备在测试中使用确定性驱动，外部 UA 传输、身份验证、地址空间与订阅均为真实 SDK 服务。

验收覆盖：安全策略发现、设备/点位浏览、64 位读取和原时间戳、真实订阅变化；默认禁止写入、匿名禁止写入、错误密码和证书拒绝；主动读回匹配/不匹配、后续本地提交写、停止重启首次数据质量；真实 UA Cancel 取消排队批量写；故障状态；热增删/重命名、删除坏质量通知及相同 Guid 恢复后的新旧订阅；手动停机持久性与端口释放；证书导出、单张信任、重建、已有会话撤销和端点主机名变更。

可单独运行：

```powershell
dotnet test tests/EdgeGateway.Tests/EdgeGateway.Tests.csproj -c Release --filter FullyQualifiedName~OpcUaServerTests
```

2026-10-23 本地 `.NET 8` 定向测试实际通过 11/11，0 失败、0 跳过，耗时 54 秒。首轮发现官方客户端回环名称校验与默认证书生成不对称的问题后，已补齐 SAN 并保持 `checkDomain=true` 重测通过。后续 [CI 39972261116](https://github.com/kingtentx/edge-gateway/actions/runs/39972261116) 在 Windows Server 2022 / 2025 各通过全套 330 项测试（包含全部 11 项 UA TCP 测试）、UA 服务/证书实际操作和 20 个等效工作区；发布包已下载核对，详见 [本轮验收记录](kepware-opcua-verification.md)。这些结果不代表未执行的现场客户端验收已经通过。

## 官方实现依据

- [OPC Foundation UA-.NETStandard 1.5.378.182 源码](https://github.com/OPCFoundation/UA-.NETStandard/tree/1.5.378.182)，本次读取的标签提交为 `6cca32dd6d79c8b57d2e00e4d2071b991d2cf71b`。
- [ApplicationInstance 证书生成与名称验证](https://github.com/OPCFoundation/UA-.NETStandard/blob/1.5.378.182/Libraries/Opc.Ua.Configuration/ApplicationInstance.cs)。
- [官方 ReferenceServer 身份验证示例](https://github.com/OPCFoundation/UA-.NETStandard/blob/1.5.378.182/Applications/Quickstarts.Servers/ReferenceServer/ReferenceServer.cs)。
- [异步节点管理写入适配](https://github.com/OPCFoundation/UA-.NETStandard/blob/1.5.378.182/Libraries/Opc.Ua.Server/NodeManager/Adapters/AsyncNodeManagerAdapter.cs)，用于在等待设备通信时释放地址空间管理锁。
- [官方 TCP transport listener](https://github.com/OPCFoundation/UA-.NETStandard/blob/1.5.378.182/Stack/Opc.Ua.Core/Stack/Tcp/TcpTransportListener.cs)，部署时还需结合实际 Windows 防火墙与网络接口验证监听范围。
