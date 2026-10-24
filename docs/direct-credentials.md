# 密码与 Token 直接保存

更新日期：2026-10-25。按当前开发要求，涉及凭据的配置直接保存字符串，移除凭据环境变量配置与运行时解析。

| 配置位置 | 当前字段 | 行为 |
|---|---|---|
| 数据库连接 | `DatabaseConnectionConfiguration.Password` | 直接用于 MySQL / PostgreSQL / Oracle 的结构化连接串构造器 |
| 平台与系统：密码 | `MqttConfiguration.Password` | 直接作为 MQTT 密码发送 |
| 平台与系统：平台 Token | `MqttConfiguration.AccessToken` | 非空时作为 MQTT 用户名，优先于普通 `Username` |
| Hsl 驱动参数 | `DeviceConfiguration.Properties["AuthorizationCode"]` | 直接交给厂商授权验证 API，仍须有效授权 |

这些字段默认留空，输入框掩码显示。密码和 Token 不执行 Trim，保留前后空格、分号与引号；空密码可以保存，连接是否接受由实际服务端决定。数据库连接串继续使用各厂商构造器，不将密码拼接为 SQL 或连接串片段。TLS 证书验证不变。

SQLite 中的配置快照以及手工导出的 JSON 都包含凭据原值。导入、复制草稿、保存后重开均保留原值。运行日志、通信诊断和 RPC 错误继续对密码与 Token 脱敏；仓库示例只留空白凭据，自动测试使用明确的虚构值。

原 `PasswordEnvironmentVariable`、`UsernameEnvironmentVariable` 和 Hsl `AuthorizationCodeEnvironmentVariable` 已移除，不读取环境变量，也不把原变量名称自动当作密码。仍使用 schema 3 的最新字段；旧凭据字段需要在对应输入框重新填写。开发阶段不添加旧格式自动迁移。数据库配置仍要求全部设备停止，平台应用仍使用完整预览、确认和原子提交。

RPC 去重身份继续使用稳定的平台、Broker、网关、客户端和账号身份。平台 Token 的轮换不创建新去重域；切换租户或实际账号时同步更换网关/客户端/账号标识。密码或 Token 改变会使 MQTT 连接重新认证，保持已有未确认消息及写指令去重记录。

## 验证

- SQLite 保存、重开及配置导入导出检查实际凭据原值，并检查新导出不存在环境变量凭据字段。
- 三种数据库连接串核对包含空格、引号和分号的密码，以及 TLS 设置；Provider 错误和后台写入错误保持脱敏。
- 真实本机 TCP MQTT 回环检查 CONNECT 报文中的用户名、Token 和密码，并验证修改凭据后重新连接。
- RPC 覆盖 Token 轮换保留去重结果、不同网关/账号分域；上报故障检查密码和 Token 不出现在状态与日志。
- Windows 导航回归检查数据库与平台密码框掩码、直接字符串提交、空密码和取消草稿隔离；原完整布局断言继续保留。

本机执行结果如下，基线为 `main` 的 `f1452a1`。行为测试不代表现场数据库、PLC 或厂商许可已验收。

| 检查 | 结果与证据 |
|---|---|
| Release 构建 | 解决方案构建成功；首次完整编译有原迁移驱动的 419 个警告、0 错误；最终增量构建 0 警告、0 错误。独立数据库集成验证工程编译成功 |
| 架构与驱动 | 8 个层边界、24 个工程无环；12 个迁移驱动、65 个原方法、74 个原参数及 176 个来源文件检查通过 |
| 自动测试 | 427 / 427 通过，0 失败、0 跳过；`artifacts/direct-credentials-20261025/verified-core-tests.trx` |
| Windows 界面 | 完整脚本退出码 0；8 页、7 类弹窗、20 / 20 等效工作区、149 张 PNG 与 15 个完成标记通过，包含密码输入与失败保存草稿保留 |
| 实际显示环境 | `DeviceDpi` 与原生窗口 DPI 均为 192；20 个组合仅模拟可用空间，不代表真实跨 DPI / 多显示器验收 |
| 原始界面证据 | `artifacts/direct-credentials-20261025/ui/ui-smoke-verification.json`、stdout/stderr、`ui-smoke-layout-matrix/layout-matrix.json` 与 PNG；凭据截图使用虚构测试值并掩码显示 |
| 开发中失败 | 首轮 RPC 新场景暴露测试主题硬编码 `edge-001`，已按实际网关 ID 展开；随后元数据测试错误假设现有驱动提供密码参数，已改为核对实际 Hsl 授权码参数。对应失败 TRX 保留于同一目录的 `core-tests.trx` / `final-core-tests.trx`，最终完整回归全部通过 |
| 未执行 | 真实 MySQL/PostgreSQL/Oracle 连接与现场采集、Hsl 厂商授权现场验证、跨 DPI / 多屏、Windows Server 双系统 CI 和发布打包 |

界面矩阵执行后还清理了空密码框的提示文字，最终构建再次通过；交互和布局断言保持不变。原有环境变量凭据规则已同步从 AGENTS、Skill、当前说明及示例中移除。数据库集成验证程序的 CI 输入仍可来自测试环境，进入连接配置后使用的是实际 `Password` 字符串，不是应用的环境变量配置。
