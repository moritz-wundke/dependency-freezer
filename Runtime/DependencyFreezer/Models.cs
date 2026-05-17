using System.Text.Json;
using System.Text.Json.Serialization;

namespace DependencyFreezer;

public enum DependencySourceKind
{
    Unknown,
    Registry,
    Embedded,
    Git,
    Local,
}

public enum PackageState
{
    RegistryManaged,
    FrozenEmbedded,
    UnfrozenPendingUpdate,
    DriftedBroken,
}

public enum FreezeMode
{
    Explicit,
    Transitive,
}

public sealed record ScopedRegistry(string Name, string Url, IReadOnlyList<string> Scopes);

public sealed record ManifestDependency(string Name, string Value, DependencySourceKind SourceKind, bool IsDirect);

public sealed record PackagesLockEntry(
    string Name,
    string Version,
    DependencySourceKind SourceKind,
    IReadOnlyDictionary<string, string> Dependencies,
    string? RegistryUrl);

public sealed record ResolvedPackage(
    string Name,
    string Version,
    DependencySourceKind SourceKind,
    string? RegistryUrl,
    string? OriginalReference,
    bool WasDirectDependency,
    FreezeMode FreezeMode,
    IReadOnlyCollection<string> Dependencies,
    IReadOnlyCollection<string> RequestedBy);

public sealed record FreezeRequest(string ProjectPath, IReadOnlyCollection<string>? PackageNames = null);

public sealed record UnfreezeRequest(string ProjectPath, IReadOnlyCollection<string>? PackageNames = null, bool UnfreezeAll = false);

public sealed record FreezeResult(IReadOnlyList<string> FrozenPackages, string LockFilePath);

public sealed record UnfreezePreview(IReadOnlyList<string> ImpactedPackages, IReadOnlyList<string> BlockingPackages)
{
    public bool CanProceed => BlockingPackages.Count == 0;
}

public sealed record UnfreezeResult(IReadOnlyList<string> UnfrozenPackages, string LockFilePath);

public sealed record ValidationIssue(string? PackageName, string Message);

public sealed record PackageStatusSnapshot(
    string Name,
    PackageState State,
    bool IsDirectDependency,
    string? CurrentReference,
    string? OriginalReference,
    IReadOnlyList<string> Dependencies);

public sealed record ValidationResult(
    bool Success,
    IReadOnlyList<ValidationIssue> Issues,
    IReadOnlyList<PackageStatusSnapshot> Packages);

public sealed record RegistryPackageMetadata(
    string Name,
    string Version,
    string RegistryUrl,
    string TarballUrl,
    string? Integrity,
    string? Shasum,
    IReadOnlyDictionary<string, string> Dependencies);

public sealed class FrozenLockFile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Dictionary<string, FrozenPackageLockEntry> Packages { get; set; } = new(StringComparer.Ordinal);
}

public sealed class FrozenPackageLockEntry
{
    public required string Name { get; set; }

    public required string Version { get; set; }

    public required string OriginalReference { get; set; }

    public required DependencySourceKind OriginalSourceKind { get; set; }

    public required bool WasDirectDependency { get; set; }

    public required FreezeMode FreezeMode { get; set; }

    public required string RegistryUrl { get; set; }

    public required string TarballUrl { get; set; }

    public required string Integrity { get; set; }

    public required string EmbeddedPath { get; set; }

    public required string DirectoryHash { get; set; }

    public required List<string> Dependencies { get; set; }

    public required List<string> RequestedBy { get; set; }

    public DateTimeOffset FrozenAt { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed record ProjectPaths(string RootPath)
{
    public string PackagesDirectory => Path.Combine(RootPath, "Packages");

    public string ManifestPath => Path.Combine(PackagesDirectory, "manifest.json");

    public string PackagesLockPath => Path.Combine(PackagesDirectory, "packages-lock.json");

    public string FrozenPackagesDirectory => Path.Combine(PackagesDirectory, "FrozenPackages");

    public string FrozenLockPath => Path.Combine(RootPath, "frozen-lock.json");
}

internal static class DependencySourceClassifier
{
    public static DependencySourceKind FromReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DependencySourceKind.Unknown;
        }

        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return value.Contains("Packages/FrozenPackages", StringComparison.OrdinalIgnoreCase)
                ? DependencySourceKind.Embedded
                : DependencySourceKind.Local;
        }

        if (value.StartsWith("git+", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return DependencySourceKind.Git;
        }

        return DependencySourceKind.Registry;
    }

    public static DependencySourceKind FromPackagesLock(string? source, string? version)
    {
        if (!string.IsNullOrWhiteSpace(source) && Enum.TryParse<DependencySourceKind>(source, true, out var parsed))
        {
            return parsed;
        }

        if (!string.IsNullOrWhiteSpace(version))
        {
            return FromReference(version);
        }

        return DependencySourceKind.Unknown;
    }
}

internal static class DependencyFreezerJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
