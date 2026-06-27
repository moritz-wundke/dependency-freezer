using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DependencyFreezer
{

internal sealed class UnityManifestDocument
{
    private readonly JsonObject _root;
    private readonly JsonObject _dependenciesNode;

    private UnityManifestDocument(JsonObject root, JsonObject dependenciesNode, IReadOnlyList<ScopedRegistry> scopedRegistries)
    {
        _root = root;
        _dependenciesNode = dependenciesNode;
        ScopedRegistries = scopedRegistries;
    }

    public IReadOnlyList<ScopedRegistry> ScopedRegistries { get; }

    public IReadOnlyDictionary<string, string> Dependencies => _dependenciesNode
        .Select(kvp => new KeyValuePair<string, string>(kvp.Key, kvp.Value?.GetValue<string>() ?? string.Empty))
        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

    public static async Task<UnityManifestDocument> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = node?.AsObject() ?? throw new InvalidOperationException($"Manifest '{path}' is not a JSON object.");
        var dependencies = root["dependencies"] as JsonObject ?? new JsonObject();
        root["dependencies"] = dependencies;

        var scopedRegistries = new List<ScopedRegistry>();
        if (root["scopedRegistries"] is JsonArray registryArray)
        {
            foreach (var registryNode in registryArray.OfType<JsonObject>())
            {
                var scopes = registryNode["scopes"] as JsonArray;
                scopedRegistries.Add(new ScopedRegistry(
                    registryNode["name"]?.GetValue<string>() ?? string.Empty,
                    registryNode["url"]?.GetValue<string>() ?? string.Empty,
                    scopes?.Select(scope => scope?.GetValue<string>() ?? string.Empty).Where(scope => scope.Length > 0).ToArray() ?? Array.Empty<string>()));
            }
        }

        return new UnityManifestDocument(root, dependencies, scopedRegistries);
    }

    public ManifestDependency? TryGetDependency(string packageName)
    {
        if (!_dependenciesNode.TryGetPropertyValue(packageName, out var valueNode) || valueNode is null)
        {
            return null;
        }

        var value = valueNode.GetValue<string>();
        return new ManifestDependency(packageName, value, DependencySourceClassifier.FromReference(value), true);
    }

    public IReadOnlyList<ManifestDependency> GetDependencies() => _dependenciesNode
        .Select(kvp => new ManifestDependency(kvp.Key, kvp.Value?.GetValue<string>() ?? string.Empty, DependencySourceClassifier.FromReference(kvp.Value?.GetValue<string>() ?? string.Empty), true))
        .OrderBy(dependency => dependency.Name, StringComparer.Ordinal)
        .ToArray();

    public void SetDependency(string packageName, string value) => _dependenciesNode[packageName] = value;

    public void RemoveDependency(string packageName) => _dependenciesNode.Remove(packageName);

    public async Task WriteAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"Unable to determine directory for '{path}'."));
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, _root.ToJsonString(DependencyFreezerJson.SerializerOptions), cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, true);
    }
}

internal static class PackagesLockDocument
{
    public static async Task<IReadOnlyDictionary<string, PackagesLockEntry>> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, PackagesLockEntry>(StringComparer.Ordinal);
        }

        await using var stream = File.OpenRead(path);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = node?.AsObject() ?? throw new InvalidOperationException($"Packages lock '{path}' is not a JSON object.");
        var dependencyObject = root["dependencies"] as JsonObject;
        if (dependencyObject is null)
        {
            return new Dictionary<string, PackagesLockEntry>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, PackagesLockEntry>(StringComparer.Ordinal);
        foreach (var (name, value) in dependencyObject)
        {
            if (value is not JsonObject packageObject)
            {
                continue;
            }

            var dependencies = (packageObject["dependencies"] as JsonObject)?
                .Select(kvp => new KeyValuePair<string, string>(kvp.Key, kvp.Value?.GetValue<string>() ?? string.Empty))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);

            var version = packageObject["version"]?.GetValue<string>() ?? string.Empty;
            var source = packageObject["source"]?.GetValue<string>();
            var registryUrl = packageObject["url"]?.GetValue<string>();
            result[name] = new PackagesLockEntry(name, version, DependencySourceClassifier.FromPackagesLock(source, version), dependencies, registryUrl);
        }

        return result;
    }
}
}
