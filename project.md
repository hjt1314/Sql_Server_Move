# SQL Server 数据库迁移工具

## 项目基础信息

- 项目目标：规划并实现一个用于将较高版本 SQL Server 数据库的数据迁移到 SQL Server 2019 的工具。
- 项目位置：`C:\Users\EDY\Documents\ChatGPT\数据库迁移`
- 技术栈：.NET 10、WPF、Microsoft.Data.SqlClient、SQL Server Management Objects（SMO）。
- 源端与目标端：同一台 Windows 服务器上的较高版本 SQL Server 静态备份还原库 → SQL Server 2019 Enterprise 全新空数据库；当前规划数据量约 20GB。

## 结构概览

- `src/SqlServerMigration.App`：WPF 主程序、连接/预检、SQL Server 2019 架构脚本、流式数据复制、CPU 节流、运行收据和逐表验证。
- `tests/SqlServerMigration.Verification`：不连接数据库的行为与 WPF 构造验收程序。
- `artifacts/SqlServerMigration-win-x64`：本地生成、Git 忽略的 Windows x64 自包含交付包。

## 当前状态

- 阶段：独立迁移工具首版已实现并完成本地无数据库验收，等待真实隔离 SQL Server 2025/2019 环境验收。
- Git 状态：远程仓库 `hjt1314/Sql_Server_Move`，开发分支 `feature/sql-server-migration-tool`；Issue #1 和 Pull Request #2 已建立，等待审阅/合并。
- 修改授权：用户已明确确认创建独立项目，并确认加入可自由切换的性能模式。

## 历史变更记录（仅追加）

### 2026-10-23

- 初始化 `project.md`，记录项目目标、当前目录状态与待确认事项。
- 本次未创建程序代码，未执行数据库连接、迁移、部署或数据写入。

### 2026-10-23 11:38 +08:00

- 修改原因：实现一个独立 Windows 程序，将同机较高版本 SQL Server 的静态备份还原库逻辑迁移到 SQL Server 2019 Enterprise，并支持运行中切换高速、平衡、低影响三档模式。
- 关联工作：GitHub Issue `#1`（`https://github.com/hjt1314/Sql_Server_Move/issues/1`）；分支 `feature/sql-server-migration-tool`。
- 修改文件：
  - 根目录：`.gitignore`、`SqlServerMigration.sln`、`README.md`、`project.md`。
  - 应用入口与界面：`src/SqlServerMigration.App/App.xaml`、`App.xaml.cs`、`AssemblyInfo.cs`、`MainWindow.xaml`、`MainWindow.xaml.cs`、`ViewModels/MainViewModel.cs`。
  - 命令基础设施：`Infrastructure/RelayCommand.cs`、`Infrastructure/AsyncRelayCommand.cs`。
  - 模型：`DatabaseEndpointInfo.cs`、`MigrationCheckpoint.cs`、`MigrationPerformanceMode.cs`、`MigrationPerformanceProfile.cs`、`MigrationPlan.cs`、`MigrationProgress.cs`、`MigrationTableInfo.cs`、`MigrationUiState.cs`、`PreflightReport.cs`、`SqlConnectionSettings.cs`。
  - 服务：`CheckpointStore.cs`、`ConnectionService.cs`、`DataCopyService.cs`、`ISystemCpuMonitor.cs`、`MigrationBatchCoordinator.cs`、`MigrationThrottleController.cs`、`MigrationUiOperations.cs`、`PreflightService.cs`、`SchemaMigrationService.cs`、`ValidationService.cs`、`WindowsSystemCpuMonitor.cs`。
  - 项目与验收：`src/SqlServerMigration.App/SqlServerMigration.App.csproj`、`tests/SqlServerMigration.Verification/SqlServerMigration.Verification.csproj`、`tests/SqlServerMigration.Verification/Program.cs`。
- 修改内容摘要：
  - 创建 .NET 10 WPF 独立项目和中文迁移界面；连接区支持同机不同实例、Windows/SQL Server 认证，密码不绑定、不落日志，并要求显式确认源库为停止写入的备份还原库。
  - 实现只读预检：目标必须为 SQL Server 2019 空库，禁止原地迁移，核对权限、兼容级别和排序规则；阻断 RLS、动态掩码、Ledger、vector/native json、Always Encrypted、CDC、复制、变更跟踪、FILESTREAM 等当前不安全或超范围功能。
  - 使用 SMO 按 SQL Server 2019 生成并先保存架构预览；按 PreData、数据、PostData、Programmability、验证的阶段执行，禁止 DROP、TRUNCATE、数据库创建/删除和实例配置修改。
  - 使用流式 `SqlDataReader` + 分批 `SqlBulkCopy` 迁移普通表数据，保留 identity/null；目标表已有数据即拒绝，失败或未知结果不自动重试或清理。
  - 实现三档可切换性能模式：高速（2 表/50,000 行/批/CPU 92%-82%）、平衡（1 表/25,000/82%-72%/50ms）、低影响默认（1 表/10,000/72%-62%/250ms）；连续 3 次 CPU 采样与滞回，监控不可用时降级低影响。
  - 实现暂停/继续/取消、CPU 自动等待状态、进度/行速率/ETA、逐表 COUNT_BIG + 非严格快速 checksum、运行收据与错误终态。
  - 生成 Windows x64 自包含单文件交付包和 ZIP；未部署到服务器。
- 影响范围：仅新增本独立仓库的应用、验证项目、文档与 Git 忽略规则；未连接、修改或部署任何真实 SQL Server 数据库，未改变系统环境、实例配置或现有业务项目。
- 当前项目状态：首版代码和自包含包已生成；适合先在隔离的简单/常规关系库上验证，不应直接对唯一生产目标执行。
- 验收情况：
  - `dotnet build SqlServerMigration.sln -c Release --no-restore`：0 警告、0 错误。
  - 无数据库验证：11 项全部通过，覆盖三档参数、运行中切换、CPU 暂停/恢复/降级、取消、Windows CPU 实采、凭据脱敏、计划指纹、校验告警语义、WPF 主窗口构造和界面模式切换。
  - `dotnet format --verify-no-changes`：通过。
  - 自包含 EXE 隐藏启动 3 秒保持运行，通过；SHA-256 `AB9D7A772266E77085D095EFE97DE1896A4FC4C8EEF785EFF158F4C58E6AEDFC`。
  - ZIP SHA-256 `38F60B20D7D7DFF545E175D3DE411EA0AEF7AC437747F672896B8B7346DDC05A`。
- 未完成/已知限制：未提供隔离 SQL Server 2025/2019 实例与凭据，因此未实际执行连接、SMO 脚本、数据复制和 20GB 长时间迁移；复杂可编程对象依赖和 2022/2025 专属模块语法需人工审阅预览；JSON 仅是运行收据而非断点续传；模式中的索引 MAXDOP 尚未安全应用；登录、用户、角色、权限和服务器级对象需单独迁移。

### 2026-10-23 11:42 +08:00

- 发布包复核：为避免覆盖已有产物，另生成最终交付目录 `artifacts/SqlServerMigration-win-x64-final2` 和 ZIP `artifacts/SqlServerMigration-win-x64-final2.zip`。
- 确认源码时间戳早于首个发布，旧包与 `final2` EXE 哈希相同属于确定性构建结果；最终对外交付只使用 `final2` 路径。
- 最终 EXE：154,194,189 字节，SHA-256 `AB9D7A772266E77085D095EFE97DE1896A4FC4C8EEF785EFF158F4C58E6AEDFC`。
- 最终 ZIP：65,403,362 字节，SHA-256 `68D5EB94AF65B5A0083C9997E2DDBF51C62AF09CE318942752C0D05937B8A6BA`。
- 对 `final2` EXE 执行隐藏启动烟测，进程 3 秒后仍正常存活；测试进程已停止。未连接或写入数据库。

### 2026-10-23 11:45 +08:00

- 最终差异检查发现 `App.xaml` 尾随空格和 `App.xaml.cs` 文件末尾多余空行；仅修复这两处格式，未改变业务逻辑。
- 格式修复后重新执行 `dotnet format --verify-no-changes`、Release 构建和无数据库验证：格式检查通过，构建 0 警告/0 错误，11 项验证全部通过。
- 为确保交付包与最终源码一致，新增且不覆盖地生成 `artifacts/SqlServerMigration-win-x64-final3` 及 `artifacts/SqlServerMigration-win-x64-final3.zip`；此后对外交付以 `final3` 为准，旧产物保留。
- `final3` EXE：154,194,189 字节，SHA-256 `E47F263DF8A375DDB1CBEC78DCB398DAFF58C6360C064B194D18086C80AABEB7`。
- `final3` ZIP：65,451,579 字节，SHA-256 `A129564562810DB595C8CCEDD3896840666C979C7EA88C6CEC2F7D2CB34AAB9A`。
- `final3` EXE 隐藏启动 3 秒后仍正常存活；仅停止本次自行启动的测试进程。未连接或写入 SQL Server。

### 2026-10-23 11:46 +08:00

- Git 交付：提交 `3023805`（`[feat] SqlServerMigration - 实现 SQL Server 逻辑迁移工具`）已推送到 `origin/feature/sql-server-migration-tool`。
- Pull Request：`#2`（`https://github.com/hjt1314/Sql_Server_Move/pull/2`），关联并关闭 Issue `#1`；PR 描述已包含完成内容、测试结果、已知风险和下一步计划。
- 当前状态：代码已提交并进入评审；本次未合并 PR，未部署到服务器，未执行真实数据库迁移。
