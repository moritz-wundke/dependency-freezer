using System.Text.Json;

namespace DependencyFreezer;

internal static class FrozenLockFileStore
{
    public static async Task<FrozenLockFile> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new FrozenLockFile();
        }

        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<FrozenLockFile>(stream, DependencyFreezerJson.SerializerOptions, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            throw new InvalidOperationException($"Frozen lock file '{path}' is empty or invalid.");
        }

        document.Packages ??= new Dictionary<string, FrozenPackageLockEntry>(StringComparer.Ordinal);
        if (document.SchemaVersion > FrozenLockFile.CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"Unsupported frozen lock schema version '{document.SchemaVersion}'.");
        }

        return document;
    }

    public static async Task SaveAsync(string path, FrozenLockFile document, CancellationToken cancellationToken)
    {
        document.UpdatedAt = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"Unable to determine directory for '{path}'."));
        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, document, DependencyFreezerJson.SerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, path, true);
    }
}
