# 统一驱动参数配置

2026-10-30：按用户要求，内置驱动、迁移驱动和上传插件统一由 DriverDescriptor.Parameters 描述配置项，再由同一 DriverParameterPanel 创建 AntdUI 输入控件。原有 Modbus TCP/RTU 专用 UI 分支移除，连接参数定义移至各自驱动工厂。

参考 [iioter/iotgateway](https://github.com/iioter/iotgateway/tree/8de442a7dec52d2993d1e9667d799846936d681b) 的 ConfigParameterAttribute 机制，迁移基线仍为 `8de442a7dec52d2993d1e9667d799846936d681b`（见 driver-migration-manifest.json）。当前迁移适配器从属性获取名称、类型、默认值、枚举与关联关系；新插件通过同一 Parameters 契约声明参数。

- 参数类型决定文本、数值、枚举或开关；已声明的秘密参数使用掩码。
- 原生连接字段通过 DeviceProperty 映射回既有强类型设备字段，保持真实驱动读取和 SQLite 存储不变；仅允许连接字段映射，不允许元数据改写设备身份或启停设置。
- 其他驱动参数保存在 Properties；原有 Id、周期和超时关联参数仍只读并从通用设置同步。
- 编辑设备默认打开对应驱动参数页；新增设备先选驱动。模拟驱动明确提示无需额外连接配置。
- “自定义参数”支持手工新增、编辑、重命名、删除名称/值。禁止与驱动声明参数同名、禁止空名和重名；值为字符串，由插件解释和校验。编辑草稿在切换驱动前保存，切回保留；保存配置前再次收集当前编辑值。
- 密码、Token、Secret、Authorization 等名称的值使用掩码。所有设置随设备整体保存；取消不修改原设备。插件必须实际读取同名参数才生效，参数编辑不能自动生成协议行为。
- 保留驱动校验。迁移驱动仍拒绝未声明的参数，以免拼写错误被静默忽略；后续插件可按自己的 Validate 实现接受扩展参数。

固定通用字段及自定义参数管理界面保留 Designer；驱动字段按元数据动态生成。现有设备 ID、点位、逐设备配置提交和平台草稿隔离语义保持。

## 本轮验证（用户已授权同范围修复重跑）

- Release 编译：0 错误；最后构建仍有既有 `GatewayWorkerClient.cs:283` CS8602 警告。见 `artifacts/driver-parameters/build-final.log`。
- 6 项定向测试通过：原生参数映射经过 JSON 目录传递、全部 12 个迁移驱动的 74 个配置参数保留、授权值元数据、强类型连接字段及自定义参数 SQLite 重开/重命名/删除、凭据与 JSON 交换。见 `artifacts/driver-parameters/tests/driver-parameters.trx`。
- 架构检查通过：8 个层边界、24 个无环工程及 UI/Application/MQTT 隔离。见 `artifacts/driver-parameters/architecture.log`。
- 隔离后台界面检查：全部 15 个驱动逐一打开，核对参数完整回读、宽窄窗口与各页签，原生字段/手工参数切换草稿、取消、只读与真实工厂无效站号拒绝；手工参数新增、修改、重命名、删除、重名、声明冲突及秘密值掩码。运行 DPI 为 192，没有修改用户桌面缩放。
- 早期失败保留：`ui-1` 发现自定义字段标题未居中，修正输入容器锚定；`ui-2` 发现旧检查仍要求默认采集设置页，更新检查至驱动参数页；`ui-3` 和 `ui-4` 已通过。截图复核进一步发现切换参数后旧重名提示残留，已修复。最终 `ui-final` 通过，退出码 0、stderr 为空，生成 31 张截图；已复核自定义参数与 RTU 窄窗口截图。

本轮未运行全量核心、真实设备、插件现场通信或 VS Designer 往返验收；固定控件已在 Designer 中创建，界面验证不等同于设计器验收。
