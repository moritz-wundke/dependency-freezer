using System.Net.Http;

namespace DependencyFreezer;

public sealed class DependencyFreezerEngine : IDisposable
{
    private static readonly StringComparer PackageNameComparer = StringComparer.Ordinal;
    private readonly HttpClient _httpClient;
    private readonly RegistryPackageSource _registryPackageSource;
    private readonly bool _disposeHttpClient;

    public DependencyFreezerEngine(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _disposeHttpClient = httpClient is null;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("dependency-freezer/0.1.0");
        }

        _registryPackageSource = new RegistryPackageSource(_httpClient);
    }

    public async Task<FreezeResult> FreezeAsync(FreezeRequest request, CancellationToken cancellationToken = default)
    {
        var paths = CreateProjectPaths(request.ProjectPath);
        var manifest = await UnityManifestDocument.LoadAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
        var packagesLock = await PackagesLockDocument.LoadAsync(paths.PackagesLockPath, cancellationToken).ConfigureAwait(false);
        var frozenLock = await FrozenLockFileStore.LoadAsync(paths.FrozenLockPath, cancellationToken).ConfigureAwait(false);
        var packagesToFreeze = ResolvePackagesToFreeze(request, manifest, packagesLock, frozenLock);
        if (packagesToFreeze.Count == 0)
        {
            return new FreezeResult(Array.Empty<string>(), paths.FrozenLockPath);
        }

        var operationRoot = Path.Combine(Path.GetTempPath(), "dependency-freezer", Guid.NewGuid().ToString("n"));
        var stagingRoot = Path.Combine(operationRoot, "staging");
        Directory.CreateDirectory(stagingRoot);

        try
        {
            var preparedPackages = new List<PreparedFrozenPackage>(packagesToFreeze.Count);
            foreach (var package in packagesToFreeze.Values.OrderBy(package => package.Name, PackageNameComparer))
            {
                var registryUrl = package.RegistryUrl ?? throw new InvalidOperationException($"Unable to determine a registry URL for '{package.Name}@{package.Version}'.");
                var metadata = await _registryPackageSource.GetPackageMetadataAsync(registryUrl, package.Name, package.Version, cancellationToken).ConfigureAwait(false);
                var tarball = await _registryPackageSource.DownloadTarballAsync(metadata.TarballUrl, cancellationToken).ConfigureAwait(false);
                var integrity = RegistryPackageSource.ResolveIntegrity(metadata, tarball);

                var stagedDirectory = Path.Combine(stagingRoot, TarballUtilities.SanitizePackagePath(package.Name));
                if (Directory.Exists(stagedDirectory))
                {
                    Directory.Delete(stagedDirectory, recursive: true);
                }

                TarballUtilities.ExtractPackageTarball(tarball, stagedDirectory);
                var (embeddedName, embeddedVersion) = TarballUtilities.ReadExtractedPackageIdentity(stagedDirectory);
                if (!string.Equals(embeddedName, package.Name, StringComparison.Ordinal)
                    || !string.Equals(embeddedVersion, package.Version, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Extracted package '{embeddedName}@{embeddedVersion}' does not match expected '{package.Name}@{package.Version}'.");
                }

                preparedPackages.Add(new PreparedFrozenPackage(
                    package,
                    stagedDirectory,
                    NormalizeEmbeddedPath(package.Name),
                    metadata.TarballUrl,
                    integrity,
                    TarballUtilities.ComputeDirectoryHash(stagedDirectory)));
            }

            await ApplyFreezeAsync(paths, manifest, frozenLock, preparedPackages, cancellationToken).ConfigureAwait(false);
            return new FreezeResult(preparedPackages.Select(package => package.Package.Name).OrderBy(name => name, PackageNameComparer).ToArray(), paths.FrozenLockPath);
        }
        finally
        {
            if (Directory.Exists(operationRoot))
            {
                Directory.Delete(operationRoot, recursive: true);
            }
        }
    }

    public async Task<UnfreezePreview> PreviewUnfreezeAsync(UnfreezeRequest request, CancellationToken cancellationToken = default)
    {
        var paths = CreateProjectPaths(request.ProjectPath);
        _ = await UnityManifestDocument.LoadAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
        var frozenLock = await FrozenLockFileStore.LoadAsync(paths.FrozenLockPath, cancellationToken).ConfigureAwait(false);
        if (frozenLock.Packages.Count == 0)
        {
            return new UnfreezePreview(Array.Empty<string>(), Array.Empty<string>());
        }

        var impacted = request.UnfreezeAll
            ? frozenLock.Packages.Keys.ToHashSet(PackageNameComparer)
            : ResolveUnfreezeClosure(request.PackageNames, frozenLock);

        if (request.UnfreezeAll)
        {
            return new UnfreezePreview(impacted.OrderBy(name => name, PackageNameComparer).ToArray(), Array.Empty<string>());
        }

        var remaining = frozenLock.Packages.Keys.Where(name => !impacted.Contains(name)).ToArray();
        var requiredByRemaining = ExpandClosure(remaining, frozenLock);
        var blocking = impacted.Where(requiredByRemaining.Contains).OrderBy(name => name, PackageNameComparer).ToArray();
        return new UnfreezePreview(impacted.OrderBy(name => name, PackageNameComparer).ToArray(), blocking);
    }

    public async Task<UnfreezeResult> UnfreezeAsync(UnfreezeRequest request, CancellationToken cancellationToken = default)
    {
        var preview = await PreviewUnfreezeAsync(request, cancellationToken).ConfigureAwait(false);
        if (!preview.CanProceed)
        {
            throw new InvalidOperationException($"Cannot unfreeze the selected package set because these packages are still required by other frozen roots: {string.Join(", ", preview.BlockingPackages)}.");
        }

        var paths = CreateProjectPaths(request.ProjectPath);
        var manifest = await UnityManifestDocument.LoadAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
        var frozenLock = await FrozenLockFileStore.LoadAsync(paths.FrozenLockPath, cancellationToken).ConfigureAwait(false);
        var originalManifest = File.Exists(paths.ManifestPath) ? await File.ReadAllTextAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false) : null;
        var originalLock = File.Exists(paths.FrozenLockPath) ? await File.ReadAllTextAsync(paths.FrozenLockPath, cancellationToken).ConfigureAwait(false) : null;
        var backups = new Dictionary<string, string>(PackageNameComparer);

        try
        {
            foreach (var packageName in preview.ImpactedPackages)
            {
                if (!frozenLock.Packages.TryGetValue(packageName, out var entry))
                {
                    continue;
                }

                if (entry.WasDirectDependency)
                {
                    manifest.SetDependency(packageName, entry.OriginalReference);
                }
                else
                {
                    manifest.RemoveDependency(packageName);
                }

                var fullEmbeddedPath = Path.Combine(paths.RootPath, entry.EmbeddedPath.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(fullEmbeddedPath))
                {
                    var backupDirectory = Path.Combine(Path.GetTempPath(), "dependency-freezer", Guid.NewGuid().ToString("n"), TarballUtilities.SanitizePackagePath(packageName));
                    Directory.CreateDirectory(Path.GetDirectoryName(backupDirectory) ?? throw new InvalidOperationException("Unable to create backup directory."));
                    Directory.Move(fullEmbeddedPath, backupDirectory);
                    backups[packageName] = backupDirectory;
                }

                frozenLock.Packages.Remove(packageName);
            }

            await manifest.WriteAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
            if (frozenLock.Packages.Count == 0)
            {
                if (File.Exists(paths.FrozenLockPath))
                {
                    File.Delete(paths.FrozenLockPath);
                }
            }
            else
            {
                await FrozenLockFileStore.SaveAsync(paths.FrozenLockPath, frozenLock, cancellationToken).ConfigureAwait(false);
            }

            foreach (var backupPath in backups.Values)
            {
                if (Directory.Exists(backupPath))
                {
                    Directory.Delete(backupPath, recursive: true);
                }
            }

            return new UnfreezeResult(preview.ImpactedPackages, paths.FrozenLockPath);
        }
        catch
        {
            if (originalManifest is not null)
            {
                await File.WriteAllTextAsync(paths.ManifestPath, originalManifest, cancellationToken).ConfigureAwait(false);
            }

            if (originalLock is not null)
            {
                await File.WriteAllTextAsync(paths.FrozenLockPath, originalLock, cancellationToken).ConfigureAwait(false);
            }
            else if (File.Exists(paths.FrozenLockPath))
            {
                File.Delete(paths.FrozenLockPath);
            }

            foreach (var (packageName, backupPath) in backups)
            {
                if (!frozenLock.Packages.TryGetValue(packageName, out var entry))
                {
                    continue;
                }

                var destination = Path.Combine(paths.RootPath, entry.EmbeddedPath.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(destination))
                {
                    Directory.Delete(destination, recursive: true);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("Unable to determine embedded package directory."));
                if (Directory.Exists(backupPath))
                {
                    Directory.Move(backupPath, destination);
                }
            }

            throw;
        }
    }

    public async Task<ValidationResult> ValidateAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var paths = CreateProjectPaths(projectPath);
        var manifest = await UnityManifestDocument.LoadAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
        var packagesLock = await PackagesLockDocument.LoadAsync(paths.PackagesLockPath, cancellationToken).ConfigureAwait(false);
        var frozenLock = await FrozenLockFileStore.LoadAsync(paths.FrozenLockPath, cancellationToken).ConfigureAwait(false);
        var issues = new List<ValidationIssue>();
        var packageIssues = new Dictionary<string, List<string>>(PackageNameComparer);

        void AddIssue(string? packageName, string message)
        {
            issues.Add(new ValidationIssue(packageName, message));
            if (packageName is null)
            {
                return;
            }

            if (!packageIssues.TryGetValue(packageName, out var packageIssueList))
            {
                packageIssueList = new List<string>();
                packageIssues[packageName] = packageIssueList;
            }

            packageIssueList.Add(message);
        }

        foreach (var dependency in manifest.GetDependencies())
        {
            if (dependency.SourceKind == DependencySourceKind.Embedded && !frozenLock.Packages.ContainsKey(dependency.Name))
            {
                AddIssue(dependency.Name, $"Manifest dependency '{dependency.Name}' points to an embedded package but is missing from frozen-lock.json.");
            }
        }

        foreach (var entry in frozenLock.Packages.Values.OrderBy(entry => entry.Name, PackageNameComparer))
        {
            var expectedReference = $"file:{entry.EmbeddedPath}";
            var currentDependency = manifest.TryGetDependency(entry.Name);
            if (currentDependency?.Value is null)
            {
                AddIssue(entry.Name, $"Manifest is missing frozen dependency '{entry.Name}'.");
            }
            else if (!string.Equals(currentDependency.Value, expectedReference, StringComparison.Ordinal))
            {
                AddIssue(entry.Name, $"Manifest dependency '{entry.Name}' should point to '{expectedReference}' but points to '{currentDependency.Value}'.");
            }

            var fullEmbeddedPath = Path.Combine(paths.RootPath, entry.EmbeddedPath.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(fullEmbeddedPath))
            {
                AddIssue(entry.Name, $"Embedded package directory '{entry.EmbeddedPath}' is missing.");
                continue;
            }

            try
            {
                var (embeddedName, embeddedVersion) = TarballUtilities.ReadExtractedPackageIdentity(fullEmbeddedPath);
                if (!string.Equals(embeddedName, entry.Name, StringComparison.Ordinal)
                    || !string.Equals(embeddedVersion, entry.Version, StringComparison.Ordinal))
                {
                    AddIssue(entry.Name, $"Embedded package identity '{embeddedName}@{embeddedVersion}' does not match '{entry.Name}@{entry.Version}'.");
                }

                var directoryHash = TarballUtilities.ComputeDirectoryHash(fullEmbeddedPath);
                if (!string.Equals(directoryHash, entry.DirectoryHash, StringComparison.OrdinalIgnoreCase))
                {
                    AddIssue(entry.Name, $"Embedded package '{entry.Name}' contents drifted from the recorded directory hash.");
                }
            }
            catch (Exception exception)
            {
                AddIssue(entry.Name, exception.Message);
            }
        }

        var packageNames = manifest.GetDependencies().Select(dependency => dependency.Name)
            .Concat(frozenLock.Packages.Keys)
            .Concat(packagesLock.Keys)
            .Distinct(PackageNameComparer)
            .OrderBy(name => name, PackageNameComparer)
            .ToArray();

        var snapshots = new List<PackageStatusSnapshot>(packageNames.Length);
        foreach (var packageName in packageNames)
        {
            manifest.TryGetDependency(packageName);
            var currentDependency = manifest.TryGetDependency(packageName);
            frozenLock.Packages.TryGetValue(packageName, out var lockEntry);
            packagesLock.TryGetValue(packageName, out var packageLockEntry);
            var embeddedPath = Path.Combine(paths.FrozenPackagesDirectory, TarballUtilities.SanitizePackagePath(packageName));
            var state = PackageState.RegistryManaged;
            if (lockEntry is not null)
            {
                state = packageIssues.ContainsKey(packageName) ? PackageState.DriftedBroken : PackageState.FrozenEmbedded;
            }
            else if (Directory.Exists(embeddedPath))
            {
                state = PackageState.UnfrozenPendingUpdate;
            }
            else if (packageIssues.ContainsKey(packageName))
            {
                state = PackageState.DriftedBroken;
            }

            snapshots.Add(new PackageStatusSnapshot(
                packageName,
                state,
                currentDependency?.IsDirect ?? false,
                currentDependency?.Value,
                lockEntry?.OriginalReference,
                (lockEntry?.Dependencies ?? packageLockEntry?.Dependencies.Keys.ToList() ?? new List<string>()).OrderBy(name => name, PackageNameComparer).ToArray()));
        }

        return new ValidationResult(issues.Count == 0, issues, snapshots);
    }

    public async Task<IReadOnlyList<PackageStatusSnapshot>> InspectAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var validation = await ValidateAsync(projectPath, cancellationToken).ConfigureAwait(false);
        return validation.Packages;
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static ProjectPaths CreateProjectPaths(string projectPath)
    {
        var rootPath = Path.GetFullPath(projectPath);
        var paths = new ProjectPaths(rootPath);
        if (!File.Exists(paths.ManifestPath))
        {
            throw new FileNotFoundException($"Could not find Unity manifest at '{paths.ManifestPath}'.", paths.ManifestPath);
        }

        return paths;
    }

    private static Dictionary<string, ResolvedPackage> ResolvePackagesToFreeze(
        FreezeRequest request,
        UnityManifestDocument manifest,
        IReadOnlyDictionary<string, PackagesLockEntry> packagesLock,
        FrozenLockFile frozenLock)
    {
        var selectedPackages = (request.PackageNames is { Count: > 0 } ? request.PackageNames : manifest.GetDependencies().Select(dependency => dependency.Name))
            .Distinct(PackageNameComparer)
            .ToArray();
        var result = new Dictionary<string, ResolvedPackage>(PackageNameComparer);
        foreach (var rootName in selectedPackages)
        {
            var directDependency = manifest.TryGetDependency(rootName);
            if (directDependency is null && !frozenLock.Packages.ContainsKey(rootName))
            {
                throw new InvalidOperationException($"Package '{rootName}' is not present in Packages/manifest.json and is not already frozen.");
            }

            if (directDependency is { SourceKind: not DependencySourceKind.Registry and not DependencySourceKind.Embedded }
                && !frozenLock.Packages.ContainsKey(rootName))
            {
                throw new InvalidOperationException($"Package '{rootName}' uses unsupported source '{directDependency.SourceKind}'. The initial implementation only supports registry-hosted dependencies.");
            }

            AddPackage(rootName, rootName, FreezeMode.Explicit, true);
        }

        return result;

        void AddPackage(string packageName, string requestedBy, FreezeMode freezeMode, bool isRoot)
        {
            if (result.TryGetValue(packageName, out var existing))
            {
                var requestedBySet = existing.RequestedBy.ToHashSet(PackageNameComparer);
                requestedBySet.Add(requestedBy);
                result[packageName] = existing with { RequestedBy = requestedBySet.OrderBy(name => name, PackageNameComparer).ToArray() };
                return;
            }

            if (!TryResolvePackage(packageName, manifest, packagesLock, frozenLock, out var resolvedPackage))
            {
                throw new InvalidOperationException($"Package '{packageName}' could not be resolved from Packages/packages-lock.json or frozen-lock.json.");
            }

            if (resolvedPackage.SourceKind != DependencySourceKind.Registry)
            {
                throw new InvalidOperationException($"Package '{packageName}' resolved to unsupported source '{resolvedPackage.SourceKind}'. Only registry-hosted packages can be frozen in the current implementation.");
            }

            var effectiveMode = isRoot ? FreezeMode.Explicit : freezeMode;
            result[packageName] = resolvedPackage with { FreezeMode = effectiveMode, RequestedBy = new[] { requestedBy } };
            foreach (var dependencyName in resolvedPackage.Dependencies.OrderBy(name => name, PackageNameComparer))
            {
                AddPackage(dependencyName, requestedBy, FreezeMode.Transitive, false);
            }
        }
    }

    private static bool TryResolvePackage(
        string packageName,
        UnityManifestDocument manifest,
        IReadOnlyDictionary<string, PackagesLockEntry> packagesLock,
        FrozenLockFile frozenLock,
        out ResolvedPackage resolvedPackage)
    {
        if (packagesLock.TryGetValue(packageName, out var packagesLockEntry))
        {
            frozenLock.Packages.TryGetValue(packageName, out var existingFrozenEntry);
            var directDependency = manifest.TryGetDependency(packageName);
            var originalReference = existingFrozenEntry?.OriginalReference ?? directDependency?.Value ?? packagesLockEntry.Version;
            var wasDirectDependency = existingFrozenEntry?.WasDirectDependency ?? directDependency is not null;
            resolvedPackage = new ResolvedPackage(
                packageName,
                packagesLockEntry.Version,
                packagesLockEntry.SourceKind,
                packagesLockEntry.RegistryUrl ?? ResolveRegistryUrl(packageName, manifest.ScopedRegistries),
                originalReference,
                wasDirectDependency,
                FreezeMode.Transitive,
                packagesLockEntry.Dependencies.Keys.OrderBy(name => name, PackageNameComparer).ToArray(),
                Array.Empty<string>());
            return true;
        }

        if (frozenLock.Packages.TryGetValue(packageName, out var frozenEntry))
        {
            resolvedPackage = new ResolvedPackage(
                packageName,
                frozenEntry.Version,
                frozenEntry.OriginalSourceKind,
                frozenEntry.RegistryUrl,
                frozenEntry.OriginalReference,
                frozenEntry.WasDirectDependency,
                frozenEntry.FreezeMode,
                frozenEntry.Dependencies.OrderBy(name => name, PackageNameComparer).ToArray(),
                frozenEntry.RequestedBy.OrderBy(name => name, PackageNameComparer).ToArray());
            return true;
        }

        resolvedPackage = null!;
        return false;
    }

    private static string ResolveRegistryUrl(string packageName, IReadOnlyList<ScopedRegistry> scopedRegistries)
    {
        foreach (var registry in scopedRegistries)
        {
            if (registry.Scopes.Any(scope => packageName.StartsWith(scope, StringComparison.Ordinal)))
            {
                return registry.Url;
            }
        }

        return "https://packages.unity.com";
    }

    private static string NormalizeEmbeddedPath(string packageName) => $"Packages/FrozenPackages/{TarballUtilities.SanitizePackagePath(packageName)}";

    private static HashSet<string> ResolveUnfreezeClosure(IReadOnlyCollection<string>? requestedPackages, FrozenLockFile frozenLock)
    {
        if (requestedPackages is null || requestedPackages.Count == 0)
        {
            throw new InvalidOperationException("Specify one or more packages to unfreeze, or use --all.");
        }

        var impacted = new HashSet<string>(PackageNameComparer);
        foreach (var packageName in requestedPackages)
        {
            if (!frozenLock.Packages.ContainsKey(packageName))
            {
                throw new InvalidOperationException($"Package '{packageName}' is not currently frozen.");
            }

            foreach (var item in ExpandClosure(new[] { packageName }, frozenLock))
            {
                impacted.Add(item);
            }
        }

        return impacted;
    }

    private static HashSet<string> ExpandClosure(IEnumerable<string> roots, FrozenLockFile frozenLock)
    {
        var visited = new HashSet<string>(PackageNameComparer);
        var stack = new Stack<string>(roots.Distinct(PackageNameComparer));
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current) || !frozenLock.Packages.TryGetValue(current, out var entry))
            {
                continue;
            }

            foreach (var dependency in entry.Dependencies)
            {
                stack.Push(dependency);
            }
        }

        return visited;
    }

    private static async Task ApplyFreezeAsync(
        ProjectPaths paths,
        UnityManifestDocument manifest,
        FrozenLockFile frozenLock,
        IReadOnlyList<PreparedFrozenPackage> preparedPackages,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.FrozenPackagesDirectory);
        var originalManifest = File.Exists(paths.ManifestPath) ? await File.ReadAllTextAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false) : null;
        var originalLock = File.Exists(paths.FrozenLockPath) ? await File.ReadAllTextAsync(paths.FrozenLockPath, cancellationToken).ConfigureAwait(false) : null;
        var backups = new Dictionary<string, string>(PackageNameComparer);
        var deployedDestinations = new List<string>();

        try
        {
            foreach (var preparedPackage in preparedPackages)
            {
                var destination = Path.Combine(paths.RootPath, preparedPackage.EmbeddedPath.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(destination))
                {
                    var backup = Path.Combine(Path.GetTempPath(), "dependency-freezer", Guid.NewGuid().ToString("n"), TarballUtilities.SanitizePackagePath(preparedPackage.Package.Name));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup) ?? throw new InvalidOperationException("Unable to create backup directory."));
                    Directory.Move(destination, backup);
                    backups[preparedPackage.Package.Name] = backup;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("Unable to create embedded package directory."));
                Directory.Move(preparedPackage.StagedDirectory, destination);
                deployedDestinations.Add(destination);
                manifest.SetDependency(preparedPackage.Package.Name, $"file:{preparedPackage.EmbeddedPath}");
                frozenLock.Packages[preparedPackage.Package.Name] = new FrozenPackageLockEntry
                {
                    Name = preparedPackage.Package.Name,
                    Version = preparedPackage.Package.Version,
                    OriginalReference = preparedPackage.Package.OriginalReference ?? preparedPackage.Package.Version,
                    OriginalSourceKind = preparedPackage.Package.SourceKind,
                    WasDirectDependency = preparedPackage.Package.WasDirectDependency,
                    FreezeMode = preparedPackage.Package.FreezeMode,
                    RegistryUrl = preparedPackage.Package.RegistryUrl ?? throw new InvalidOperationException($"Package '{preparedPackage.Package.Name}' is missing a registry URL."),
                    TarballUrl = preparedPackage.TarballUrl,
                    Integrity = preparedPackage.Integrity,
                    EmbeddedPath = preparedPackage.EmbeddedPath,
                    DirectoryHash = preparedPackage.DirectoryHash,
                    Dependencies = preparedPackage.Package.Dependencies.OrderBy(name => name, PackageNameComparer).ToList(),
                    RequestedBy = preparedPackage.Package.RequestedBy.OrderBy(name => name, PackageNameComparer).ToList(),
                    FrozenAt = DateTimeOffset.UtcNow,
                };
            }

            await manifest.WriteAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
            await FrozenLockFileStore.SaveAsync(paths.FrozenLockPath, frozenLock, cancellationToken).ConfigureAwait(false);

            foreach (var backup in backups.Values)
            {
                if (Directory.Exists(backup))
                {
                    Directory.Delete(backup, recursive: true);
                }
            }
        }
        catch
        {
            if (originalManifest is not null)
            {
                await File.WriteAllTextAsync(paths.ManifestPath, originalManifest, cancellationToken).ConfigureAwait(false);
            }

            if (originalLock is not null)
            {
                await File.WriteAllTextAsync(paths.FrozenLockPath, originalLock, cancellationToken).ConfigureAwait(false);
            }
            else if (File.Exists(paths.FrozenLockPath))
            {
                File.Delete(paths.FrozenLockPath);
            }

            foreach (var destination in deployedDestinations)
            {
                if (Directory.Exists(destination))
                {
                    Directory.Delete(destination, recursive: true);
                }
            }

            foreach (var (packageName, backupPath) in backups)
            {
                var destination = Path.Combine(paths.RootPath, NormalizeEmbeddedPath(packageName).Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("Unable to restore embedded package directory."));
                if (Directory.Exists(backupPath))
                {
                    Directory.Move(backupPath, destination);
                }
            }

            throw;
        }
    }

    private sealed record PreparedFrozenPackage(
        ResolvedPackage Package,
        string StagedDirectory,
        string EmbeddedPath,
        string TarballUrl,
        string Integrity,
        string DirectoryHash);
}
