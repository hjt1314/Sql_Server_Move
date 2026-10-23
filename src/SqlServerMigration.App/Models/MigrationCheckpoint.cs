namespace SqlServerMigration.App.Models;

public enum MigrationTaskState
{
    Draft,
    PreflightPassed,
    SchemaCreated,
    Copying,
    Validating,
    Completed,
    Paused,
    Failed,
    ResultUnknown,
    Cancelled
}

public enum TableCopyState
{
    Pending,
    Copying,
    Completed,
    Failed,
    ResultUnknown
}

public sealed class MigrationCheckpoint
{
    public required Guid TaskId { get; init; }

    public required string PlanFingerprint { get; init; }

    public MigrationTaskState State { get; set; } = MigrationTaskState.Draft;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? LastError { get; set; }

    public Dictionary<string, TableCheckpoint> Tables { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TableCheckpoint
{
    public required string SchemaName { get; init; }

    public required string TableName { get; init; }

    public TableCopyState State { get; set; } = TableCopyState.Pending;

    public long ConfirmedRowsCopied { get; set; }

    public long CompletedBatches { get; set; }

    public string? LastConfirmedKey { get; set; }

    public string? QuickChecksum { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
