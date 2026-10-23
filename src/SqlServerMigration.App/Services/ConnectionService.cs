using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SqlServerMigration.App.Models;

namespace SqlServerMigration.App.Services;

public sealed class ConnectionService
{
    /// <summary>
    /// 打开由调用方负责释放的数据库连接。
    /// </summary>
    public async Task<SqlConnection> OpenConnectionAsync(
        SqlConnectionSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var connection = new SqlConnection(settings.BuildConnectionString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 打开连接并通过只读查询返回实例与当前数据库信息。
    /// </summary>
    public async Task<DatabaseEndpointInfo> TestConnectionAsync(
        SqlConnectionSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var stopwatch = Stopwatch.StartNew();

        await using var connection = await OpenConnectionAsync(settings, cancellationToken).ConfigureAwait(false);
        const string sql = """
            SELECT
                CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')),
                CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')),
                CONVERT(nvarchar(128), SERVERPROPERTY('ProductLevel')),
                CONVERT(nvarchar(256), SERVERPROPERTY('Edition')),
                CONVERT(int, SERVERPROPERTY('EngineEdition')),
                d.name,
                d.compatibility_level,
                d.state_desc,
                d.is_read_only,
                d.collation_name
            FROM sys.databases AS d
            WHERE d.database_id = DB_ID();
            """;

        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = settings.CommandTimeoutSeconds
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("连接成功，但无法读取当前数据库元数据。");
        }

        stopwatch.Stop();
        var productVersion = reader.GetString(1);
        var versionPart = productVersion.Split('.', 2, StringSplitOptions.TrimEntries)[0];
        _ = int.TryParse(versionPart, out var majorVersion);

        return new DatabaseEndpointInfo
        {
            ServerName = reader.GetString(0),
            ProductVersion = productVersion,
            ProductLevel = reader.GetString(2),
            Edition = reader.GetString(3),
            EngineEdition = reader.GetInt32(4),
            DatabaseName = reader.GetString(5),
            CompatibilityLevel = reader.GetInt32(6),
            DatabaseState = reader.GetString(7),
            IsReadOnly = reader.GetBoolean(8),
            CollationName = reader.IsDBNull(9) ? null : reader.GetString(9),
            MajorVersion = majorVersion,
            ConnectionElapsed = stopwatch.Elapsed
        };
    }
}
