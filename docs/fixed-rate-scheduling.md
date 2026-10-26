# 设备与点位固定节拍扫描

## 行为变更

从“本轮完成后再等待整个采样周期”改为“按设备会话的固定时间轴扫描”。设备会话使用现有的 `Stopwatch` 单调时间，起点为 0，首次立即读取。配置为 1000 ms 时，正常计划点为 0、1000、2000、3000 ms；读取、锁排队和输出入队耗时占用这个周期，不额外叠加到下一轮。

每个会话有自己的时间原点，并不要求 50 台设备同一墙钟时刻一起读取。采样记录仍保留实际读取的时间和质量，不用计划时间覆盖真实时间，不以缓存或虚构记录填补错过的扫描。

| 场景（周期 1000 ms） | 下一轮计划时间 |
| --- | --- |
| 首轮开始于 0，读取和入队在 200 ms 完成 | 1000 ms，剩余等待约 800 ms |
| 首轮完成于 1000 ms | 当前 1000 ms 节拍可执行，无需再等一秒 |
| 首轮完成于 1500 ms | 跳过已错过的 1000 ms，等待 2000 ms |
| 读取完成于 200 ms，但输出入队至 2300 ms 才结束 | 等待 3000 ms，不用入队前的剩余时间继续延迟，也不补发 1000/2000 ms 的读取 |
| 等待或重连后 7300 ms 才恢复，当前读取完成于 7450 ms | 只进行一次当前读取，再等待 8000 ms，不补发历史读取 |

上表是调度规则示例，不是 Windows 实机时序或吞吐测量。系统线程调度、设备通信和下游处理仍可能使实际开始晚于计划时间；该修复不提供硬实时保证。

## 实现位置

- `DeviceScanPlanner.Complete` 从上一计划时间推进一个周期，并用整数余数运算直接跳到不早于本轮结束的下一计划点，避免逐次循环追赶。恰好等于本轮结束的计划点不算错过。
- `GatewayRuntime.CollectDeviceAsync` 在采样成功进入有界输出通道之后，才推进本轮参与点位的计划并重新计算等待，覆盖输出背压耗时。
- `DeviceRate` 忽略点位单独周期；`TagRate` 使用各点位周期，0 继承设备周期。只推进本轮实际尝试的子集，其他已到期点位仍可以读取。
- 无启用点位时仍按设备周期执行连接检查，避免空循环。
- 同一设备仍只有一个顺序读取任务，采集、写入和诊断仍共用原来的设备锁及通道锁。
- 通道的 `InterRequestDelayMs` 仍从上一操作结束后计算；它是通信间隔，不改成固定采样节拍。
- 失败后的指数退避、故障降速和取消令牌保持原语义。正常扫描节拍不绕过故障等待；恢复后回到原时间轴，不集中补读。
- 设备重启/配置变更仍按原生命周期重建该设备会话及计划，不重新安排其他设备。

## 测试范围与当前状态

已新增 `DeviceScanPlannerTests` 的确定性用例：读取耗时、周期边界、超期跳过、延迟首轮、长时间无累积漂移、输出入队等待、混合点位周期、禁用点位、会话重启和超过 Int32 范围的运行时长。逻辑 24 小时/50 天只表示人工推进单调时间的算法用例，不表示实际运行了这么久。

`AcquisitionPoliciesTests` 同步移除旧的“读取完成后还必须等待一个周期”语义，保留读写序列化断言，并新增含模拟读取耗时的实际运行时定向回归。已有同通道间隔、独立设备、故障降速、真实 TCP 断线恢复与单设备启停用例可作为获准后的邻接回归范围。

本次未执行构建、测试、静态验证脚本或 Windows 界面验收；按仓库约定须用户单独确认后执行。提交使用 `[skip ci]`，不通过自动 CI 间接触发未经确认的测试。没有新增 NuGet 依赖，没有修改配置结构、界面或驱动协议。

建议获准后先运行最小调度回归：

```powershell
dotnet test tests/EdgeGateway.Tests/EdgeGateway.Tests.csproj -c Release --filter "FullyQualifiedName~DeviceScanPlannerTests|FullyQualifiedName~FixedRateScanningIncludesReadTime|FullyQualifiedName~PointScanRates|FullyQualifiedName~DeviceScanRateIgnoresPointOverrides|FullyQualifiedName~SharedChannelSerializes|FullyQualifiedName~IndependentChannelsAllow|FullyQualifiedName~FailureDemotion|FullyQualifiedName~RealTcpDisconnect"
```

修正扫描节拍不等于已经验证“50 台 × 200 点 × 1 Hz”稳定吞吐。设备批读、队列背压、日志/数据库输出、持久缓冲和真实硬件长稳仍需另行授权验收。
