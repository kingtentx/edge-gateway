# 验证记录

> 当前功能与本次验证入口见 [单设备启停与配置自动保存](device-control-autosave.md)，最新运行包以 [README 最新交付](../README.md) 为准。
>
> 本页保留 2026-10-22 全部驱动迁移基线的历史验证。后续历史记录包括 [DPI 与界面升级](ui-dpi.md)、[设备行内配置](device-configuration.md) 和 [COM11 RTU 初始交付](modbus-rtu-com11.md)；这些既有结果不作为单设备启停与自动保存改动的验证结论。

验证日期：2026-10-22。目标框架：.NET 8，Windows Forms。

**本轮完整 Windows 验证成功。** 对应程序源码提交为 [`0a150056e8e6417392fc1438dea9466f8b2276d2`](https://github.com/kingtentx/edge-gateway/commit/0a150056e8e6417392fc1438dea9466f8b2276d2)，[Windows CI 39818478291](https://github.com/kingtentx/edge-gateway/actions/runs/39818478291) 的构建、测试、原生界面烟测、发布与上传步骤均为成功。本页后续补录验证结果，不改变该运行包的程序源码。

## 最终执行结果

| 项目 | 实际结果 |
|---|---|
| 源驱动迁移清单 | 通过：12 个原工程、65 个原读取方法、74 个原配置参数、176 个来源文件 |
| Windows 干净 Release 构建 | 成功：0 错误、419 个保留源码警告，分类见下文 |
| Linux 全量自动测试 | 93 执行、93 通过、0 失败、0 跳过 |
| Windows 全量自动测试 | 93 执行、93 通过、0 失败、0 跳过；包含 Windows STA 断言 |
| 原生 WinForms 启动与布局 | 成功；6 个主页面、15 个设备编辑器、15 个点位编辑器，共 36 张 PNG |
| 驱动编辑器元数据检查 | 15 个驱动入口、77 个实际参数、65 个读取方法；原 74 个参数全部保留，另有 3 个运行配置扩展 |
| 界面错误输出 | `ui-smoke.stderr.log` 为 0 字节 |
| Windows x64 自包含包 | 发布成功；下载后核对 30 个关键文件、全部 12 个迁移驱动程序集与原生驱动、运行时和文档均存在 |
| 发布压缩包 | 572 个 ZIP 条目，全部条目的 CRC 检查通过；内含开发 skill 的 5 个文件 |
| 开发 skill | 格式校验通过，并随 Git 提交及运行包交付 |

Linux 环境为 Ubuntu 24.04、SDK 8.0.426 / Runtime 8.0.32。WinForms 使用 Windows targeting pack 交叉编译；另已成功执行 `win-x64` 自包含交叉发布。此前增量构建报告的 0 警告不代表迁入源码干净编译无警告，以本表 Windows 干净构建结果为准。

迁移基准为上游提交 `8de442a7dec52d2993d1e9667d799846936d681b`，来源文件及哈希见 [迁移清单](driver-migration-manifest.json)。校验脚本检查来源文件存在且非空、原方法及参数保留、目标框架为 `net8.0`、所有工程纳入解决方案。来源 SHA256 用于追溯，不要求修改后的迁移文件仍与原文件逐字节相同。

## 测试覆盖

| 最新测试分类 | 通过数 | 覆盖内容 |
|---|---:|---|
| 原生驱动 | 27 | Modbus TCP 真实回环、编解码、CRC 和模拟驱动回归 |
| 运行时、配置与存储 | 34 | 在原 20 项上增加 v1→v2 内存迁移与备份、未知版本拒绝、64 位整数与文本读写、空白/空字符串保留、数组形状、结构值落盘、Bit 逆换算和整数浮点舍入边界 |
| MQTT/TLS 回环 | 3 | QoS1 确认、超时与证书拒绝测试 |
| 迁移工厂与元数据 | 7 | 12 个工厂及全部原方法/参数、方法读写能力、26 种类型与 5 种显式字节序映射、结构值与空值、未启用示例配置、源/原生串口设备统一独占校验 |
| 同步驱动适配生命周期 | 7 | 构造/读取/异步写入/清理的线程一致性、空值与异常质量、连接失败清理、取消和超时隔离、同设备重连限制、迟到回调与严格参数转换 |
| 原 Modbus 实际 TCP 回环 | 7 | 真实寄存器读取与 `Read` 兼容入口、批量位缓存及 Bool/Bit 顺序读取、UInt64 四种字节序写入与回显 |
| 原流式协议实际 TCP 回环 | 2 | Mock 拆帧、有符号字节及拒绝第二次旧值；Toledo 连续帧重组与停止来帧后的超时 |
| 原 MTConnect 实际 HTTP 回环 | 6 | Agent 探测及指定数据项读取；非 Agent 内容、损坏 XML、HTTP 错误、缺失数据项与 `UNAVAILABLE` 不得报告成功 |
| 合计 | **93** | 原 50 项基线加 43 项新增用例 |


原有 50 项测试全部保留，本轮新增 43 项。协议测试使用真实本机 TCP / HTTP 端点；MQTT 测试检查 QoS1 发布在 PUBACK 前不完成、无确认超时后断开，并实际进行 TLS 握手以拒绝不受信任证书。自动测试中的模拟服务端不等同于现场设备联调。

MTConnect 中间验证曾发现缺失数据项被旧客户端错误映射到第一个返回值，已修复为严格检查 DataItemID；最新 Linux 和 Windows 全量测试均通过。

同步适配测试覆盖同线程构造/调用/清理、异步继续执行、超时隔离及同设备重连限制。Windows 测试验证 STA 线程约束；没有连接真实 OPC DA COM 服务器或 FANUC 原生库。

## 界面验证与下载

完整烟测检查主页面工具栏与最小宽度命令按钮，遍历全部驱动的设备和点位编辑器、参数枚举、默认值、方法、数据类型、写入能力及各页签字段可达性。每个公开输入控件按真实焦点访问路径滚动后检查自身与全部祖先边界；NumericUpDown 的内部文本框也单独检查边界。禁用字段仍检查可见性，没有放宽断言。

本轮发现并修正了驱动下拉框初始化时机，以及烟测布局期间旧输入焦点引起的滚动回退。失败时保留现场截图；最终运行未产生失败截图，36 张正常截图全部生成。已查看主页面与驱动编辑器，包含原 Modbus 长参数页和固定底部命令按钮。

烟测使用未启用的设备配置，不调用现场驱动连接。36 张截图烟测的退出上限为 90 秒，实际本轮界面检查约 30 秒。单次 CI 环境下的渲染不能替代多分辨率、多 DPI 和人工交互验收。

- [Windows x64 运行包](https://github.com/kingtentx/edge-gateway/actions/runs/39818478291/artifacts/12443481330)：下载的 artifact 包内含 `EdgeGateway-win-x64.zip`，完整解压后运行 `EdgeGateway.WinForms.exe`。
- [验证证据](https://github.com/kingtentx/edge-gateway/actions/runs/39818478291/artifacts/12442174799)：Windows `tests.trx`、36 张 PNG 及烟测标准输出/错误日志。
- [完整构建日志及步骤](https://github.com/kingtentx/edge-gateway/actions/runs/39818478291)。

实际应用 ZIP（artifact 中的 `EdgeGateway-win-x64.zip`）SHA256：

```text
7a8490828aa91cef98264aa4503fde0cd2caa102c737244c823e9506fcff412d
```

运行包中的验证文档记录打包提交时的状态；最终结果以本页及对应 CI 运行记录为准。Actions artifact 按仓库保留策略存储，亦可使用下列脚本重新发布。

## 复核命令

```powershell
python scripts/verify-driver-migration.py
dotnet restore EdgeGateway.sln
dotnet build EdgeGateway.sln -c Release --no-restore
dotnet test tests/EdgeGateway.Tests/EdgeGateway.Tests.csproj -c Release --no-build --logger "trx;LogFileName=tests.trx"
# 以下在 Windows 桌面会话执行
dotnet run --project src/EdgeGateway.WinForms -c Release --no-build -- --smoke-test --screenshot artifacts/ui-smoke.png
powershell -ExecutionPolicy Bypass -File scripts/publish-win-x64.ps1
```

## 保留源码的编译警告

Windows 干净构建的 419 个警告未被全局屏蔽：

| 类别 | 数量 | 来源 |
|---|---:|---|
| CS0618 | 368 | FANUC FOCAS 的 `UnmanagedType.AsAny` 原生声明 |
| CS0618 | 43 | OPC UA 保留的同步、Begin/End、旧 Session 与 telemetry 重载 |
| CS0168 / CS0219 | 7 | 未使用的异常或局部变量 |
| CS1998 | 1 | Hsl 驱动明确返回“不支持写入”的方法没有 await |

审查未发现这些警告指向本次迁移的类型或框架不兼容。FOCAS 封送声明涉及厂商 ABI，未为了消除警告而无实机依据地统一改写；保留 API 在 .NET 8 下编译通过不证明全部设备调用已完成现场验收。

## NuGet 依赖审计

本轮审计使用官方 NuGet 源，范围为 WinForms 宿主及其直接、传递 NuGet 包；退出码为 0，报告未列出已知漏洞条目：

```text
dotnet list src/EdgeGateway.WinForms/EdgeGateway.WinForms.csproj package --vulnerable --include-transitive --source https://api.nuget.org/v3/index.json --format json
```

此结果按当时的漏洞数据与报告范围记录，不扩大为迁入源码、COM 组件或厂商原生库的安全审计，也不代表完成商业组件运行授权验证。

## 尚需现场验证

- 真实 PLC、串口 RS485、CNC、OPC、称重及 MTConnect 设备/服务器的型号、固件、点表、字节序、采集质量与断线恢复。
- OPC DA 的 COM/DCOM 注册、位数、权限与回调/释放；FANUC 官方 Windows x64 FOCAS 原生库及设备功能；HslCommunication 有效授权。
- 用户 MQTT Broker 的认证、可信 TLS 证书、离线恢复与云端 messageId 去重。
- Windows 11 / Windows Server 实际用户会话，1366×768 / 1920×1080 / 3200×2000，100% / 150% / 200% DPI。
- 预期设备数、点位数和周期下 24–72 小时持续运行，磁盘满、强制断电与数据恢复。

`SerialPort.Open` 和某些同步厂商调用不能由托管取消令牌强制中断。迁入同步调用超时后隔离旧实例，保留同设备租约，待原调用返回后在原线程清理；不会在其他线程强制释放。隔离测试通过不代表所有厂商库都能及时退出。
