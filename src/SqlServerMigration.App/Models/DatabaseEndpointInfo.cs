namespace SqlServerMigration.App.Models;

/// <summary>
/// 通过只读查询获得的 SQL Server 端点信息。
/// </summary>
public sealed class DatabaseEndpointInfo
{
    public required string ServerName { get; init; }

    public required string DatabaseName { get; init; }

    public required string ProductVersion { get; init; }

    public required string ProductLevel { get; init; }

    public required string Edition { get; init; }

    public int EngineEdition { get; init; }

    public int MajorVersion { get; init; }

    public int CompatibilityLevel { get; init; }

    public required string DatabaseState { get; init; }

    public bool IsReadOnly { get; init; }

    public string? CollationName { get; init; }

    public TimeSpan ConnectionElapsed { get; init; }

    public bool IsSqlServer2019 => MajorVersion == 15;

    public override string ToString() => $"{ServerName} / {DatabaseName} ({ProductVersion}, {Edition})";
}
