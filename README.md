# SQL Server 2025 → 2019 数据迁移工具

这是一个独立的 Windows WPF 程序，用于把较高版本 SQL Server 中的**静态备份还原库**，按逻辑方式迁移到同一台机器上的 SQL Server 2019 Enterprise 空数据库。

高版本数据库备份不能直接还原到低版本实例。本工具采用“生成 SQL Server 2019 架构脚本 + 流式批量复制 + 逐表校验”，不会尝试降级还原备份。

## 使用前提

- Windows 服务器，安装可访问的源 SQL Server 实例和 SQL Server 2019 目标实例。
- 源数据库来自备份还原，并在整个迁移期间停止业务写入；界面要求操作员显式确认。
- 目标数据库需要提前创建，必须是 SQL Server 2019（15.x）上的全新空库；建议兼容级别 150。
- 登录账号需能读取源库全部数据，并具有目标库建表、建视图、建过程、建函数、ALTER 和 INSERT 权限。
- 开始前仍应保留原始备份。工具不会删除、清空或覆盖已有目标对象。

## 使用步骤

1. 在 SQL Server 2025（或其他较高版本）实例上还原源备份，并停止对该还原库的写入。
2. 在 SQL Server 2019 Enterprise 上新建一个空目标数据库。
3. 启动程序，填写同一台机器的主机名、源/目标实例名和数据库名。
4. 选择 Windows 认证或 SQL Server 认证；密码仅在当前操作内存中使用，不写入日志或运行收据。
5. 勾选“源数据库为静态备份还原库”，依次执行“测试连接”和“执行预检”。
6. 选择性能模式后开始迁移。运行中可以切换模式；当前批次不强制中断，从后续批次/调度边界生效。

## 性能模式

| 模式 | 同时复制表数 | 每批行数 | CPU 暂停/恢复 | 批间延迟 |
|---|---:|---:|---:|---:|
| 高速 | 最多 2 | 50,000 | 92% / 82% | 0 ms |
| 平衡 | 1 | 25,000 | 82% / 72% | 50 ms |
| 低影响（默认） | 1 | 10,000 | 72% / 62% | 250 ms |

CPU 阈值采用连续 3 次采样和滞回控制；CPU 监控不可用时会显式降级为低影响模式。当前服务器已有约 50% CPU 占用时，建议从低影响模式开始。

模式模型中预留了索引 `MAXDOP` 值，但当前版本不会自动改写 SMO 生成的索引 DDL，因此不会修改实例或数据库级 MAXDOP 配置；运行日志会明确提示这一限制。

## 20GB 预计耗时

同机迁移不经过公网，200 Mbps 公网带宽不参与数据库复制。实际时间主要取决于本机磁盘、表数量、大字段、索引/外键创建和当前 SQL Server 负载。可先按以下范围预估：

- 低影响：约 1～3 小时。
- 平衡：约 30～120 分钟。
- 高速：约 20～90 分钟。

这是容量规划区间，不是保证时间；预检和实际前几个批次显示的行速率、CPU 与剩余时间更有参考价值。

## 安全边界

- 只接受 SQL Server 2019 目标实例和空目标库。
- 禁止源端与目标端为同一实例的同一数据库。
- 不生成或执行 `DROP`、`TRUNCATE`、`CREATE DATABASE`、`ALTER DATABASE`，不修改实例配置。
- 架构脚本先保存到运行目录，再执行 `PreData → 数据 → PostData → Programmability → 校验`。
- 数据通过 `SqlDataReader` + `SqlBulkCopy` 流式分批处理，不在内存中缓存整表。
- 每批使用内部事务；取消不会把已提交批次伪装成未发生，也不会自动清理或重试未知结果。
- 校验使用逐表 `COUNT_BIG` 和可选快速 checksum；checksum 可能碰撞，不是密码学严格哈希。
- RLS、动态数据掩码、Ledger、原生 vector/json、Always Encrypted、内存优化表、时态表、图表、FileTable、外部表、分区和全文索引等会在预检中阻断或明确列为超范围。

数据库登录、用户、角色、权限、SQL Agent 作业、链接服务器、凭据、证书、备份策略等服务器或安全对象不在迁移范围内，需要单独迁移和复核。

复杂函数/视图/存储过程之间的依赖，以及模块正文中的 SQL Server 2022/2025 专属语法，SMO 不会自动改写成 2019 语法。当前版本会保留预览脚本并在执行失败时停止，不会把失败标为成功；正式迁移前应人工审阅预览脚本。

## 运行收据与恢复

每次任务会在 `%LocalAppData%\SqlServerMigration\runs\<时间-任务ID>` 保存：

- `schema-preview.sql`：面向 SQL Server 2019 的架构预览。
- `checkpoints\<任务ID>.json`：阶段、整表完成状态和失败信息。

当前 JSON 是**运行收据/故障诊断**，不是断点续传。单表中途失败后，程序不会猜测最后一行或自动清理目标表；再次执行应使用新的空目标数据库，或先由 DBA 根据收据制定人工恢复方案。

## 构建与验证

要求 .NET 10 SDK：

```powershell
dotnet restore SqlServerMigration.sln
dotnet build SqlServerMigration.sln -c Release --no-restore
dotnet run --project tests/SqlServerMigration.Verification -c Release --no-build
```

启动应用：

```powershell
dotnet run --project src/SqlServerMigration.App -c Release
```

验证程序不连接 SQL Server，覆盖性能档位、动态切换、CPU 暂停/恢复与降级、取消、Windows CPU 实采、凭据脱敏、计划指纹、校验告警语义、WPF 主窗口构造和界面模式切换。真实 SQL Server 迁移必须在隔离实例上另行验收。
