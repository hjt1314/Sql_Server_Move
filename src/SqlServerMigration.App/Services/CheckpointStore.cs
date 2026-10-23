using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using SqlServerMigration.App.Models;

namespace SqlServerMigration.App.Services;

public sealed class CheckpointStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _checkpointDirectory;

    public CheckpointStore(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        _checkpointDirectory = Path.Combine(Path.GetFullPath(outputDirectory), "checkpoints");
    }

    public string GetCheckpointPath(Guid taskId) =>
        Path.Combine(_checkpointDirectory, $"{taskId:N}.json");

    public async Task SaveAsync(MigrationCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        Directory.CreateDirectory(_checkpointDirectory);
        checkpoint.UpdatedAt = DateTimeOffset.UtcNow;

        var targetPath = GetCheckpointPath(checkpoint.TaskId);
        var temporaryPath = targetPath + ".writing";

        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, checkpoint, SerializerOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, targetPath, overwrite: true);
    }

    public async Task<MigrationCheckpoint?> LoadAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var path = GetCheckpointPath(taskId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await JsonSerializer.DeserializeAsync<MigrationCheckpoint>(
            stream,
            SerializerOptions,
            cancellationToken);
    }

    public static string CreatePlanFingerprint(MigrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var normalized = string.Join(
            "\n",
            plan.Source.Server.Trim().ToUpperInvariant(),
            plan.Source.Database.Trim().ToUpperInvariant(),
            plan.Target.Server.Trim().ToUpperInvariant(),
            plan.TargetDatabaseName.Trim().ToUpperInvariant(),
            plan.CreateNewTargetDatabase,
            plan.IncludeProgrammableObjects);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
