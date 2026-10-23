namespace SqlServerMigration.App.Models;

/// <summary>
/// Defines the performance policy used by subsequent migration batches.
/// </summary>
public enum MigrationPerformanceMode
{
    /// <summary>
    /// Prioritizes migration throughput when enough CPU capacity is available.
    /// </summary>
    HighSpeed,

    /// <summary>
    /// Balances migration throughput with the workload already running on the server.
    /// </summary>
    Balanced,

    /// <summary>
    /// Minimizes additional pressure on the SQL Server host.
    /// </summary>
    LowImpact
}
