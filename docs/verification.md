# 验证记录

验证日期：2026-10-22。目标框架：.NET 8，Windows Forms。以下区分执行结果与待执行验收，不将构建通过等同于现场设备验证。

## 本地自动验证

执行环境为 Ubuntu 24.04，SDK 8.0.426 / Runtime 8.0.32。UI 使用 Windows targeting pack 进行交叉编译，核心及协议测试在 Linux 原生运行。

| 项目 | 本地结果 |
|---|---|
| Service / DataAccess Release 编译 | 0 警告、0 错误 |
| WinForms Release 交叉编译 | 0 警告、0 错误 |
| 驱动测试 | 27 通过，0 失败，0 跳过 |
| 运行时与存储测试 | 20 通过，0 失败，0 跳过 |
| MQTT/TLS 真实 socket 回环 | 3 通过，0 失败，0 跳过 |
| 开发 skill 格式校验 | 通过 |
| 完整解决方案 Release 构建 | 0 警告、0 错误 |
| 全量集成测试 | 50 通过，0 失败，0 跳过 |

命令：

```text
dotnet build EdgeGateway.sln -c Release
dotnet test tests/EdgeGateway.Tests/EdgeGateway.Tests.csproj -c Release
```

驱动测试覆盖真实 TCP 拆包、FC1/2/3/4、FC5/6/16、错误 MBAP 事务号/协议号/站号/长度/功能码、返回字节数、异常响应、取消、超时、写入回显，另有固定字节序与 CRC 向量。

运行时测试覆盖深拷贝配置、工程量、失败质量、读写互斥、写入权限、整数逆换算与边界、停止重启、历史落盘与补传重试。存储测试覆盖原子保存、备份、损坏配置、取消保存、SQLite 重启恢复、并发容量限制、确认删除及保留时间。

MQTT 测试通过真实本机 TCP Broker 测试端验证 3.1.1 协议字节、QoS1、UTF-8 消息内容、PUBACK 前不完成、无确认超时后断开。TLS 测试实际进行握手并拒绝未受信任自签证书。

## Windows CI

`.github/workflows/build.yml` 定义 Windows Release 构建、相同自动测试、原生 WinForms 启动和六页布局烟测、PNG 截图，以及 Windows x64 自包含 ZIP。每次实际执行结果见 [Actions](https://github.com/kingtentx/edge-gateway/actions)，测试 TRX 与截图保存在 `verification` artifact。

本文件初次提交时本地尚未执行 Windows 原生运行；以对应 commit 的 Actions 结果为准。只有成功完成该任务后才能称 Windows 烟测已通过。烟测不会开启现场设备连接，也不能代替人工交互和多 DPI 验收。

## 尚需现场验证

- 真实 Modbus TCP PLC/仪表、实际点表与字节序；串口 RS485 电气连接、RTU 多种设备兼容性。
- 用户 Broker 的认证、可信 TLS 证书与离线恢复；云端按 messageId 去重。
- Windows 11 / Windows Server 实际用户会话，1366×768 / 1920×1080 / 3200×2000 以及 100% / 150% / 200% DPI。
- 预期设备数、点位数和周期下 24–72 小时运行，磁盘满、强制断电与数据恢复。

RTU 的 CRC 和代码编译已验证，但未连接真实串口。`SerialPort.Open` 无原生可取消接口，极端驱动阻塞无法由托管取消令牌强制中止。第三方插件也必须遵守可取消契约，不保证安全中断不合作插件。
