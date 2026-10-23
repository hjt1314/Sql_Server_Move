using System.Collections.ObjectModel;

namespace SqlServerMigration.App.Models;

/// <summary>
/// Immutable runtime limits for one migration performance mode.
/// </summary>
public sealed record MigrationPerformanceProfile
{
    private static readonly ReadOnlyCollection<MigrationPerformanceProfile> Profiles =
        Array.AsReadOnly<MigrationPerformanceProfile>(
        [
            new(
                MigrationPerformanceMode.HighSpeed,
                "高速",
                maxConcurrentTables: 2,
                batchSize: 50_000,
                pauseCpuThresholdPercent: 92,
                resumeCpuThresholdPercent: 82,
                maxDegreeOfParallelism: 2,
                interBatchDelay: TimeSpan.Zero),
            new(
                MigrationPerformanceMode.Balanced,
                "平衡",
                maxConcurrentTables: 1,
                batchSize: 25_000,
                pauseCpuThresholdPercent: 82,
                resumeCpuThresholdPercent: 72,
                maxDegreeOfParallelism: 1,
                interBatchDelay: TimeSpan.FromMilliseconds(50)),
            new(
                MigrationPerformanceMode.LowImpact,
                "低影响",
                maxConcurrentTables: 1,
                batchSize: 10_000,
                pauseCpuThresholdPercent: 72,
                resumeCpuThresholdPercent: 62,
                maxDegreeOfParallelism: 1,
                interBatchDelay: TimeSpan.FromMilliseconds(250))
        ]);

    private MigrationPerformanceProfile(
        MigrationPerformanceMode mode,
        string displayName,
        int maxConcurrentTables,
        int batchSize,
        double pauseCpuThresholdPercent,
        double resumeCpuThresholdPercent,
        int maxDegreeOfParallelism,
        TimeSpan interBatchDelay)
    {
        Mode = mode;
        DisplayName = displayName;
        MaxConcurrentTables = maxConcurrentTables;
        BatchSize = batchSize;
        PauseCpuThresholdPercent = pauseCpuThresholdPercent;
        ResumeCpuThresholdPercent = resumeCpuThresholdPercent;
        MaxDegreeOfParallelism = maxDegreeOfParallelism;
        InterBatchDelay = interBatchDelay;
    }

    /// <summary>
    /// Gets all supported profiles in UI display order.
    /// </summary>
    public static IReadOnlyList<MigrationPerformanceProfile> All => Profiles;

    /// <summary>
    /// Gets the mode represented by this profile.
    /// </summary>
    public MigrationPerformanceMode Mode { get; }

    /// <summary>
    /// Gets the localized mode name intended for the desktop UI.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the maximum number of tables that may be copied concurrently.
    /// </summary>
    public int MaxConcurrentTables { get; }

    /// <summary>
    /// Gets the maximum number of source rows requested for one copy batch.
    /// </summary>
    public int BatchSize { get; }

    /// <summary>
    /// Gets the total CPU percentage that must be sustained before new batches pause.
    /// </summary>
    public double PauseCpuThresholdPercent { get; }

    /// <summary>
    /// Gets the total CPU percentage that must be sustained before paused batches resume.
    /// </summary>
    public double ResumeCpuThresholdPercent { get; }

    /// <summary>
    /// Gets the maximum degree of parallelism used when rebuilding indexes.
    /// </summary>
    public int MaxDegreeOfParallelism { get; }

    /// <summary>
    /// Gets the pacing delay applied before each subsequent data batch.
    /// </summary>
    public TimeSpan InterBatchDelay { get; }

    /// <summary>
    /// Resolves the immutable profile for a supported mode.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The supplied mode is not supported.</exception>
    public static MigrationPerformanceProfile For(MigrationPerformanceMode mode) => mode switch
    {
        MigrationPerformanceMode.HighSpeed => Profiles[0],
        MigrationPerformanceMode.Balanced => Profiles[1],
        MigrationPerformanceMode.LowImpact => Profiles[2],
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported migration performance mode.")
    };
}
