// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace MinRT.Core;

/// <summary>
/// Fluent builder for constructing a runnable .NET context.
/// </summary>
public sealed class MinRTBuilder
{
    private string? _targetFramework;
    private string? _runtimeIdentifier;
    private string? _cacheDirectory;
    private readonly List<PackageReference> _packages = [];
    private readonly List<string> _probingPaths = [];
    private RuntimeMode _runtimeMode = RuntimeMode.Download;
    private string? _runtimeVersion;
    private string? _customRuntimePath;

    /// <summary>
    /// Target framework (e.g., "net9.0", "net10.0")
    /// </summary>
    public MinRTBuilder WithTargetFramework(string tfm)
    {
        _targetFramework = tfm;
        return this;
    }

    /// <summary>
    /// Runtime identifier (e.g., "win-x64", "linux-x64", "osx-arm64")
    /// </summary>
    public MinRTBuilder WithRuntimeIdentifier(string rid)
    {
        _runtimeIdentifier = rid;
        return this;
    }

    /// <summary>
    /// Add a NuGet package reference
    /// </summary>
    public MinRTBuilder AddPackageReference(string packageId, string version)
    {
        _packages.Add(new PackageReference(packageId, version));
        return this;
    }

    /// <summary>
    /// Add additional probing paths for assembly resolution
    /// </summary>
    public MinRTBuilder AddProbingPath(string path)
    {
        _probingPaths.Add(path);
        return this;
    }

    /// <summary>
    /// Use the system-installed .NET runtime (DOTNET_ROOT or default location).
    /// NOT RECOMMENDED - prefer UseDownloadedRuntime for isolation.
    /// </summary>
    public MinRTBuilder UseSystemRuntime()
    {
        _runtimeMode = RuntimeMode.System;
        return this;
    }

    /// <summary>
    /// Download runtime from NuGet if not cached (default)
    /// </summary>
    public MinRTBuilder UseDownloadedRuntime(string? version = null)
    {
        _runtimeMode = RuntimeMode.Download;
        _runtimeVersion = version;
        return this;
    }

    /// <summary>
    /// Use runtime at a specific path
    /// </summary>
    public MinRTBuilder UseRuntimeAt(string path)
    {
        _runtimeMode = RuntimeMode.Custom;
        _customRuntimePath = path;
        return this;
    }

    /// <summary>
    /// Root directory for all caching, downloads, and temp files.
    /// Default: ~/.minrt
    /// 
    /// Layout:
    ///   {cacheDirectory}/runtimes/   - Downloaded .NET runtimes
    ///   {cacheDirectory}/packages/   - Extracted NuGet packages
    ///   {cacheDirectory}/downloads/  - Temporary .nupkg files
    ///   {cacheDirectory}/temp/       - Other temporary files
    /// </summary>
    public MinRTBuilder WithCacheDirectory(string path)
    {
        _cacheDirectory = path;
        return this;
    }

    /// <summary>
    /// Build the runtime context - downloads packages and runtime as needed
    /// </summary>
    public async Task<MinRTContext> BuildAsync(CancellationToken ct = default)
    {
        _targetFramework ??= "net9.0";
        _runtimeIdentifier ??= RuntimeIdentifierHelper.GetCurrent();
        _cacheDirectory ??= GetDefaultCacheDirectory();

        // Ensure cache directories exist
        var paths = new CachePaths(_cacheDirectory);
        paths.EnsureDirectoriesExist();

        // 1. Resolve runtime path
        var (runtimePath, runtimeVersion) = await ResolveRuntimeAsync(paths, ct);

        // 2. Build assembly paths from probing paths (package resolution TODO)
        var assemblyPaths = BuildAssemblyPaths();

        return new MinRTContext(runtimePath, runtimeVersion, assemblyPaths, [.. _probingPaths]);
    }

    private async Task<(string path, string version)> ResolveRuntimeAsync(CachePaths paths, CancellationToken ct)
    {
        return _runtimeMode switch
        {
            RuntimeMode.System => ResolveSystemRuntime(),
            RuntimeMode.Custom => (_customRuntimePath!, _runtimeVersion ?? "unknown"),
            RuntimeMode.Download => await DownloadRuntimeAsync(paths, ct),
            _ => throw new InvalidOperationException($"Unknown runtime mode: {_runtimeMode}")
        };
    }

    private async Task<(string path, string version)> DownloadRuntimeAsync(CachePaths paths, CancellationToken ct)
    {
        // Determine version - use specified or detect from target framework
        var version = _runtimeVersion ?? GetDefaultRuntimeVersion(_targetFramework!);
        var rid = _runtimeIdentifier!;

        using var http = new HttpClient();
        var downloader = new RuntimeDownloader(http, paths);
        
        var runtimePath = await downloader.EnsureRuntimeAsync(version, rid, ct);
        return (runtimePath, version);
    }

    private static string GetDefaultRuntimeVersion(string tfm)
    {
        // Extract version from tfm like "net9.0" -> "9.0.0"
        if (tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            var versionPart = tfm[3..];
            if (Version.TryParse(versionPart, out var ver))
            {
                return $"{ver.Major}.{ver.Minor}.0";
            }
        }
        return "9.0.0"; // Default fallback
    }

    private static (string path, string version) ResolveSystemRuntime()
    {
        var dotnetRoot = GetSystemDotnetRoot();
        if (dotnetRoot is null)
        {
            throw new InvalidOperationException("Could not find system .NET runtime. Set DOTNET_ROOT or install .NET.");
        }

        var fxrDir = Path.Combine(dotnetRoot, "host", "fxr");
        if (!Directory.Exists(fxrDir))
        {
            throw new InvalidOperationException($"hostfxr directory not found at {fxrDir}");
        }

        var highestVersion = Directory.GetDirectories(fxrDir)
            .Select(Path.GetFileName)
            .Where(v => v is not null)
            .OrderByDescending(v => v)
            .FirstOrDefault();

        if (highestVersion is null)
        {
            throw new InvalidOperationException($"No runtime versions found in {fxrDir}");
        }

        return (dotnetRoot, highestVersion);
    }

    private Dictionary<string, string> BuildAssemblyPaths()
    {
        var assemblyPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var probingPath in _probingPaths)
        {
            if (!Directory.Exists(probingPath)) continue;

            foreach (var dll in Directory.GetFiles(probingPath, "*.dll"))
            {
                var name = Path.GetFileNameWithoutExtension(dll);
                assemblyPaths.TryAdd(name, dll);
            }
        }

        return assemblyPaths;
    }

    private static string? GetSystemDotnetRoot()
    {
        // Check DOTNET_ROOT environment variable
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot) && Directory.Exists(dotnetRoot))
        {
            return dotnetRoot;
        }

        // Check default installation paths
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var defaultPath = Path.Combine(programFiles, "dotnet");
            if (Directory.Exists(defaultPath)) return defaultPath;
        }
        else
        {
            // Linux/macOS
            if (Directory.Exists("/usr/share/dotnet")) return "/usr/share/dotnet";
            if (Directory.Exists("/usr/local/share/dotnet")) return "/usr/local/share/dotnet";
            
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var homeDotnet = Path.Combine(home, ".dotnet");
            if (Directory.Exists(homeDotnet)) return homeDotnet;
        }

        // Search PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is not null)
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                var dotnetExe = Path.Combine(dir, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
                if (File.Exists(dotnetExe))
                {
                    return Path.GetDirectoryName(dotnetExe);
                }
            }
        }

        return null;
    }

    private static string GetDefaultCacheDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".minrt");
    }
}

public readonly record struct PackageReference(string Id, string Version);

internal enum RuntimeMode { System, Download, Custom }

/// <summary>
/// Manages paths within the cache directory.
/// </summary>
public sealed class CachePaths
{
    public string Root { get; }
    public string Runtimes { get; }
    public string Packages { get; }
    public string Downloads { get; }
    public string Temp { get; }

    public CachePaths(string root)
    {
        Root = root;
        Runtimes = Path.Combine(root, "runtimes");
        Packages = Path.Combine(root, "packages");
        Downloads = Path.Combine(root, "downloads");
        Temp = Path.Combine(root, "temp");
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(Runtimes);
        Directory.CreateDirectory(Packages);
        Directory.CreateDirectory(Downloads);
        Directory.CreateDirectory(Temp);
    }

    public string GetRuntimePath(string version, string rid) =>
        Path.Combine(Runtimes, $"{version}-{rid}");

    public string GetPackagePath(string id, string version) =>
        Path.Combine(Packages, id.ToLowerInvariant(), version);

    public string GetDownloadPath(string filename) =>
        Path.Combine(Downloads, filename);
}
