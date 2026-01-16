using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;

namespace MinRT.NuGet;

/// <summary>
/// Resolves NuGet package dependencies and downloads them.
/// </summary>
public class PackageResolver
{
    private readonly List<string> _feeds = [];
    private readonly List<PackageIdentity> _packages = [];
    private readonly NuGetFramework _framework;
    private readonly string _cacheDirectory;
    private readonly ILogger _logger;

    public PackageResolver(string targetFramework, string cacheDirectory, ILogger? logger = null)
    {
        _framework = NuGetFramework.Parse(targetFramework);
        _cacheDirectory = cacheDirectory;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Adds a NuGet feed URL.
    /// </summary>
    public PackageResolver WithFeed(string feedUrl)
    {
        _feeds.Add(feedUrl);
        return this;
    }

    /// <summary>
    /// Adds a package to resolve.
    /// </summary>
    public PackageResolver WithPackage(string id, string version)
    {
        _packages.Add(new PackageIdentity(id, NuGetVersion.Parse(version)));
        return this;
    }

    /// <summary>
    /// Resolves all packages and their dependencies, downloads them, and returns the assembly paths.
    /// </summary>
    public async Task<ResolvedPackages> ResolveAsync(CancellationToken ct = default)
    {
        if (_feeds.Count == 0)
        {
            _feeds.Add("https://api.nuget.org/v3/index.json");
        }

        var cache = new SourceCacheContext();
        var repositories = _feeds.Select(f => Repository.Factory.GetCoreV3(f)).ToList();

        // Collect all package dependencies
        var allPackages = new HashSet<SourcePackageDependencyInfo>(PackageIdentityComparer.Default);
        
        foreach (var package in _packages)
        {
            await CollectDependenciesAsync(package, repositories, cache, allPackages, ct);
        }

        // Resolve the dependency graph
        var resolverContext = new PackageResolverContext(
            DependencyBehavior.Lowest,
            _packages.Select(p => p.Id),
            Enumerable.Empty<string>(),
            Enumerable.Empty<PackageReference>(),
            Enumerable.Empty<PackageIdentity>(),
            allPackages,
            repositories.Select(r => r.PackageSource),
            _logger);

        var nugetResolver = new global::NuGet.Resolver.PackageResolver();
        var resolvedPackages = nugetResolver.Resolve(resolverContext, ct)
            .Select(p => allPackages.Single(x => PackageIdentityComparer.Default.Equals(x, p)))
            .ToList();

        // Download and extract packages
        var packagePaths = new Dictionary<string, string>();
        var assemblyPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in resolvedPackages)
        {
            var packagePath = await DownloadPackageAsync(package, repositories, cache, ct);
            packagePaths[package.Id] = packagePath;

            // Find assemblies for our target framework
            var reader = new PackageFolderReader(packagePath);
            var libItems = (await reader.GetLibItemsAsync(ct)).ToList();
            
            var bestFramework = NuGetFrameworkUtility.GetNearest(libItems, _framework, item => item.TargetFramework);
            if (bestFramework != null)
            {
                foreach (var item in bestFramework.Items)
                {
                    if (item.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        var fullPath = Path.Combine(packagePath, item);
                        var assemblyName = Path.GetFileNameWithoutExtension(item);
                        assemblyPaths.TryAdd(assemblyName, fullPath);
                    }
                }
            }
        }

        return new ResolvedPackages(packagePaths, assemblyPaths);
    }

    private async Task CollectDependenciesAsync(
        PackageIdentity package,
        List<SourceRepository> repositories,
        SourceCacheContext cache,
        HashSet<SourcePackageDependencyInfo> allPackages,
        CancellationToken ct)
    {
        if (allPackages.Contains(package)) return;

        foreach (var repository in repositories)
        {
            var dependencyResource = await repository.GetResourceAsync<DependencyInfoResource>(ct);
            var dependencyInfo = await dependencyResource.ResolvePackage(
                package, _framework, cache, _logger, ct);

            if (dependencyInfo == null) continue;

            allPackages.Add(dependencyInfo);

            foreach (var dependency in dependencyInfo.Dependencies)
            {
                var depIdentity = new PackageIdentity(dependency.Id, dependency.VersionRange.MinVersion);
                await CollectDependenciesAsync(depIdentity, repositories, cache, allPackages, ct);
            }

            return;
        }

        throw new InvalidOperationException($"Package not found: {package.Id} {package.Version}");
    }

    private async Task<string> DownloadPackageAsync(
        SourcePackageDependencyInfo package,
        List<SourceRepository> repositories,
        SourceCacheContext cache,
        CancellationToken ct)
    {
        var packagePath = Path.Combine(_cacheDirectory, "packages", package.Id.ToLowerInvariant(), package.Version.ToNormalizedString());
        
        if (Directory.Exists(packagePath) && Directory.GetFiles(packagePath, "*.dll", SearchOption.AllDirectories).Length > 0)
        {
            return packagePath;
        }

        Directory.CreateDirectory(packagePath);

        var downloadResource = await package.Source.GetResourceAsync<DownloadResource>(ct);
        var downloadResult = await downloadResource.GetDownloadResourceResultAsync(
            package, new PackageDownloadContext(cache), packagePath, _logger, ct);

        if (downloadResult.Status != DownloadResourceResultStatus.Available)
        {
            throw new InvalidOperationException($"Failed to download package: {package.Id}");
        }

        // Extract
        using var reader = downloadResult.PackageReader;
        var files = await reader.GetFilesAsync(ct);
        
        foreach (var file in files)
        {
            var destPath = Path.Combine(packagePath, file);
            var destDir = Path.GetDirectoryName(destPath)!;
            Directory.CreateDirectory(destDir);

            using var stream = await reader.GetStreamAsync(file, ct);
            using var fileStream = File.Create(destPath);
            await stream.CopyToAsync(fileStream, ct);
        }

        return packagePath;
    }
}

/// <summary>
/// Result of package resolution containing paths to packages and assemblies.
/// </summary>
public class ResolvedPackages
{
    public IReadOnlyDictionary<string, string> PackagePaths { get; }
    public IReadOnlyDictionary<string, string> AssemblyPaths { get; }

    public ResolvedPackages(
        Dictionary<string, string> packagePaths,
        Dictionary<string, string> assemblyPaths)
    {
        PackagePaths = packagePaths;
        AssemblyPaths = assemblyPaths;
    }
}
