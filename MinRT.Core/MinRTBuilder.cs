// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace MinRT.Core;

/// <summary>
/// Shared frameworks that can be included with the runtime.
/// </summary>
public enum SharedFramework
{
    /// <summary>Microsoft.NETCore.App - Base runtime (always included)</summary>
    NetCore,
    /// <summary>Microsoft.AspNetCore.App - ASP.NET Core</summary>
    AspNetCore
}

/// <summary>
/// Fluent builder for constructing a runnable .NET context.
/// Downloads runtime and apphost from NuGet, patches apphost, ready to execute.
/// </summary>
public sealed class MinRTBuilder
{
    private string? _targetFramework;
    private string? _runtimeIdentifier;
    private string? _cacheDirectory;
    private string? _appPath;
    private string? _runtimeVersion;
    private string? _layoutPath;  // Use existing layout instead of downloading
    private readonly List<string> _probingPaths = [];
    private readonly HashSet<SharedFramework> _sharedFrameworks = [SharedFramework.NetCore];

    /// <summary>
    /// Path to the application DLL to run (required)
    /// </summary>
    public MinRTBuilder WithAppPath(string appPath)
    {
        _appPath = appPath;
        return this;
    }

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
    /// Auto-detected if not specified.
    /// </summary>
    public MinRTBuilder WithRuntimeIdentifier(string rid)
    {
        _runtimeIdentifier = rid;
        return this;
    }

    /// <summary>
    /// Runtime version to download (e.g., "9.0.0", "10.0.0")
    /// Derived from target framework if not specified.
    /// </summary>
    public MinRTBuilder WithRuntimeVersion(string version)
    {
        _runtimeVersion = version;
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
    /// Root directory for all caching, downloads, and temp files.
    /// Default: ~/.minrt
    /// 
    /// Layout:
    ///   {cacheDirectory}/runtimes/   - Downloaded .NET runtimes
    ///   {cacheDirectory}/packages/   - Extracted NuGet packages
    ///   {cacheDirectory}/downloads/  - Temporary .nupkg files
    ///   {cacheDirectory}/apphosts/   - Patched apphost executables
    /// </summary>
    public MinRTBuilder WithCacheDirectory(string path)
    {
        _cacheDirectory = path;
        return this;
    }

    /// <summary>
    /// Include a shared framework (e.g., ASP.NET Core).
    /// Microsoft.NETCore.App is always included.
    /// </summary>
    public MinRTBuilder WithSharedFramework(SharedFramework framework)
    {
        _sharedFrameworks.Add(framework);
        return this;
    }

    /// <summary>
    /// Include ASP.NET Core shared framework.
    /// Shorthand for WithSharedFramework(SharedFramework.AspNetCore)
    /// </summary>
    public MinRTBuilder WithAspNetCore()
    {
        _sharedFrameworks.Add(SharedFramework.AspNetCore);
        return this;
    }

    /// <summary>
    /// Use an existing runtime layout instead of downloading.
    /// The layout should have host/fxr/ and shared/ directories.
    /// </summary>
    public MinRTBuilder WithLayout(string layoutPath)
    {
        _layoutPath = layoutPath;
        return this;
    }

    /// <summary>
    /// Build the runtime context - downloads runtime/apphost (or uses existing layout) and patches apphost
    /// </summary>
    public async Task<MinRTContext> BuildAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_appPath))
        {
            throw new InvalidOperationException("App path is required. Call WithAppPath() to set it.");
        }

        // Convert to absolute path for consistent behavior
        var appPath = Path.GetFullPath(_appPath);

        if (!File.Exists(appPath))
        {
            throw new FileNotFoundException($"App not found: {appPath}");
        }

        _targetFramework ??= "net9.0";
        _runtimeIdentifier ??= RuntimeIdentifierHelper.GetCurrent();
        _cacheDirectory ??= GetDefaultCacheDirectory();
        _runtimeVersion ??= GetDefaultRuntimeVersion(_targetFramework);

        var paths = new CachePaths(_cacheDirectory);
        paths.EnsureDirectoriesExist();

        string runtimePath;
        string appHostTemplate;

        if (!string.IsNullOrEmpty(_layoutPath))
        {
            // Use existing layout
            runtimePath = _layoutPath;
            
            // Find apphost in layout or download it
            var apphostName = OperatingSystem.IsWindows() ? "apphost.exe" : "apphost";
            var layoutAppHost = Path.Combine(_layoutPath, apphostName);
            if (File.Exists(layoutAppHost))
            {
                appHostTemplate = layoutAppHost;
            }
            else
            {
                // Download apphost template only
                using var http = new HttpClient();
                var downloader = new RuntimeDownloader(http, paths);
                appHostTemplate = await downloader.GetAppHostTemplateAsync(_runtimeVersion, _runtimeIdentifier, ct);
            }
        }
        else
        {
            // Download runtime and apphost
            using var http = new HttpClient();
            var downloader = new RuntimeDownloader(http, paths);
            runtimePath = await downloader.EnsureRuntimeAsync(_runtimeVersion, _runtimeIdentifier, _sharedFrameworks, ct);
            appHostTemplate = await downloader.GetAppHostTemplateAsync(_runtimeVersion, _runtimeIdentifier, ct);
        }

        // 3. Create app directory and patch apphost
        var appFileName = Path.GetFileName(appPath);
        var appDir = GetAppDirectory(paths, appPath);
        Directory.CreateDirectory(appDir);

        var appHostPath = Path.Combine(appDir, GetAppHostName(appFileName));
        var appDllDest = Path.Combine(appDir, appFileName);

        // Copy app DLL next to apphost (apphost expects it relative to itself)
        File.Copy(appPath, appDllDest, overwrite: true);

        // Copy runtimeconfig.json if exists
        var runtimeConfigSrc = Path.ChangeExtension(appPath, ".runtimeconfig.json");
        if (File.Exists(runtimeConfigSrc))
        {
            var runtimeConfigDest = Path.Combine(appDir, Path.GetFileName(runtimeConfigSrc));
            File.Copy(runtimeConfigSrc, runtimeConfigDest, overwrite: true);
        }

        // Copy deps.json if exists
        var depsSrc = Path.ChangeExtension(appPath, ".deps.json");
        if (File.Exists(depsSrc))
        {
            var depsDest = Path.Combine(appDir, Path.GetFileName(depsSrc));
            File.Copy(depsSrc, depsDest, overwrite: true);
        }

        // Patch apphost
        if (!File.Exists(appHostPath))
        {
            AppHostPatcher.PatchAppHost(appHostTemplate, appHostPath, appFileName);
        }

        // 4. Build assembly paths from probing paths
        var assemblyPaths = BuildAssemblyPaths();

        return new MinRTContext(runtimePath, _runtimeVersion, appHostPath, assemblyPaths, [.. _probingPaths]);
    }

    private static string GetAppDirectory(CachePaths paths, string appPath)
    {
        // Create a unique directory per app based on the app's full path hash
        var appHash = appPath.GetHashCode().ToString("X8");
        return Path.Combine(paths.AppHosts, appHash);
    }

    private static string GetAppHostName(string appFileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(appFileName);
        return OperatingSystem.IsWindows() ? $"{baseName}.exe" : baseName;
    }

    private static string GetDefaultRuntimeVersion(string tfm)
    {
        if (tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            var versionPart = tfm[3..];
            if (Version.TryParse(versionPart, out var ver))
            {
                return $"{ver.Major}.{ver.Minor}.0";
            }
        }
        return "9.0.0";
    }

    /// <summary>
    /// Create a standalone runtime layout that can be distributed and used offline.
    /// This downloads the runtime and copies it to the output directory.
    /// </summary>
    /// <param name="outputPath">Directory to create the layout in</param>
    public async Task CreateLayoutAsync(string outputPath, CancellationToken ct = default)
    {
        _runtimeIdentifier ??= RuntimeIdentifierHelper.GetCurrent();
        _cacheDirectory ??= GetDefaultCacheDirectory();
        _targetFramework ??= "net9.0";
        _runtimeVersion ??= GetDefaultRuntimeVersion(_targetFramework);

        var paths = new CachePaths(_cacheDirectory);
        paths.EnsureDirectoriesExist();

        using var http = new HttpClient();
        var downloader = new RuntimeDownloader(http, paths);

        // Download runtime to cache
        var runtimePath = await downloader.EnsureRuntimeAsync(_runtimeVersion, _runtimeIdentifier, _sharedFrameworks, ct);

        // Copy to output directory
        Directory.CreateDirectory(outputPath);
        CopyDirectory(runtimePath, outputPath);

        // Also copy apphost template
        var appHostTemplate = await downloader.GetAppHostTemplateAsync(_runtimeVersion, _runtimeIdentifier, ct);
        var appHostName = OperatingSystem.IsWindows() ? "apphost.exe" : "apphost";
        File.Copy(appHostTemplate, Path.Combine(outputPath, appHostName), overwrite: true);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
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

    private static string GetDefaultCacheDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".minrt");
    }
}

/// <summary>
/// Manages paths within the cache directory.
/// </summary>
public sealed class CachePaths
{
    public string Root { get; }
    public string Runtimes { get; }
    public string Packages { get; }
    public string Downloads { get; }
    public string AppHosts { get; }

    public CachePaths(string root)
    {
        Root = root;
        Runtimes = Path.Combine(root, "runtimes");
        Packages = Path.Combine(root, "packages");
        Downloads = Path.Combine(root, "downloads");
        AppHosts = Path.Combine(root, "apphosts");
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(Runtimes);
        Directory.CreateDirectory(Packages);
        Directory.CreateDirectory(Downloads);
        Directory.CreateDirectory(AppHosts);
    }

    public string GetRuntimePath(string version, string rid) =>
        Path.Combine(Runtimes, $"{version}-{rid}");

    public string GetPackagePath(string id, string version) =>
        Path.Combine(Packages, id.ToLowerInvariant(), version);

    public string GetDownloadPath(string filename) =>
        Path.Combine(Downloads, filename);
}
