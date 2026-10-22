# 验证记录

验证日期：2026-10-22。目标框架：.NET 8，Windows Forms。以下区分执行结果与待执行验收，不将构建通过等同于现场设备验证。

## 本地自动验证

执行环境为 Ubuntu 24.04，SDK 8.0.426 / Runtime 8.0.32。UI 使用 Windows targeting pack 进行交叉编译，核心及协议测试在 Linux 原生运行。

### 原始 50 项回归基线

以下保留迁移前已执行的测试分类与覆盖说明。本轮在该基线上增加迁移适配、原协议和数据保真测试，最新全量结果见下一节；50 项不是当前测试总数。

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

### 全量驱动迁移后的最新结果

| 项目 | 本轮本地结果 |
|---|---|
| 源驱动迁移清单校验 | 通过：12 个原驱动工程、65 个原读取方法、74 个原配置参数、176 个来源文件 |
| 完整解决方案 Release 构建 | 本地增量构建报告 0 警告、0 错误；首次 Windows 干净构建 419 警告、0 错误，详见 Windows CI |
| 全量自动测试 | **93 执行、93 通过、0 失败、0 跳过** |
| Windows x64 自包含交叉发布 | 成功；12 个原驱动及宿主、接口、适配层、OPC Automation、运行时等 19 个关键文件存在，发布目录共 533 个文件；不代替 Windows 执行验证 |
| Windows 原生启动与新版 UI 烟测 | Windows 93 项测试通过；驱动选择器已修复，长参数页检查已按真实焦点访问调整，待最新提交完整复验 |
| 真实设备与厂商运行环境 | 未执行现场验收；不由构建、元数据或回环结果推定通过 |

迁移基准为上游提交 `8de442a7dec52d2993d1e9667d799846936d681b`，来源与完整条目见 [迁移清单](driver-migration-manifest.json)。清单脚本核对文件存在且非空、原方法与参数保留、目标框架为 `net8.0`、工程纳入解决方案；来源 SHA256 用于追溯，不要求已修改的迁移文件仍与原文件逐字节相同。此检查证明迁移范围完整，不证明全部协议已完成设备联调。

本轮命令：

```text
python scripts/verify-driver-migration.py
dotnet build EdgeGateway.sln -c Release --no-restore
dotnet test tests/EdgeGateway.Tests/EdgeGateway.Tests.csproj -c Release --no-build --logger "trx;LogFileName=tests.trx"
```

最新 `tests.trx` 计数为 93/93。MTConnect 缺失数据项曾在中间验证中暴露错误，修复后已由最新全量测试重新验证；本表记录的是修复后的结果。

| 最新测试分类 | 通过数 | 覆盖内容 |
|---|---:|---|
| 原生驱动 | 27 | 保留上述 Modbus TCP、编解码、CRC 和模拟驱动回归 |
| 运行时、配置与存储 | 34 | 在原 20 项上增加 v1→v2 内存迁移与备份、未知版本拒绝、64 位整数与文本读写、空白/空字符串保留、数组形状、结构值落盘、Bit 逆换算和整数浮点舍入边界 |
| MQTT/TLS 回环 | 3 | 保留上述 QoS1 确认、超时与证书拒绝测试 |
| 迁移工厂与元数据 | 7 | 12 个工厂及全部原方法/参数、方法读写能力、26 种类型与 5 种显式字节序映射、结构值与空值、未启用示例配置、源/原生串口设备统一独占校验 |
| 同步驱动适配生命周期 | 7 | 构造/读取/异步写入/清理的线程一致性、空值与异常质量、连接失败清理、取消和超时隔离、同设备重连限制、迟到回调与严格参数转换 |
| 原 Modbus 实际 TCP 回环 | 7 | 真实寄存器读取与 `Read` 兼容入口、批量位缓存及 Bool/Bit 顺序读取、UInt64 四种字节序写入与回显 |
| 原流式协议实际 TCP 回环 | 2 | Mock 拆帧、有符号字节及拒绝第二次旧值；Toledo 连续帧重组与停止来帧后的超时 |
| 原 MTConnect 实际 HTTP 回环 | 6 | Agent 探测及指定数据项读取；非 Agent 内容、损坏 XML、HTTP 错误、缺失数据项与 `UNAVAILABLE` 不得报告成功 |
| 合计 | **93** | 原 50 项基线加 43 项新增用例 |

上述生命周期测试已在 Linux 及首轮 Windows CI 验证线程和隔离行为，Windows STA 断言由同次 Windows 测试执行。批量结构值测试不等于已对所有源驱动的数组协议完成实机验证。

## NuGet 依赖审计

本轮审计报告使用官方 NuGet 源 `https://api.nuget.org/v3/index.json`，选项为 `--vulnerable --include-transitive`，JSON 中的项目范围为 `src/EdgeGateway.WinForms/EdgeGateway.WinForms.csproj`。审计进程退出码为 0，报告未列出该宿主直接或传递 NuGet 包的已知漏洞条目。

同范围复核命令：

```text
dotnet list src/EdgeGateway.WinForms/EdgeGateway.WinForms.csproj package --vulnerable --include-transitive --source https://api.nuget.org/v3/index.json --format json
```

此结果按审计当时的漏洞数据与实际报告范围记录，不扩大为整个解决方案所有独立依赖、迁入源码、COM 组件或厂商原生库的安全审计，也不代表完成许可证与运行授权验证。FOCAS 原生 DLL、Hsl 有效授权及 OPC DA COM/DCOM 环境仍须另行准备和验收。

## Windows CI

`.github/workflows/build.yml` 已定义迁移清单校验、Windows Release 构建、相同自动测试、原生 WinForms 启动、PNG 截图，以及 Windows x64 自包含 ZIP。新版烟测设计生成 **36 张 UI 截图**：

| 截图对象 | 数量 | 设计检查 |
|---|---:|---|
| 主窗体六个页面 | 6 | 顶部启停/保存按钮不重叠，页面工具栏可见，页面可布局和渲染 |
| 15 个驱动的设备编辑器 | 15 | 包含 12 个迁移驱动及 3 个原生入口；参数绑定、枚举/默认值和滚动后的字段可达性 |
| 15 个驱动的点位编辑器 | 15 | 方法、类型、字节序与写入能力绑定；各页签布局和操作按钮可达性 |
| 合计 | **36** | 每个编辑器保存一张截图；烟测另遍历其页签检查布局 |

烟测使用未启用的设备配置，不调用现场驱动连接。CI 为 36 张截图烟测设置 90 秒退出上限并检查退出码和主截图生成，随后上传 `verification` artifact 中的测试 TRX、烟测日志和 `ui-smoke*.png`；发布包上传为 `EdgeGateway-win-x64` artifact。对应运行见 [Actions](https://github.com/kingtentx/edge-gateway/actions)。

首轮 [Windows CI 39813962761](https://github.com/kingtentx/edge-gateway/actions/runs/39813962761) 通过迁移清单、干净构建和 93 项测试，发现并随后修复驱动选择器初始化问题。[第二轮](https://github.com/kingtentx/edge-gateway/actions/runs/39814849556)及[第三轮](https://github.com/kingtentx/edge-gateway/actions/runs/39816782821)再次通过 93 项测试，后续在原 Modbus 长参数页检查中发现滚动位置被旧输入焦点拉回。

已对照 .NET 8 WinForms 官方源码核实：布局期间容器会重新将活动控件滚入视图。最新烟测改为先聚焦待检查字段，禁用字段则将焦点移到固定底部按钮，再执行完整滚动、布局和原有全部边界断言；数值控件整体滚动，并保留内部文本框检查及失败现场截图。**这些最新改动仍须由对应新提交的 Windows CI 完整复验，当前不宣称 36 张截图验收已通过。**

干净构建的警告中，411 条为保留的源兼容 API 弃用提示（CS0618）：368 条来自 FOCAS 的 UnmanagedType.AsAny 声明，43 条来自 OPC UA 的同步/旧重载；另有 7 条未使用变量及 1 条无 await 的 async 方法提示。审查未发现这些警告指向类型不兼容或依赖漏洞告警，未使用全局抑制隐藏它们。本地增量构建的 0 警告输出不代表迁入源码干净编译无警告。单次 CI 环境下的渲染也不能替代人工交互、真实 COM/FOCAS 环境及多分辨率、多 DPI 验收。

## 尚需现场验证

- 真实 Modbus TCP PLC/仪表、实际点表与字节序；串口 RS485 电气连接、RTU 多种设备兼容性。
- 迁入的 S7、MC、FINS、EtherNet/IP、FANUC、OPC、称重及 MTConnect 驱动在对应真实型号、固件和服务器环境中的采集、质量与断线恢复；本地回环不代替现场联调。
- Windows 上实际 OPC DA COM/DCOM 安装注册、位数、权限与 STA 回调/释放；FANUC 官方 x64 FOCAS 原生运行库及其依赖、设备功能与授权；HslCommunication 有效授权。当前不能宣称这些依赖和通信已经验证成功。
- 用户 Broker 的认证、可信 TLS 证书与离线恢复；云端按 messageId 去重。
- Windows 11 / Windows Server 实际用户会话，1366×768 / 1920×1080 / 3200×2000 以及 100% / 150% / 200% DPI。
- 预期设备数、点位数和周期下 24–72 小时运行，磁盘满、强制断电与数据恢复。

RTU 的 CRC 和代码编译已验证，但未连接真实串口。`SerialPort.Open` 无原生可取消接口，极端驱动阻塞无法由托管取消令牌强制中止。迁入的同步厂商调用超时后隔离旧实例；未退出调用不能复用，也不能在其他线程强制释放，须等原线程返回后清理。隔离测试通过不代表已证明所有厂商库能及时退出。第三方插件也必须遵守其调用契约，不保证安全中断不合作插件。
