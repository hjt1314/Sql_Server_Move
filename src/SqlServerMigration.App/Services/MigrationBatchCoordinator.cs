using System.Runtime.ExceptionServices;
using SqlServerMigration.App.Models;

namespace SqlServerMigration.App.Services;

/// <summary>
/// Coordinates table copy tasks while honoring the effective runtime profile.
/// Already running tables are never interrupted solely because the concurrency limit drops.
/// </summary>
internal sealed class MigrationBatchCoordinator
{
    private readonly object _limitSignalLock = new();
    private readonly DataCopyService _dataCopyService;
    private readonly MigrationThrottleController _throttleController;
    private readonly Func<CancellationToken, Task> _waitForRunPermissionAsync;
    private TaskCompletionSource _limitChanged = CreateSignal();

    public MigrationBatchCoordinator(
        DataCopyService dataCopyService,
        MigrationThrottleController throttleController,
        Func<CancellationToken, Task> waitForRunPermissionAsync)
    {
        _dataCopyService = dataCopyService ?? throw new ArgumentNullException(nameof(dataCopyService));
        _throttleController = throttleController ?? throw new ArgumentNullException(nameof(throttleController));
        _waitForRunPermissionAsync = waitForRunPermissionAsync ??
                                     throw new ArgumentNullException(nameof(waitForRunPermissionAsync));
    }

    /// <summary>
    /// Wakes the scheduler after either the requested mode or CPU-derived effective mode changes.
    /// </summary>
    public void NotifyLimitsChanged()
    {
        TaskCompletionSource signal;
        lock (_limitSignalLock)
        {
            signal = _limitChanged;
            _limitChanged = CreateSignal();
        }

        signal.TrySetResult();
    }

    public async Task<IReadOnlyList<TableCopyResult>> CopyAsync(
        SqlConnectionSettings sourceSettings,
        SqlConnectionSettings targetSettings,
        IReadOnlyCollection<MigrationTableInfo> tables,
        IProgress<MigrationProgress>? progress,
        Func<MigrationTableInfo, TableCopyResult, Task>? tableCompletedAsync,
        Func<MigrationTableInfo, Exception, Task>? tableFailedAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceSettings);
        ArgumentNullException.ThrowIfNull(targetSettings);
        ArgumentNullException.ThrowIfNull(tables);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new Queue<MigrationTableInfo>(tables);
        var active = new Dictionary<Task<TableCopyResult>, MigrationTableInfo>();
        var results = new List<TableCopyResult>(tables.Count);
        Exception? firstFailure = null;

        while (pending.Count > 0 || active.Count > 0)
        {
            var limitChangedTask = CaptureLimitChangedTask();

            if (firstFailure is null && !linkedCancellation.IsCancellationRequested)
            {
                var maximumConcurrency = Math.Max(
                    1,
                    _throttleController.EffectiveProfile.MaxConcurrentTables);

                while (pending.Count > 0 && active.Count < maximumConcurrency)
                {
                    await _waitForRunPermissionAsync(linkedCancellation.Token).ConfigureAwait(false);
                    var table = pending.Dequeue();
                    var tableProgress = new BoundaryProgress<MigrationProgress>(
                        value => progress?.Report(value),
                        _waitForRunPermissionAsync,
                        linkedCancellation.Token);
                    var copyTask = _dataCopyService.CopyTableAsync(
                        sourceSettings,
                        targetSettings,
                        table,
                        _throttleController,
                        tableProgress,
                        linkedCancellation.Token);
                    active.Add(copyTask, table);
                }
            }

            if (active.Count == 0)
            {
                if (firstFailure is not null)
                {
                    ExceptionDispatchInfo.Capture(firstFailure).Throw();
                }

                linkedCancellation.Token.ThrowIfCancellationRequested();
                continue;
            }

            Task<Task<TableCopyResult>> completionTask = Task.WhenAny(active.Keys);
            Task winner = await Task.WhenAny(completionTask, limitChangedTask).ConfigureAwait(false);
            if (ReferenceEquals(winner, limitChangedTask))
            {
                continue;
            }

            Task<TableCopyResult> completedTask = await completionTask.ConfigureAwait(false);
            MigrationTableInfo completedTable = active[completedTask];
            active.Remove(completedTask);

            try
            {
                TableCopyResult result = await completedTask.ConfigureAwait(false);
                results.Add(result);
                if (tableCompletedAsync is not null)
                {
                    await tableCompletedAsync(completedTable, result).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
                linkedCancellation.Cancel();
                if (tableFailedAsync is not null)
                {
                    await tableFailedAsync(completedTable, exception).ConfigureAwait(false);
                }
            }
        }

        if (firstFailure is not null)
        {
            ExceptionDispatchInfo.Capture(firstFailure).Throw();
        }

        return results;
    }

    private Task CaptureLimitChangedTask()
    {
        lock (_limitSignalLock)
        {
            return _limitChanged.Task;
        }
    }

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Reports a committed object/batch and then observes a manual pause at that safe boundary.
    /// </summary>
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
