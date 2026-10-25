# 驱动开发与协议测试

## 维护迁移驱动

原目录名就是稳定 DriverId，完整清单见 [迁移说明](../../../../docs/driver-migration.md)。12 个源驱动通过 `EdgeGateway.IotGateway` 注册；不要将其改名为原生插件命名或删除原 `ConfigParameter` / `Method`。`DeviceId`、`Timeout`、`MinPeriod` 绑定通用配置，其他参数保存到 `DeviceConfiguration.Properties`。

原同步调用由适配器的专属线程执行；OPC DA 构造、读取、释放遵守同 STA 线程约束。超时返回失败后，不再复用尚有操作运行的实例，同设备不能借重连创建无限新线程。错误/空值不得转换为 Good。更新驱动后运行完整性清单和实际行为测试，不能只检查程序集文件名。

点位保存实际方法名与类型。保留所有原读取方法；原 OPC/Modbus 的样例 Read 已改为真实读取兼容入口，默认使用明确的 ReadNode / HoldingRegisters 等方法。若新增诊断方法必须显式标识。写能力按驱动和方法同时判断。Hsl 授权码直接输入并随配置保存，界面掩码显示且不写入日志；FANUC 本机 DLL 不以占位文件代替。

源 Modbus 的 `ReadMultiple` 返回原始 `bool[]` 或 `ushort[]`，不是任意元素类型的已解码数组。例如先配置 `ReadMultiple` / 地址 `3,0,4,cache1` / 类型 `Any`，再依次用 `ReadFromCache` / `cache1,0` 和 `cache1,2` / 类型 `Float32` 解两个浮点值，并核对字节序。若要一个点位直接返回 `float[]`，需要单独新增并验证转换方法，不能仅更改类型名称。源 MC 的批量方法则按点位数据类型解析批量结果，返回结构值；IO 类型与返回形状不要混为一谈。

## 新原生驱动扩展步骤

1. 在 `Plugins/Drivers/EdgeGateway.Driver.<Protocol>` 建立 `net8.0` 工程，引用 `EdgeGateway.Abstractions`；保持输出程序集名以 `EdgeGateway.Driver.` 开头。
2. 实现公共无参 `IDriverFactory`，提供唯一 `Descriptor.Id`、中文名称、是否允许写入与 `Validate`。
3. 每次 `Create` 返回独立 `IDeviceDriver`。连接、读取、写入接受取消令牌；`DisposeAsync` 关闭连接，重复调用安全。
4. 返回 `TagReadResult` 的真实原始值。发生点位错误返回 Bad；链路损坏抛出异常让运行时重连。不要在驱动内二次缩放。
5. 仅接受已启用且授权写入点位，验证地址、数据类型与数据范围。写入失败不自动重试，记录结果不确定。
6. 在 UI 中增加有业务含义的配置输入，或在高级配置的 `DeviceConfiguration.Properties` 中保存协议专用参数；禁止将密码写入该字典。
7. 为插件设置 `EnableDynamicLoading=true`，发布依赖与 `.deps.json`，把完整输出放在程序目录 `drivers/<Protocol>/` 后重启；符合单 DLL 条件的插件可使用管理窗口上传。
8. 使用与宿主一致的契约版本。插件加载器共享宿主 Model / Abstractions；插件有本机代码执行权限，只安装可信插件。内置与上传驱动可在引用设备停止后更新，无设备引用时删除；同 DLL 的驱动整组处理，不承诺强制回收本机代码或沙箱隔离。

## 原生 modbus-tcp / modbus-rtu 约定

本节仅描述本项目原生的两个 Modbus 驱动。迁入的 `PLC.ModBusMaster` 另有 `ReadMultiple` / `ReadFromCache` 和 TCP/UDP/ASCII/RTU 透传模式，需按该驱动的方法说明配置；原生驱动的自动分批策略不改变迁移驱动的方法及缓存顺序。

| 项目 | 约定 |
|---|---|
| 地址 | 十进制 0–65535，起始地址加寄存器长度不能越界 |
| 站号 | 1–247，不执行广播写入 |
| 读取 | Coil/FC1、DiscreteInput/FC2、HoldingRegister/FC3、InputRegister/FC4 |
| 写入 | Coil/FC5、单寄存器/FC6、多寄存器/FC16 |
| Boolean | 位区仅支持 Boolean；寄存器中的 Boolean 占一个 16 位寄存器 |
| 数值类型 | Int16、UInt16、Int32、UInt32、Float32、Float64 |
| 缩放 | 驱动返回原始值；服务层处理倍率与偏移 |
| RTU 总线 | 当前同一串口最多一个启用设备，尚无多站共享总线调度 |
| 批量优化 | 仅合并本轮启用点位中同功能码、连续或重叠的地址；FC1/2 最多 2000 位，FC3/4 最多 125 寄存器，不跨空洞、不拆分单点多寄存器值 |

批量规划针对每次 `ReadAsync` 的实际扫描子集，不能借配置中的未到期点桥接空洞。响应按点位偏移、类型和字节序解码，返回顺序保持输入点位顺序。异常码 02/03 的多点批次允许每点一次回退读取，继承原扫描取消/超时预算；其他设备异常整批返回 Bad，坏帧、断线和超时关闭传输并传播，不自动重试写入。回环请求计数、分块边界、混合类型、质量与取消证据见 [批量读取说明](../../../../docs/modbus-batch-reading.md)，不能将请求数下降比例当作现场吞吐提升比例。

16 位 ABCD/CDAB 均为 AB，BADC/DCBA 均为 BA。32 位按枚举名称表示 ABCD、BADC、CDAB、DCBA。64 位如下：

| 枚举 | 64 位顺序 |
|---|---|
| ABCD | ABCDEFGH |
| BADC | BADCFEHG |
| CDAB | GHEFCDAB |
| DCBA | HGFEDCBA |

设备字节序可能使用不同命名，现场以厂家手册与已知数值确认，不凭枚举名字推断。

## 测试设计

- 使用固定独立字节向量验证数值，不只做 Encode→Decode 回环。
- TCP 测试绑定 `IPAddress.Loopback` 和系统分配端口，真实读写 socket，制造拆包、异常响应、错误事务号、错误长度和断线。
- RTU 用标准 CRC 向量测试编解码，并用真实 USB-RS485 / PLC 检验串口参数、帧间隔、超时和重开。
- 对每个写功能码验证地址、数值和回显，模拟“发送成功但确认丢失”，确保不会自动重复写入。
- 验证取消后后续请求不消费残留帧，停止采集等待 IO 释放，读写不交错。
- 使用多台设备测试故障隔离。多站 RTU 新增时必须引入串口级队列及公共锁，而非仅设备锁。

官方协议资料入口：[Modbus Specifications](https://www.modbus.org/specifications)。实现前阅读适用的 Application Protocol、TCP Messaging、Serial Line 规范。
