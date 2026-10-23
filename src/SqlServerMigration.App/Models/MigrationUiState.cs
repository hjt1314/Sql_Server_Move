using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security;

namespace SqlServerMigration.App.Models;

public enum MigrationEndpointRole
{
    Source,
    Target,
}

public enum MigrationUiStage
{
    Connection,
    Preflight,
    Schema,
    Data,
    Validation,
}

public enum MigrationStepStatus
{
    Pending,
    Running,
    Paused,
    Succeeded,
    Failed,
    Cancelled,
}

public enum MigrationLogLevel
{
    Information,
    Success,
    Warning,
    Error,
}

public enum MigrationUiOutcome
{
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record UiSelectionOption<T>(string Label, T Value);

public sealed record SqlEndpointDefinition(
    string RoleName,
    string ServerAddress,
    string DatabaseName,
    SqlAuthenticationMode AuthenticationMode,
    string? UserName);

/// <summary>
/// Owns short-lived password copies used by one UI operation. Callers must dispose it.
/// </summary>
public sealed class MigrationConnectionSecrets : IDisposable
{
    public MigrationConnectionSecrets(SecureString? sourcePassword, SecureString? targetPassword)
    {
        SourcePassword = sourcePassword?.Copy();
        TargetPassword = targetPassword?.Copy();
    }

    public SecureString? SourcePassword { get; }

    public SecureString? TargetPassword { get; }

    public void Dispose()
    {
        SourcePassword?.Dispose();
        TargetPassword?.Dispose();
    }
}

/// <summary>
/// Immutable connection snapshot for a single operation. It never contains a plaintext password.
/// </summary>
public sealed class MigrationConnectionRequest : IDisposable
{
    public MigrationConnectionRequest(
        SqlEndpointDefinition source,
        SqlEndpointDefinition target,
        MigrationConnectionSecrets secrets)
    {
        Source = source;
        Target = target;
        Secrets = secrets;
    }

    public SqlEndpointDefinition Source { get; }

    public SqlEndpointDefinition Target { get; }

    public MigrationConnectionSecrets Secrets { get; }

    public void Dispose() => Secrets.Dispose();
}

public sealed record MigrationStartRequest(
    MigrationConnectionRequest Connections,
    MigrationPerformanceMode PerformanceMode);

public sealed record MigrationUiOperationResult(
    MigrationUiOutcome Outcome,
    string Message)
{
    public bool Succeeded => Outcome == MigrationUiOutcome.Succeeded;
}

public sealed record MigrationUiProgressSnapshot(
    MigrationUiStage Stage,
    MigrationStepStatus StageStatus,
    double OverallProgressPercent,
    double? CpuUsagePercent,
    double? RowsPerSecond,
    TimeSpan? EstimatedRemaining,
    long CompletedRows,
    long? TotalRows,
    string? CurrentObject,
    MigrationPerformanceMode? EffectiveMode,
    MigrationPerformanceMode? RequestedMode,
    bool IsModeChangePending,
    bool IsCpuThrottled,
    string? Message);

public sealed record MigrationLogEntry(
    DateTimeOffset Timestamp,
    MigrationLogLevel Level,
    string Message)
{
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss");

    public string LevelText => Level switch
    {
        MigrationLogLevel.Success => "成功",
        MigrationLogLevel.Warning => "提醒",
        MigrationLogLevel.Error => "错误",
        _ => "信息",
    };

    public string LevelColor => Level switch
    {
        MigrationLogLevel.Success => "#15803D",
        MigrationLogLevel.Warning => "#B45309",
        MigrationLogLevel.Error => "#B91C1C",
        _ => "#475569",
    };
}

public sealed class MigrationUiProgressEventArgs(MigrationUiProgressSnapshot snapshot) : EventArgs
{
    public MigrationUiProgressSnapshot Snapshot { get; } = snapshot;
}

public sealed class MigrationUiLogEventArgs(MigrationLogEntry entry) : EventArgs
{
    public MigrationLogEntry Entry { get; } = entry;
}

/// <summary>
/// Adapter boundary between the window and the real migration services.
/// Implementations must report actual outcomes; the UI has no simulated success fallback.
/// </summary>
public interface IMigrationUiOperations
{
    event EventHandler<MigrationUiProgressEventArgs>? ProgressChanged;

    event EventHandler<MigrationUiLogEventArgs>? LogReceived;

    Task<MigrationUiOperationResult> TestConnectionsAsync(
        MigrationConnectionRequest request,
        CancellationToken cancellationToken);

    Task<MigrationUiOperationResult> RunPreflightAsync(
        MigrationConnectionRequest request,
        CancellationToken cancellationToken);

    Task<MigrationUiOperationResult> StartMigrationAsync(
        MigrationStartRequest request,
        CancellationToken cancellationToken);

    Task<MigrationUiOperationResult> SetPausedAsync(
        bool shouldPause,
        CancellationToken cancellationToken);

    Task<MigrationUiOperationResult> ChangePerformanceModeAsync(
        MigrationPerformanceMode mode,
        CancellationToken cancellationToken);

    Task<MigrationUiOperationResult> CancelAsync(CancellationToken cancellationToken);
}

public sealed class MigrationStepItem : INotifyPropertyChanged
{
    private MigrationStepStatus _status;
    private string _detail;

    public MigrationStepItem(MigrationUiStage stage, string title, string detail)
    {
        Stage = stage;
        Title = title;
        _detail = detail;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MigrationUiStage Stage { get; }

    public string Title { get; }

    public string Detail
    {
        get => _detail;
        private set
        {
            if (_detail == value)
            {
                return;
            }

            _detail = value;
            OnPropertyChanged();
        }
    }

    public MigrationStepStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusGlyph));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(StatusBackground));
        }
    }

    public string StatusText => Status switch
    {
        MigrationStepStatus.Running => "进行中",
        MigrationStepStatus.Paused => "已暂停",
        MigrationStepStatus.Succeeded => "已完成",
        MigrationStepStatus.Failed => "失败",
        MigrationStepStatus.Cancelled => "已取消",
        _ => "等待",
    };

    public string StatusGlyph => Status switch
    {
        MigrationStepStatus.Running => "●",
        MigrationStepStatus.Paused => "Ⅱ",
        MigrationStepStatus.Succeeded => "✓",
        MigrationStepStatus.Failed => "!",
        MigrationStepStatus.Cancelled => "—",
        _ => "○",
    };

    public string StatusColor => Status switch
    {
        MigrationStepStatus.Running => "#2563EB",
        MigrationStepStatus.Paused => "#B45309",
        MigrationStepStatus.Succeeded => "#15803D",
        MigrationStepStatus.Failed => "#B91C1C",
        MigrationStepStatus.Cancelled => "#64748B",
        _ => "#94A3B8",
    };

    public string StatusBackground => Status switch
    {
        MigrationStepStatus.Running => "#EFF6FF",
        MigrationStepStatus.Paused => "#FFF7ED",
        MigrationStepStatus.Succeeded => "#F0FDF4",
        MigrationStepStatus.Failed => "#FEF2F2",
        _ => "#F8FAFC",
    };

    public void SetStatus(MigrationStepStatus status, string? detail = null)
    {
        Status = status;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            Detail = detail;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
