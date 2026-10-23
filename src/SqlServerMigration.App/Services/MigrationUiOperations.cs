using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using SqlServerMigration.App.Models;

namespace SqlServerMigration.App.Services;

/// <summary>
/// Connects the WPF workflow to the read-only preflight, schema, copy, and validation services.
/// It never creates or deletes a database and only proceeds when the selected target database is empty.
/// </summary>
public sealed partial class MigrationUiOperations : IMigrationUiOperations
{
    private readonly object _activeRunLock = new();
    private readonly ConnectionService _connectionService;
    private readonly PreflightService _preflightService;
    private readonly SchemaMigrationService _schemaMigrationService;
    private readonly DataCopyService _dataCopyService;
    private readonly ValidationService _validationService;
    private readonly ISystemCpuMonitor _cpuMonitor;
    private MigrationPerformanceMode _configuredMode = MigrationPerformanceMode.LowImpact;
    private RunContext? _activeRun;

    public MigrationUiOperations(
        ConnectionService? connectionService = null,
        PreflightService? preflightService = null,
        SchemaMigrationService? schemaMigrationService = null,
        DataCopyService? dataCopyService = null,
        ValidationService? validationService = null,
        ISystemCpuMonitor? cpuMonitor = null)
    {
        _connectionService = connectionService ?? new ConnectionService();
        _preflightService = preflightService ?? new PreflightService(_connectionService);
        _schemaMigrationService = schemaMigrationService ?? new SchemaMigrationService(_connectionService);
        _dataCopyService = dataCopyService ?? new DataCopyService(_connectionService);
        _validationService = validationService ?? new ValidationService(_connectionService);
        _cpuMonitor = cpuMonitor ?? new WindowsSystemCpuMonitor();
    }

    public event EventHandler<MigrationUiProgressEventArgs>? ProgressChanged;

    public event EventHandler<MigrationUiLogEventArgs>? LogReceived;

    public async Task<MigrationUiOperationResult> TestConnectionsAsync(
        MigrationConnectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ConnectionSettingsLease? settings = null;

        try
        {
            settings = ConnectionSettingsLease.Create(request);
            PublishStandaloneProgress(
                MigrationUiStage.Connection,
                MigrationStepStatus.Running,
                0,
                "正在执行只读连接测试。");

            Task<DatabaseEndpointInfo> sourceTask =
                _connectionService.TestConnectionAsync(settings.Source, cancellationToken);
            Task<DatabaseEndpointInfo> targetTask =
                _connectionService.TestConnectionAsync(settings.Target, cancellationToken);
            await Task.WhenAll(sourceTask, targetTask).ConfigureAwait(false);

            DatabaseEndpointInfo source = await sourceTask.ConfigureAwait(false);
            DatabaseEndpointInfo target = await targetTask.ConfigureAwait(false);
            string message =
                $"连接成功：源端 {source.ProductVersion}，目标端 {target.ProductVersion}（{target.Edition}）。";
            PublishStandaloneProgress(
                MigrationUiStage.Connection,
                MigrationStepStatus.Succeeded,
                0,
                message);
            PublishLog(MigrationLogLevel.Success, message);
            return Success(message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            const string message = "连接测试已取消。";
            PublishStandaloneProgress(MigrationUiStage.Connection, MigrationStepStatus.Cancelled, 0, message);
            PublishLog(MigrationLogLevel.Warning, message);
            return Cancelled(message);
        }
        catch (Exception exception)
        {
            string message = $"连接测试失败：{GetSafeExceptionMessage(exception, settings)}";
            PublishStandaloneProgress(MigrationUiStage.Connection, MigrationStepStatus.Failed, 0, message);
            PublishLog(MigrationLogLevel.Error, message);
            return Failed(message);
        }
        finally
        {
            settings?.Dispose();
        }
    }

    public async Task<MigrationUiOperationResult> RunPreflightAsync(
        MigrationConnectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ConnectionSettingsLease? settings = null;

        try
        {
            settings = ConnectionSettingsLease.Create(request);
            PublishStandaloneProgress(
                MigrationUiStage.Preflight,
                MigrationStepStatus.Running,
                2,
                "正在读取系统目录并检查目标库是否为空。预检不会写入数据库。");
            PreflightReport report = await _preflightService.RunAsync(
                    settings.Source,
                    settings.Target,
                    targetDatabaseSelectionConfirmed: true,
                    cancellationToken)
                .ConfigureAwait(false);
            PublishPreflightIssues(report);

            if (!report.CanMigrate)
            {
                string message = BuildPreflightFailureMessage(report);
                PublishStandaloneProgress(MigrationUiStage.Preflight, MigrationStepStatus.Failed, 5, message);
                return Failed(message);
            }

            string successMessage =
                $"只读预检通过：{report.Tables.Count} 张表，{report.Issues.Count(issue => issue.Severity == PreflightIssueSeverity.Warning)} 项提醒。";
            PublishStandaloneProgress(
                MigrationUiStage.Preflight,
                MigrationStepStatus.Succeeded,
                5,
                successMessage);
            PublishLog(MigrationLogLevel.Success, successMessage);
            return Success(successMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            const string message = "只读预检已取消。";
            PublishStandaloneProgress(MigrationUiStage.Preflight, MigrationStepStatus.Cancelled, 0, message);
            PublishLog(MigrationLogLevel.Warning, message);
            return Cancelled(message);
        }
        catch (Exception exception)
        {
            string message = $"只读预检失败：{GetSafeExceptionMessage(exception, settings)}";
            PublishStandaloneProgress(MigrationUiStage.Preflight, MigrationStepStatus.Failed, 0, message);
            PublishLog(MigrationLogLevel.Error, message);
            return Failed(message);
        }
        finally
        {
            settings?.Dispose();
        }
    }

    public async Task<MigrationUiOperationResult> StartMigrationAsync(
        MigrationStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = MigrationPerformanceProfile.For(request.PerformanceMode);

        ConnectionSettingsLease? settings = null;
        RunContext? context = null;
        var activated = false;

        try
        {
            settings = ConnectionSettingsLease.Create(request.Connections);
            Guid taskId = Guid.NewGuid();
            string runDirectory = CreateRunDirectory(taskId);
            var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pauseGate = new ManualPauseGate();
            var throttle = new MigrationThrottleController(_cpuMonitor, request.PerformanceMode);
            var coordinator = new MigrationBatchCoordinator(
                _dataCopyService,
                throttle,
                pauseGate.WaitAsync);
            context = new RunContext(
                taskId,
                runDirectory,
                linkedCancellation,
                pauseGate,
                throttle,
                coordinator);

            lock (_activeRunLock)
            {
                if (_activeRun is not null)
                {
                    context.Dispose();
                    context = null;
                    return Failed("已有迁移任务正在运行，不能同时启动第二个任务。");
                }

                _configuredMode = request.PerformanceMode;
                _activeRun = context;
                activated = true;
            }

            context.ThrottleStatusHandler = (_, eventArgs) =>
                OnThrottleStatusChanged(context, eventArgs.CurrentStatus);
            throttle.StatusChanged += context.ThrottleStatusHandler;

            PublishLog(
                MigrationLogLevel.Information,
                $"迁移任务 {taskId:N} 已创建，输出目录：{runDirectory}");
            PublishLog(
                MigrationLogLevel.Warning,
                "性能模式已应用于表级并发、数据批量大小、批间延迟和 CPU 门控；当前核心未安全改写索引 DDL，因此 MAXDOP 暂不应用于索引创建。");

            return await ExecuteMigrationAsync(context, settings).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested || context?.Cancellation.IsCancellationRequested == true)
        {
            const string message = "迁移已取消；已提交批次保持不变，结果未被标记为成功。";
            if (context is not null)
            {
                await SaveTerminalCheckpointAsync(
                        context,
                        MigrationTaskState.Cancelled,
                        message)
                    .ConfigureAwait(false);
                PublishRunProgress(context, MigrationStepStatus.Cancelled, message);
            }

            PublishLog(MigrationLogLevel.Warning, message);
            return Cancelled(message);
        }
        catch (Exception exception)
        {
            string message = $"迁移失败：{GetSafeExceptionMessage(exception, settings)}";
            if (context is not null)
            {
                MigrationTaskState terminalState = context.TargetWritesStarted
                    ? MigrationTaskState.ResultUnknown
                    : MigrationTaskState.Failed;
                await SaveTerminalCheckpointAsync(context, terminalState, message).ConfigureAwait(false);
                PublishRunProgress(context, MigrationStepStatus.Failed, message);
            }

            PublishLog(MigrationLogLevel.Error, message);
            return Failed(message);
        }
        finally
        {
            if (activated && context is not null)
            {
                if (context.ThrottleStatusHandler is not null)
                {
                    context.Throttle.StatusChanged -= context.ThrottleStatusHandler;
                }

                lock (_activeRunLock)
                {
                    if (ReferenceEquals(_activeRun, context))
                    {
                        _activeRun = null;
                    }
                }

                context.MarkFinished();
                context.PauseGate.Resume();
                context.Dispose();
            }

            settings?.Dispose();
        }
    }

    public Task<MigrationUiOperationResult> SetPausedAsync(
        bool shouldPause,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunContext? context = GetActiveRun();
        if (context is null || context.IsFinished)
        {
            return Task.FromResult(Failed("当前没有可暂停或继续的迁移任务。"));
        }

        string message;
        if (shouldPause)
        {
            context.PauseGate.Pause();
            message = "已请求暂停；当前 SQL 批次不会被强制中断，任务将在下一个安全边界停住。";
            PublishRunProgress(context, MigrationStepStatus.Paused, message);
        }
        else
        {
            context.PauseGate.Resume();
            message = "迁移已继续，将按当前性能模式启动后续批次。";
            PublishRunProgress(context, MigrationStepStatus.Running, message);
        }

        PublishLog(MigrationLogLevel.Information, message);
        return Task.FromResult(Success(message));
    }

    public Task<MigrationUiOperationResult> ChangePerformanceModeAsync(
        MigrationPerformanceMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MigrationPerformanceProfile requestedProfile = MigrationPerformanceProfile.For(mode);
        RunContext? context = GetActiveRun();

        if (context is null || context.IsFinished)
        {
            _configuredMode = mode;
            string idleMessage = $"已选择{requestedProfile.DisplayName}模式，将在下次迁移启动时生效。";
            return Task.FromResult(Success(idleMessage));
        }

        context.Throttle.SetMode(mode);
        context.Coordinator.NotifyLimitsChanged();
        _configuredMode = mode;
        string message =
            $"已请求切换到{requestedProfile.DisplayName}模式；当前批次不打断，从下一批次/调度边界生效。";
        PublishRunProgress(
            context,
            context.PauseGate.IsPaused || context.Throttle.IsPaused
                ? MigrationStepStatus.Paused
                : MigrationStepStatus.Running,
            message);
        PublishLog(MigrationLogLevel.Information, message);
        return Task.FromResult(Success(message));
    }

    public Task<MigrationUiOperationResult> CancelAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunContext? context = GetActiveRun();
        if (context is null || context.IsFinished)
        {
            return Task.FromResult(Failed("当前没有可取消的迁移任务。"));
        }

        context.PauseGate.Resume();
        try
        {
            context.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return Task.FromResult(Failed("迁移任务已结束，无需再次取消。"));
        }

        const string message = "取消请求已发送；正在等待当前数据库操作响应取消。";
        PublishLog(MigrationLogLevel.Warning, message);
        return Task.FromResult(Success(message));
    }

    private async Task<MigrationUiOperationResult> ExecuteMigrationAsync(
        RunContext context,
        ConnectionSettingsLease settings)
    {
        CancellationToken cancellationToken = context.Cancellation.Token;
        await context.PauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetRunStage(context, MigrationUiStage.Preflight, 1, "启动前重新执行只读预检。");
        PreflightReport preflight = await _preflightService.RunAsync(
                settings.Source,
                settings.Target,
                targetDatabaseSelectionConfirmed: true,
                cancellationToken)
            .ConfigureAwait(false);
        PublishPreflightIssues(preflight);

        if (!preflight.CanMigrate)
        {
            string message = BuildPreflightFailureMessage(preflight);
            PublishRunProgress(context, MigrationStepStatus.Failed, message);
            PublishLog(MigrationLogLevel.Error, message);
            return Failed(message);
        }

        var plan = new MigrationPlan
        {
            TaskId = context.TaskId,
            Source = settings.Source,
            Target = settings.Target,
            TargetDatabaseName = settings.Target.Database,
            OutputDirectory = context.RunDirectory,
            PerformanceMode = context.Throttle.RequestedMode,
            CreateNewTargetDatabase = false,
            IncludeProgrammableObjects = true
        };
        context.Checkpoint = CreateCheckpoint(plan, preflight.Tables);
        await context.CheckpointStore.SaveAsync(context.Checkpoint, cancellationToken).ConfigureAwait(false);
        PublishRunProgress(context, MigrationStepStatus.Succeeded, "启动前只读预检通过。", 5);

        await context.PauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetRunStage(context, MigrationUiStage.Schema, 6, "正在生成面向 SQL Server 2019 的架构预览脚本。");
        SchemaMigrationPlan schemaPlan = await _schemaMigrationService.GeneratePreviewAsync(
                settings.Source,
                cancellationToken)
            .ConfigureAwait(false);
        string previewPath = Path.Combine(context.RunDirectory, "schema-preview.sql");
        await File.WriteAllTextAsync(
                previewPath,
                schemaPlan.PreviewScript,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken)
            .ConfigureAwait(false);
        ValidateSchemaPlanSafety(schemaPlan);
        PublishLog(
            MigrationLogLevel.Success,
            $"已先保存 SQL Server 2019 架构预览脚本：{previewPath}");

        await context.PauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        context.TargetWritesStarted = true;
        IProgress<MigrationProgress> preDataProgress = CreateBoundaryProgress(
            context,
            MigrationUiStage.Schema,
            8,
            20,
            cancellationToken);
        SchemaApplyResult preDataResult = await _schemaMigrationService.ExecutePhaseAsync(
                settings.Target,
                schemaPlan,
                SchemaScriptPhase.PreData,
                preDataProgress,
                cancellationToken)
            .ConfigureAwait(false);
        context.Checkpoint.State = MigrationTaskState.SchemaCreated;
        await context.CheckpointStore.SaveAsync(context.Checkpoint, cancellationToken).ConfigureAwait(false);
        PublishLog(
            MigrationLogLevel.Success,
            $"PreData 架构阶段完成，共执行 {preDataResult.ExecutedBatchCount} 个 SQL 批次。");

        await context.PauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        long estimatedRows = SumEstimatedRows(preflight.Tables);
        lock (context.StateLock)
        {
            context.TotalRows = estimatedRows;
            context.Stage = MigrationUiStage.Data;
            context.OverallProgressPercent = 20;
            context.CurrentObject = null;
            context.EstimatedRemaining = null;
        }

        context.Checkpoint.State = MigrationTaskState.Copying;
        await context.CheckpointStore.SaveAsync(context.Checkpoint, cancellationToken).ConfigureAwait(false);
        PublishRunProgress(context, MigrationStepStatus.Running, "开始流式复制数据。");

        var dataProgress = new InlineProgress<MigrationProgress>(value => ForwardDataProgress(context, value));
        IReadOnlyList<TableCopyResult> copyResults = await context.Coordinator.CopyAsync(
                settings.Source,
                settings.Target,
                preflight.Tables,
                dataProgress,
                (table, result) => OnTableCopyCompletedAsync(context, table, result),
                (table, exception) => OnTableCopyFailedAsync(context, table, exception, settings),
                cancellationToken)
            .ConfigureAwait(false);
        long copiedRows = SumSaturated(copyResults.Select(result => result.RowsCopied));
        lock (context.StateLock)
        {
            context.CompletedRows = copiedRows;
            context.TotalRows = copiedRows;
            context.OverallProgressPercent = 78;
            context.EstimatedRemaining = TimeSpan.Zero;
        }

        PublishRunProgress(context, MigrationStepStatus.Running, $"数据复制完成，共提交 {copiedRows:N0} 行。");

        await context.PauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IProgress<MigrationProgress> postDataProgress = CreateBoundaryProgress(
            context,
            MigrationUiStage.Data,
            78,
            85,
            cancellationToken,
            "正在创建索引、约束、外键与触发器");
        SchemaApplyResult postDataResult = await _schemaMigrationService.ExecutePhaseAsync(
                settings.Target,
                schemaPlan,
                SchemaScriptPhase.PostData,
                postDataProgress,
                cancellationToken)
            .ConfigureAwait(false);
        PublishLog(
            MigrationLogLevel.Success,
            $"PostData 架构阶段完成，共执行 {postDataResult.ExecutedBatchCount} 个 SQL 批次。");

        await context.PauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IProgress<MigrationProgress> programmabilityProgress = CreateBoundaryProgress(
            context,
            MigrationUiStage.Data,
            85,
            90,
            cancellationToken,
            "正在创建视图、函数与存储过程");
        SchemaApplyResult programmabilityResult = await _schemaMigrationService.ExecutePhaseAsync(
                settings.Target,
                schemaPlan,
                SchemaScriptPhase.Programmability,
                programmabilityProgress,
                cancellationToken)
            .ConfigureAwait(false);
        PublishLog(
            MigrationLogLevel.Success,
            $"Programmability 阶段完成，共执行 {programmabilityResult.ExecutedBatchCount} 个 SQL 批次。");

        await context.PauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (context.StateLock)
        {
            context.Stage = MigrationUiStage.Validation;
            context.OverallProgressPercent = 90;
            context.CurrentObject = null;
            context.EstimatedRemaining = null;
        }

        context.Checkpoint.State = MigrationTaskState.Validating;
        await context.CheckpointStore.SaveAsync(context.Checkpoint, cancellationToken).ConfigureAwait(false);
        PublishRunProgress(context, MigrationStepStatus.Running, "正在逐表核对 COUNT_BIG 与快速校验值。");
        IProgress<MigrationProgress> validationProgress = CreateBoundaryProgress(
            context,
            MigrationUiStage.Validation,
            90,
            99,
            cancellationToken);
        ValidationReport validation = await _validationService.ValidateAsync(
                settings.Source,
                settings.Target,
                preflight.Tables,
                includeQuickChecksum: true,
                validationProgress,
                cancellationToken)
            .ConfigureAwait(false);

        foreach (TableValidationResult table in validation.Tables)
        {
            MigrationLogLevel level = table.Status == TableValidationStatus.Passed
                ? MigrationLogLevel.Success
                : table.Status == TableValidationStatus.Failed
                    ? MigrationLogLevel.Error
                    : MigrationLogLevel.Warning;
            PublishLog(
                level,
                $"校验 {table.TableName}：源 {table.SourceRowCount:N0} 行，目标 {table.TargetRowCount:N0} 行；{table.Detail}");
        }

        if (!validation.IsSuccessful)
        {
            int failures = validation.Tables.Count(table => table.Status == TableValidationStatus.Failed);
            int unavailable = validation.Tables.Count(table => table.Status == TableValidationStatus.QuickChecksumUnavailable);
            string message =
                $"逐表校验未全部通过：{failures} 张不一致，{unavailable} 张快速校验不可用。任务未标记为成功。";
            context.Checkpoint.State = MigrationTaskState.Failed;
            context.Checkpoint.LastError = message;
            await context.CheckpointStore.SaveAsync(context.Checkpoint, CancellationToken.None).ConfigureAwait(false);
            PublishRunProgress(context, MigrationStepStatus.Failed, message, 99);
            PublishLog(MigrationLogLevel.Error, message);
            return Failed(message);
        }

        int checksumWarnings = validation.Tables.Count(
            table => table.Status == TableValidationStatus.QuickChecksumUnavailable);
        context.Checkpoint.State = MigrationTaskState.Completed;
        context.Checkpoint.LastError = null;
        await context.CheckpointStore.SaveAsync(context.Checkpoint, cancellationToken).ConfigureAwait(false);
        string completedMessage = checksumWarnings == 0
            ? "迁移完成：架构、数据、逐表行数与快速校验均通过；快速校验不是密码学严格哈希。"
            : $"迁移完成并通过逐表行数校验；{checksumWarnings} 张表的快速校验不适用，已作为提醒记录。";
        PublishRunProgress(context, MigrationStepStatus.Succeeded, completedMessage, 100);
        PublishLog(MigrationLogLevel.Success, completedMessage);
        return Success(completedMessage);
    }

    private void OnThrottleStatusChanged(RunContext context, MigrationThrottleStatus status)
    {
        if (context.IsFinished)
        {
            return;
        }

        context.Coordinator.NotifyLimitsChanged();
        lock (context.StateLock)
        {
            context.CpuUsagePercent = status.CpuUsagePercent;
        }

        PublishRunProgress(
            context,
            context.PauseGate.IsPaused || status.IsPaused
                ? MigrationStepStatus.Paused
                : MigrationStepStatus.Running,
            status.Message);
    }

    private void ForwardDataProgress(RunContext context, MigrationProgress progress)
    {
        lock (context.StateLock)
        {
            string key = progress.CurrentObject ?? string.Empty;
            context.RowsByTable[key] = Math.Max(0, progress.RowsProcessed);
            context.BatchCountByTable[key] = context.BatchCountByTable.GetValueOrDefault(key) + 1;
            context.CompletedRows = SumSaturated(context.RowsByTable.Values);
            context.CurrentObject = progress.CurrentObject;
            context.EstimatedRemaining = progress.EstimatedRemaining;
            context.RowsPerSecond = progress.RowsPerSecond;
            context.Stage = MigrationUiStage.Data;
            context.StageStatus = context.PauseGate.IsPaused || context.Throttle.IsPaused
                ? MigrationStepStatus.Paused
                : MigrationStepStatus.Running;
            context.OverallProgressPercent = context.TotalRows > 0
                ? 20 + (58 * Math.Clamp(context.CompletedRows / (double)context.TotalRows.Value, 0, 1))
                : 20;
        }

        PublishRunProgress(context, context.StageStatus, progress.Message ?? "正在复制数据。");
    }

    private async Task OnTableCopyCompletedAsync(
        RunContext context,
        MigrationTableInfo table,
        TableCopyResult result)
    {
        TableCheckpoint checkpoint = context.Checkpoint!.Tables[table.DisplayName];
        checkpoint.State = TableCopyState.Completed;
        checkpoint.ConfirmedRowsCopied = result.RowsCopied;
        checkpoint.CompletedBatches = result.BatchesCompleted;
        checkpoint.LastError = null;
        checkpoint.UpdatedAt = DateTimeOffset.UtcNow;
        await context.CheckpointStore.SaveAsync(context.Checkpoint, CancellationToken.None).ConfigureAwait(false);
        PublishLog(
            MigrationLogLevel.Success,
            $"表 {table.DisplayName} 复制完成：{result.RowsCopied:N0} 行，{result.BatchesCompleted} 批。");
    }

    private async Task OnTableCopyFailedAsync(
        RunContext context,
        MigrationTableInfo table,
        Exception exception,
        ConnectionSettingsLease settings)
    {
        long confirmedRows;
        long confirmedBatches;
        lock (context.StateLock)
        {
            confirmedRows = context.RowsByTable.GetValueOrDefault(table.DisplayName);
            confirmedBatches = context.BatchCountByTable.GetValueOrDefault(table.DisplayName);
        }

        string message = GetSafeExceptionMessage(exception, settings);
        TableCheckpoint checkpoint = context.Checkpoint!.Tables[table.DisplayName];
        checkpoint.State = confirmedRows > 0 ? TableCopyState.ResultUnknown : TableCopyState.Failed;
        checkpoint.ConfirmedRowsCopied = confirmedRows;
        checkpoint.CompletedBatches = confirmedBatches;
        checkpoint.LastError = message;
        checkpoint.UpdatedAt = DateTimeOffset.UtcNow;
        await context.CheckpointStore.SaveAsync(context.Checkpoint, CancellationToken.None).ConfigureAwait(false);
        PublishLog(
            MigrationLogLevel.Error,
            $"表 {table.DisplayName} 复制中断；已确认 {confirmedRows:N0} 行，未自动重试或清理目标表。原因：{message}");
    }

    private IProgress<MigrationProgress> CreateBoundaryProgress(
        RunContext context,
        MigrationUiStage stage,
        double rangeStart,
        double rangeEnd,
        CancellationToken cancellationToken,
        string? messagePrefix = null) =>
        new BoundaryProgress<MigrationProgress>(
            value => ForwardObjectProgress(context, stage, rangeStart, rangeEnd, value, messagePrefix),
            context.PauseGate.WaitAsync,
            cancellationToken);

    private void ForwardObjectProgress(
        RunContext context,
        MigrationUiStage stage,
        double rangeStart,
        double rangeEnd,
        MigrationProgress progress,
        string? messagePrefix)
    {
        double fraction = progress.ObjectsTotal > 0
            ? Math.Clamp(progress.ObjectsCompleted / (double)progress.ObjectsTotal, 0, 1)
            : 1;
        string message = string.IsNullOrWhiteSpace(messagePrefix)
            ? progress.Message ?? "正在处理对象。"
            : $"{messagePrefix}：{progress.CurrentObject}";

        lock (context.StateLock)
        {
            context.Stage = stage;
            context.StageStatus = context.PauseGate.IsPaused
                ? MigrationStepStatus.Paused
                : MigrationStepStatus.Running;
            context.OverallProgressPercent = rangeStart + ((rangeEnd - rangeStart) * fraction);
            context.CurrentObject = progress.CurrentObject;
            context.EstimatedRemaining = progress.EstimatedRemaining;
        }

        PublishRunProgress(context, context.StageStatus, message);
    }

    private static MigrationCheckpoint CreateCheckpoint(
        MigrationPlan plan,
        IReadOnlyList<MigrationTableInfo> tables)
    {
        var tableCheckpoints = new Dictionary<string, TableCheckpoint>(StringComparer.OrdinalIgnoreCase);
        foreach (MigrationTableInfo table in tables)
        {
            tableCheckpoints.Add(
                table.DisplayName,
                new TableCheckpoint
                {
                    SchemaName = table.SchemaName,
                    TableName = table.TableName
                });
        }

        return new MigrationCheckpoint
        {
            TaskId = plan.TaskId,
            PlanFingerprint = CheckpointStore.CreatePlanFingerprint(plan),
            State = MigrationTaskState.PreflightPassed,
            Tables = tableCheckpoints
        };
    }

    private async Task SaveTerminalCheckpointAsync(
        RunContext context,
        MigrationTaskState state,
        string error)
    {
        if (context.Checkpoint is null)
        {
            return;
        }

        context.Checkpoint.State = state;
        context.Checkpoint.LastError = error;
        try
        {
            await context.CheckpointStore.SaveAsync(context.Checkpoint, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception checkpointException)
        {
            PublishLog(
                MigrationLogLevel.Error,
                $"保存终态检查点失败：{GetSafeExceptionMessage(checkpointException, settings: null)}");
        }
    }

    private void SetRunStage(
        RunContext context,
        MigrationUiStage stage,
        double overallProgressPercent,
        string message)
    {
        lock (context.StateLock)
        {
            context.Stage = stage;
            context.StageStatus = context.PauseGate.IsPaused
                ? MigrationStepStatus.Paused
                : MigrationStepStatus.Running;
            context.OverallProgressPercent = overallProgressPercent;
            context.CurrentObject = null;
            context.EstimatedRemaining = null;
            context.RowsPerSecond = null;
        }

        PublishRunProgress(context, context.StageStatus, message);
        PublishLog(MigrationLogLevel.Information, message);
    }

    private void PublishRunProgress(
        RunContext context,
        MigrationStepStatus status,
        string message,
        double? overallProgressPercent = null)
    {
        MigrationUiProgressSnapshot snapshot;
        lock (context.StateLock)
        {
            if (overallProgressPercent.HasValue)
            {
                context.OverallProgressPercent = overallProgressPercent.Value;
            }

            context.StageStatus = status;
            MigrationThrottleStatus throttleStatus = context.Throttle.Status;
            snapshot = new MigrationUiProgressSnapshot(
                context.Stage,
                status,
                context.OverallProgressPercent,
                context.CpuUsagePercent ?? throttleStatus.CpuUsagePercent,
                context.RowsPerSecond,
                context.EstimatedRemaining,
                context.CompletedRows,
                context.TotalRows,
                context.CurrentObject,
                throttleStatus.EffectiveMode,
                throttleStatus.RequestedMode,
                throttleStatus.EffectiveMode != throttleStatus.RequestedMode,
                throttleStatus.IsPaused,
                message);
        }

        RaiseProgress(snapshot);
    }

    private void PublishStandaloneProgress(
        MigrationUiStage stage,
        MigrationStepStatus status,
        double overallProgressPercent,
        string message) =>
        RaiseProgress(
            new MigrationUiProgressSnapshot(
                stage,
                status,
                overallProgressPercent,
                CpuUsagePercent: null,
                RowsPerSecond: null,
                EstimatedRemaining: null,
                CompletedRows: 0,
                TotalRows: null,
                CurrentObject: null,
                EffectiveMode: null,
                RequestedMode: null,
                IsModeChangePending: false,
                IsCpuThrottled: false,
                message));

    private void PublishPreflightIssues(PreflightReport report)
    {
        foreach (PreflightIssue issue in report.Issues)
        {
            MigrationLogLevel level = issue.Severity switch
            {
                PreflightIssueSeverity.Blocking => MigrationLogLevel.Error,
                PreflightIssueSeverity.Warning => MigrationLogLevel.Warning,
                _ => MigrationLogLevel.Information
            };
            string objectPrefix = string.IsNullOrWhiteSpace(issue.ObjectName)
                ? string.Empty
                : $"{issue.ObjectName}：";
            PublishLog(level, $"预检 [{issue.Code}] {objectPrefix}{issue.Message}");
        }
    }

    private static string BuildPreflightFailureMessage(PreflightReport report)
    {
        PreflightIssue[] blocking = report.Issues
            .Where(issue => issue.Severity == PreflightIssueSeverity.Blocking)
            .ToArray();
        string first = blocking.FirstOrDefault()?.Message ?? "存在阻断项。";
        return $"预检未通过，共 {blocking.Length} 个阻断项。首项：{first}";
    }

    private static void ValidateSchemaPlanSafety(SchemaMigrationPlan plan)
    {
        foreach (SchemaScriptBatch batch in plan.Batches)
        {
            string? firstStatement = FindFirstOperativeStatement(batch.Sql);
            if (firstStatement is null)
            {
                continue;
            }

            if (firstStatement.StartsWith("DROP ", StringComparison.OrdinalIgnoreCase) ||
                firstStatement.StartsWith("TRUNCATE ", StringComparison.OrdinalIgnoreCase) ||
                firstStatement.StartsWith("CREATE DATABASE", StringComparison.OrdinalIgnoreCase) ||
                firstStatement.StartsWith("ALTER DATABASE", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"架构预览包含被禁止的顶层语句（{batch.ObjectName}），已阻止执行。");
            }
        }
    }

    private static string? FindFirstOperativeStatement(string script)
    {
        using var reader = new StringReader(script);
        while (reader.ReadLine() is { } line)
        {
            string candidate = line.Trim();
            if (candidate.Length == 0 ||
                candidate.StartsWith("--", StringComparison.Ordinal) ||
                candidate.Equals("GO", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("SET ANSI_NULLS", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("SET QUOTED_IDENTIFIER", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("USE ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static string CreateRunDirectory(Guid taskId)
    {
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("无法解析当前用户的 LocalApplicationData 目录。");
        }

        string root = Path.Combine(localApplicationData, "SqlServerMigration", "runs");
        string runDirectory = Path.Combine(root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{taskId:N}");
        Directory.CreateDirectory(runDirectory);
        return runDirectory;
    }

    private static long SumEstimatedRows(IEnumerable<MigrationTableInfo> tables) =>
        SumSaturated(tables.Select(table => Math.Max(0, table.EstimatedRowCount)));

    private static long SumSaturated(IEnumerable<long> values)
    {
        long total = 0;
        foreach (long value in values)
        {
            if (value <= 0)
            {
                continue;
            }

            if (long.MaxValue - total < value)
            {
                return long.MaxValue;
            }

            total += value;
        }

        return total;
    }

    private RunContext? GetActiveRun()
    {
        lock (_activeRunLock)
        {
            return _activeRun;
        }
    }

    private void RaiseProgress(MigrationUiProgressSnapshot snapshot)
    {
        var eventArgs = new MigrationUiProgressEventArgs(snapshot);
        foreach (EventHandler<MigrationUiProgressEventArgs> handler in
                 ProgressChanged?.GetInvocationList().Cast<EventHandler<MigrationUiProgressEventArgs>>() ?? [])
        {
            try
            {
                handler(this, eventArgs);
            }
            catch
            {
                // A UI subscriber must not turn a completed database operation into a false failure.
            }
        }
    }

    private void PublishLog(MigrationLogLevel level, string message)
    {
        var eventArgs = new MigrationUiLogEventArgs(new MigrationLogEntry(DateTimeOffset.Now, level, message));
        foreach (EventHandler<MigrationUiLogEventArgs> handler in
                 LogReceived?.GetInvocationList().Cast<EventHandler<MigrationUiLogEventArgs>>() ?? [])
        {
            try
            {
                handler(this, eventArgs);
            }
            catch
            {
                // Logging callbacks are observational and must not affect migration state.
            }
        }
    }

    private static MigrationUiOperationResult Success(string message) =>
        new(MigrationUiOutcome.Succeeded, message);

    private static MigrationUiOperationResult Failed(string message) =>
        new(MigrationUiOutcome.Failed, message);

    private static MigrationUiOperationResult Cancelled(string message) =>
        new(MigrationUiOutcome.Cancelled, message);

    private static string GetSafeExceptionMessage(
        Exception exception,
        ConnectionSettingsLease? settings)
    {
        string message = exception.GetBaseException().Message;
        if (settings is not null)
        {
            message = Redact(message, settings.Source.Password);
            message = Redact(message, settings.Target.Password);
        }

        return PasswordAssignmentRegex().Replace(message, "$1=***");
    }

    private static string Redact(string value, string? secret) =>
        string.IsNullOrEmpty(secret)
            ? value
            : value.Replace(secret, "***", StringComparison.Ordinal);

    [GeneratedRegex(@"(?i)\b(Password|Pwd)\s*=\s*[^;\r\n]*")]
    private static partial Regex PasswordAssignmentRegex();

    private sealed class ConnectionSettingsLease : IDisposable
    {
        private ConnectionSettingsLease(
            SqlConnectionSettings source,
            SqlConnectionSettings target)
        {
            Source = source;
            Target = target;
        }

        public SqlConnectionSettings Source { get; }

        public SqlConnectionSettings Target { get; }

        public static ConnectionSettingsLease Create(MigrationConnectionRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            SqlConnectionSettings source = CreateSettings(
                request.Source,
                request.Secrets.SourcePassword);
            try
            {
                SqlConnectionSettings target = CreateSettings(
                    request.Target,
                    request.Secrets.TargetPassword);
                return new ConnectionSettingsLease(source, target);
            }
            catch
            {
                source.Password = null;
                throw;
            }
        }

        public void Dispose()
        {
            Source.Password = null;
            Target.Password = null;
        }

        private static SqlConnectionSettings CreateSettings(
            SqlEndpointDefinition endpoint,
            SecureString? securePassword)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.ServerAddress);
            ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.DatabaseName);

            string? password = null;
            if (endpoint.AuthenticationMode == SqlAuthenticationMode.SqlServer)
            {
                if (securePassword is null || securePassword.Length == 0)
                {
                    throw new InvalidOperationException($"{endpoint.RoleName}缺少 SQL Server 认证密码。");
                }

                password = CopySecureString(securePassword);
            }

            return new SqlConnectionSettings
            {
                Server = endpoint.ServerAddress.Trim(),
                Database = endpoint.DatabaseName.Trim(),
                AuthenticationMode = endpoint.AuthenticationMode,
                UserName = endpoint.AuthenticationMode == SqlAuthenticationMode.SqlServer
                    ? endpoint.UserName?.Trim()
                    : null,
                Password = password,
                Encrypt = true,
                TrustServerCertificate = true,
                ConnectTimeoutSeconds = 15,
                CommandTimeoutSeconds = 600
            };
        }

        private static string CopySecureString(SecureString secureString)
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                buffer = Marshal.SecureStringToGlobalAllocUnicode(secureString);
                return Marshal.PtrToStringUni(buffer, secureString.Length) ?? string.Empty;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.ZeroFreeGlobalAllocUnicode(buffer);
                }
            }
        }
    }

    private sealed class RunContext : IDisposable
    {
        private int _finished;

        public RunContext(
            Guid taskId,
            string runDirectory,
            CancellationTokenSource cancellation,
            ManualPauseGate pauseGate,
            MigrationThrottleController throttle,
            MigrationBatchCoordinator coordinator)
        {
            TaskId = taskId;
            RunDirectory = runDirectory;
            Cancellation = cancellation;
            PauseGate = pauseGate;
            Throttle = throttle;
            Coordinator = coordinator;
            CheckpointStore = new CheckpointStore(runDirectory);
        }

        public object StateLock { get; } = new();

        public Guid TaskId { get; }

        public string RunDirectory { get; }

        public CancellationTokenSource Cancellation { get; }

        public ManualPauseGate PauseGate { get; }

        public MigrationThrottleController Throttle { get; }

        public MigrationBatchCoordinator Coordinator { get; }

        public CheckpointStore CheckpointStore { get; }

        public MigrationCheckpoint? Checkpoint { get; set; }

        public EventHandler<MigrationThrottleStatusChangedEventArgs>? ThrottleStatusHandler { get; set; }

        public Dictionary<string, long> RowsByTable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, long> BatchCountByTable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public MigrationUiStage Stage { get; set; } = MigrationUiStage.Preflight;

        public MigrationStepStatus StageStatus { get; set; } = MigrationStepStatus.Running;

        public double OverallProgressPercent { get; set; }

        public double? CpuUsagePercent { get; set; }

        public TimeSpan? EstimatedRemaining { get; set; }

        public double? RowsPerSecond { get; set; }

        public long CompletedRows { get; set; }

        public long? TotalRows { get; set; }

        public string? CurrentObject { get; set; }

        public bool TargetWritesStarted { get; set; }

        public bool IsFinished => Volatile.Read(ref _finished) != 0;

        public void MarkFinished() => Interlocked.Exchange(ref _finished, 1);

        public void Dispose() => Cancellation.Dispose();
    }

    private sealed class ManualPauseGate
    {
        private readonly object _lock = new();
        private bool _isPaused;
        private TaskCompletionSource _resumeSignal = CreateResumeSignal(completed: true);

        public bool IsPaused
        {
            get
            {
                lock (_lock)
                {
                    return _isPaused;
                }
            }
        }

        public void Pause()
        {
            lock (_lock)
            {
                if (_isPaused)
                {
                    return;
                }

                _isPaused = true;
                _resumeSignal = CreateResumeSignal(completed: false);
            }
        }

        public void Resume()
        {
            TaskCompletionSource signal;
            lock (_lock)
            {
                if (!_isPaused)
                {
                    return;
                }

                _isPaused = false;
                signal = _resumeSignal;
            }

            signal.TrySetResult();
        }

        public Task WaitAsync(CancellationToken cancellationToken)
        {
            Task waitTask;
            lock (_lock)
            {
                waitTask = _resumeSignal.Task;
            }

            return waitTask.WaitAsync(cancellationToken);
        }

        private static TaskCompletionSource CreateResumeSignal(bool completed)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (completed)
            {
                signal.TrySetResult();
            }

            return signal;
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class BoundaryProgress<T>(
        Action<T> report,
        Func<CancellationToken, Task> waitForRunPermissionAsync,
        CancellationToken cancellationToken) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
            waitForRunPermissionAsync(cancellationToken).GetAwaiter().GetResult();
        }
    }
}
