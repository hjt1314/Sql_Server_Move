using Microsoft.Data.SqlClient;
using SqlServerMigration.App.Models;

namespace SqlServerMigration.App.Services;

public enum TableValidationStatus
{
    Passed,
    Failed,
    QuickChecksumUnavailable
}

public sealed class TableValidationResult
{
    public required string TableName { get; init; }

    public long SourceRowCount { get; init; }

    public long TargetRowCount { get; init; }

    public int? SourceQuickChecksum { get; init; }

    public int? TargetQuickChecksum { get; init; }

    public bool QuickChecksumRequested { get; init; }

    /// <summary>
    /// CHECKSUM_AGG(BINARY_CHECKSUM(...)) 只是快速抽查，可能碰撞，不是严格哈希。
    /// </summary>
    public bool IsStrictHash => false;

    public required TableValidationStatus Status { get; init; }

    public string? Detail { get; init; }

    public bool RowCountsMatch => SourceRowCount == TargetRowCount;
}

public sealed class ValidationReport
{
    public IReadOnlyList<TableValidationResult> Tables { get; init; } = Array.Empty<TableValidationResult>();

    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.Now;

    public bool IsSuccessful => Tables.All(table => table.Status != TableValidationStatus.Failed);

    public bool UsesStrictHash => false;
}

/// <summary>
/// 执行逐表 COUNT_BIG 和可选快速校验。快速校验不能作为密码学严格一致性证明。
/// </summary>
public sealed class ValidationService
{
    private readonly ConnectionService _connectionService;

    public ValidationService(ConnectionService? connectionService = null)
    {
        _connectionService = connectionService ?? new ConnectionService();
    }

    public async Task<ValidationReport> ValidateAsync(
        SqlConnectionSettings sourceSettings,
        SqlConnectionSettings targetSettings,
        IReadOnlyCollection<MigrationTableInfo> tables,
        bool includeQuickChecksum = true,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceSettings);
        ArgumentNullException.ThrowIfNull(targetSettings);
        ArgumentNullException.ThrowIfNull(tables);

        await using var sourceConnection = await _connectionService.OpenConnectionAsync(sourceSettings, cancellationToken).ConfigureAwait(false);
        await using var targetConnection = await _connectionService.OpenConnectionAsync(targetSettings, cancellationToken).ConfigureAwait(false);
        var results = new List<TableValidationResult>(tables.Count);
        var completed = 0;

        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceCount = await CountRowsAsync(sourceConnection, sourceSettings, table, cancellationToken).ConfigureAwait(false);
            var targetCount = await CountRowsAsync(targetConnection, targetSettings, table, cancellationToken).ConfigureAwait(false);

            int? sourceChecksum = null;
            int? targetChecksum = null;
            string? checksumError = null;
            if (includeQuickChecksum && table.CopyableColumns.Count > 0)
            {
                try
                {
                    sourceChecksum = await QuickChecksumAsync(sourceConnection, sourceSettings, table, cancellationToken).ConfigureAwait(false);
                    targetChecksum = await QuickChecksumAsync(targetConnection, targetSettings, table, cancellationToken).ConfigureAwait(false);
                }
                catch (SqlException exception) when (exception.Number is 8116 or 8117)
                {
                    checksumError = $"快速校验不适用于该表的列类型（SQL {exception.Number}）。";
                }
            }

            var countsMatch = sourceCount == targetCount;
            var checksumAvailable = !includeQuickChecksum || table.CopyableColumns.Count == 0 || checksumError is null;
            var checksumMatches = !includeQuickChecksum || table.CopyableColumns.Count == 0 ||
                                  (checksumError is null && sourceChecksum == targetChecksum);
            var status = !countsMatch || !checksumMatches
                ? TableValidationStatus.Failed
                : checksumAvailable
                    ? TableValidationStatus.Passed
                    : TableValidationStatus.QuickChecksumUnavailable;

            // 校验不可用不应被伪装成失败或严格哈希通过。
            if (countsMatch && checksumError is not null)
            {
                status = TableValidationStatus.QuickChecksumUnavailable;
            }

            results.Add(new TableValidationResult
            {
                TableName = table.DisplayName,
                SourceRowCount = sourceCount,
                TargetRowCount = targetCount,
                SourceQuickChecksum = sourceChecksum,
                TargetQuickChecksum = targetChecksum,
                QuickChecksumRequested = includeQuickChecksum,
                Status = status,
                Detail = checksumError ?? BuildDetail(countsMatch, checksumMatches, includeQuickChecksum)
            });

            completed++;
            progress?.Report(new MigrationProgress
            {
                Stage = MigrationStage.Validation,
                CurrentObject = table.DisplayName,
                Message = status switch
                {
                    TableValidationStatus.Passed => "行数与快速校验值一致（快速校验非严格哈希）。",
                    TableValidationStatus.QuickChecksumUnavailable => "行数一致，快速校验不可用。",
                    _ => "行数或快速校验值不一致。"
                },
                ObjectsCompleted = completed,
                ObjectsTotal = tables.Count
            });
        }

        return new ValidationReport
        {
            Tables = results,
            CompletedAt = DateTimeOffset.Now
        };
    }

    private static async Task<long> CountRowsAsync(
        SqlConnection connection,
        SqlConnectionSettings settings,
        MigrationTableInfo table,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT COUNT_BIG(*) FROM {DataCopyService.QuoteMultipartName(table.SchemaName, table.TableName)};";
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = settings.CommandTimeoutSeconds
        };
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int?> QuickChecksumAsync(
        SqlConnection connection,
        SqlConnectionSettings settings,
        MigrationTableInfo table,
        CancellationToken cancellationToken)
    {
        var columns = string.Join(", ", table.CopyableColumns
            .OrderBy(column => column.Ordinal)
            .Select(column => DataCopyService.QuoteIdentifier(column.Name)));
        var sql = $"SELECT CHECKSUM_AGG(BINARY_CHECKSUM({columns})) FROM {DataCopyService.QuoteMultipartName(table.SchemaName, table.TableName)};";
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = settings.CommandTimeoutSeconds
        };
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string BuildDetail(bool countsMatch, bool checksumMatches, bool checksumRequested)
    {
        if (!countsMatch)
        {
            return "源端与目标端 COUNT_BIG 不一致。";
        }

        if (checksumRequested && !checksumMatches)
        {
            return "COUNT_BIG 一致，但 CHECKSUM_AGG(BINARY_CHECKSUM(...)) 不一致。";
        }

        return checksumRequested
            ? "COUNT_BIG 和快速校验值一致；快速校验可能碰撞，不是严格哈希。"
            : "COUNT_BIG 一致；未执行快速校验。";
    }
}
