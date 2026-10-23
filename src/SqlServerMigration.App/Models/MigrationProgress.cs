namespace SqlServerMigration.App.Models;

public enum MigrationStage
{
    Preflight,
    SchemaPreview,
    SchemaApply,
    DataCopy,
    Validation,
    Completed
}

/// <summary>
/// 不包含任何业务行内容的迁移进度快照。
/// </summary>
public sealed class MigrationProgress
{
    public required MigrationStage Stage { get; init; }

    public string? CurrentObject { get; init; }

    public string? Message { get; init; }

    public long RowsProcessed { get; init; }

    public long? TotalRows { get; init; }

    public int ObjectsCompleted { get; init; }

    public int ObjectsTotal { get; init; }

    public double RowsPerSecond { get; init; }

    public TimeSpan? EstimatedRemaining { get; init; }
}
