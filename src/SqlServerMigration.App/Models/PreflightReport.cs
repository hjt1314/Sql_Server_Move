namespace SqlServerMigration.App.Models;

public enum PreflightIssueSeverity
{
    Information,
    Warning,
    Blocking
}

public sealed class PreflightIssue
{
    public required string Code { get; init; }

    public required PreflightIssueSeverity Severity { get; init; }

    public required string Message { get; init; }

    public string? ObjectName { get; init; }
}

/// <summary>
/// 只读预检结果。只有 <see cref="CanMigrate"/> 为 true 时才应允许后续写入。
/// </summary>
public sealed class PreflightReport
{
    public required DatabaseEndpointInfo Source { get; init; }

    public required DatabaseEndpointInfo Target { get; init; }

    public IReadOnlyList<MigrationTableInfo> Tables { get; init; } = Array.Empty<MigrationTableInfo>();

    public IReadOnlyList<PreflightIssue> Issues { get; init; } = Array.Empty<PreflightIssue>();

    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.Now;

    public bool CanMigrate => Issues.All(issue => issue.Severity != PreflightIssueSeverity.Blocking);
}
