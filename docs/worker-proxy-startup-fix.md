# 后台代理启动空引用修复

日期：2026-10-26。

## 原因与修改

启动时抛出 `System.NullReferenceException`，堆栈指向 `GatewayWorkerClient.Invoke` 第 54 行。`DispatchProxy` 实际生成的是代理子类；在 `GetType()` 上用 `BindingFlags.Instance | BindingFlags.NonPublic` 查找基类私有方法，会返回 null。首次调用返回 `Task<T>` 的应用用例时，后续 `MakeGenericMethod` 解引用失败；`ValueTask<T>` 分支也有相同问题。

两个分支均改为在声明私有方法的 `typeof(GatewayWorkerClient)` 上查找。应用契约、IPC 报文、运行时和配置结构不变，后台继续持有采集资源。

## 定向验证

用户确认范围：Debug/Release 构建、5 项代理 IPC 测试和架构检查；同范围最多两轮修复重跑。构建前通过当前用户命名管道确认现有 Debug 后台 `IsRunning=false`，随后发送正常退出命令，等待资源释放。未强制结束采集进程。

测试链接实际 `GatewayWorkerClient.cs`，使用真正生成的代理和本地命名管道，覆盖 `Task<T>` 返回、`ValueTask<T>` 返回、后台错误传递、提交前取消、子目录代理及普通 Task 调用。测试目录与当前用户配置隔离，不启动现场驱动，不读写运行数据库。

首轮结果：

- Debug 构建成功，0 错误、1 条修改前已存在的空引用分析警告。
- Release 构建成功，0 错误、420 条警告，其中 419 条来自迁移驱动，另 1 条为上述既有警告。
- 5 项 `GatewayWorkerClientTests` 全部通过。
- 架构检查通过：8 层边界、24 个无环工程，UI/Application 隔离及 MQTT 依赖归属通过。

原始日志和 TRX 位于 `artifacts/worker-proxy-startup-20261026/`。未运行全量回归或原生界面烟测；定向 IPC 检查不替代完整 Windows 启动与托盘生命周期验收。本次未修改配置库或连接现场 PLC。
