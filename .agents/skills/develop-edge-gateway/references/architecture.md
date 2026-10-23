# 结构参考与工程约束

## 参考基准

上游：[iioter/iotgateway](https://github.com/iioter/iotgateway)。结构与源码迁移固定在提交 [`8de442a7dec52d2993d1e9667d799846936d681b`](https://github.com/iioter/iotgateway/commit/8de442a7dec52d2993d1e9667d799846936d681b)，该版本已升级到 .NET 10，采用 ASP.NET / WTM 的 B/S 界面。本项目主体按其模块职责独立实现，同时将全部 12 个原驱动工程与 PluginInterface 重新编译为 .NET 8，以适配层接入 WinForms；不提供与上游数据库或 .NET 10 驱动 DLL 的二进制兼容承诺。

| 上游结构 | 本项目对应 | 适配决策 |
|---|---|---|
| IoTGateway.Model | src/EdgeGateway.Model | 设备、点位配置与运行时样本分开 |
| IoTGateway.DataAccess | src/EdgeGateway.DataAccess | 本地 JSON 配置；SQLite 历史与 outbox |
| Plugins/PluginInterface | Plugins/PluginInterface + src/EdgeGateway.IotGateway | 保留源契约；适配至 Abstractions 异步工厂接口 |
| Plugins/Drivers | Plugins/Drivers 下相同的 12 个原目录 | 保留方法与参数，逐工程迁移 .NET 8，每设备独立实例 |
| DriverService | Service/DriverRegistry | 工厂注册、插件依赖解析与契约共享 |
| DeviceService / DeviceThread | Service/GatewayRuntime | 设备会话与配置应用共用生命周期锁；目标局部重启、互斥、质量、退避与独立发布 |
| MessageService / PlatformHandler | Service/MqttTelemetryPublisher | 通用 JSON 遥测；厂商格式另行适配 |
| IoTGateway / ViewModel / WalkingTec.Mvvm | EdgeGateway.WinForms | 替换控制器、Web 视图和 WTM 依赖 |

## 运行流程

```mermaid
flowchart TD
    UI[WinForms 配置与监控] --> Config[生命周期锁内完整校验与原子保存]
    Config --> Runtime[采集运行时]
    Runtime --> Drivers[每设备驱动实例]
    Drivers --> Samples[原始值 工程值 质量 时间戳]
    Samples --> UI
    Samples --> History[SQLite 历史]
    Samples --> Snapshot[按启用和上传配置生成快照]
    Snapshot --> Outbox[SQLite 待发送队列]
    Outbox --> MQTT[MQTT QoS 1]
    MQTT --> Ack[收到 Broker PUBACK 后删除]
    Ack --> Outbox
```

配置编辑对象不得直接交给运行中的采集任务。驱动实例由运行时持有；存储与发布器由程序组合根持有和释放。原生 UI 限 Windows；OPC DA 和 FOCAS 的实际调用还有 Windows、COM 或厂商原生库要求，不能将核心跨平台编译能力等同于这些驱动可在任意系统运行。

## 配置事务与会话边界

`GatewayRuntime.ApplyConfigurationAsync` 接收完整候选及 `IConfigurationStore`，与设备启停共用生命周期锁。先深拷贝并校验所有设备和点位，再原子保存；提交前失败或取消不动现有会话。保存提交后完成同步，不再因调用方取消留下磁盘和运行时不同步的状态。

按稳定 ID 计算新增、变更和删除：新建与原本停止的设备保持停止；配置发生实质变化且原本运行的设备取消并等待旧会话与写入，再用新配置建立会话；禁用或删除只停止对应目标。其他设备的连接、状态、最新样本和共享历史/MQTT 工作任务保持不变。仅重启唯一运行设备时仍复用共享任务；禁用或删除最后一个运行目标时才排空历史并关闭共享任务。旧 `Configure` 保留给全停初始化，不用于 UI 先保存后配置的流程。

每设备变更包含其点位集合，因此运行设备增删点位、切换上传或换算参数会重启该设备；旧显示样本被清除，等待新会话产生新数据，历史数据库不因编辑或删除被清空。字典键顺序不触发无意义的重启，点位数组顺序继续有意义。配置应用完成表示新会话已调度，实际连接结果通过设备状态、错误与重连机制报告。

平台草稿独立于已应用配置，未应用时按已有设置启动。修改网关、历史、MQTT 全局字段及整份配置替换仍要求全停。设备与点位弹窗通过可等待回调，校验、保存及运行时应用全部成功才关闭；失败保留输入。流程、结果集合和验收用例见 [运行中配置与数据闭环](../../../../docs/live-configuration.md)。

遥测筛选当前配置中的启用设备、启用点位及 `UploadEnabled`。上传关闭不改变采集和历史，已经持久化的 outbox 保留原主题、原消息 ID 与原载荷；Broker 确认不等于平台业务入库。诊断使用不可变快照供 UI 读取，后台不直接访问控件。

## 源码定位

- [上游设备模型](https://github.com/iioter/iotgateway/blob/8de442a7dec52d2993d1e9667d799846936d681b/IoTGateway.Model/Device.cs)
- [上游点位模型](https://github.com/iioter/iotgateway/blob/8de442a7dec52d2993d1e9667d799846936d681b/IoTGateway.Model/DeviceVariable.cs)
- [上游驱动接口](https://github.com/iioter/iotgateway/blob/8de442a7dec52d2993d1e9667d799846936d681b/Plugins/PluginInterface/IDriver.cs)
- [上游采集线程](https://github.com/iioter/iotgateway/blob/8de442a7dec52d2993d1e9667d799846936d681b/Plugins/Plugin/DeviceThread.cs)
- [上游 MIT 许可](https://github.com/iioter/iotgateway/blob/8de442a7dec52d2993d1e9667d799846936d681b/LICENSE)
- [Windows Forms .NET 8](https://learn.microsoft.com/dotnet/desktop/winforms/whats-new/net80)

## 迁移边界

全部 12 个驱动的目录、文件、原方法和参数有显式迁移清单与自动检查。完整信息见 [驱动迁移说明](../../../../docs/driver-migration.md)。保留 3 个本项目原生驱动入口，共 15 个工厂。驱动迁入和程序集加载不等于每个型号都经过现场验证；新增依赖核对许可证、目标框架和包版本，不直接复制上游 .NET 10 的包清单。
