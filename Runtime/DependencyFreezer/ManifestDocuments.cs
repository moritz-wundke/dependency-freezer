using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DependencyFreezer
{

internal sealed class UnityManifestDocument
{
    private readonly Dictionary<string, object?> _root;
    private readonly Dictionary<string, object?> _dependenciesNode;

    private UnityManifestDocument(Dictionary<string, object?> root, Dictionary<string, object?> dependenciesNode, IReadOnlyList<ScopedRegistry> scopedRegistries)
    {
        _root = root;
        _dependenciesNode = dependenciesNode;
        ScopedRegistries = scopedRegistries;
    }

    public IReadOnlyList<ScopedRegistry> ScopedRegistries { get; }

    public IReadOnlyDictionary<string, string> Dependencies => _dependenciesNode
        .Select(kvp => new KeyValuePair<string, string>(kvp.Key, SimpleJson.ReadString(kvp.Value) ?? string.Empty))
        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

    public static async Task<UnityManifestDocument> LoadAsync(string path, CancellationToken cancellationToken)
    {
        var root = SimpleJson.ParseObject(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        var dependencies = root.TryGetValue("dependencies", out var dependenciesValue) && dependenciesValue is Dictionary<string, object?> dependencyObject
            ? dependencyObject
            : new Dictionary<string, object?>(StringComparer.Ordinal);
        root["dependencies"] = dependencies;

        var scopedRegistries = new List<ScopedRegistry>();
        if (root.TryGetValue("scopedRegistries", out var scopedRegistriesValue) && scopedRegistriesValue is List<object?> registryArray)
        {
            foreach (var registryNode in registryArray.OfType<Dictionary<string, object?>>())
            {
                scopedRegistries.Add(new ScopedRegistry(
                    SimpleJson.ReadString(registryNode.TryGetValue("name", out var name) ? name : null) ?? string.Empty,
                    SimpleJson.ReadString(registryNode.TryGetValue("url", out var url) ? url : null) ?? string.Empty,
                    SimpleJson.ReadStringList(registryNode.TryGetValue("scopes", out var scopes) ? scopes : null).ToArray()));
            }
        }

        return new UnityManifestDocument(root, dependencies, scopedRegistries);
    }

    public ManifestDependency? TryGetDependency(string packageName)
    {
        if (!_dependenciesNode.TryGetValue(packageName, out var valueNode) || valueNode is null)
        {
            return null;
        }

        var value = SimpleJson.ReadString(valueNode) ?? string.Empty;
        return new ManifestDependency(packageName, value, DependencySourceClassifier.FromReference(value), true);
    }

    public IReadOnlyList<ManifestDependency> GetDependencies() => _dependenciesNode
        .Select(kvp =>
        {
            var value = SimpleJson.ReadString(kvp.Value) ?? string.Empty;
            return new ManifestDependency(kvp.Key, value, DependencySourceClassifier.FromReference(value), true);
        })
        .OrderBy(dependency => dependency.Name, StringComparer.Ordinal)
        .ToArray();

    public void SetDependency(string packageName, string value) => _dependenciesNode[packageName] = value;

    public void RemoveDependency(string packageName) => _dependenciesNode.Remove(packageName);

    public async Task WriteAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"Unable to determine directory for '{path}'."));
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, SimpleJson.Serialize(_root, writeIndented: true), cancellationToken).ConfigureAwait(false);
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

        var root = SimpleJson.ParseObject(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        var dependencyObject = root.TryGetValue("dependencies", out var dependenciesValue) ? dependenciesValue as Dictionary<string, object?> : null;
        if (dependencyObject is null)
        {
            return new Dictionary<string, PackagesLockEntry>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, PackagesLockEntry>(StringComparer.Ordinal);
        foreach (var (name, value) in dependencyObject)
        {
            if (value is not Dictionary<string, object?> packageObject)
            {
                continue;
            }

            var dependencies = SimpleJson.ReadStringMap(packageObject.TryGetValue("dependencies", out var nestedDependencies) ? nestedDependencies : null);
            var version = SimpleJson.ReadString(packageObject.TryGetValue("version", out var versionValue) ? versionValue : null) ?? string.Empty;
            var source = SimpleJson.ReadString(packageObject.TryGetValue("source", out var sourceValue) ? sourceValue : null);
            var registryUrl = SimpleJson.ReadString(packageObject.TryGetValue("url", out var registryUrlValue) ? registryUrlValue : null);
            result[name] = new PackagesLockEntry(name, version, DependencySourceClassifier.FromPackagesLock(source, version), dependencies, registryUrl);
        }

        return result;
    }
}
}
