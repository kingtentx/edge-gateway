# 程序运行目录与持久化约定

正式运行目录以 EdgeGateway.WinForms.exe 所在目录为根目录。程序默认不再把运行数据写入 LocalApplicationData。

    EdgeGateway.WinForms/
    ├── EdgeGateway.WinForms.exe
    ├── AppData/
    │   └── gateway.config.db       # SQLite：当前/上一版配置、驱动目录清单
    ├── drivers/
    │   ├── CNC.Fanuc.dll           # 已迁移的内置协议驱动程序集
    │   ├── PLC.SiemensS7.dll
    │   ├── EdgeGateway.Driver.Modbus.dll
    │   └── installed/
    │       └── <驱动ID哈希>/
    │           ├── revisions/<版本>-<修订ID>/<程序集>.dll
    │           └── driver-plugin.json # 激活修订、身份及哈希；卸载保留停用标记
    ├── logs/                       # 程序运行日志 yyyyMMdd.log
    │   └── data/                   # 点位采样日志 yyyyMMdd.log
    ├── messaging/
    │   ├── outbox/                 # MQTT 持久化待发送消息
    │   └── rpc/                    # RPC 消息状态
    └── gateway.lock                # 单实例锁

## SQLite 的职责

2026-10-26 起，数据库结构版本为 4，按功能存储 17 张强类型关系表，配置模型/JSON 交换仍为 schema 3。移除整包 JSON 配置表，驱动登记与可用目录合并至 `driver_registry`；旧数据库明确拒绝且不自动清空或迁移。详见 [SQLite 配置结构](sqlite-configuration-schema.md)。

gateway.config.db 保存网关配置快照（设备、点位、通信参数、日志策略、外部数据库连接及写入映射）、上一版本配置、已安装插件元数据和本次启动发现的完整驱动目录清单。数据库不保存采集历史数据，也不把 DLL 二进制写入 SQLite。

drivers/ 中的 DLL 是可执行代码文件；SQLite 保存驱动 ID、名称、版本、程序集名、SHA-256、来源及状态。上传安装插件时先把 DLL 安装到版本化目录，校验通过后才登记。启动时重新校验文件哈希，并刷新 SQLite 中的可用驱动目录。driver-plugin.json 决定当前激活修订或停用状态；SQLite 提供驱动目录索引，重启时根据清单重新校验并同步。安装、更新、卸载的失败处理见 [插件管理](driver-plugin-management.md)。

配置、插件登记和目录刷新由同一配置存储串行使用 SQLite 连接。刷新只提交或回滚本次事务，连接保留到宿主退出时释放。2026-10-25 修正了目录刷新提前释放连接、导致后续保存报 `BeginTransaction can only be called when the connection is open` 的问题；当前/上一版配置与清单回滚继续通过真实 SQLite 测试核对。

## 初始化与权限

- 首次启动创建 AppData、drivers、logs、logs/data、messaging/outbox、messaging/rpc，读取驱动及停用清单后，仅为可用驱动生成默认设备，完整校验后保存到 SQLite。
- 正式部署应将整个程序目录放在网关服务账户可写的专用目录（例如 C:\\EdgeGateway），不建议安装到受保护的 Program Files 后仍以普通用户运行。
- AppData 与 drivers 是明确指定的子目录；日志、消息队列与进程锁位于程序根目录。用户通过文件对话框导出的文件保存在用户指定的位置。
- 本项目不导入旧 SQLite 历史数据，也不自动搬迁之前 LocalApplicationData 中的旧配置；新部署按当前目录初始化，避免意外覆盖或误用旧配置。
- WinForms 的自动化烟测使用临时隔离根目录，并在结束后清理，不污染实际程序目录。

## 日志分目录（2026-10-25）

程序事件写入程序根目录 `logs/yyyyMMdd.log`；点位采样写入 `logs/data/yyyyMMdd.log`（2026-10-26 起移除文件名的 gateway- 前缀，按采集服务器当地写入日期）。大小轮转使用 `yyyyMMdd.001.log` 等未占用序号。两类日志各有独立文件流、大小轮转及保留数量/天数，继续共用有界后台队列和磁盘保护。刷新与退出会落盘两个文件流。文件查询勾选包含采样时同时扫描两个目录，文件名显示相对路径以区分来源；不包含采样时不扫描 data。已有日志保持原位，仍可查询及按保留策略清理，不回写历史内容。

日志设置的路径展示及“打开日志目录”使用应用返回的写入器实际目录，不再由 AppData 推导。此前 Debug 输出目录的 `AppData/logs` 是旧按钮误创建的空目录，本次已删除。

经用户确认，本次 WinForms Release 编译通过（419 条现有迁移驱动警告、0 错误）；19 项日志定向测试通过，覆盖分目录写入与独立保留、采样目录故障隔离、队列与磁盘保护、查询和导出。证据：`artifacts/log-directories-20261025/build.log`、`tests.log`、`log-directories.trx`。未执行全量测试或窗口验收。
