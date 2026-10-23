using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using SqlServerMigration.App.Models;

namespace SqlServerMigration.App.Services;

/// <summary>
/// Describes the current CPU throttling decision for UI binding and diagnostics.
/// </summary>
public enum MigrationThrottleState
{
    /// <summary>
    /// Batches may run with the requested profile.
    /// </summary>
    Running,

    /// <summary>
    /// High CPU has been observed but has not yet met the sustained-sample requirement.
    /// </summary>
    ObservingHighCpu,

    /// <summary>
    /// New batches are paused until CPU remains below the resume threshold.
    /// </summary>
    WaitingForCpu,

    /// <summary>
    /// CPU monitoring is unavailable or warming up, so low-impact limits are used explicitly.
    /// </summary>
    CpuMonitorFallback
}

/// <summary>
/// Immutable status snapshot published by <see cref="MigrationThrottleController"/>.
/// </summary>
public sealed record MigrationThrottleStatus(
    long Revision,
    MigrationThrottleState State,
    MigrationPerformanceMode RequestedMode,
    MigrationPerformanceMode EffectiveMode,
    double? CpuUsagePercent,
    CpuMonitorStatus CpuMonitorStatus,
    string Message,
    DateTimeOffset UpdatedAtUtc)
{
    /// <summary>
    /// Gets whether new batches are currently paused for sustained CPU pressure.
    /// </summary>
    public bool IsPaused => State == MigrationThrottleState.WaitingForCpu;

    /// <summary>
    /// Gets whether CPU monitoring has forced an explicit low-impact fallback.
    /// </summary>
    public bool IsFallback => State == MigrationThrottleState.CpuMonitorFallback;
}

/// <summary>
/// Carries an atomic requested-mode change.
/// </summary>
public sealed class MigrationPerformanceModeChangedEventArgs : EventArgs
{
    internal MigrationPerformanceModeChangedEventArgs(
        MigrationPerformanceMode previousMode,
        MigrationPerformanceMode currentMode,
        MigrationPerformanceProfile currentProfile)
    {
        PreviousMode = previousMode;
        CurrentMode = currentMode;
        CurrentProfile = currentProfile;
    }

    public MigrationPerformanceMode PreviousMode { get; }

    public MigrationPerformanceMode CurrentMode { get; }

    public MigrationPerformanceProfile CurrentProfile { get; }
}

/// <summary>
/// Carries a new immutable throttling status snapshot.
/// </summary>
public sealed class MigrationThrottleStatusChangedEventArgs : EventArgs
{
    internal MigrationThrottleStatusChangedEventArgs(
        MigrationThrottleStatus previousStatus,
        MigrationThrottleStatus currentStatus)
    {
        PreviousStatus = previousStatus;
        CurrentStatus = currentStatus;
    }

    public MigrationThrottleStatus PreviousStatus { get; }

    public MigrationThrottleStatus CurrentStatus { get; }
}

/// <summary>
/// Applies runtime-selectable batch profiles and CPU hysteresis before a migration batch starts.
/// </summary>
/// <remarks>
/// A caller must use the profile returned by <see cref="WaitForNextBatchAsync"/> for the whole
/// batch. A mode changed while that batch is running is therefore applied to the next batch.
/// Notifications are raised on the thread that updates the controller; UI consumers should
/// marshal custom event handlers to their dispatcher when necessary.
/// </remarks>
public sealed class MigrationThrottleController : INotifyPropertyChanged
{
    private readonly object _stateLock = new();
    private readonly ISystemCpuMonitor _cpuMonitor;
    private readonly TimeSpan _sampleInterval;
    private readonly int _sustainedSampleCount;
    private readonly SemaphoreSlim _evaluationGate = new(1, 1);

    private int _requestedModeValue;
    private int _modeVersion;
    private bool _isCpuPaused;
    private int _consecutiveHighSamples;
    private int _consecutiveLowSamples;
    private SystemCpuSample? _lastCpuSample;
    private long _lastCpuSampleTimestamp;
    private long _statusRevision;
    private MigrationThrottleStatus _status;

    /// <summary>
    /// Initializes a throttling controller.
    /// </summary>
    /// <param name="cpuMonitor">Total CPU monitor used for gating new batches.</param>
    /// <param name="initialMode">Requested mode for the first batch.</param>
    /// <param name="sampleInterval">Minimum interval between samples used by hysteresis.</param>
    /// <param name="sustainedSampleCount">Consecutive samples required to pause or resume.</param>
    public MigrationThrottleController(
        ISystemCpuMonitor cpuMonitor,
        MigrationPerformanceMode initialMode = MigrationPerformanceMode.LowImpact,
        TimeSpan? sampleInterval = null,
        int sustainedSampleCount = 3)
    {
        _cpuMonitor = cpuMonitor ?? throw new ArgumentNullException(nameof(cpuMonitor));
        _ = MigrationPerformanceProfile.For(initialMode);

        _sampleInterval = sampleInterval ?? TimeSpan.FromSeconds(1);
        if (_sampleInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleInterval), "CPU sample interval must be greater than zero.");
        }

        if (sustainedSampleCount < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sustainedSampleCount),
                "At least two consecutive samples are required so one CPU spike cannot pause migration.");
        }

        _sustainedSampleCount = sustainedSampleCount;
        _requestedModeValue = (int)initialMode;
        _statusRevision = 1;
        _status = new MigrationThrottleStatus(
            _statusRevision,
            MigrationThrottleState.CpuMonitorFallback,
            initialMode,
            MigrationPerformanceMode.LowImpact,
            null,
            CpuMonitorStatus.WarmingUp,
            "等待首个有效 CPU 样本，下一批数据暂时使用低影响模式。",
            DateTimeOffset.UtcNow);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised after the requested mode changes atomically.
    /// </summary>
    public event EventHandler<MigrationPerformanceModeChangedEventArgs>? ModeChanged;

    /// <summary>
    /// Raised when the controller publishes a materially different status snapshot.
    /// </summary>
    public event EventHandler<MigrationThrottleStatusChangedEventArgs>? StatusChanged;

    /// <summary>
    /// Gets the mode requested for the next batch boundary.
    /// </summary>
    public MigrationPerformanceMode RequestedMode =>
        (MigrationPerformanceMode)Volatile.Read(ref _requestedModeValue);

    /// <summary>
    /// Gets the immutable profile requested for the next batch boundary.
    /// </summary>
    public MigrationPerformanceProfile RequestedProfile =>
        MigrationPerformanceProfile.For(RequestedMode);

    /// <summary>
    /// Gets the latest immutable CPU throttling status.
    /// </summary>
    public MigrationThrottleStatus Status => Volatile.Read(ref _status);

    /// <summary>
    /// Gets the profile represented by the latest effective status.
    /// </summary>
    public MigrationPerformanceProfile EffectiveProfile =>
        MigrationPerformanceProfile.For(Status.EffectiveMode);

    /// <summary>
    /// Gets whether new batches are currently paused for CPU pressure.
    /// </summary>
    public bool IsPaused => Status.IsPaused;

    /// <summary>
    /// Gets whether the controller is explicitly using the low-impact fallback.
    /// </summary>
    public bool IsUsingFallback => Status.IsFallback;

    /// <summary>
    /// Requests a mode change. A batch that already captured its profile is not interrupted.
    /// </summary>
    public void SetMode(MigrationPerformanceMode mode)
    {
        var profile = MigrationPerformanceProfile.For(mode);
        MigrationPerformanceMode previousMode;
        MigrationThrottleStatus previousStatus;
        MigrationThrottleStatus currentStatus;

        lock (_stateLock)
        {
            previousMode = (MigrationPerformanceMode)_requestedModeValue;
            if (previousMode == mode)
            {
                return;
            }

            _requestedModeValue = (int)mode;
            _modeVersion++;
            _consecutiveHighSamples = 0;
            _consecutiveLowSamples = 0;

            var oldStatus = _status;
            var monitorReady = oldStatus.CpuMonitorStatus == CpuMonitorStatus.Available;
            var state = _isCpuPaused
                ? MigrationThrottleState.WaitingForCpu
                : monitorReady
                    ? MigrationThrottleState.Running
                    : MigrationThrottleState.CpuMonitorFallback;
            var effectiveMode = monitorReady && !_isCpuPaused
                ? mode
                : MigrationPerformanceMode.LowImpact;
            var message = _isCpuPaused
                ? $"已切换为{profile.DisplayName}模式；正在等待 CPU 持续低于 {profile.ResumeCpuThresholdPercent:0}%。"
                : monitorReady
                    ? $"已切换为{profile.DisplayName}模式，将从下一批数据开始生效。"
                    : $"已选择{profile.DisplayName}模式；CPU 监控尚未可用，下一批保守使用低影响模式。";

            previousStatus = oldStatus;
            currentStatus = CreateStatusLocked(
                state,
                mode,
                effectiveMode,
                oldStatus.CpuUsagePercent,
                oldStatus.CpuMonitorStatus,
                message);
            _status = currentStatus;
        }

        OnPropertyChanged(nameof(RequestedMode));
        OnPropertyChanged(nameof(RequestedProfile));
        RaiseStatusChanged(previousStatus, currentStatus);
        ModeChanged?.Invoke(
            this,
            new MigrationPerformanceModeChangedEventArgs(previousMode, mode, profile));
    }

    /// <summary>
    /// Waits until the next batch is permitted and returns its immutable effective profile.
    /// </summary>
    /// <remarks>
    /// The method serializes CPU decisions across concurrent table workers so sustained sample
    /// counters cannot be advanced twice by one observation. Cancellation stops both CPU waiting
    /// and inter-batch pacing without changing the requested mode.
    /// </remarks>
    public async Task<MigrationPerformanceProfile> WaitForNextBatchAsync(
        CancellationToken cancellationToken = default)
    {
        await _evaluationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var modeCapture = CaptureRequestedProfile();
                var sampleSnapshot = await GetCpuSampleAsync(cancellationToken).ConfigureAwait(false);
                var decision = Evaluate(modeCapture, sampleSnapshot);

                if (decision.PreviousStatus is not null && decision.CurrentStatus is not null)
                {
                    RaiseStatusChanged(decision.PreviousStatus, decision.CurrentStatus);
                }

                if (decision.ShouldRetryImmediately)
                {
                    continue;
                }

                if (decision.ShouldWaitForCpu)
                {
                    await Task.Delay(decision.RetryAfter, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (decision.EffectiveProfile.InterBatchDelay > TimeSpan.Zero)
                {
                    await Task.Delay(
                            decision.EffectiveProfile.InterBatchDelay,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (modeCapture.Version != Volatile.Read(ref _modeVersion))
                {
                    continue;
                }

                return decision.EffectiveProfile;
            }
        }
        finally
        {
            _evaluationGate.Release();
        }
    }

    private RequestedProfileCapture CaptureRequestedProfile()
    {
        lock (_stateLock)
        {
            var mode = (MigrationPerformanceMode)_requestedModeValue;
            return new RequestedProfileCapture(_modeVersion, MigrationPerformanceProfile.For(mode));
        }
    }

    private async ValueTask<CpuSampleSnapshot> GetCpuSampleAsync(CancellationToken cancellationToken)
    {
        SystemCpuSample? cachedSample;
        TimeSpan retryAfter;

        lock (_stateLock)
        {
            cachedSample = _lastCpuSample;
            if (cachedSample.HasValue)
            {
                var elapsed = Stopwatch.GetElapsedTime(_lastCpuSampleTimestamp);
                if (elapsed < _sampleInterval)
                {
                    retryAfter = _sampleInterval - elapsed;
                    return new CpuSampleSnapshot(cachedSample.Value, false, retryAfter);
                }
            }
        }

        SystemCpuSample sample;
        try
        {
            sample = await _cpuMonitor.SampleAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            sample = new SystemCpuSample(
                CpuMonitorStatus.Unavailable,
                null,
                $"CPU 监控器采样失败：{exception.Message}",
                DateTimeOffset.UtcNow);
        }

        lock (_stateLock)
        {
            _lastCpuSample = sample;
            _lastCpuSampleTimestamp = Stopwatch.GetTimestamp();
        }

        return new CpuSampleSnapshot(sample, true, _sampleInterval);
    }

    private ThrottleDecision Evaluate(
        RequestedProfileCapture modeCapture,
        CpuSampleSnapshot sampleSnapshot)
    {
        MigrationThrottleStatus? previousStatus = null;
        MigrationThrottleStatus? currentStatus = null;
        var shouldWaitForCpu = false;
        var shouldRetryImmediately = false;
        var effectiveProfile = modeCapture.Profile;

        lock (_stateLock)
        {
            if (modeCapture.Version != _modeVersion)
            {
                shouldRetryImmediately = true;
            }
            else if (!sampleSnapshot.Sample.IsAvailable)
            {
                _isCpuPaused = false;
                _consecutiveHighSamples = 0;
                _consecutiveLowSamples = 0;
                effectiveProfile = MigrationPerformanceProfile.For(MigrationPerformanceMode.LowImpact);

                TrySetStatusLocked(
                    MigrationThrottleState.CpuMonitorFallback,
                    modeCapture.Profile.Mode,
                    MigrationPerformanceMode.LowImpact,
                    null,
                    sampleSnapshot.Sample.Status,
                    $"{sampleSnapshot.Sample.Message} 已明确降级为低影响模式。",
                    out previousStatus,
                    out currentStatus);
            }
            else if (!sampleSnapshot.IsFresh)
            {
                shouldWaitForCpu = _isCpuPaused;
            }
            else
            {
                var cpuUsage = Math.Clamp(sampleSnapshot.Sample.TotalCpuUsagePercent!.Value, 0d, 100d);
                EvaluateFreshCpuSampleLocked(
                    modeCapture.Profile,
                    cpuUsage,
                    out shouldWaitForCpu,
                    out previousStatus,
                    out currentStatus);
            }
        }

        return new ThrottleDecision(
            shouldWaitForCpu,
            shouldRetryImmediately,
            effectiveProfile,
            sampleSnapshot.RetryAfter,
            previousStatus,
            currentStatus);
    }

    private void EvaluateFreshCpuSampleLocked(
        MigrationPerformanceProfile profile,
        double cpuUsage,
        out bool shouldWaitForCpu,
        out MigrationThrottleStatus? previousStatus,
        out MigrationThrottleStatus? currentStatus)
    {
        shouldWaitForCpu = false;

        if (_isCpuPaused)
        {
            _consecutiveHighSamples = 0;
            _consecutiveLowSamples = cpuUsage <= profile.ResumeCpuThresholdPercent
                ? _consecutiveLowSamples + 1
                : 0;

            if (_consecutiveLowSamples >= _sustainedSampleCount)
            {
                _isCpuPaused = false;
                _consecutiveLowSamples = 0;
                TrySetStatusLocked(
                    MigrationThrottleState.Running,
                    profile.Mode,
                    profile.Mode,
                    cpuUsage,
                    CpuMonitorStatus.Available,
                    $"CPU 已连续 {_sustainedSampleCount} 次低于 {profile.ResumeCpuThresholdPercent:0}%，恢复{profile.DisplayName}模式。",
                    out previousStatus,
                    out currentStatus);
                return;
            }

            shouldWaitForCpu = true;
            TrySetStatusLocked(
                MigrationThrottleState.WaitingForCpu,
                profile.Mode,
                MigrationPerformanceMode.LowImpact,
                cpuUsage,
                CpuMonitorStatus.Available,
                $"CPU {cpuUsage:0.0}%，暂停新批次；需连续 {_sustainedSampleCount} 次低于 {profile.ResumeCpuThresholdPercent:0}% 后恢复（已达 {_consecutiveLowSamples} 次）。",
                out previousStatus,
                out currentStatus);
            return;
        }

        _consecutiveLowSamples = 0;
        _consecutiveHighSamples = cpuUsage >= profile.PauseCpuThresholdPercent
            ? _consecutiveHighSamples + 1
            : 0;

        if (_consecutiveHighSamples >= _sustainedSampleCount)
        {
            _isCpuPaused = true;
            shouldWaitForCpu = true;
            TrySetStatusLocked(
                MigrationThrottleState.WaitingForCpu,
                profile.Mode,
                MigrationPerformanceMode.LowImpact,
                cpuUsage,
                CpuMonitorStatus.Available,
                $"CPU 已连续 {_sustainedSampleCount} 次达到 {profile.PauseCpuThresholdPercent:0}%，暂停启动新批次。",
                out previousStatus,
                out currentStatus);
            return;
        }

        if (_consecutiveHighSamples > 0)
        {
            TrySetStatusLocked(
                MigrationThrottleState.ObservingHighCpu,
                profile.Mode,
                profile.Mode,
                cpuUsage,
                CpuMonitorStatus.Available,
                $"CPU {cpuUsage:0.0}% 达到暂停阈值，正在确认是否持续（{_consecutiveHighSamples}/{_sustainedSampleCount}）。",
                out previousStatus,
                out currentStatus);
            return;
        }

        TrySetStatusLocked(
            MigrationThrottleState.Running,
            profile.Mode,
            profile.Mode,
            cpuUsage,
            CpuMonitorStatus.Available,
            $"CPU {cpuUsage:0.0}%，{profile.DisplayName}模式正常运行。",
            out previousStatus,
            out currentStatus);
    }

    private bool TrySetStatusLocked(
        MigrationThrottleState state,
        MigrationPerformanceMode requestedMode,
        MigrationPerformanceMode effectiveMode,
        double? cpuUsagePercent,
        CpuMonitorStatus cpuMonitorStatus,
        string message,
        out MigrationThrottleStatus? previousStatus,
        out MigrationThrottleStatus? currentStatus)
    {
        var existing = _status;
        if (existing.State == state &&
            existing.RequestedMode == requestedMode &&
            existing.EffectiveMode == effectiveMode &&
            existing.CpuUsagePercent == cpuUsagePercent &&
            existing.CpuMonitorStatus == cpuMonitorStatus &&
            string.Equals(existing.Message, message, StringComparison.Ordinal))
        {
            previousStatus = null;
            currentStatus = null;
            return false;
        }

        previousStatus = existing;
        currentStatus = CreateStatusLocked(
            state,
            requestedMode,
            effectiveMode,
            cpuUsagePercent,
            cpuMonitorStatus,
            message);
        _status = currentStatus;
        return true;
    }

    private MigrationThrottleStatus CreateStatusLocked(
        MigrationThrottleState state,
        MigrationPerformanceMode requestedMode,
        MigrationPerformanceMode effectiveMode,
        double? cpuUsagePercent,
        CpuMonitorStatus cpuMonitorStatus,
        string message) =>
        new(
            ++_statusRevision,
            state,
            requestedMode,
            effectiveMode,
            cpuUsagePercent,
            cpuMonitorStatus,
            message,
            DateTimeOffset.UtcNow);

    private void RaiseStatusChanged(
        MigrationThrottleStatus previousStatus,
        MigrationThrottleStatus currentStatus)
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(EffectiveProfile));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsUsingFallback));
        StatusChanged?.Invoke(
            this,
            new MigrationThrottleStatusChangedEventArgs(previousStatus, currentStatus));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private readonly record struct RequestedProfileCapture(
        int Version,
        MigrationPerformanceProfile Profile);

    private readonly record struct CpuSampleSnapshot(
        SystemCpuSample Sample,
        bool IsFresh,
        TimeSpan RetryAfter);

    private readonly record struct ThrottleDecision(
        bool ShouldWaitForCpu,
        bool ShouldRetryImmediately,
        MigrationPerformanceProfile EffectiveProfile,
        TimeSpan RetryAfter,
        MigrationThrottleStatus? PreviousStatus,
        MigrationThrottleStatus? CurrentStatus);
}
