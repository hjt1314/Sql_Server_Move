namespace SqlServerMigration.App.Services;

/// <summary>
/// Describes whether a total CPU sample can be used for migration throttling.
/// </summary>
public enum CpuMonitorStatus
{
    /// <summary>
    /// A usable total CPU percentage is available.
    /// </summary>
    Available,

    /// <summary>
    /// The monitor is collecting the baseline needed to calculate CPU usage.
    /// </summary>
    WarmingUp,

    /// <summary>
    /// Total CPU usage cannot currently be read.
    /// </summary>
    Unavailable
}

/// <summary>
/// Represents one total CPU observation and its availability state.
/// </summary>
/// <param name="Status">Availability state of the observation.</param>
/// <param name="TotalCpuUsagePercent">Total host CPU usage, or <see langword="null"/> when unavailable.</param>
/// <param name="Message">Human-readable status suitable for diagnostics.</param>
/// <param name="SampledAtUtc">UTC time at which the sample was produced.</param>
public readonly record struct SystemCpuSample(
    CpuMonitorStatus Status,
    double? TotalCpuUsagePercent,
    string Message,
    DateTimeOffset SampledAtUtc)
{
    /// <summary>
    /// Gets whether this sample contains a usable total CPU percentage.
    /// </summary>
    public bool IsAvailable => Status == CpuMonitorStatus.Available && TotalCpuUsagePercent.HasValue;
}

/// <summary>
/// Supplies total host CPU observations without adding a platform-monitoring package.
/// </summary>
public interface ISystemCpuMonitor
{
    /// <summary>
    /// Reads the next CPU observation.
    /// </summary>
    /// <param name="cancellationToken">Cancels the observation before native work starts.</param>
    /// <returns>An explicit available, warming-up, or unavailable sample.</returns>
    ValueTask<SystemCpuSample> SampleAsync(CancellationToken cancellationToken = default);
}
