# 数据库写入组选点与 PostgreSQL 类型错误排查

2026-10-31。

## 点位选择

“点位选择”页使用左右结构。左侧为 AntdUI Tree 的分组/设备树，上方按分组筛选；点击全部设备、分组或设备，在右侧显示对应点位。右侧按点位名称、地址搜索，也支持设备名称；每行使用官方 Checkbox，“全选当前列表”只改变当前显示点位，支持半选和空结果禁用。

勾选以点位 ID 保存；切换树节点、分组或搜索不移除隐藏点位的勾选。底部显示当前数量和全部已勾选数量。“全部启用点位”也改用复选框，保持空 TagIds 代表包含后续新增启用点位的原契约；此时显示实际启用项并禁用手动勾选，取消该模式恢复手动草稿。取消对话框不修改来源配置，保存继续通过应用层提交。

固定树、下拉、搜索、全选及列表容器在 Designer 创建，树节点和可见点位行由数据生成。列表沿用官方滚动容器和虚拟行，批量更换条目只渲染一次。新增 `DatabaseSelection` 隔离后台检查入口，覆盖分组/设备、搜索、跨范围保持、鼠标/键盘、全选、自动包含、滚动与宽窄往返。

## 本机 PostgreSQL 排查

只读检查现有运行日志、配置中的写入模式/字段映射、持久缓冲值类型以及目标表的 information_schema；没有修改现有配置、表或缓冲，也没有发起写入重试。

- 23:14–23:15 的日志重复出现 SQLSTATE `42804`。PostgreSQL 将其定义为 `datatype_mismatch`，见[官方错误码](https://www.postgresql.org/docs/16/errcodes-appendix.html)。
- 当前目标表为标准 `gateway_point` 结构：身份、名称、地址、原值、工程值文本和质量为字符列，`timestamp` 为 `timestamp with time zone`。
- 当前写入组却使用 `PointMapping`，全部 10 个字段均为 `EngineeringValue / Auto`。现有失败缓冲中的记录也显示全部字段为 `ushort`，包含 `timestamp`；这是数值采样被映射到时间列的直接证据。
- 23:17:01 又出现持久缓冲目标配置变化警告：为避免旧数据写入新目标而暂停补传。它与最初类型不匹配是两个不同阶段的问题。

对于该标准表，应使用“标准点行”模式。若确实需要自定义逐点模式，必须逐列绑定：event_id→事件标识，device_id/name→设备标识/名称，tag_id/name→点位标识/名称，address→地址，raw_value→原始值，value_text→工程值，timestamp→采样时间，quality→质量；字符目标选择文本类型，timestamp 选择时间类型。标准点行模式同时提供标准历史查询与 event_id 去重契约，不能把自定义逐点模式当作完全等价替代。

切换模式不会自动修复已经生成的错误缓冲。保留这些数据，后续需单独决定导出/核对及处理方式；不能清空缓冲或绕过目标指纹检查来宣称恢复。此轮尚未修改用户写入配置，也未验证恢复入库。

## 验证状态

用户已授权 Release 编译与隔离后台选点定向检查，并允许同范围修复重跑。

- 首轮编译发现 Tree 事件参数应读取 `Item` 而非 `Value`，修复后编译通过；最终 `artifacts/database-selection-build-3.log` 为 0 错误、1 条既有 GatewayWorkerClient 空引用警告。产物位于独立 `artifacts/database-selection-runtime`，未替换运行中的程序。
- 首轮 UI 检查发现搜索框没有统一 InputFrame，已为分组筛选和搜索框补齐 Designer 中的布局容器，保留原边界断言。失败日志保留于 `artifacts/database-selection-ui`。
- 复验 `DatabaseSelection` 通过，进程退出 0、stderr 为空；证据位于 `artifacts/database-selection-ui-2`。覆盖设备及分组节点、分组筛选、名称/地址搜索、跨范围勾选保持、全选/取消/半选、空结果、自动包含与手动草稿恢复、确认时 TagIds 输出及取消源对象隔离、官方复选框鼠标/Space、列表上下键及滚动同步。
- 实际 `DeviceDpi=192`，检查 1000×650→640×480→1000×650 逻辑客户区往返；人工查看 wide/narrow 截图。`ProcessWindowVerified=true`，`InputDesktopSwitch=false`，未占用用户桌面。
- 没有执行全量测试、架构脚本、真实设备、数据库写入、跨显示器 DPI 或设计器往返验收。选点检查验证表单候选结果，未扩大到数据库应用层持久化回归。只读故障排查不是恢复入库验收。
