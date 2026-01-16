using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;
using NuGetLogLevel = NuGet.Common.LogLevel;
using INuGetLogger = NuGet.Common.ILogger;
using NuGetNullLogger = NuGet.Common.NullLogger;

namespace MinRT.NuGet;

/// <summary>
/// A fluent builder for creating an AssemblyLoadContext from NuGet packages.
/// Handles dependency resolution, downloading, and assembly loading similar to dotnet restore.
/// </summary>
public sealed class NuGetAssemblyLoader
{
    private readonly List<PackageSource> _packageSources = [];
    private readonly List<(string Id, VersionRange Version)> _packages = [];
    private NuGetFramework _framework = NuGetFramework.Parse("net9.0");
    private string _packagesDirectory;
    private string? _nugetConfigPath;
    private string? _nugetConfigRoot;
    private bool _useDefaultConfig;
    private bool _isCollectible;
    private ILogger _logger;
    private DependencyBehavior _dependencyBehavior = DependencyBehavior.Lowest;

    private NuGetAssemblyLoader()
    {
        _packagesDirectory = Path.Combine(Path.GetTempPath(), "minrt-nuget", "packages");
        _logger = NullLogger.Instance;
    }

    /// <summary>
    /// Creates a new NuGetAssemblyLoader builder.
    /// </summary>
    public static NuGetAssemblyLoader CreateBuilder() => new();

    /// <summary>
    /// Adds a package reference with an exact version.
    /// </summary>
    public NuGetAssemblyLoader AddPackage(string packageId, string version)
    {
        _packages.Add((packageId, VersionRange.Parse(version)));
        return this;
    }

    /// <summary>
    /// Adds a package reference with a minimum version.
    /// If allowNewer is true, allows any version >= the specified version.
    /// </summary>
    public NuGetAssemblyLoader AddPackage(string packageId, string version, bool allowNewer)
    {
        if (allowNewer)
        {
            // Create a range like [version, ) - meaning >= version
            var nugetVersion = NuGetVersion.Parse(version);
            _packages.Add((packageId, new VersionRange(minVersion: nugetVersion, includeMinVersion: true)));
        }
        else
        {
            _packages.Add((packageId, VersionRange.Parse(version)));
        }
        return this;
    }

    /// <summary>
    /// Adds a package reference with a version range (e.g., "[1.0.0, 2.0.0)").
    /// </summary>
    public NuGetAssemblyLoader AddPackageRange(string packageId, string versionRange)
    {
        _packages.Add((packageId, VersionRange.Parse(versionRange)));
        return this;
    }

    /// <summary>
    /// Adds a NuGet feed URL.
    /// </summary>
    public NuGetAssemblyLoader AddFeed(string feedUrl, string? name = null)
    {
        _packageSources.Add(new PackageSource(feedUrl, name ?? feedUrl));
        return this;
    }

    /// <summary>
    /// Adds an authenticated NuGet feed.
    /// </summary>
    public NuGetAssemblyLoader AddFeed(string feedUrl, string name, string username, string password)
    {
        var source = new PackageSource(feedUrl, name)
        {
            Credentials = new PackageSourceCredential(name, username, password, isPasswordClearText: true, validAuthenticationTypesText: null)
        };
        _packageSources.Add(source);
        return this;
    }

    /// <summary>
    /// Loads feeds from a specific nuget.config file.
    /// </summary>
    public NuGetAssemblyLoader WithNuGetConfig(string configPath)
    {
        _nugetConfigPath = Path.GetFullPath(configPath);
        return this;
    }

    /// <summary>
    /// Use default NuGet config resolution (like dotnet restore).
    /// Walks up directory tree from root, then user config, then machine-wide config.
    /// </summary>
    /// <param name="root">Root directory to start searching from. Defaults to current directory.</param>
    public NuGetAssemblyLoader UseDefaultNuGetConfig(string? root = null)
    {
        _useDefaultConfig = true;
        _nugetConfigRoot = root != null ? Path.GetFullPath(root) : Directory.GetCurrentDirectory();
        return this;
    }

    /// <summary>
    /// Sets the target framework (e.g., "net9.0", "net8.0").
    /// </summary>
    public NuGetAssemblyLoader WithTargetFramework(string targetFramework)
    {
        _framework = NuGetFramework.Parse(targetFramework);
        return this;
    }

    /// <summary>
    /// Sets the directory where packages are downloaded and cached.
    /// </summary>
    public NuGetAssemblyLoader WithPackagesDirectory(string path)
    {
        _packagesDirectory = Path.GetFullPath(path);
        return this;
    }

    /// <summary>
    /// Sets the dependency resolution behavior.
    /// Default is Lowest (like NuGet restore).
    /// </summary>
    public NuGetAssemblyLoader WithDependencyBehavior(DependencyBehavior behavior)
    {
        _dependencyBehavior = behavior;
        return this;
    }

    /// <summary>
    /// Makes the AssemblyLoadContext collectible (can be unloaded).
    /// WARNING: Collectible ALCs cannot be used with AssemblyLoadContext.Default.Resolving
    /// because non-collectible assemblies cannot reference collectible ones.
    /// Use this only when loading assemblies via reflection within the ALC.
    /// </summary>
    public NuGetAssemblyLoader AsCollectible(bool collectible = true)
    {
        _isCollectible = collectible;
        return this;
    }

    /// <summary>
    /// Sets the logger for diagnostics.
    /// </summary>
    public NuGetAssemblyLoader WithLogger(ILogger logger)
    {
        _logger = logger;
        return this;
    }
    
    /// <summary>
    /// Sets the logger for diagnostics.
    /// </summary>
    public NuGetAssemblyLoader WithLogger(ILogger<NuGetAssemblyLoader> logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>
    /// Sets the logger for diagnostics.
    /// </summary>
    public NuGetAssemblyLoader WithLogger(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<NuGetAssemblyLoader>();
        return this;
    }

    /// <summary>
    /// Resolves all packages and creates an AssemblyLoadContext.
    /// </summary>
    public async Task<NuGetAssemblyLoadContext> BuildAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting NuGet package resolution for {PackageCount} packages targeting {Framework}",
            _packages.Count, _framework);

        // 1. Load package sources
        var sources = await LoadPackageSourcesAsync();
        _logger.LogDebug("Using {SourceCount} package sources", sources.Count);
        foreach (var source in sources)
        {
            _logger.LogDebug("  Source: {SourceName} ({SourceUrl})", source.Name, source.Source);
        }

        // 2. Create repositories
        var repositories = sources.Select(s => Repository.Factory.GetCoreV3(s)).ToList();
        var cache = new SourceCacheContext();
        var nugetLogger = new NuGetLoggingAdapter(_logger);

        // 3. Collect all dependencies
        _logger.LogInformation("Resolving dependency graph...");
        var allDependencies = new HashSet<SourcePackageDependencyInfo>(PackageIdentityComparer.Default);

        foreach (var (id, versionRange) in _packages)
        {
            _logger.LogDebug("Resolving package {PackageId} {VersionRange}", id, versionRange);
            await CollectDependenciesAsync(id, versionRange, repositories, cache, nugetLogger, allDependencies, ct);
        }

        _logger.LogInformation("Found {DependencyCount} packages in dependency graph", allDependencies.Count);

        // 4. Resolve version conflicts
        _logger.LogInformation("Resolving version conflicts using {Behavior} strategy...", _dependencyBehavior);
        var resolvedPackages = ResolveVersionConflicts(allDependencies, nugetLogger, ct);
        _logger.LogInformation("Resolved to {PackageCount} packages", resolvedPackages.Count);

        foreach (var pkg in resolvedPackages.OrderBy(p => p.Id))
        {
            _logger.LogDebug("  {PackageId} {Version}", pkg.Id, pkg.Version);
        }

        // 5. Download and extract packages
        _logger.LogInformation("Downloading packages to {Directory}...", _packagesDirectory);
        var assemblyPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in resolvedPackages)
        {
            var packageDir = await DownloadAndExtractAsync(package, repositories, cache, nugetLogger, ct);
            await CollectAssembliesAsync(package, packageDir, assemblyPaths, ct);
        }

        _logger.LogInformation("Loaded {AssemblyCount} assemblies", assemblyPaths.Count);

        // 6. Create the AssemblyLoadContext
        var context = new NuGetAssemblyLoadContext(_logger, assemblyPaths, _isCollectible);

        _logger.LogInformation("NuGet assembly loader ready (collectible: {IsCollectible})", _isCollectible);
        return context;
    }

    private async Task<List<PackageSource>> LoadPackageSourcesAsync()
    {
        var sources = new List<PackageSource>(_packageSources);
        
        _logger.LogDebug("Starting package source resolution");
        _logger.LogDebug("  Explicit sources count: {Count}", _packageSources.Count);
        _logger.LogDebug("  UseDefaultConfig: {UseDefault}", _useDefaultConfig);
        _logger.LogDebug("  NuGetConfigPath: {ConfigPath}", _nugetConfigPath ?? "(none)");

        // Load from default NuGet config resolution (like dotnet restore)
        if (_useDefaultConfig)
        {
            _logger.LogDebug("Loading package sources using default NuGet config resolution from root: {Root}", _nugetConfigRoot);
            
            var settings = Settings.LoadDefaultSettings(_nugetConfigRoot);
            _logger.LogDebug("Settings loaded, checking config file paths...");
            
            // Log which config files were loaded
            var configFilePaths = settings.GetConfigFilePaths();
            foreach (var configPath in configFilePaths)
            {
                _logger.LogDebug("  Config file: {ConfigFile}", configPath);
            }
            
            var provider = new PackageSourceProvider(settings);
            var allSources = provider.LoadPackageSources().ToList();
            _logger.LogDebug("Found {Count} total sources in config (enabled and disabled)", allSources.Count);

            foreach (var source in allSources)
            {
                _logger.LogDebug("  Source: {Name} ({Url}) - Enabled: {Enabled}", source.Name, source.Source, source.IsEnabled);
                if (source.IsEnabled)
                {
                    sources.Add(source);
                }
            }
        }
        // Load from specific nuget.config file
        else if (_nugetConfigPath != null)
        {
            _logger.LogDebug("Loading package sources from specific config: {ConfigPath}", _nugetConfigPath);

            if (!File.Exists(_nugetConfigPath))
            {
                throw new FileNotFoundException($"NuGet config file not found: {_nugetConfigPath}");
            }

            var configDir = Path.GetDirectoryName(_nugetConfigPath)!;
            var configFile = Path.GetFileName(_nugetConfigPath);
            _logger.LogDebug("  Config directory: {Dir}", configDir);
            _logger.LogDebug("  Config filename: {File}", configFile);
            
            var settings = Settings.LoadSpecificSettings(configDir, configFile);
            var provider = new PackageSourceProvider(settings);

            foreach (var source in provider.LoadPackageSources())
            {
                _logger.LogDebug("  Source: {Name} ({Url}) - Enabled: {Enabled}", source.Name, source.Source, source.IsEnabled);
                if (source.IsEnabled)
                {
                    sources.Add(source);
                }
            }
        }
        else
        {
            _logger.LogDebug("No config resolution specified, will use fallback");
        }

        // Add nuget.org as fallback if no sources configured
        if (sources.Count == 0)
        {
            _logger.LogInformation("No package sources configured, using nuget.org as fallback");
            sources.Add(new PackageSource("https://api.nuget.org/v3/index.json", "nuget.org"));
        }

        _logger.LogDebug("Final source count: {Count}", sources.Count);
        return sources;
    }

    private async Task CollectDependenciesAsync(
        string packageId,
        VersionRange versionRange,
        List<SourceRepository> repositories,
        SourceCacheContext cache,
        INuGetLogger nugetLogger,
        HashSet<SourcePackageDependencyInfo> allDependencies,
        CancellationToken ct)
    {
        _logger.LogDebug("CollectDependenciesAsync: Looking for {PackageId} {VersionRange}", packageId, versionRange);
        _logger.LogDebug("  Searching {Count} repositories", repositories.Count);
        
        // Find the best version for this package
        PackageIdentity? bestMatch = null;
        SourceRepository? bestSource = null;

        foreach (var repo in repositories)
        {
            _logger.LogDebug("  Querying repository: {Name} ({Url})", repo.PackageSource.Name, repo.PackageSource.Source);
            try
            {
                var findResource = await repo.GetResourceAsync<FindPackageByIdResource>(ct);
                _logger.LogDebug("    Got FindPackageByIdResource");
                
                var versions = await findResource.GetAllVersionsAsync(packageId, cache, nugetLogger, ct);
                var versionList = versions.ToList();
                _logger.LogDebug("    Found {Count} versions for {PackageId}", versionList.Count, packageId);
                
                if (versionList.Count > 0)
                {
                    _logger.LogDebug("    Available versions: {Versions}", string.Join(", ", versionList.Take(10)));
                }

                var matchingVersion = versionRange.FindBestMatch(versionList);
                if (matchingVersion != null)
                {
                    _logger.LogDebug("    Best match from this repo: {Version}", matchingVersion);
                    if (bestMatch == null || versionRange.IsBetter(bestMatch.Version, matchingVersion))
                    {
                        bestMatch = new PackageIdentity(packageId, matchingVersion);
                        bestSource = repo;
                        _logger.LogDebug("    New best overall: {PackageId} {Version} from {Repo}", packageId, matchingVersion, repo.PackageSource.Name);
                    }
                }
                else
                {
                    _logger.LogDebug("    No matching version found in range {Range}", versionRange);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query {Repository} for {PackageId}", repo.PackageSource.Name, packageId);
            }
        }

        if (bestMatch == null)
        {
            _logger.LogError("Package not found: {PackageId} {VersionRange} after searching all repositories", packageId, versionRange);
            throw new InvalidOperationException($"Package not found: {packageId} {versionRange}");
        }

        _logger.LogDebug("Selected {PackageId} {Version} from {Source}", bestMatch.Id, bestMatch.Version, bestSource?.PackageSource.Name);
        await CollectDependenciesRecursiveAsync(bestMatch, repositories, cache, nugetLogger, allDependencies, ct);
    }

    private async Task CollectDependenciesRecursiveAsync(
        PackageIdentity package,
        List<SourceRepository> repositories,
        SourceCacheContext cache,
        INuGetLogger nugetLogger,
        HashSet<SourcePackageDependencyInfo> allDependencies,
        CancellationToken ct)
    {
        if (allDependencies.Any(d => PackageIdentityComparer.Default.Equals(d, package)))
        {
            return;
        }

        foreach (var repo in repositories)
        {
            try
            {
                var dependencyResource = await repo.GetResourceAsync<DependencyInfoResource>(ct);
                var dependencyInfo = await dependencyResource.ResolvePackage(
                    package, _framework, cache, nugetLogger, ct);

                if (dependencyInfo == null) continue;

                allDependencies.Add(dependencyInfo);
                _logger.LogTrace("  Found {PackageId} {Version} with {DepCount} dependencies",
                    dependencyInfo.Id, dependencyInfo.Version, dependencyInfo.Dependencies.Count());

                foreach (var dependency in dependencyInfo.Dependencies)
                {
                    // Find best version for dependency
                    PackageIdentity? depIdentity = null;

                    foreach (var depRepo in repositories)
                    {
                        try
                        {
                            var findResource = await depRepo.GetResourceAsync<FindPackageByIdResource>(ct);
                            var versions = await findResource.GetAllVersionsAsync(dependency.Id, cache, nugetLogger, ct);
                            var matchingVersion = dependency.VersionRange.FindBestMatch(versions);

                            if (matchingVersion != null)
                            {
                                if (depIdentity == null || dependency.VersionRange.IsBetter(depIdentity.Version, matchingVersion))
                                {
                                    depIdentity = new PackageIdentity(dependency.Id, matchingVersion);
                                }
                            }
                        }
                        catch { }
                    }

                    if (depIdentity != null)
                    {
                        await CollectDependenciesRecursiveAsync(depIdentity, repositories, cache, nugetLogger, allDependencies, ct);
                    }
                }

                return;
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Failed to get dependency info from {Repository}", repo.PackageSource.Name);
            }
        }

        throw new InvalidOperationException($"Could not resolve package: {package.Id} {package.Version}");
    }

    private List<SourcePackageDependencyInfo> ResolveVersionConflicts(
        HashSet<SourcePackageDependencyInfo> allDependencies,
        INuGetLogger nugetLogger,
        CancellationToken ct)
    {
        var resolverContext = new PackageResolverContext(
            _dependencyBehavior,
            _packages.Select(p => p.Id),
            Enumerable.Empty<string>(),
            Enumerable.Empty<PackageReference>(),
            Enumerable.Empty<PackageIdentity>(),
            allDependencies,
            allDependencies.Select(d => d.Source.PackageSource).Distinct(),
            nugetLogger);

        var resolver = new PackageResolver();
        var resolved = resolver.Resolve(resolverContext, ct);

        return resolved
            .Select(p => allDependencies.Single(d => PackageIdentityComparer.Default.Equals(d, p)))
            .ToList();
    }

    private async Task<string> DownloadAndExtractAsync(
        SourcePackageDependencyInfo package,
        List<SourceRepository> repositories,
        SourceCacheContext cache,
        INuGetLogger nugetLogger,
        CancellationToken ct)
    {
        var packageDir = Path.Combine(_packagesDirectory, package.Id.ToLowerInvariant(), package.Version.ToNormalizedString());

        // Check if already downloaded
        if (Directory.Exists(packageDir) && Directory.GetFiles(packageDir, "*.nuspec").Length > 0)
        {
            _logger.LogDebug("Package {PackageId} {Version} already cached", package.Id, package.Version);
            return packageDir;
        }

        _logger.LogDebug("Downloading {PackageId} {Version}...", package.Id, package.Version);

        var downloadResource = await package.Source.GetResourceAsync<DownloadResource>(ct);
        var downloadResult = await downloadResource.GetDownloadResourceResultAsync(
            package,
            new PackageDownloadContext(cache),
            globalPackagesFolder: _packagesDirectory,
            nugetLogger,
            ct);

        if (downloadResult.Status != DownloadResourceResultStatus.Available)
        {
            throw new InvalidOperationException($"Failed to download package: {package.Id} {package.Version}");
        }

        // The download already extracts to globalPackagesFolder - just return the path
        // Dispose the result to release file handles
        downloadResult.Dispose();

        _logger.LogDebug("Installed {PackageId} {Version}", package.Id, package.Version);
        return packageDir;
    }

    private async Task CollectAssembliesAsync(
        PackageIdentity package,
        string packageDir,
        Dictionary<string, string> assemblyPaths,
        CancellationToken ct)
    {
        var reader = new PackageFolderReader(packageDir);
        var libItems = (await reader.GetLibItemsAsync(ct)).ToList();

        var bestFramework = NuGetFrameworkUtility.GetNearest(libItems, _framework, item => item.TargetFramework);

        if (bestFramework == null)
        {
            _logger.LogDebug("No compatible lib items for {PackageId} targeting {Framework}", package.Id, _framework);
            return;
        }

        _logger.LogDebug("Using {TargetFramework} from {PackageId}", bestFramework.TargetFramework, package.Id);

        foreach (var item in bestFramework.Items)
        {
            if (item.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var fullPath = Path.Combine(packageDir, item);
                var assemblyName = Path.GetFileNameWithoutExtension(item);

                if (!assemblyPaths.ContainsKey(assemblyName))
                {
                    assemblyPaths[assemblyName] = fullPath;
                    _logger.LogTrace("  Assembly: {AssemblyName} -> {Path}", assemblyName, fullPath);
                }
            }
        }
    }
}

/// <summary>
/// An AssemblyLoadContext that resolves assemblies from NuGet packages.
/// </summary>
public sealed class NuGetAssemblyLoadContext : AssemblyLoadContext
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, string> _assemblyPaths;
    private readonly Dictionary<string, Assembly> _loadedAssemblies = new(StringComparer.OrdinalIgnoreCase);

    internal NuGetAssemblyLoadContext(ILogger logger, Dictionary<string, string> assemblyPaths, bool isCollectible = false)
        : base(name: "NuGetAssemblyLoadContext", isCollectible: isCollectible)
    {
        _logger = logger;
        _assemblyPaths = assemblyPaths;
    }

    /// <summary>
    /// Gets the resolved assembly paths.
    /// </summary>
    public IReadOnlyDictionary<string, string> AssemblyPaths => _assemblyPaths;

    /// <summary>
    /// Loads an assembly by name.
    /// </summary>
    public Assembly LoadAssembly(string assemblyName)
    {
        return LoadFromAssemblyName(new AssemblyName(assemblyName));
    }

    /// <summary>
    /// Gets a type from the loaded assemblies.
    /// </summary>
    public Type? GetType(string assemblyName, string typeName)
    {
        var assembly = LoadAssembly(assemblyName);
        return assembly.GetType(typeName);
    }

    /// <summary>
    /// Creates an instance of a type from the loaded assemblies.
    /// </summary>
    public object? CreateInstance(string assemblyName, string typeName, params object[] args)
    {
        var type = GetType(assemblyName, typeName)
            ?? throw new TypeLoadException($"Type {typeName} not found in {assemblyName}");
        return Activator.CreateInstance(type, args);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name == null) return null;

        // Check if already loaded in this context
        if (_loadedAssemblies.TryGetValue(name, out var loaded))
        {
            return loaded;
        }

        // Check our resolved paths
        if (_assemblyPaths.TryGetValue(name, out var path))
        {
            if (File.Exists(path))
            {
                _logger.LogDebug("Loading assembly {AssemblyName} from {Path}", name, path);
                var assembly = LoadFromAssemblyPath(path);
                _loadedAssemblies[name] = assembly;
                return assembly;
            }
            else
            {
                _logger.LogWarning("Assembly file not found: {Path}", path);
            }
        }

        _logger.LogTrace("Assembly {AssemblyName} not found in NuGet packages, falling back to default", name);
        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        // For now, just fall back to default
        _logger.LogTrace("Native library {Name} requested, falling back to default", unmanagedDllName);
        return IntPtr.Zero;
    }
}

/// <summary>
/// Adapts Microsoft.Extensions.Logging.ILogger to INuGetLogger.
/// </summary>
internal sealed class NuGetLoggingAdapter : INuGetLogger
{
    private readonly ILogger _logger;

    public NuGetLoggingAdapter(ILogger logger)
    {
        _logger = logger;
    }

    public void LogDebug(string data) => _logger.LogDebug("{NuGetMessage}", data);
    public void LogVerbose(string data) => _logger.LogTrace("{NuGetMessage}", data);
    public void LogInformation(string data) => _logger.LogInformation("{NuGetMessage}", data);
    public void LogMinimal(string data) => _logger.LogInformation("{NuGetMessage}", data);
    public void LogWarning(string data) => _logger.LogWarning("{NuGetMessage}", data);
    public void LogError(string data) => _logger.LogError("{NuGetMessage}", data);
    public void LogInformationSummary(string data) => _logger.LogInformation("{NuGetMessage}", data);

    public void Log(NuGetLogLevel level, string data)
    {
        var msLevel = level switch
        {
            NuGetLogLevel.Debug => Microsoft.Extensions.Logging.LogLevel.Debug,
            NuGetLogLevel.Verbose => Microsoft.Extensions.Logging.LogLevel.Trace,
            NuGetLogLevel.Information => Microsoft.Extensions.Logging.LogLevel.Information,
            NuGetLogLevel.Minimal => Microsoft.Extensions.Logging.LogLevel.Information,
            NuGetLogLevel.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
            NuGetLogLevel.Error => Microsoft.Extensions.Logging.LogLevel.Error,
            _ => Microsoft.Extensions.Logging.LogLevel.Information
        };
        _logger.Log(msLevel, "{NuGetMessage}", data);
    }

    public Task LogAsync(NuGetLogLevel level, string data)
    {
        Log(level, data);
        return Task.CompletedTask;
    }

    public void Log(global::NuGet.Common.ILogMessage message) => Log(message.Level, message.Message);
    public Task LogAsync(global::NuGet.Common.ILogMessage message) => LogAsync(message.Level, message.Message);
}
