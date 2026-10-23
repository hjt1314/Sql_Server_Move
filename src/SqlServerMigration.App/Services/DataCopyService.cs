using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SqlServerMigration.App.Models;

namespace SqlServerMigration.App.Services;

public sealed class TableCopyResult
{
    public required string TableName { get; init; }

    public long RowsCopied { get; init; }

    public int BatchesCompleted { get; init; }

    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// 使用流式 SqlDataReader 和分批 SqlBulkCopy 复制单表数据。
/// </summary>
public sealed class DataCopyService
{
    private readonly ConnectionService _connectionService;

    public DataCopyService(ConnectionService? connectionService = null)
    {
        _connectionService = connectionService ?? new ConnectionService();
    }

    public async Task<TableCopyResult> CopyTableAsync(
        SqlConnectionSettings sourceSettings,
        SqlConnectionSettings targetSettings,
        MigrationTableInfo table,
        MigrationThrottleController throttleController,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceSettings);
        ArgumentNullException.ThrowIfNull(targetSettings);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(throttleController);

        if (SameConfiguredDatabase(sourceSettings, targetSettings))
        {
            throw new InvalidOperationException("源端和目标端配置相同，禁止原地复制。");
        }

        var columns = table.CopyableColumns.OrderBy(column => column.Ordinal).ToArray();
        if (columns.Length == 0 && table.EstimatedRowCount > 0)
        {
            throw new InvalidOperationException($"表 {table.DisplayName} 有数据，但没有可写入的非计算、非 rowversion 列。");
        }

        await using var sourceConnection = await _connectionService.OpenConnectionAsync(sourceSettings, cancellationToken).ConfigureAwait(false);
        await using var targetConnection = await _connectionService.OpenConnectionAsync(targetSettings, cancellationToken).ConfigureAwait(false);
        await EnsureTargetTableEmptyAsync(targetConnection, targetSettings, table, cancellationToken).ConfigureAwait(false);

        if (columns.Length == 0)
        {
            return new TableCopyResult
            {
                TableName = table.DisplayName,
                RowsCopied = 0,
                BatchesCompleted = 0,
                Elapsed = TimeSpan.Zero
            };
        }

        var quotedColumns = string.Join(", ", columns.Select(column => QuoteIdentifier(column.Name)));
        var selectSql = $"SELECT {quotedColumns} FROM {QuoteMultipartName(table.SchemaName, table.TableName)};";
        await using var selectCommand = new SqlCommand(selectSql, sourceConnection)
        {
            CommandTimeout = sourceSettings.CommandTimeoutSeconds
        };
        await using var sourceReader = await selectCommand.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
            cancellationToken).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        long copiedRows = 0;
        var completedBatches = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = await throttleController.WaitForNextBatchAsync(cancellationToken).ConfigureAwait(false);
            var batchReader = new LimitedBatchDataReader(sourceReader, profile.BatchSize);

            var options = SqlBulkCopyOptions.KeepIdentity |
                          SqlBulkCopyOptions.KeepNulls |
                          SqlBulkCopyOptions.UseInternalTransaction;
            using var bulkCopy = new SqlBulkCopy(targetConnection, options, externalTransaction: null)
            {
                DestinationTableName = QuoteMultipartName(table.SchemaName, table.TableName),
                BatchSize = profile.BatchSize,
                BulkCopyTimeout = targetSettings.CommandTimeoutSeconds,
                EnableStreaming = true
            };

            foreach (var column in columns)
            {
                bulkCopy.ColumnMappings.Add(column.Name, column.Name);
            }

            await bulkCopy.WriteToServerAsync(batchReader, cancellationToken).ConfigureAwait(false);
            if (batchReader.RowsRead == 0)
            {
                break;
            }

            copiedRows += batchReader.RowsRead;
            completedBatches++;
            var rowsPerSecond = stopwatch.Elapsed.TotalSeconds > 0
                ? copiedRows / stopwatch.Elapsed.TotalSeconds
                : 0;
            TimeSpan? remaining = null;
            if (table.EstimatedRowCount > copiedRows && rowsPerSecond > 0)
            {
                remaining = TimeSpan.FromSeconds((table.EstimatedRowCount - copiedRows) / rowsPerSecond);
            }

            progress?.Report(new MigrationProgress
            {
                Stage = MigrationStage.DataCopy,
                CurrentObject = table.DisplayName,
                Message = $"已完成 {completedBatches} 个批次，当前批次采用{profile.DisplayName}模式。",
                RowsProcessed = copiedRows,
                TotalRows = table.EstimatedRowCount,
                RowsPerSecond = rowsPerSecond,
                EstimatedRemaining = remaining
            });
        }

        stopwatch.Stop();
        return new TableCopyResult
        {
            TableName = table.DisplayName,
            RowsCopied = copiedRows,
            BatchesCompleted = completedBatches,
            Elapsed = stopwatch.Elapsed
        };
    }

    private static async Task EnsureTargetTableEmptyAsync(
        SqlConnection targetConnection,
        SqlConnectionSettings targetSettings,
        MigrationTableInfo table,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT TOP (1) CONVERT(int, 1) FROM {QuoteMultipartName(table.SchemaName, table.TableName)};";
        await using var command = new SqlCommand(sql, targetConnection)
        {
            CommandTimeout = targetSettings.CommandTimeoutSeconds
        };
        var firstRow = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (firstRow is not null)
        {
            throw new InvalidOperationException($"目标表 {table.DisplayName} 已有数据。为避免重复或覆盖，本工具不会自动追加、TRUNCATE 或重试。");
        }
    }

    private static bool SameConfiguredDatabase(SqlConnectionSettings source, SqlConnectionSettings target) =>
        string.Equals(source.Server.Trim(), target.Server.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(source.Database.Trim(), target.Database.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    internal static string QuoteMultipartName(string schemaName, string objectName) =>
        $"{QuoteIdentifier(schemaName)}.{QuoteIdentifier(objectName)}";

    /// <summary>
    /// 将单个源端 reader 切成可独立提交的批次，不缓存业务行。
    /// </summary>
    private sealed class LimitedBatchDataReader : DbDataReader
    {
        private readonly DbDataReader _inner;
        private readonly int _maximumRows;

        public LimitedBatchDataReader(DbDataReader inner, int maximumRows)
        {
            _inner = inner;
            _maximumRows = maximumRows > 0
                ? maximumRows
                : throw new ArgumentOutOfRangeException(nameof(maximumRows));
        }

        public int RowsRead { get; private set; }

        public override bool Read()
        {
            if (RowsRead >= _maximumRows || !_inner.Read())
            {
                return false;
            }

            RowsRead++;
            return true;
        }

        public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            if (RowsRead >= _maximumRows || !await _inner.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            RowsRead++;
            return true;
        }

        public override bool NextResult() => false;

        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public override object this[int ordinal] => _inner[ordinal];

        public override object this[string name] => _inner[name];

        public override int Depth => _inner.Depth;

        public override int FieldCount => _inner.FieldCount;

        public override bool HasRows => _inner.HasRows;

        public override bool IsClosed => _inner.IsClosed;

        public override int RecordsAffected => _inner.RecordsAffected;

        public override int VisibleFieldCount => _inner.VisibleFieldCount;

        public override bool GetBoolean(int ordinal) => _inner.GetBoolean(ordinal);

        public override byte GetByte(int ordinal) => _inner.GetByte(ordinal);

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
            _inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

        public override char GetChar(int ordinal) => _inner.GetChar(ordinal);

        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
            _inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);

        public override string GetDataTypeName(int ordinal) => _inner.GetDataTypeName(ordinal);

        public override DateTime GetDateTime(int ordinal) => _inner.GetDateTime(ordinal);

        public override decimal GetDecimal(int ordinal) => _inner.GetDecimal(ordinal);

        public override double GetDouble(int ordinal) => _inner.GetDouble(ordinal);

        public override IEnumerator GetEnumerator() => ((IEnumerable)_inner).GetEnumerator();

        public override Type GetFieldType(int ordinal) => _inner.GetFieldType(ordinal);

        public override float GetFloat(int ordinal) => _inner.GetFloat(ordinal);

        public override Guid GetGuid(int ordinal) => _inner.GetGuid(ordinal);

        public override short GetInt16(int ordinal) => _inner.GetInt16(ordinal);

        public override int GetInt32(int ordinal) => _inner.GetInt32(ordinal);

        public override long GetInt64(int ordinal) => _inner.GetInt64(ordinal);

        public override string GetName(int ordinal) => _inner.GetName(ordinal);

        public override int GetOrdinal(string name) => _inner.GetOrdinal(name);

        public override DataTable? GetSchemaTable() => _inner.GetSchemaTable();

        public override string GetString(int ordinal) => _inner.GetString(ordinal);

        public override object GetValue(int ordinal) => _inner.GetValue(ordinal);

        public override int GetValues(object[] values) => _inner.GetValues(values);

        public override bool IsDBNull(int ordinal) => _inner.IsDBNull(ordinal);

        public override T GetFieldValue<T>(int ordinal) => _inner.GetFieldValue<T>(ordinal);

        public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) =>
            _inner.GetFieldValueAsync<T>(ordinal, cancellationToken);

        public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) =>
            _inner.IsDBNullAsync(ordinal, cancellationToken);

        public override void Close()
        {
            // SqlBulkCopy 结束单个批次时不得关闭共享的源端 reader。
        }
    }
}
