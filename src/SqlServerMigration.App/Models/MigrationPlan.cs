namespace SqlServerMigration.App.Models;

public sealed class MigrationPlan
{
    public Guid TaskId { get; init; } = Guid.NewGuid();

    public required SqlConnectionSettings Source { get; init; }

    public required SqlConnectionSettings Target { get; init; }

    public required string TargetDatabaseName { get; init; }

    public required string OutputDirectory { get; init; }

    public MigrationPerformanceMode PerformanceMode { get; init; } = MigrationPerformanceMode.LowImpact;

    public bool CreateNewTargetDatabase { get; init; } = true;

    public bool IncludeProgrammableObjects { get; init; } = true;
}
