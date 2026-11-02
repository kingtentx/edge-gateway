# JSON 中文输出

2026-11-02：JSON 中的中文名称、字符串值及错误说明直接输出 UTF-8 中文，例如 `"tagName":"压力状态"`，不再将普通中文写成 `\u538B\u529B...`。

GatewayJson 提供公共 TextEncoder 与保持默认契约的 TextOptions；配置专用 Options 同样使用此编码器，保留其 camelCase、字符串枚举和缩进规则。MQTT 五种平台的遥测、在线状态与 RPC 回复、配置导出、数据库 JSON 字段、补传/RPC 状态文件、外库持久缓冲、Worker 通信、插件清单及原生驱动结构值输出均采用可读中文。已有采样日志本就直接输出中文，继续保留其格式。

独立 UTF-8 JSON 文件/报文与现有日志使用相同的 UnsafeRelaxedJsonEscaping 编码器，保留引号、反斜杠、控制字符的必要转义，不额外转义中文及时间偏移中的加号；不对序列化后的字符串执行替换或 Unicode 解码，不改变字段名、数值类型、质量、时间或平台协议。稳定去重键、数据库目标指纹及连接缓存键维持原序列化算法。

CSV 中的结构值和界面结构值也使用中文编码。诊断 CSV 本身直接写入 UTF-8 中文。用户随后明确不需要旧日志/补传兼容，已移除本轮新增的旧日志导出重编码分支和兼容用例，日志导出继续冻结当前查询快照，不重写原始记录。新增定向用例覆盖五种平台中文名称/值/错误、实际配置文件导出及回读、嵌套 JSON、特殊字符往返。

用户授权清理本机 Debug/Release 运行目录的旧 logs、messaging/outbox 及 acquisition-spool.db（含 WAL/SHM），保留配置与 RPC 去重记录。已核对没有网关进程占用，但删除命令被自动审批策略拒绝（blocked by policy）；文件尚未删除，未绕过限制。验证产物保留。

## 本轮验证

- Release 独立目录编译通过，0 错误、420 项既有警告：`artifacts/chinese-json-build-2.log`，输出 `artifacts/chinese-json-runtime`。
- 80 项定向测试通过，0 失败、0 跳过：`artifacts/chinese-json-tests-2/chinese-json.trx`。范围为 ChineseJsonTests、PlatformAdapterTests、MqttRpcTransportTests、SeparatedLoggingTests、FileGatewayStoreTests、SqliteAcquisitionSpoolTests、GatewayWorkerClientTests、ConfigurationStoreTests。仅隔离文件与本机回环，未连接现场服务。
- 首轮 81 项中 4 项失败，保留 `artifacts/chinese-json-tests-1`。修正 IoTSharp 设备名位于主题的测试断言、旧 schema 7 错误提示与关系表清单断言；用户要求移除旧日志兼容后删除相应用例与重编码分支，公共编码器与既有日志一致。重跑数量减少 1 源于明确删除旧日志兼容用例。
- 未运行完整回归或界面烟测，未替换正在使用的程序。本轮源码、公共序列化与 Windows Release 编译分别记录，不将历史结果算作本轮通过。
