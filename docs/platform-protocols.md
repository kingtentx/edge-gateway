# 平台 MQTT 接入与点位 RPC

本模块提供通用 MQTT、ThingsBoard、ThingsCloud、ThingsPanel、IoTSharp 五种模式。专用模式遵循对应平台的网关主题与 JSON 外层格式，点位方法统一为 `read` / `write`。配置界面保存候选后，运行时在设备采集期间建立真实 MQTT 连接、校验订阅确认、接收请求并发送结果。平台配置属于全局设置，修改须全部设备停止后应用。切换 `Platform` 前还必须将原平台的持久补传队列发送完成；队列未清空时在保存前拒绝切换，避免旧平台载荷被发往另一协议。

## 配置与身份

`Mqtt.Platform` 缺省为 `Generic`，旧配置保持原遥测格式。`RpcEnabled` 与 `AllowRemoteWrite` 均缺省为 `false`；启用 RPC 后先提供只读诊断。远程写同时要求全局允许、设备正在运行且启用、目标点位启用且 `Writable`，再由统一设备操作服务在设备互斥锁内复查权限和会话。表达式点位不能写入。

Broker 地址、端口、TLS、客户端标识、用户名配置沿用已有字段。令牌用作用户名的平台直接设置 `AccessToken`，其值作为 MQTT 用户名并优先于 `Username`。密码直接设置 `Password`；平台允许空密码时留空。凭据字符串保留输入原值，写入配置 SQLite 和 JSON 导出，界面使用掩码，日志与诊断继续脱敏。TLS 使用系统证书信任链。

ThingsBoard 可配置 `PlatformDeviceType`，默认 `default`。ThingsCloud 的 `ThingsCloudTypeKey` 是可选的子设备类型标识；已有且已绑定网关的子设备可以留空。ThingsPanel RPC 必须配置云端网关的 `PlatformGatewayId`（文档中的 `device_number`）。本地设备名称用作平台子设备名称或地址；专用模式要求名称唯一，IoTSharp 名称不能含 MQTT 主题分隔符或通配符。

## 真实主题与格式

| 模式 | 遥测 | 请求订阅 | 回复 |
|---|---|---|---|
| Generic | 可配置 `Topic`，默认 `edge/{gatewayId}/telemetry` | 可配置 `RpcRequestTopic`，默认 `edge/{gatewayId}/rpc/request` | 可配置 `RpcResponseTopic`，默认 `edge/{gatewayId}/rpc/response` |
| ThingsBoard | `v1/gateway/telemetry` | `v1/gateway/rpc` | `v1/gateway/rpc`，保留原请求 ID 的数字或字符串类型 |
| ThingsCloud | `gateway/attributes` | `gateway/command/send` | `gateway/command/reply`，回复放在 `command.params` |
| ThingsPanel | `gateway/telemetry` | `gateway/command/{PlatformGatewayId}/+` | `gateway/command/response/{message_id}` |
| IoTSharp | `devices/{设备名称}/telemetry` | `devices/+/rpc/request/+/+` | `devices/{设备名称}/rpc/response/{方法}/{请求标识}` |

ThingsBoard 与 ThingsPanel 上报按原采样时间组织 `ts` / `values`；ThingsCloud 与 IoTSharp 的值映射带 `_edge` 元数据记录原采样时间。所有专用模式的 `_edge` 均包含 `messageId`、网关标识、发送快照时间、每点质量与错误。`Bad` 和 `Uncertain` 不发送普通值字段，以免平台把停止或失败的旧值当作新有效值；质量变更仍通过元数据上报。平台侧物模型/规则应允许 `_edge` 对象，并明确消费质量，不能只读取此前留下的普通数值。点位不能命名为 `_edge`。

专用格式并不保证平台业务处理成功。所有发送使用 QoS 1，PUBACK 仅代表 Broker 确认；遥测沿用 SQLite outbox 持久补传与至少一次语义，重复消息用 `_edge.messageId` 识别。同一快照分为多条 ThingsCloud / IoTSharp 消息时共用消息标识，消费去重需同时使用设备标识。ThingsCloud 每条遥测只包含一台设备，避免大批量注册占用一次通信超时。

ThingsBoard 与 ThingsCloud 依据实际采集状态发送子设备连接/断开通知，MQTT 重连后重新通知当前在线设备。ThingsBoard 官方遥测 API 可自动创建设备，历史补传无需伪造当前在线状态；RPC 路由需要连接通知。

ThingsCloud 在同一个 MQTT 互斥区内处理注册和遥测：当前运行目标先发送 `gateway/connect`，除了 PUBACK 还必须收到匹配子设备地址的 `gateway/connect/response` 且 `result=1`，之后发送属性；属性还须等 `gateway/attributes/response` 的 `result=1` 才完成发送。平台拒绝、缺失响应或连接中断时保留 outbox。该平台响应比 Broker 确认更进一步，但仍不等于所有下游业务规则执行完成。

本地已停止或已删除目标的原有快照仍继续补传。ThingsCloud 先尝试原载荷，云端已有绑定时可直接接受；若明确返回 421 且配置了 `ThingsCloudTypeKey`，执行临时注册、原载荷重发及立即断开路由。临时注册只用于恢复历史投递路由，样本质量和原采样时间不改变，也不在本地把设备标记运行。云端无绑定且未提供类型标识时会显示外部绑定错误，必须恢复该绑定，不能丢弃历史伪报成功。这些是平台路由状态，不替代物理设备的实时质量。

ThingsCloud 当前使用实时属性主题保持平台属性与规则能力；补传的原采样时间位于 `_edge.tags`。平台按到达时间建立的属性历史/规则可能在补传时再次处理旧值，须按该元数据设定云端去重和时间策略。官方 `gateway/attributes/series` 是另一种不触发实时规则的历史 API，本模式未将已经持久化的属性消息改写为该 API。

## 请求示例

通用请求（设备与点位可用稳定 Guid，也可用唯一名称）：

```json
{"id":"operator-20261023-001","device":"pump","method":"write","params":{"tag":"speed","value":1200}}
```

通用读请求将 `method` 改为 `read`，不传 `value`。可选 `params.expiresAt` 使用带时区 ISO 8601 时间，必须尚未过期且不超过未来 10 分钟。数值、字符串和布尔值保持对应输入类型；64 位整数不经 `double` 中转。

ThingsBoard：

```json
{"device":"pump","data":{"id":21,"method":"read","params":{"tag":"speed"}}}
```

ThingsCloud：

```json
{"device":"pump","command":{"id":21,"method":"write","params":{"tag":"speed","value":1200}}}
```

ThingsPanel 发布到 `gateway/command/{云端网关编号}/21`：

```json
{"sub_device_data":{"pump":{"method":"read","params":{"tag":"speed"}}}}
```

ThingsPanel 每请求只处理一个直接子设备的一个点位；多目标命令明确拒绝。使用带请求标识的命令通道，不订阅缺少请求标识的 `telemetry/control` 作为写旁路。回复使用该平台的数字 `result`（0 成功、1 失败）、`message`、`errcode`、`ts`，并附本次操作数据。

IoTSharp 发布到 `devices/pump/rpc/request/write/operator-001`：

```json
{"tag":"speed","value":1200}
```

回复保留源适配的 `DeviceId`、`Method`、`ResponseId`、字符串 JSON `Data` 结构。平台方法名称限定 `read`、`write`；不接受任意驱动方法、配置写入、脚本、启动或重启命令。

## 读写完成语义与重试

`read` 执行真实驱动单次读取，复用运行设备会话与串行门闩，不用缓存值冒充本次读取。设备停止、未启用或点位不存在时明确拒绝，不通过 RPC 创建临时连接。

`write` 走统一 `WriteVerifiedAsync`，执行类型检查、工程值逆转换、权限检查、写入及同一门闩内显式读回。回复只有 `Matched` 才 `success=true`；`Mismatched`、`TimedOut`、`Cancelled`、`Failed` 分别保留，不将 Broker 确认或写请求已提交视为数值核对通过。回复含命令 Guid、是否已提交、实际读回、次数与耗时。RPC 使用精确比较（绝对和相对容差均 0），浮点期望值按实际写入数据类型转换后再比较。

远程写执行前在 `messaging/rpc` 命令文件 事务中占位，记录平台/连接/网关/设备/请求标识的键与命令指纹。去重域包含 Broker、客户端标识、本地 `GatewayId`、`PlatformGatewayId` 和配置的账号身份；令牌账号使用认证模式与配置的 `Username` 作为稳定账号身份，不将可轮换的 `AccessToken` 值加入去重键。为同一逻辑网关轮换 Token 时保持本地网关、客户端、云端网关及账号标识稳定；切换租户或账号时必须分配新的网关/客户端/账号标识，不能仅替换 Token。身份仅以 SHA-256 键持久化，不记录令牌。

相同写请求重投只重放已保存回复；同标识改用不同目标点位、值或方法拒绝，读取也会检查持久写占位，不能把已有写请求改成读取。占位存在而无完成记录，表示进程中断或结果持久化不确定，返回 `Indeterminate`，禁止自动再写。远程调用方必须为新的有意操作使用新的请求标识；平台若循环复用短 ID，应调整调用方生成策略。没有持久审计接口的存储实现拒绝远程写入。

当前审计容量为 10000 条，达到上限后保留已有证据并拒绝新的远程写，诊断显示具体原因；不会静默删除待确定命令或旧记录以允许请求重放。此限制不影响本地采集、上报与本地写。维护应先备份审计数据库并核对尚未确定的写入，不能把清库当作正常请求重试手段。

读取请求使用最多 1024 条的进程内响应缓存；缓存内的读取标识也不能改作写入。读取证据不持久化，缓存淘汰或重启后旧读请求可能重新采样，调用方仍须保证标识不跨方法复用。写回复发送失败不会再次执行写入，调用方使用同一请求标识查询原结果。停止时取消命令，并给予结果审计独立的 3 秒取消预算，随后关闭 MQTT；已经开始的 SQLite 同步 SQL 不会被该令牌中断，外部数据库写锁仍受 SQLite 的锁等待超时约束，因此这不是严格的停机时间上限。

## 容量与诊断

MQTT 回调只复制并投递请求，设备 I/O 在独立接收任务执行。入站队列 64 条、载荷 64 KiB、JSON 深度 16、每秒最多 20 条处理；超限、无效 UTF-8、MQTT retained 消息和已超时排队请求不执行设备操作。retained 与溢出计数在 `GetRpcStatus()` 可见。订阅必须收到成功 SUBACK，失败显示未订阅并按有上限退避重连。

`GetRpcStatus()` 提供启用/允许写入/订阅状态、请求/回复/拒绝/重放/丢弃计数、最近请求、结果与错误。计数为本进程观察值。连接恢复只清理连接错误，不把设备命令失败清成成功。

## 依据与验证边界

参考源项目固定提交 `8de442a7dec52d2993d1e9667d799846936d681b` 的四个 `Plugins/Plugin/PlatformHandler`。其中 ThingsCloud 的回复和 ThingsPanel 的接收/回复原为空实现，本次增加真实协议处理。

- [ThingsBoard Gateway RPC](https://thingsboard.io/docs/reference/gateway-api/rpc/) 与 [MQTT Gateway API](https://thingsboard.io/docs/edge/reference/apis-and-sdks/gateway-mqtt-api/)：请求、回复、连接与遥测。
- [ThingsCloud 网关 MQTT 接入](https://www.thingscloud.xyz/docs/guide/connect-device/gateway-mqtt.html)：属性上报、子设备通知与命令/回复。
- [ThingsPanel 网关规范](https://thingspanel.io/docs/device-connectivity/mqtt-gateway-rule) 和 [回复结构](https://thingspanel.io/docs/device-connectivity/mqtt-device-rule)：命令主题与结果字段。
- [IoTSharp MQTT 协议](https://iotsharp.net/docs/iotsharp/docs/tutorial-extras/mqtt/)：网关遥测、六段下行 RPC 主题；回复对象同时保留参考项目适配约定。

自动测试包含五模式独立 JSON 向量、真实 MQTT TCP SUBSCRIBE/SUBACK/PUBLISH/PUBACK、订阅拒绝、保留命令拒绝、RPC 通过实际 TCP 进入运行时后默认拒写、ThingsCloud 注册顺序/平台应用响应/拒绝/已删除目标临时路由、真实运行时权限、显式读回、持久去重、重启重放、跨方法标识冲突、云网关/账号隔离与令牌轮换，以及 SQLite 待发送消息阻止跨平台切换。真实平台账号、租户物模型和现场设备仍需在部署环境联调；自动测试不能替代这些验收。
