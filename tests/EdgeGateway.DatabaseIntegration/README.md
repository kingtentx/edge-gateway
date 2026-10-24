# 外部数据库真实集成验证

这是独立执行程序，不混入无需外部服务的 xUnit 测试。缺少数据库、密码环境变量或建表授权时直接失败，不记为通过或跳过。仅使用专用测试库；程序建立带随机 `eg_it_` 前缀的表，验证结束后删除这些表。

当前实现最低 SQL 基线为 MySQL 8.0.19+、PostgreSQL 12+；推荐使用仍受厂家支持的版本。Oracle 使用托管 ODP.NET，默认单元测试验证绑定、SQL 和错误分支；本执行程序不把未连接 Oracle 的测试声称为 Oracle 现场验收。

先用本地实例或 CI service 创建专用数据库和测试账户，设置：

```bash
export EDGE_DB_TEST_ALLOW_DDL=1
export EDGE_DB_TEST_HOST=127.0.0.1
export EDGE_DB_TEST_PORT=5432
export EDGE_DB_TEST_DATABASE=edge_test
export EDGE_DB_TEST_USERNAME=edge_test
# 将测试账户密码放入 EDGE_DB_TEST_PASSWORD；不要提交真实凭据。
dotnet run --project tests/EdgeGateway.DatabaseIntegration -c Release -- postgres

# 修改主机/端口/账户后验证 MySQL。
dotnet run --project tests/EdgeGateway.DatabaseIntegration -c Release -- mysql
```

MySQL 测试账户需具有专用库的建表、索引、修改和删除表权限。验证不会修改服务器全局设置。若要覆盖数据库默认宽松模式的边界，可在启动一次性 MySQL 实例时添加 `--sql-mode=''`；Connector 必须仍拒绝过长文本，而不是让数据库静默截断。

覆盖内容包括：显式建表、普通写入不建表、元数据、中文字段、参数化值、UTC 微秒时间、64 位数及高精度 decimal、JSON 类型还原、标准点表重放幂等、自定义业务表跨批事务回滚、锁等待取消、筛选与查询上限、MySQL 新增业务唯一键与短期元数据缓存并存的故障边界。

输出 JSON 含检查项、服务器版本与 200 行批次的本机写入耗时。该计时属于回环功能烟测，不能替代现场网络、多网关并发、断网恢复与长稳容量验证。
