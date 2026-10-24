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
    │       └── <程序集>/<版本>/
    │           ├── <程序集>.dll    # 上传安装的驱动
    │           └── driver-plugin.json # 文件哈希校验侧车
    ├── logs/                       # 运行日志
    ├── messaging/
    │   ├── outbox/                 # MQTT 持久化待发送消息
    │   └── rpc/                    # RPC 消息状态
    └── gateway.lock                # 单实例锁

## SQLite 的职责

gateway.config.db 保存网关配置快照（设备、点位、通信参数、日志策略、外部数据库连接及写入映射）、上一版本配置、已安装插件元数据和本次启动发现的完整驱动目录清单。数据库不保存采集历史数据，也不把 DLL 二进制写入 SQLite。

drivers/ 中的 DLL 是可执行代码文件；SQLite 保存驱动 ID、名称、版本、程序集名、SHA-256、来源及状态。上传安装插件时先把 DLL 安装到版本化目录，校验通过后才登记。启动时重新校验文件哈希，并刷新 SQLite 中的可用驱动目录。driver-plugin.json 只用于将文件与其哈希/驱动身份关联，不取代 SQLite 目录索引。

## 初始化与权限

- 首次启动创建 AppData、drivers、logs、messaging/outbox、messaging/rpc，并将默认配置立即保存到 SQLite。
- 正式部署应将整个程序目录放在网关服务账户可写的专用目录（例如 C:\\EdgeGateway），不建议安装到受保护的 Program Files 后仍以普通用户运行。
- AppData 与 drivers 是明确指定的子目录；日志、消息队列与进程锁位于程序根目录。用户通过文件对话框导出的文件保存在用户指定的位置。
- 本项目不导入旧 SQLite 历史数据，也不自动搬迁之前 LocalApplicationData 中的旧配置；新部署按当前目录初始化，避免意外覆盖或误用旧配置。
- WinForms 的自动化烟测使用临时隔离根目录，并在结束后清理，不污染实际程序目录。
