# 驱动开发与协议测试

## 扩展步骤

1. 在 `Plugins/Drivers/EdgeGateway.Driver.<Protocol>` 建立 `net8.0` 工程，引用 `EdgeGateway.Abstractions`；保持输出程序集名以 `EdgeGateway.Driver.` 开头。
2. 实现公共无参 `IDriverFactory`，提供唯一 `Descriptor.Id`、中文名称、是否允许写入与 `Validate`。
3. 每次 `Create` 返回独立 `IDeviceDriver`。连接、读取、写入接受取消令牌；`DisposeAsync` 关闭连接，重复调用安全。
4. 返回 `TagReadResult` 的真实原始值。发生点位错误返回 Bad；链路损坏抛出异常让运行时重连。不要在驱动内二次缩放。
5. 仅接受已启用且授权写入点位，验证地址、数据类型与数据范围。写入失败不自动重试，记录结果不确定。
6. 在 UI 中增加有业务含义的配置输入，或在高级配置的 `DeviceConfiguration.Properties` 中保存协议专用参数；禁止将密码写入该字典。
7. 为插件设置 `EnableDynamicLoading=true`，发布依赖与 `.deps.json`，把完整输出放在程序目录 `plugins/<Protocol>/` 后重启。
8. 使用与宿主一致的契约版本。插件加载器共享宿主 Model / Abstractions；插件有本机代码执行权限，只安装可信插件。无热卸载或沙箱隔离承诺。

## 内置 Modbus 约定

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
| 批量优化 | 当前逐点请求；相邻地址合并读取需要单独实现并回归 |

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
