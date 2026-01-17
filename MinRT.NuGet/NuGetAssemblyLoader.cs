using System.Reflection;
using System.Runtime.InteropServices;
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
using NuGet.RuntimeModel;
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
    private readonly Dictionary<string, List<string>> _sourceMapping = new(StringComparer.OrdinalIgnoreCase);
    private NuGetFramework _framework = NuGetFramework.Parse("net9.0");
    private string? _runtimeIdentifier;
    private string _packagesDirectory;
    private string? _nugetConfigPath;
    private string? _nugetConfigRoot;
    private bool _useDefaultConfig;
    private bool _useSourceMappingFromConfig;
    private bool _isCollectible;
    private ILogger _logger;
    private DependencyBehavior _dependencyBehavior = DependencyBehavior.Lowest;

    private NuGetAssemblyLoader()
    {
        _packagesDirectory = Path.Combine(Path.GetTempPath(), "minrt-nuget", "packages");
        _logger = NullLogger.Instance;
        _runtimeIdentifier = GetCurrentRuntimeIdentifier();
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
    /// Sets the runtime identifier for RID-specific asset selection (e.g., "win-x64", "linux-x64", "osx-arm64").
    /// Defaults to the current platform's RID.
    /// </summary>
    public NuGetAssemblyLoader WithRuntimeIdentifier(string runtimeIdentifier)
    {
        _runtimeIdentifier = runtimeIdentifier;
        return this;
    }

    /// <summary>
    /// Adds a source mapping rule that restricts which packages can come from which sources.
    /// Patterns support glob wildcards (e.g., "Newtonsoft.*", "Microsoft.*").
    /// </summary>
    /// <param name="sourceName">The name of the package source (must match a configured source name).</param>
    /// <param name="packagePatterns">Package ID patterns that should come from this source.</param>
    public NuGetAssemblyLoader WithSourceMapping(string sourceName, params string[] packagePatterns)
    {
        if (!_sourceMapping.TryGetValue(sourceName, out var patterns))
        {
            patterns = [];
            _sourceMapping[sourceName] = patterns;
        }
        patterns.AddRange(packagePatterns);
        return this;
    }

    /// <summary>
    /// Reads package source mapping from the nuget.config file.
    /// Only works when UseDefaultNuGetConfig() or WithNuGetConfig() is also called.
    /// </summary>
    public NuGetAssemblyLoader UseSourceMappingFromConfig()
    {
        _useSourceMappingFromConfig = true;
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
        _logger.LogInformation("Starting NuGet package resolution for {PackageCount} packages targeting {Framework} ({RID})",
            _packages.Count, _framework, _runtimeIdentifier);

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

        // 5. Download and extract packages, collecting both managed and native assets
        _logger.LogInformation("Downloading packages to {Directory}...", _packagesDirectory);
        var assemblyPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nativeLibraryPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var frameworkReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in resolvedPackages)
        {
            var packageDir = await DownloadAndExtractAsync(package, repositories, cache, nugetLogger, ct);
            await CollectAssetsAsync(package, packageDir, assemblyPaths, nativeLibraryPaths, frameworkReferences, ct);
        }

        _logger.LogInformation("Loaded {AssemblyCount} assemblies, {NativeCount} native libraries", 
            assemblyPaths.Count, nativeLibraryPaths.Count);
        
        if (frameworkReferences.Count > 0)
        {
            _logger.LogInformation("Required framework references: {FrameworkRefs}", 
                string.Join(", ", frameworkReferences.OrderBy(x => x)));
        }

        // 6. Create the AssemblyLoadContext
        var context = new NuGetAssemblyLoadContext(_logger, assemblyPaths, nativeLibraryPaths, frameworkReferences, _isCollectible);

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
            
            // Load source mapping from config if requested
            LoadSourceMappingFromSettings(settings);
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
            
            // Load source mapping from config if requested
            LoadSourceMappingFromSettings(settings);
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

    private PackageSourceMapping? _packageSourceMapping;

    private void LoadSourceMappingFromSettings(ISettings settings)
    {
        if (!_useSourceMappingFromConfig)
            return;

        _packageSourceMapping = PackageSourceMapping.GetPackageSourceMapping(settings);
        if (!_packageSourceMapping.IsEnabled)
        {
            _logger.LogDebug("No source mapping found in config");
            _packageSourceMapping = null;
            return;
        }

        _logger.LogDebug("Source mapping enabled from config");
    }

    private List<SourceRepository> FilterRepositoriesForPackage(string packageId, List<SourceRepository> allRepositories)
    {
        // If no source mapping configured (neither programmatic nor from config), use all repositories
        if (_sourceMapping.Count == 0 && _packageSourceMapping == null)
            return allRepositories;

        var matchingSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // Check programmatic source mapping first
        foreach (var (sourceName, patterns) in _sourceMapping)
        {
            foreach (var pattern in patterns)
            {
                if (PackagePatternMatches(packageId, pattern))
                {
                    matchingSources.Add(sourceName);
                    _logger.LogDebug("    Package {PackageId} matches pattern '{Pattern}' for source {Source}", 
                        packageId, pattern, sourceName);
                    break;
                }
            }
        }
        
        // Check config-based source mapping
        if (_packageSourceMapping != null)
        {
            var configSources = _packageSourceMapping.GetConfiguredPackageSources(packageId);
            foreach (var sourceName in configSources)
            {
                matchingSources.Add(sourceName);
                _logger.LogDebug("    Package {PackageId} matches config source mapping for source {Source}", 
                    packageId, sourceName);
            }
        }

        // If package matches no patterns, use all sources (warn only if mapping was configured)
        if (matchingSources.Count == 0)
        {
            if (_sourceMapping.Count > 0 || _packageSourceMapping != null)
            {
                _logger.LogWarning("Package {PackageId} does not match any source mapping patterns. Using all sources.", packageId);
            }
            return allRepositories;
        }

        // Filter to only matching sources
        var filtered = allRepositories.Where(r => matchingSources.Contains(r.PackageSource.Name)).ToList();
        if (filtered.Count == 0)
        {
            _logger.LogWarning("No configured sources match patterns for {PackageId}. Using all sources.", packageId);
            return allRepositories;
        }

        return filtered;
    }

    private static bool PackagePatternMatches(string packageId, string pattern)
    {
        // NuGet's pattern matching is case-insensitive
        // Patterns support * as a suffix wildcard (e.g., "Newtonsoft.*" matches "Newtonsoft.Json")
        
        // Handle catch-all pattern
        if (pattern == "*")
            return true;
        
        // Glob matching: * at the end matches any suffix
        if (pattern.EndsWith("*"))
        {
            var prefix = pattern[..^1];
            return packageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        
        // Exact match (case-insensitive like NuGet)
        return packageId.Equals(pattern, StringComparison.OrdinalIgnoreCase);
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
        
        // Filter repositories based on source mapping
        var filteredRepos = FilterRepositoriesForPackage(packageId, repositories);
        _logger.LogDebug("  Searching {Count} repositories (filtered from {Total})", filteredRepos.Count, repositories.Count);
        
        // Find the best version for this package
        PackageIdentity? bestMatch = null;
        SourceRepository? bestSource = null;

        foreach (var repo in filteredRepos)
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

    private async Task CollectAssetsAsync(
        PackageIdentity package,
        string packageDir,
        Dictionary<string, string> assemblyPaths,
        Dictionary<string, string> nativeLibraryPaths,
        HashSet<string> frameworkReferences,
        CancellationToken ct)
    {
        var reader = new PackageFolderReader(packageDir);
        
        // Get all files in the package
        var files = (await reader.GetFilesAsync(ct)).ToList();
        
        // 1. First try RID-specific managed assemblies: runtimes/{rid}/lib/{tfm}/
        var ridLibCollected = await CollectRidSpecificAssembliesAsync(package, packageDir, files, assemblyPaths);
        
        // 2. If no RID-specific assemblies found, fall back to portable: lib/{tfm}/
        if (!ridLibCollected)
        {
            await CollectPortableAssembliesAsync(package, packageDir, reader, assemblyPaths, ct);
        }
        
        // 3. Collect native libraries: runtimes/{rid}/native/
        CollectNativeLibraries(package, packageDir, files, nativeLibraryPaths);
        
        // 4. Collect framework references from nuspec
        await CollectFrameworkReferencesAsync(reader, frameworkReferences, ct);
    }

    private Task<bool> CollectRidSpecificAssembliesAsync(
        PackageIdentity package,
        string packageDir,
        List<string> files,
        Dictionary<string, string> assemblyPaths)
    {
        if (_runtimeIdentifier == null) return Task.FromResult(false);
        
        // Get compatible RIDs (including fallbacks)
        var compatibleRids = GetCompatibleRids(_runtimeIdentifier);
        var found = false;
        
        foreach (var rid in compatibleRids)
        {
            // Pattern: runtimes/{rid}/lib/{tfm}/*.dll
            var ridLibPrefix = $"runtimes/{rid}/lib/";
            var ridLibFiles = files
                .Where(f => f.StartsWith(ridLibPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            
            if (ridLibFiles.Count == 0) continue;
            
            // Find best TFM match within this RID
            var tfmGroups = ridLibFiles
                .Select(f => {
                    var parts = f.Substring(ridLibPrefix.Length).Split('/');
                    return parts.Length >= 1 ? parts[0] : null;
                })
                .Where(tfm => tfm != null)
                .Distinct()
                .Select(tfm => NuGetFramework.Parse(tfm!))
                .Where(fw => fw != null && DefaultCompatibilityProvider.Instance.IsCompatible(_framework, fw))
                .ToList();
            
            var bestTfm = tfmGroups
                .OrderByDescending(fw => fw.Version)
                .FirstOrDefault();
            
            if (bestTfm == null) continue;
            
            var prefix = $"runtimes/{rid}/lib/{bestTfm.GetShortFolderName()}/";
            var matchingFiles = ridLibFiles.Where(f => 
                f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            
            foreach (var file in matchingFiles)
            {
                var fullPath = Path.Combine(packageDir, file.Replace('/', Path.DirectorySeparatorChar));
                var assemblyName = Path.GetFileNameWithoutExtension(file);
                
                if (!assemblyPaths.ContainsKey(assemblyName) && File.Exists(fullPath))
                {
                    assemblyPaths[assemblyName] = fullPath;
                    _logger.LogDebug("  RID Assembly ({Rid}): {AssemblyName} -> {Path}", rid, assemblyName, fullPath);
                    found = true;
                }
            }
            
            if (found) break; // Use first matching RID
        }
        
        return Task.FromResult(found);
    }

    private async Task CollectPortableAssembliesAsync(
        PackageIdentity package,
        string packageDir,
        PackageFolderReader reader,
        Dictionary<string, string> assemblyPaths,
        CancellationToken ct)
    {
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

    private void CollectNativeLibraries(
        PackageIdentity package,
        string packageDir,
        List<string> files,
        Dictionary<string, string> nativeLibraryPaths)
    {
        if (_runtimeIdentifier == null) return;
        
        var compatibleRids = GetCompatibleRids(_runtimeIdentifier);
        var nativeExtensions = GetNativeLibraryExtensions();
        
        foreach (var rid in compatibleRids)
        {
            // Pattern: runtimes/{rid}/native/*
            var nativePrefix = $"runtimes/{rid}/native/";
            var nativeFiles = files
                .Where(f => f.StartsWith(nativePrefix, StringComparison.OrdinalIgnoreCase))
                .Where(f => nativeExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            
            foreach (var file in nativeFiles)
            {
                var fullPath = Path.Combine(packageDir, file.Replace('/', Path.DirectorySeparatorChar));
                var libName = Path.GetFileNameWithoutExtension(file);
                
                // Remove lib prefix on Unix
                if (libName.StartsWith("lib", StringComparison.OrdinalIgnoreCase) && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    libName = libName.Substring(3);
                }
                
                if (!nativeLibraryPaths.ContainsKey(libName) && File.Exists(fullPath))
                {
                    nativeLibraryPaths[libName] = fullPath;
                    _logger.LogDebug("  Native ({Rid}): {LibName} -> {Path}", rid, libName, fullPath);
                }
            }
            
            if (nativeFiles.Count > 0) break; // Use first matching RID
        }
    }

    private static List<string> GetCompatibleRids(string rid)
    {
        // Simplified RID fallback chain for common scenarios.
        // The SDK uses NuGet's RuntimeGraph from Microsoft.NETCore.Platforms for full RID resolution,
        // which handles complex fallbacks like "linux-musl-x64" -> "linux-musl" -> "linux-x64" -> "linux" -> "unix" -> "any".
        // Our simplified approach covers the common cases but may miss edge cases for distro-specific RIDs.
        // TODO: Consider loading RuntimeGraph from Microsoft.NETCore.Platforms package for full compatibility.
        var rids = new List<string> { rid };
        
        // Add common fallbacks
        if (rid.Contains("-"))
        {
            // e.g., "win-x64" -> "win"
            var basePart = rid.Split('-')[0];
            if (!rids.Contains(basePart))
                rids.Add(basePart);
        }
        
        // Add generic fallbacks
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!rids.Contains("win")) rids.Add("win");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (!rids.Contains("linux")) rids.Add("linux");
            if (!rids.Contains("unix")) rids.Add("unix");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            if (!rids.Contains("osx")) rids.Add("osx");
            if (!rids.Contains("unix")) rids.Add("unix");
        }
        
        rids.Add("any");
        return rids;
    }

    private static string[] GetNativeLibraryExtensions()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return [".dll"];
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return [".dylib", ".so"];
        return [".so"];
    }

    private static string GetCurrentRuntimeIdentifier()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => "x64"
        };
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return $"win-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return $"linux-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return $"osx-{arch}";
        
        return $"linux-{arch}";
    }

    private async Task CollectFrameworkReferencesAsync(
        PackageFolderReader reader,
        HashSet<string> frameworkReferences,
        CancellationToken ct)
    {
        try
        {
            var nuspec = await reader.GetNuspecReaderAsync(ct);
            
            // Get framework references for the target framework
            var groups = nuspec.GetFrameworkRefGroups();
            foreach (var group in groups)
            {
                // Check if this group applies to our target framework
                if (!_framework.IsAny && 
                    !NuGetFrameworkUtility.IsCompatibleWithFallbackCheck(
                        _framework, 
                        group.TargetFramework))
                {
                    continue;
                }

                foreach (var frameworkRef in group.FrameworkReferences)
                {
                    frameworkReferences.Add(frameworkRef.Name);
                    _logger.LogDebug("    Found framework reference: {FrameworkRef}", frameworkRef.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace("Could not read framework references from nuspec: {Error}", ex.Message);
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
    private readonly Dictionary<string, string> _nativeLibraryPaths;
    private readonly HashSet<string> _requiredFrameworkReferences;
    private readonly Dictionary<string, Assembly> _loadedAssemblies = new(StringComparer.OrdinalIgnoreCase);

    internal NuGetAssemblyLoadContext(
        ILogger logger, 
        Dictionary<string, string> assemblyPaths, 
        Dictionary<string, string> nativeLibraryPaths,
        HashSet<string> requiredFrameworkReferences,
        bool isCollectible = false)
        : base(name: "NuGetAssemblyLoadContext", isCollectible: isCollectible)
    {
        _logger = logger;
        _assemblyPaths = assemblyPaths;
        _nativeLibraryPaths = nativeLibraryPaths;
        _requiredFrameworkReferences = requiredFrameworkReferences;
    }

    /// <summary>
    /// Gets the resolved assembly paths.
    /// </summary>
    public IReadOnlyDictionary<string, string> AssemblyPaths => _assemblyPaths;

    /// <summary>
    /// Gets the framework references required by the loaded packages.
    /// Common values include "Microsoft.NETCore.App", "Microsoft.AspNetCore.App", "Microsoft.WindowsDesktop.App".
    /// </summary>
    public IReadOnlySet<string> RequiredFrameworkReferences => _requiredFrameworkReferences;

    /// <summary>
    /// Gets the resolved native library paths.
    /// </summary>
    public IReadOnlyDictionary<string, string> NativeLibraryPaths => _nativeLibraryPaths;

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
        // Try to find the native library in our resolved paths
        var searchNames = GetNativeLibrarySearchNames(unmanagedDllName);
        
        foreach (var searchName in searchNames)
        {
            if (_nativeLibraryPaths.TryGetValue(searchName, out var path))
            {
                if (File.Exists(path))
                {
                    _logger.LogDebug("Loading native library {Name} from {Path}", unmanagedDllName, path);
                    return NativeLibrary.Load(path);
                }
            }
        }
        
        // Also try direct path lookup by file name
        foreach (var kvp in _nativeLibraryPaths)
        {
            var fileName = Path.GetFileNameWithoutExtension(kvp.Value);
            if (fileName.Equals(unmanagedDllName, StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals($"lib{unmanagedDllName}", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(kvp.Value))
                {
                    _logger.LogDebug("Loading native library {Name} from {Path}", unmanagedDllName, kvp.Value);
                    return NativeLibrary.Load(kvp.Value);
                }
            }
        }
        
        _logger.LogTrace("Native library {Name} not found in NuGet packages, falling back to default", unmanagedDllName);
        return IntPtr.Zero;
    }

    private static IEnumerable<string> GetNativeLibrarySearchNames(string name)
    {
        yield return name;
        
        // Try without extension
        var withoutExt = Path.GetFileNameWithoutExtension(name);
        if (withoutExt != name)
            yield return withoutExt;
        
        // Try without lib prefix (Unix convention)
        if (name.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
            yield return name.Substring(3);
        if (withoutExt.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
            yield return withoutExt.Substring(3);
        
        // Try common variations
        yield return name.Replace("-", "_");
        yield return name.Replace("_", "-");
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
