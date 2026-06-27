using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DependencyFreezer
{

internal static class FrozenLockFileStore
{
    public static async Task<FrozenLockFile> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new FrozenLockFile();
        }

        var payload = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var root = SimpleJson.ParseObject(payload);
        var document = new FrozenLockFile
        {
            SchemaVersion = SimpleJson.ReadInt32(root.TryGetValue("schemaVersion", out var schemaVersionValue) ? schemaVersionValue : null) ?? FrozenLockFile.CurrentSchemaVersion,
            UpdatedAt = ParseTimestamp(root.TryGetValue("updatedAt", out var updatedAtValue) ? updatedAtValue : null),
            Packages = new Dictionary<string, FrozenPackageLockEntry>(StringComparer.Ordinal),
        };

        if (root.TryGetValue("packages", out var packagesValue) && packagesValue is Dictionary<string, object?> packagesObject)
        {
            foreach (var (packageName, packageValue) in packagesObject)
            {
                if (packageValue is not Dictionary<string, object?> packageObject)
                {
                    continue;
                }

                var entry = new FrozenPackageLockEntry
                {
                    Name = SimpleJson.ReadString(packageObject.TryGetValue("name", out var nameValue) ? nameValue : null) ?? packageName,
                    Version = SimpleJson.ReadString(packageObject.TryGetValue("version", out var versionValue) ? versionValue : null) ?? string.Empty,
                    OriginalReference = SimpleJson.ReadString(packageObject.TryGetValue("originalReference", out var originalReferenceValue) ? originalReferenceValue : null) ?? string.Empty,
                    OriginalSourceKind = SimpleJson.ReadEnum(packageObject.TryGetValue("originalSourceKind", out var originalSourceKindValue) ? originalSourceKindValue : null, DependencySourceKind.Unknown),
                    WasDirectDependency = packageObject.TryGetValue("wasDirectDependency", out var wasDirectDependencyValue) && wasDirectDependencyValue is bool wasDirectDependency && wasDirectDependency,
                    FreezeMode = SimpleJson.ReadEnum(packageObject.TryGetValue("freezeMode", out var freezeModeValue) ? freezeModeValue : null, FreezeMode.Explicit),
                    RegistryUrl = SimpleJson.ReadString(packageObject.TryGetValue("registryUrl", out var registryUrlValue) ? registryUrlValue : null) ?? string.Empty,
                    TarballUrl = SimpleJson.ReadString(packageObject.TryGetValue("tarballUrl", out var tarballUrlValue) ? tarballUrlValue : null) ?? string.Empty,
                    Integrity = SimpleJson.ReadString(packageObject.TryGetValue("integrity", out var integrityValue) ? integrityValue : null) ?? string.Empty,
                    EmbeddedPath = SimpleJson.ReadString(packageObject.TryGetValue("embeddedPath", out var embeddedPathValue) ? embeddedPathValue : null) ?? string.Empty,
                    DirectoryHash = SimpleJson.ReadString(packageObject.TryGetValue("directoryHash", out var directoryHashValue) ? directoryHashValue : null) ?? string.Empty,
                    Dependencies = SimpleJson.ReadStringList(packageObject.TryGetValue("dependencies", out var dependenciesValue) ? dependenciesValue : null),
                    RequestedBy = SimpleJson.ReadStringList(packageObject.TryGetValue("requestedBy", out var requestedByValue) ? requestedByValue : null),
                    FrozenAt = ParseTimestamp(packageObject.TryGetValue("frozenAt", out var frozenAtValue) ? frozenAtValue : null),
                };

                document.Packages[packageName] = entry;
            }
        }

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

        var packages = document.Packages
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(
                entry => entry.Key,
                entry => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = entry.Value.Name,
                    ["version"] = entry.Value.Version,
                    ["originalReference"] = entry.Value.OriginalReference,
                    ["originalSourceKind"] = (int)entry.Value.OriginalSourceKind,
                    ["wasDirectDependency"] = entry.Value.WasDirectDependency,
                    ["freezeMode"] = (int)entry.Value.FreezeMode,
                    ["registryUrl"] = entry.Value.RegistryUrl,
                    ["tarballUrl"] = entry.Value.TarballUrl,
                    ["integrity"] = entry.Value.Integrity,
                    ["embeddedPath"] = entry.Value.EmbeddedPath,
                    ["directoryHash"] = entry.Value.DirectoryHash,
                    ["dependencies"] = entry.Value.Dependencies.Select(dependency => (object?)dependency).ToList(),
                    ["requestedBy"] = entry.Value.RequestedBy.Select(requestedBy => (object?)requestedBy).ToList(),
                    ["frozenAt"] = entry.Value.FrozenAt.ToString("O"),
                },
                StringComparer.Ordinal);

        var root = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = document.SchemaVersion,
            ["updatedAt"] = document.UpdatedAt.ToString("O"),
            ["packages"] = packages,
        };

        await File.WriteAllTextAsync(tempPath, SimpleJson.Serialize(root, writeIndented: true), cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, true);
    }

    private static DateTimeOffset ParseTimestamp(object? value)
    {
        var text = SimpleJson.ReadString(value);
        return DateTimeOffset.TryParse(text, out var timestamp) ? timestamp : DateTimeOffset.UtcNow;
    }
}
}
