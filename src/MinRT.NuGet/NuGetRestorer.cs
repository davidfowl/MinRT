using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Commands;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.ProjectModel;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using INuGetLogger = NuGet.Common.ILogger;
using NuGetLogLevel = NuGet.Common.LogLevel;
using NuGetLogMessage = NuGet.Common.ILogMessage;

namespace MinRT.NuGet;

/// <summary>
/// A fluent builder for performing NuGet restore without a csproj file.
/// Uses NuGet's RestoreRunner to produce a project.assets.json file.
/// </summary>
public sealed class NuGetRestorer
{
    private readonly List<PackageSource> _packageSources = [];
    private readonly List<(string Id, string Version)> _packages = [];
    private NuGetFramework _framework = NuGetFramework.Parse("net10.0");
    private string _packagesDirectory;
    private string _outputPath;
    private string? _nugetConfigPath;
    private string? _nugetConfigRoot;
    private bool _useDefaultConfig;
    private ILogger _logger;

    private NuGetRestorer()
    {
        _packagesDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        _outputPath = Path.Combine(Directory.GetCurrentDirectory(), "obj");
        _logger = NullLogger.Instance;
    }

    /// <summary>
    /// Creates a new NuGetRestorer builder.
    /// </summary>
    public static NuGetRestorer CreateBuilder() => new();

    /// <summary>
    /// Adds a package reference with an exact version.
    /// </summary>
    public NuGetRestorer AddPackage(string packageId, string version)
    {
        _packages.Add((packageId, version));
        return this;
    }

    /// <summary>
    /// Adds a NuGet feed URL.
    /// </summary>
    public NuGetRestorer AddFeed(string feedUrl, string? name = null)
    {
        _packageSources.Add(new PackageSource(feedUrl, name ?? feedUrl));
        return this;
    }

    /// <summary>
    /// Loads feeds from a specific nuget.config file.
    /// </summary>
    public NuGetRestorer WithNuGetConfig(string configPath)
    {
        _nugetConfigPath = Path.GetFullPath(configPath);
        return this;
    }

    /// <summary>
    /// Use default NuGet config resolution (like dotnet restore).
    /// </summary>
    public NuGetRestorer UseDefaultNuGetConfig(string? root = null)
    {
        _useDefaultConfig = true;
        _nugetConfigRoot = root != null ? Path.GetFullPath(root) : Directory.GetCurrentDirectory();
        return this;
    }

    /// <summary>
    /// Sets the target framework (e.g., "net10.0", "net9.0").
    /// </summary>
    public NuGetRestorer WithTargetFramework(string targetFramework)
    {
        _framework = NuGetFramework.Parse(targetFramework);
        return this;
    }

    /// <summary>
    /// Sets the directory where packages are downloaded and cached.
    /// Defaults to ~/.nuget/packages.
    /// </summary>
    public NuGetRestorer WithPackagesDirectory(string path)
    {
        _packagesDirectory = Path.GetFullPath(path);
        return this;
    }

    /// <summary>
    /// Sets the output directory for the project.assets.json file.
    /// Defaults to ./obj.
    /// </summary>
    public NuGetRestorer WithOutputPath(string path)
    {
        _outputPath = Path.GetFullPath(path);
        return this;
    }

    /// <summary>
    /// Sets the logger for diagnostics.
    /// </summary>
    public NuGetRestorer WithLogger(ILogger logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>
    /// Sets the logger for diagnostics.
    /// </summary>
    public NuGetRestorer WithLogger(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<NuGetRestorer>();
        return this;
    }

    /// <summary>
    /// Performs NuGet restore and returns the LockFile (project.assets.json model).
    /// </summary>
    public async Task<LockFile> RestoreAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting NuGet restore for {PackageCount} packages targeting {Framework}",
            _packages.Count, _framework);

        // Ensure output directory exists
        Directory.CreateDirectory(_outputPath);

        // 1. Load package sources
        var sources = LoadPackageSources();
        _logger.LogDebug("Using {SourceCount} package sources", sources.Count);

        // 2. Build PackageSpec
        var packageSpec = BuildPackageSpec(sources);
        _logger.LogDebug("Created PackageSpec: {Name}", packageSpec.Name);

        // 3. Create DependencyGraphSpec
        var dgSpec = new DependencyGraphSpec();
        dgSpec.AddProject(packageSpec);
        dgSpec.AddRestore(packageSpec.RestoreMetadata.ProjectUniqueName);

        // 4. Setup providers
        var providerCache = new RestoreCommandProvidersCache();
        var providers = new List<IPreLoadedRestoreRequestProvider>
        {
            new DependencyGraphSpecRequestProvider(providerCache, dgSpec)
        };

        // 5. Run restore
        using var cacheContext = new SourceCacheContext();
        var nugetLogger = new NuGetLoggingAdapter(_logger);

        var restoreContext = new RestoreArgs
        {
            CacheContext = cacheContext,
            Log = nugetLogger,
            PreLoadedRequestProviders = providers,
            DisableParallel = Environment.ProcessorCount == 1,
            AllowNoOp = false,
            GlobalPackagesFolder = _packagesDirectory
        };

        _logger.LogInformation("Running restore...");
        var results = await RestoreRunner.RunAsync(restoreContext, ct);

        // 6. Check results
        var summary = results.FirstOrDefault();
        if (summary == null)
        {
            throw new InvalidOperationException("Restore returned no results");
        }

        if (!summary.Success)
        {
            var errors = string.Join(Environment.NewLine, 
                summary.Errors?.Select(e => e.Message) ?? ["Unknown error"]);
            _logger.LogError("Restore failed: {Errors}", errors);
            throw new InvalidOperationException($"Restore failed: {errors}");
        }

        _logger.LogInformation("Restore completed successfully");
        
        // The LockFile is written to disk by RestoreRunner, but we need to read it back
        var lockFilePath = Path.Combine(_outputPath, "project.assets.json");
        if (File.Exists(lockFilePath))
        {
            var lockFileFormat = new LockFileFormat();
            return lockFileFormat.Read(lockFilePath);
        }

        throw new InvalidOperationException($"Restore succeeded but lock file not found at {lockFilePath}");
    }

    /// <summary>
    /// Performs NuGet restore and writes project.assets.json to the specified path.
    /// </summary>
    public async Task RestoreToFileAsync(string outputPath, CancellationToken ct = default)
    {
        var targetDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(targetDir))
        {
            _outputPath = targetDir;
        }

        var lockFile = await RestoreAsync(ct);
        
        // Copy to specified output if different from default location
        var defaultPath = Path.Combine(_outputPath, "project.assets.json");
        var fullOutputPath = Path.GetFullPath(outputPath);
        
        if (!string.Equals(defaultPath, fullOutputPath, StringComparison.OrdinalIgnoreCase))
        {
            var targetDirectory = Path.GetDirectoryName(fullOutputPath);
            if (!string.IsNullOrEmpty(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }
            
            var lockFileFormat = new LockFileFormat();
            lockFileFormat.Write(fullOutputPath, lockFile);
            _logger.LogInformation("Wrote project.assets.json to {Path}", fullOutputPath);
        }
    }

    private List<PackageSource> LoadPackageSources()
    {
        var sources = new List<PackageSource>(_packageSources);

        if (_useDefaultConfig)
        {
            _logger.LogDebug("Loading package sources from default NuGet config");
            var settings = Settings.LoadDefaultSettings(_nugetConfigRoot);
            var provider = new PackageSourceProvider(settings);

            foreach (var source in provider.LoadPackageSources())
            {
                if (source.IsEnabled)
                {
                    sources.Add(source);
                }
            }
        }
        else if (_nugetConfigPath != null)
        {
            _logger.LogDebug("Loading package sources from {ConfigPath}", _nugetConfigPath);
            var configDir = Path.GetDirectoryName(_nugetConfigPath)!;
            var configFile = Path.GetFileName(_nugetConfigPath);
            var settings = Settings.LoadSpecificSettings(configDir, configFile);
            var provider = new PackageSourceProvider(settings);

            foreach (var source in provider.LoadPackageSources())
            {
                if (source.IsEnabled)
                {
                    sources.Add(source);
                }
            }
        }

        // Add nuget.org as fallback
        if (sources.Count == 0)
        {
            _logger.LogInformation("No package sources configured, using nuget.org");
            sources.Add(new PackageSource("https://api.nuget.org/v3/index.json", "nuget.org"));
        }

        return sources;
    }

    private PackageSpec BuildPackageSpec(List<PackageSource> sources)
    {
        var projectName = "MinRTRestore";
        var projectPath = Path.Combine(_outputPath, "project.json");
        var tfmShort = _framework.GetShortFolderName();

        // Build dependencies as ImmutableArray
        var dependencies = _packages.Select(p => new LibraryDependency
        {
            LibraryRange = new LibraryRange(
                p.Id,
                VersionRange.Parse(p.Version),
                LibraryDependencyTarget.Package)
        }).ToImmutableArray();

        // Build target framework info with dependencies set via init
        var tfInfo = new TargetFrameworkInformation
        {
            FrameworkName = _framework,
            TargetAlias = tfmShort,
            Dependencies = dependencies
        };

        // Build restore metadata
        var restoreMetadata = new ProjectRestoreMetadata
        {
            ProjectUniqueName = projectName,
            ProjectName = projectName,
            ProjectPath = projectPath,
            ProjectStyle = ProjectStyle.PackageReference,
            OutputPath = _outputPath,
            PackagesPath = _packagesDirectory,
            ConfigFilePaths = _nugetConfigPath != null ? [_nugetConfigPath] : [],
            OriginalTargetFrameworks = [tfmShort],
        };

        // Add sources
        foreach (var source in sources)
        {
            restoreMetadata.Sources.Add(source);
        }

        // Add target framework to restore metadata with alias
        restoreMetadata.TargetFrameworks.Add(new ProjectRestoreMetadataFrameworkInfo(_framework)
        {
            TargetAlias = tfmShort
        });

        // Create PackageSpec
        var packageSpec = new PackageSpec([tfInfo])
        {
            Name = projectName,
            FilePath = projectPath,
            RestoreMetadata = restoreMetadata,
        };

        return packageSpec;
    }

    /// <summary>
    /// Adapter to bridge Microsoft.Extensions.Logging to NuGet.Common.ILogger.
    /// </summary>
    private sealed class NuGetLoggingAdapter(ILogger logger) : INuGetLogger
    {
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
            logger.Log(msLevel, "{Message}", data);
        }

        public void Log(NuGetLogMessage message) => Log(message.Level, message.Message);
        public Task LogAsync(NuGetLogLevel level, string data) { Log(level, data); return Task.CompletedTask; }
        public Task LogAsync(NuGetLogMessage message) { Log(message); return Task.CompletedTask; }
        public void LogDebug(string data) => Log(NuGetLogLevel.Debug, data);
        public void LogError(string data) => Log(NuGetLogLevel.Error, data);
        public void LogInformation(string data) => Log(NuGetLogLevel.Information, data);
        public void LogInformationSummary(string data) => Log(NuGetLogLevel.Information, data);
        public void LogMinimal(string data) => Log(NuGetLogLevel.Minimal, data);
        public void LogVerbose(string data) => Log(NuGetLogLevel.Verbose, data);
        public void LogWarning(string data) => Log(NuGetLogLevel.Warning, data);
    }
}
