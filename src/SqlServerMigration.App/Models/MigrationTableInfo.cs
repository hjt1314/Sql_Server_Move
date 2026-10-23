namespace SqlServerMigration.App.Models;

public sealed class MigrationColumnInfo
{
    public required string Name { get; init; }

    public required string SqlTypeName { get; init; }

    public int Ordinal { get; init; }

    public bool IsNullable { get; init; }

    public bool IsIdentity { get; init; }

    public bool IsComputed { get; init; }

    public bool IsRowVersion { get; init; }

    public int GeneratedAlwaysType { get; init; }

    public bool CanCopy => !IsComputed && !IsRowVersion && GeneratedAlwaysType == 0;
}

/// <summary>
/// 预检阶段确认的常规用户表及其可迁移列。
/// </summary>
public sealed class MigrationTableInfo
{
    public required string SchemaName { get; init; }

    public required string TableName { get; init; }

    public long EstimatedRowCount { get; init; }

    public IReadOnlyList<MigrationColumnInfo> Columns { get; init; } = Array.Empty<MigrationColumnInfo>();

    public IReadOnlyList<MigrationColumnInfo> CopyableColumns => Columns.Where(column => column.CanCopy).ToArray();

    public bool HasIdentityColumn => CopyableColumns.Any(column => column.IsIdentity);

    public string DisplayName => $"{SchemaName}.{TableName}";

    public override string ToString() => DisplayName;
}
