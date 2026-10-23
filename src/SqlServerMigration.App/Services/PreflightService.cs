using Microsoft.Data.SqlClient;
using SqlServerMigration.App.Models;

namespace SqlServerMigration.App.Services;

/// <summary>
/// 执行不会修改源端或目标端的迁移前检查。
/// </summary>
public sealed class PreflightService
{
    private readonly ConnectionService _connectionService;

    public PreflightService(ConnectionService? connectionService = null)
    {
        _connectionService = connectionService ?? new ConnectionService();
    }

    public async Task<PreflightReport> RunAsync(
        SqlConnectionSettings sourceSettings,
        SqlConnectionSettings targetSettings,
        bool targetDatabaseSelectionConfirmed = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceSettings);
        ArgumentNullException.ThrowIfNull(targetSettings);

        var source = await _connectionService.TestConnectionAsync(sourceSettings, cancellationToken).ConfigureAwait(false);
        var target = await _connectionService.TestConnectionAsync(targetSettings, cancellationToken).ConfigureAwait(false);
        var issues = new List<PreflightIssue>();

        CheckEndpoints(source, target, targetDatabaseSelectionConfirmed, issues);

        await using var sourceConnection = await _connectionService.OpenConnectionAsync(sourceSettings, cancellationToken).ConfigureAwait(false);
        await using var targetConnection = await _connectionService.OpenConnectionAsync(targetSettings, cancellationToken).ConfigureAwait(false);

        await CheckTargetIsEmptyAsync(targetConnection, targetSettings.CommandTimeoutSeconds, issues, cancellationToken).ConfigureAwait(false);
        await CheckTargetPermissionsAsync(targetConnection, targetSettings.CommandTimeoutSeconds, issues, cancellationToken).ConfigureAwait(false);
        await CheckUnsupportedSourceFeaturesAsync(sourceConnection, source, sourceSettings.CommandTimeoutSeconds, issues, cancellationToken).ConfigureAwait(false);
        var tables = await LoadTablesAsync(sourceConnection, sourceSettings.CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        if (!source.IsReadOnly)
        {
            issues.Add(new PreflightIssue
            {
                Code = "SOURCE_WRITE_FREEZE_NOT_VERIFIABLE",
                Severity = PreflightIssueSeverity.Warning,
                Message = "源库当前不是只读状态。程序无法证明它一定是停止写入的备份还原库，开始前需由操作人员确认。"
            });
        }

        issues.Add(new PreflightIssue
        {
            Code = "PREFLIGHT_READ_ONLY",
            Severity = PreflightIssueSeverity.Information,
            Message = "本次预检仅读取系统目录和权限元数据，未执行 DDL、INSERT、UPDATE 或 DELETE。"
        });

        return new PreflightReport
        {
            Source = source,
            Target = target,
            Tables = tables,
            Issues = issues,
            CompletedAt = DateTimeOffset.Now
        };
    }

    private static void CheckEndpoints(
        DatabaseEndpointInfo source,
        DatabaseEndpointInfo target,
        bool targetDatabaseSelectionConfirmed,
        ICollection<PreflightIssue> issues)
    {
        if (!target.IsSqlServer2019)
        {
            issues.Add(new PreflightIssue
            {
                Code = "TARGET_VERSION_NOT_2019",
                Severity = PreflightIssueSeverity.Blocking,
                Message = $"目标实例为 SQL Server {target.ProductVersion}，本工具只允许写入 15.x (SQL Server 2019)。"
            });
        }

        if (!string.Equals(source.DatabaseState, "ONLINE", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new PreflightIssue
            {
                Code = "SOURCE_DATABASE_NOT_ONLINE",
                Severity = PreflightIssueSeverity.Blocking,
                Message = $"源数据库状态为 {source.DatabaseState}，必须先恢复为 ONLINE。"
            });
        }

        if (!string.Equals(target.DatabaseState, "ONLINE", StringComparison.OrdinalIgnoreCase) || target.IsReadOnly)
        {
            issues.Add(new PreflightIssue
            {
                Code = "TARGET_DATABASE_NOT_WRITABLE",
                Severity = PreflightIssueSeverity.Blocking,
                Message = $"目标数据库必须为 ONLINE 且可写；当前状态为 {target.DatabaseState}，只读={target.IsReadOnly}。"
            });
        }

        if (target.CompatibilityLevel != 150)
        {
            issues.Add(new PreflightIssue
            {
                Code = "TARGET_COMPATIBILITY_NOT_150",
                Severity = PreflightIssueSeverity.Warning,
                Message = $"目标库兼容级别为 {target.CompatibilityLevel}，脚本仍按 SQL Server 2019 生成，但应在执行前人工复核语义差异。"
            });
        }

        if (!string.IsNullOrWhiteSpace(source.CollationName) &&
            !string.Equals(source.CollationName, target.CollationName, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new PreflightIssue
            {
                Code = "DATABASE_COLLATION_MISMATCH",
                Severity = PreflightIssueSeverity.Blocking,
                Message = $"源库排序规则为 {source.CollationName}，目标库为 {target.CollationName ?? "未知"}。请使用相同排序规则重新创建空目标库。"
            });
        }

        var sameServer = string.Equals(source.ServerName, target.ServerName, StringComparison.OrdinalIgnoreCase);
        var sameDatabase = string.Equals(source.DatabaseName, target.DatabaseName, StringComparison.OrdinalIgnoreCase);
        if (sameServer && sameDatabase)
        {
            issues.Add(new PreflightIssue
            {
                Code = "SOURCE_AND_TARGET_ARE_IDENTICAL",
                Severity = PreflightIssueSeverity.Blocking,
                Message = "源端和目标端解析到同一实例的同一数据库，禁止原地迁移。"
            });
        }
        else if (sameDatabase && !targetDatabaseSelectionConfirmed)
        {
            issues.Add(new PreflightIssue
            {
                Code = "SAME_NAME_TARGET_NOT_CONFIRMED",
                Severity = PreflightIssueSeverity.Blocking,
                Message = "目标库与源库同名。必须在界面中明确选中并确认该目标库，不能只依赖默认名称。"
            });
        }
    }

    private static async Task CheckTargetIsEmptyAsync(
        SqlConnection connection,
        int commandTimeout,
        ICollection<PreflightIssue> issues,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                (SELECT COUNT_BIG(*)
                 FROM sys.objects
                 WHERE is_ms_shipped = 0
                   AND type IN ('U','V','P','PC','FN','IF','TF','FS','FT','AF','TR','SO','SN'))
              + (SELECT COUNT_BIG(*) FROM sys.types WHERE is_user_defined = 1)
              + (SELECT COUNT_BIG(*) FROM sys.xml_schema_collections WHERE xml_collection_id > 1);
            """;

        var count = await ExecuteInt64ScalarAsync(connection, sql, commandTimeout, cancellationToken).ConfigureAwait(false);
        if (count > 0)
        {
            issues.Add(new PreflightIssue
            {
                Code = "TARGET_DATABASE_NOT_EMPTY",
                Severity = PreflightIssueSeverity.Blocking,
                Message = $"目标数据库已包含 {count} 个用户对象或用户定义类型。本工具不会 DROP、TRUNCATE 或覆盖它们，请改用全新空库。"
            });
        }
    }

    private static async Task CheckTargetPermissionsAsync(
        SqlConnection connection,
        int commandTimeout,
        ICollection<PreflightIssue> issues,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                CONVERT(int, HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE')),
                CONVERT(int, HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE VIEW')),
                CONVERT(int, HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE PROCEDURE')),
                CONVERT(int, HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE FUNCTION')),
                CONVERT(int, HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER')),
                CONVERT(int, HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'INSERT'));
            """;

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = commandTimeout };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("无法读取目标数据库权限。");
        }

        var permissionNames = new[] { "CREATE TABLE", "CREATE VIEW", "CREATE PROCEDURE", "CREATE FUNCTION", "ALTER", "INSERT" };
        var missing = permissionNames.Where((_, index) => reader.IsDBNull(index) || reader.GetInt32(index) != 1).ToArray();
        if (missing.Length > 0)
        {
            issues.Add(new PreflightIssue
            {
                Code = "TARGET_PERMISSION_MISSING",
                Severity = PreflightIssueSeverity.Blocking,
                Message = $"当前登录缺少目标库权限：{string.Join(", ", missing)}。预检不会尝试授权。"
            });
        }
    }

    private static async Task CheckUnsupportedSourceFeaturesAsync(
        SqlConnection connection,
        DatabaseEndpointInfo source,
        int commandTimeout,
        ICollection<PreflightIssue> issues,
        CancellationToken cancellationToken)
    {
        await AddObjectIssuesAsync(
            connection,
            """
            SELECT TOP (50)
                QUOTENAME(s.name) + N'.' + QUOTENAME(p.name),
                CASE WHEN p.is_enabled = 1 THEN N'ENABLED' ELSE N'DISABLED' END
            FROM sys.security_policies AS p
            INNER JOIN sys.schemas AS s ON s.schema_id = p.schema_id
            ORDER BY s.name, p.name;
            """,
            commandTimeout,
            issues,
            row => new PreflightIssue
            {
                Code = "SOURCE_ROW_LEVEL_SECURITY_OUT_OF_SCOPE",
                Severity = PreflightIssueSeverity.Blocking,
                ObjectName = row.Name,
                Message = $"{row.Name} 是 {row.Detail} 行级安全策略。当前工具不迁移安全策略，也不能证明当前登录可见全部源数据。"
            },
            cancellationToken).ConfigureAwait(false);

        await AddObjectIssuesAsync(
            connection,
            """
            SELECT TOP (50)
                QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N'.' + QUOTENAME(c.name),
                COALESCE(c.masking_function, N'UNKNOWN')
            FROM sys.masked_columns AS c
            INNER JOIN sys.tables AS t ON t.object_id = c.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0 AND c.is_masked = 1
            ORDER BY s.name, t.name, c.column_id;
            """,
            commandTimeout,
            issues,
            row => new PreflightIssue
            {
                Code = "SOURCE_DYNAMIC_DATA_MASKING_OUT_OF_SCOPE",
                Severity = PreflightIssueSeverity.Blocking,
                ObjectName = row.Name,
                Message = $"{row.Name} 使用动态数据掩码（{row.Detail}）。当前工具不迁移掩码规则，也不假定当前登录具有 UNMASK 权限。"
            },
            cancellationToken).ConfigureAwait(false);

        await AddObjectIssuesAsync(
            connection,
            """
            SELECT TOP (50)
                QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N'.' + QUOTENAME(c.name),
                ty.name
            FROM sys.columns AS c
            INNER JOIN sys.tables AS t ON t.object_id = c.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
            WHERE t.is_ms_shipped = 0 AND ty.name IN (N'vector', N'json')
            ORDER BY s.name, t.name, c.column_id;
            """,
            commandTimeout,
            issues,
            row => new PreflightIssue
            {
                Code = row.Detail.Equals("vector", StringComparison.OrdinalIgnoreCase)
                    ? "SOURCE_VECTOR_TYPE_UNSUPPORTED"
                    : "SOURCE_NATIVE_JSON_TYPE_UNSUPPORTED",
                Severity = PreflightIssueSeverity.Blocking,
                ObjectName = row.Name,
                Message = $"{row.Name} 使用 SQL Server 2019 不支持的 {row.Detail} 原生类型。本工具不会猜测转换方案。"
            },
            cancellationToken).ConfigureAwait(false);

        if (source.MajorVersion >= 16)
        {
            await AddObjectIssuesAsync(
                connection,
                """
                SELECT TOP (50)
                    QUOTENAME(s.name) + N'.' + QUOTENAME(t.name),
                    CONVERT(nvarchar(32), t.ledger_type)
                FROM sys.tables AS t
                INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
                WHERE t.is_ms_shipped = 0 AND t.ledger_type <> 0
                ORDER BY s.name, t.name;
                """,
                commandTimeout,
                issues,
                row => new PreflightIssue
                {
                    Code = "SOURCE_LEDGER_UNSUPPORTED",
                    Severity = PreflightIssueSeverity.Blocking,
                    ObjectName = row.Name,
                    Message = $"{row.Name} 是 Ledger 表。SQL Server 2019 不支持 Ledger，必须先制定业务可接受的降级方案。"
                },
                cancellationToken).ConfigureAwait(false);
        }

        await AddObjectIssuesAsync(
            connection,
            """
            SELECT TOP (50)
                QUOTENAME(s.name) + N'.' + QUOTENAME(t.name),
                CASE
                    WHEN t.temporal_type <> 0 THEN N'TEMPORAL'
                    WHEN t.is_memory_optimized = 1 THEN N'MEMORY_OPTIMIZED'
                    WHEN t.is_filetable = 1 THEN N'FILETABLE'
                    WHEN t.is_node = 1 OR t.is_edge = 1 THEN N'GRAPH'
                    WHEN t.is_tracked_by_cdc = 1 THEN N'CDC'
                    WHEN t.is_replicated = 1 OR t.is_merge_published = 1 OR t.is_sync_tran_subscribed = 1 THEN N'REPLICATION'
                    WHEN ct.object_id IS NOT NULL THEN N'CHANGE_TRACKING'
                    ELSE N'UNKNOWN'
                END
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            LEFT JOIN sys.change_tracking_tables AS ct ON ct.object_id = t.object_id
            WHERE t.is_ms_shipped = 0
              AND (t.temporal_type <> 0 OR t.is_memory_optimized = 1 OR t.is_filetable = 1 OR
                   t.is_node = 1 OR t.is_edge = 1 OR t.is_tracked_by_cdc = 1 OR
                   t.is_replicated = 1 OR t.is_merge_published = 1 OR
                   t.is_sync_tran_subscribed = 1 OR ct.object_id IS NOT NULL)
            ORDER BY s.name, t.name;
            """,
            commandTimeout,
            issues,
            row => new PreflightIssue
            {
                Code = $"SOURCE_{row.Detail}_OUT_OF_SCOPE",
                Severity = PreflightIssueSeverity.Blocking,
                ObjectName = row.Name,
                Message = $"{row.Name} 属于 {row.Detail} 表，不在本工具的常规表迁移范围内。"
            },
            cancellationToken).ConfigureAwait(false);

        await AddObjectIssuesAsync(
            connection,
            """
            SELECT TOP (50)
                QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N'.' + QUOTENAME(c.name),
                N'FILESTREAM'
            FROM sys.columns AS c
            INNER JOIN sys.tables AS t ON t.object_id = c.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0 AND c.is_filestream = 1
            ORDER BY s.name, t.name, c.column_id;
            """,
            commandTimeout,
            issues,
            row => new PreflightIssue
            {
                Code = "SOURCE_FILESTREAM_OUT_OF_SCOPE",
                Severity = PreflightIssueSeverity.Blocking,
                ObjectName = row.Name,
                Message = $"{row.Name} 使用 {row.Detail}。当前工具不迁移 FILESTREAM 文件组或外部文件内容。"
            },
            cancellationToken).ConfigureAwait(false);

        await AddObjectIssuesAsync(
            connection,
            """
            SELECT TOP (50)
                QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N'.' + QUOTENAME(c.name),
                ty.name
            FROM sys.columns AS c
            INNER JOIN sys.tables AS t ON t.object_id = c.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
            WHERE t.is_ms_shipped = 0
              AND (ty.is_user_defined = 1 OR c.xml_collection_id <> 0 OR c.encryption_type IS NOT NULL)
            ORDER BY s.name, t.name, c.column_id;
            """,
            commandTimeout,
            issues,
            row => new PreflightIssue
            {
                Code = "SOURCE_COLUMN_DEPENDENCY_OUT_OF_SCOPE",
                Severity = PreflightIssueSeverity.Blocking,
                ObjectName = row.Name,
                Message = $"{row.Name} 依赖用户定义类型、XML Schema Collection 或 Always Encrypted（类型：{row.Detail}），当前不自动迁移该依赖。"
            },
            cancellationToken).ConfigureAwait(false);

        await AddObjectIssuesAsync(
            connection,
            """
            SELECT TOP (50)
                QUOTENAME(s.name) + N'.' + QUOTENAME(o.name),
                o.type_desc
            FROM sys.objects AS o
            INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0
              AND o.type IN ('SO','SN','AF','FS','FT','PC')
            ORDER BY s.name, o.name;
            """,
            commandTimeout,
            issues,
            row => new PreflightIssue
            {
                Code = "SOURCE_OBJECT_KIND_OUT_OF_SCOPE",
                Severity = PreflightIssueSeverity.Blocking,
                ObjectName = row.Name,
                Message = $"{row.Name} ({row.Detail}) 不在当前的 schemas/tables/keys/default/check/index/views/functions/procedures/triggers 范围内。"
            },
            cancellationToken).ConfigureAwait(false);

        await AddObjectIssuesAsync(
            connection,
            """
            SELECT TOP (50)
                QUOTENAME(s.name) + N'.' + QUOTENAME(t.name),
                N'PARTITION_SCHEME'
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            INNER JOIN sys.indexes AS i ON i.object_id = t.object_id AND i.index_id IN (0, 1)
            INNER JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
            WHERE t.is_ms_shipped = 0 AND ds.type = 'PS'
            UNION ALL
            SELECT TOP (50)
                QUOTENAME(OBJECT_SCHEMA_NAME(fi.object_id)) + N'.' + QUOTENAME(OBJECT_NAME(fi.object_id)),
                N'FULLTEXT_INDEX'
            FROM sys.fulltext_indexes AS fi;
            """,
            commandTimeout,
            issues,
            row => new PreflightIssue
            {
                Code = $"SOURCE_{row.Detail}_OUT_OF_SCOPE",
                Severity = PreflightIssueSeverity.Blocking,
                ObjectName = row.Name,
                Message = $"{row.Name} 使用 {row.Detail}，当前预览脚本不会携带它的完整依赖。"
            },
            cancellationToken).ConfigureAwait(false);

        await AddObjectIssuesAsync(
            connection,
            """
            SELECT TOP (50)
                QUOTENAME(s.name) + N'.' + QUOTENAME(et.name),
                N'EXTERNAL_TABLE'
            FROM sys.external_tables AS et
            INNER JOIN sys.schemas AS s ON s.schema_id = et.schema_id
            ORDER BY s.name, et.name;
            """,
            commandTimeout,
            issues,
            row => new PreflightIssue
            {
                Code = "SOURCE_EXTERNAL_TABLE_OUT_OF_SCOPE",
                Severity = PreflightIssueSeverity.Blocking,
                ObjectName = row.Name,
                Message = $"{row.Name} 是外部表，本工具不会迁移外部数据源或凭据。"
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<MigrationTableInfo>> LoadTablesAsync(
        SqlConnection connection,
        int commandTimeout,
        CancellationToken cancellationToken)
    {
        const string tableSql = """
            SELECT
                s.name,
                t.name,
                CONVERT(bigint, COALESCE(SUM(CASE WHEN p.index_id IN (0, 1) THEN p.rows ELSE 0 END), 0))
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            LEFT JOIN sys.partitions AS p ON p.object_id = t.object_id
            WHERE t.is_ms_shipped = 0
            GROUP BY s.name, t.name
            ORDER BY s.name, t.name;
            """;
        const string columnSql = """
            SELECT
                s.name,
                t.name,
                c.name,
                ty.name,
                c.column_id,
                c.is_nullable,
                c.is_identity,
                c.is_computed,
                CASE WHEN ty.name IN (N'timestamp', N'rowversion') THEN CONVERT(bit, 1) ELSE CONVERT(bit, 0) END,
                c.generated_always_type
            FROM sys.columns AS c
            INNER JOIN sys.tables AS t ON t.object_id = c.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name, c.column_id;
            """;

        var tableRows = new List<(string Schema, string Table, long Rows)>();
        await using (var command = new SqlCommand(tableSql, connection) { CommandTimeout = commandTimeout })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tableRows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            }
        }

        var columns = new Dictionary<(string Schema, string Table), List<MigrationColumnInfo>>();
        await using (var command = new SqlCommand(columnSql, connection) { CommandTimeout = commandTimeout })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!columns.TryGetValue(key, out var list))
                {
                    list = new List<MigrationColumnInfo>();
                    columns.Add(key, list);
                }

                list.Add(new MigrationColumnInfo
                {
                    Name = reader.GetString(2),
                    SqlTypeName = reader.GetString(3),
                    Ordinal = reader.GetInt32(4),
                    IsNullable = reader.GetBoolean(5),
                    IsIdentity = reader.GetBoolean(6),
                    IsComputed = reader.GetBoolean(7),
                    IsRowVersion = reader.GetBoolean(8),
                    GeneratedAlwaysType = reader.GetInt32(9)
                });
            }
        }

        return tableRows.Select(row => new MigrationTableInfo
        {
            SchemaName = row.Schema,
            TableName = row.Table,
            EstimatedRowCount = row.Rows,
            Columns = columns.TryGetValue((row.Schema, row.Table), out var value)
                ? value
                : Array.Empty<MigrationColumnInfo>()
        }).ToArray();
    }

    private static async Task AddObjectIssuesAsync(
        SqlConnection connection,
        string sql,
        int commandTimeout,
        ICollection<PreflightIssue> issues,
        Func<(string Name, string Detail), PreflightIssue> issueFactory,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = commandTimeout };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            issues.Add(issueFactory((reader.GetString(0), reader.GetString(1))));
        }
    }

    private static async Task<long> ExecuteInt64ScalarAsync(
        SqlConnection connection,
        string sql,
        int commandTimeout,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = commandTimeout };
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
