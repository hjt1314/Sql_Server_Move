using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using SqlServerMigration.App.Models;

namespace SqlServerMigration.App.Services;

public enum SchemaScriptPhase
{
    PreData,
    PostData,
    Programmability
}

public sealed class SchemaScriptBatch
{
    public required SchemaScriptPhase Phase { get; init; }

    public required string ObjectName { get; init; }

    public required string Sql { get; init; }
}

/// <summary>
/// 由 SMO 针对 SQL Server 2019 生成的可预览脚本计划。
/// </summary>
public sealed class SchemaMigrationPlan
{
    internal SchemaMigrationPlan(IReadOnlyList<SchemaScriptBatch> batches)
    {
        Batches = batches;
    }

    public IReadOnlyList<SchemaScriptBatch> Batches { get; }

    public DateTimeOffset GeneratedAt { get; } = DateTimeOffset.Now;

    public string PreviewScript => string.Join(
        Environment.NewLine + "GO" + Environment.NewLine,
        Batches.Select(batch => $"-- Phase: {batch.Phase}; Object: {batch.ObjectName}{Environment.NewLine}{batch.Sql.Trim()}"));
}

public sealed class SchemaApplyResult
{
    public int ExecutedBatchCount { get; init; }

    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// 生成 CREATE/ALTER 架构脚本。此服务不生成 DROP、TRUNCATE 或覆盖目标对象的脚本。
/// </summary>
public sealed partial class SchemaMigrationService
{
    private readonly ConnectionService _connectionService;

    public SchemaMigrationService(ConnectionService? connectionService = null)
    {
        _connectionService = connectionService ?? new ConnectionService();
    }

    public async Task<SchemaMigrationPlan> GeneratePreviewAsync(
        SqlConnectionSettings sourceSettings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceSettings);
        await using var connection = await _connectionService.OpenConnectionAsync(sourceSettings, cancellationToken).ConfigureAwait(false);

        return await Task.Run(
            () => GeneratePlan(connection, sourceSettings.Database, sourceSettings.CommandTimeoutSeconds, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<SchemaApplyResult> ExecuteAsync(
        SqlConnectionSettings targetSettings,
        SchemaMigrationPlan plan,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteInternalAsync(targetSettings, plan, phase: null, progress, cancellationToken);

    public Task<SchemaApplyResult> ExecutePhaseAsync(
        SqlConnectionSettings targetSettings,
        SchemaMigrationPlan plan,
        SchemaScriptPhase phase,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteInternalAsync(targetSettings, plan, phase, progress, cancellationToken);

    private static SchemaMigrationPlan GeneratePlan(
        SqlConnection connection,
        string databaseName,
        int commandTimeout,
        CancellationToken cancellationToken)
    {
        var serverConnection = new ServerConnection(connection)
        {
            StatementTimeout = commandTimeout
        };
        var server = new Server(serverConnection);
        var database = server.Databases[databaseName]
            ?? throw new InvalidOperationException($"找不到源数据库 {databaseName}。");

        var batches = new List<SchemaScriptBatch>();
        var baseOptions = CreateOptions();
        baseOptions.DriAll = false;
        baseOptions.Indexes = false;
        baseOptions.Triggers = false;

        foreach (Schema schema in database.Schemas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsBuiltInSchema(schema.Name))
            {
                continue;
            }

            AddScripts(batches, SchemaScriptPhase.PreData, $"schema {schema.Name}", schema.Script(baseOptions));
        }

        var tables = database.Tables.Cast<Table>()
            .Where(table => !table.IsSystemObject)
            .OrderBy(table => table.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(table => table.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddScripts(batches, SchemaScriptPhase.PreData, $"table {table.Schema}.{table.Name}", table.Script(baseOptions));
        }

        var postOptions = CreateOptions();
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (Column column in table.Columns)
            {
                if (column.DefaultConstraint is not null)
                {
                    AddScripts(batches, SchemaScriptPhase.PostData, $"default {table.Schema}.{table.Name}.{column.Name}", column.DefaultConstraint.Script(postOptions));
                }
            }

            foreach (Check check in table.Checks)
            {
                AddScripts(batches, SchemaScriptPhase.PostData, $"check {table.Schema}.{table.Name}.{check.Name}", check.Script(postOptions));
            }

            foreach (Microsoft.SqlServer.Management.Smo.Index index in table.Indexes)
            {
                AddScripts(batches, SchemaScriptPhase.PostData, $"index {table.Schema}.{table.Name}.{index.Name}", index.Script(postOptions));
            }
        }

        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (ForeignKey foreignKey in table.ForeignKeys)
            {
                AddScripts(batches, SchemaScriptPhase.PostData, $"foreign key {table.Schema}.{table.Name}.{foreignKey.Name}", foreignKey.Script(postOptions));
            }

            foreach (Trigger trigger in table.Triggers)
            {
                AddScripts(batches, SchemaScriptPhase.PostData, $"trigger {table.Schema}.{table.Name}.{trigger.Name}", trigger.Script(postOptions));
            }
        }

        var programmableOptions = CreateOptions();
        foreach (UserDefinedFunction function in database.UserDefinedFunctions.Cast<UserDefinedFunction>()
                     .Where(value => !value.IsSystemObject)
                     .OrderBy(value => value.Schema, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddScripts(batches, SchemaScriptPhase.Programmability, $"function {function.Schema}.{function.Name}", function.Script(programmableOptions));
        }

        foreach (View view in database.Views.Cast<View>()
                     .Where(value => !value.IsSystemObject)
                     .OrderBy(value => value.Schema, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddScripts(batches, SchemaScriptPhase.Programmability, $"view {view.Schema}.{view.Name}", view.Script(programmableOptions));
        }

        foreach (StoredProcedure procedure in database.StoredProcedures.Cast<StoredProcedure>()
                     .Where(value => !value.IsSystemObject)
                     .OrderBy(value => value.Schema, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddScripts(batches, SchemaScriptPhase.Programmability, $"procedure {procedure.Schema}.{procedure.Name}", procedure.Script(programmableOptions));
        }

        return new SchemaMigrationPlan(batches);
    }

    private async Task<SchemaApplyResult> ExecuteInternalAsync(
        SqlConnectionSettings targetSettings,
        SchemaMigrationPlan plan,
        SchemaScriptPhase? phase,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetSettings);
        ArgumentNullException.ThrowIfNull(plan);

        var selected = phase is null
            ? plan.Batches
            : plan.Batches.Where(batch => batch.Phase == phase.Value).ToArray();
        var startedAt = DateTimeOffset.UtcNow;
        var executed = 0;

        await using var connection = await _connectionService.OpenConnectionAsync(targetSettings, cancellationToken).ConfigureAwait(false);
        foreach (var scriptBatch in selected)
        {
            foreach (var sqlBatch in SplitGoBatches(scriptBatch.Sql))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var command = new SqlCommand(sqlBatch.Sql, connection)
                {
                    CommandTimeout = targetSettings.CommandTimeoutSeconds
                };

                for (var repetition = 0; repetition < sqlBatch.RepeatCount; repetition++)
                {
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    executed++;
                }
            }

            progress?.Report(new MigrationProgress
            {
                Stage = MigrationStage.SchemaApply,
                CurrentObject = scriptBatch.ObjectName,
                Message = $"已执行 {scriptBatch.Phase} 架构批次。",
                ObjectsCompleted = Array.IndexOf(selected.ToArray(), scriptBatch) + 1,
                ObjectsTotal = selected.Count
            });
        }

        return new SchemaApplyResult
        {
            ExecutedBatchCount = executed,
            Elapsed = DateTimeOffset.UtcNow - startedAt
        };
    }

    private static ScriptingOptions CreateOptions() => new()
    {
        TargetServerVersion = SqlServerVersion.Version150,
        TargetDatabaseEngineType = DatabaseEngineType.Standalone,
        ScriptSchema = true,
        ScriptData = false,
        ScriptDrops = false,
        ScriptForCreateDrop = false,
        IncludeDatabaseContext = false,
        IncludeHeaders = false,
        IncludeIfNotExists = false,
        SchemaQualify = true,
        SchemaQualifyForeignKeysReferences = true,
        NoFileGroup = true,
        NoTablePartitioningSchemes = true,
        NoIndexPartitioningSchemes = true,
        Permissions = false,
        WithDependencies = false,
        ContinueScriptingOnError = false,
        EnforceScriptingOptions = true
    };

    private static void AddScripts(
        ICollection<SchemaScriptBatch> batches,
        SchemaScriptPhase phase,
        string objectName,
        StringCollection scripts)
    {
        foreach (string? script in scripts)
        {
            if (!string.IsNullOrWhiteSpace(script))
            {
                batches.Add(new SchemaScriptBatch
                {
                    Phase = phase,
                    ObjectName = objectName,
                    Sql = script
                });
            }
        }
    }

    private static bool IsBuiltInSchema(string schemaName) =>
        schemaName.Equals("dbo", StringComparison.OrdinalIgnoreCase) ||
        schemaName.Equals("guest", StringComparison.OrdinalIgnoreCase) ||
        schemaName.Equals("sys", StringComparison.OrdinalIgnoreCase) ||
        schemaName.Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase) ||
        schemaName.StartsWith("db_", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<(string Sql, int RepeatCount)> SplitGoBatches(string script)
    {
        var result = new List<(string Sql, int RepeatCount)>();
        var builder = new StringBuilder();

        using var reader = new StringReader(script);
        while (reader.ReadLine() is { } line)
        {
            var match = GoLineRegex().Match(line);
            if (!match.Success)
            {
                builder.AppendLine(line);
                continue;
            }

            AddCurrentBatch(result, builder, ParseRepeatCount(match));
        }

        AddCurrentBatch(result, builder, repeatCount: 1);
        return result;
    }

    private static void AddCurrentBatch(
        ICollection<(string Sql, int RepeatCount)> result,
        StringBuilder builder,
        int repeatCount)
    {
        var sql = builder.ToString().Trim();
        builder.Clear();
        if (sql.Length > 0)
        {
            result.Add((sql, repeatCount));
        }
    }

    private static int ParseRepeatCount(Match match) =>
        match.Groups[1].Success && int.TryParse(match.Groups[1].Value, out var count) && count > 0
            ? count
            : 1;

    [GeneratedRegex(@"^\s*GO(?:\s+(\d+))?\s*(?:--.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoLineRegex();
}
