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
/// Downloads runtime from official release archives and runs via dotnet muxer.
/// </summary>
public sealed class MinRTBuilder
{
    private string? _targetFramework;
    private string? _runtimeIdentifier;
    private string? _cacheDirectory;
    private string? _appPath;
    private string? _runtimeVersion;
    private string? _layoutPath;  // Use existing layout instead of downloading
    private bool _requireOffline; // Fail if any download is attempted
    private readonly List<string> _probingPaths = [];
    private readonly HashSet<SharedFramework> _sharedFrameworks = [SharedFramework.NetCore];
    private readonly List<(string Id, string Version)> _packages = [];
    private string? _packagesJsonPath;

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
    /// Add a folder of DLLs to the runtime probing paths.
    /// The runtime will look in these folders when resolving assemblies.
    /// </summary>
    public MinRTBuilder AddProbingPath(string path)
    {
        _probingPaths.Add(Path.GetFullPath(path));
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
    /// Require offline mode - fail if any download is attempted.
    /// Use with WithLayout() to ensure only pre-built layouts are used.
    /// </summary>
    public MinRTBuilder RequireOffline()
    {
        _requireOffline = true;
        return this;
    }

    /// <summary>
    /// Add a NuGet package to restore and include in the app's probing paths.
    /// Packages are restored using the embedded minrt tool.
    /// </summary>
    public MinRTBuilder WithPackage(string packageId, string version)
    {
        _packages.Add((packageId, version));
        return this;
    }

    /// <summary>
    /// Add multiple NuGet packages to restore and include in the app's probing paths.
    /// </summary>
    public MinRTBuilder WithPackages(IEnumerable<(string Id, string Version)> packages)
    {
        _packages.AddRange(packages);
        return this;
    }

    /// <summary>
    /// Load packages from a JSON file. The JSON should have the format:
    /// { "packages": [{ "id": "...", "version": "..." }, ...] }
    /// </summary>
    public MinRTBuilder WithPackagesFromJson(string jsonPath)
    {
        _packagesJsonPath = jsonPath;
        return this;
    }

    /// <summary>
    /// Build the runtime context - downloads runtime and prepares app for execution
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

        _targetFramework ??= "net10.0";
        _runtimeIdentifier ??= RuntimeIdentifierHelper.GetCurrent();
        _cacheDirectory ??= GetDefaultCacheDirectory();
        _runtimeVersion ??= GetDefaultRuntimeVersion(_targetFramework);

        var paths = new CachePaths(_cacheDirectory);
        paths.EnsureDirectoriesExist();

        string runtimePath;
        string muxerPath;

        if (!string.IsNullOrEmpty(_layoutPath))
        {
            // Use existing layout
            runtimePath = _layoutPath;
            
            // Find muxer in layout
            var muxerName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            muxerPath = Path.Combine(_layoutPath, muxerName);
            
            if (!File.Exists(muxerPath))
            {
                throw new InvalidOperationException(
                    $"Muxer not found in layout at {muxerPath}. " +
                    "Ensure the layout was created with CreateLayoutAsync().");
            }
            
            // Ensure muxer is executable on Unix
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    var mode = File.GetUnixFileMode(muxerPath);
                    if ((mode & UnixFileMode.UserExecute) == 0)
                    {
                        File.SetUnixFileMode(muxerPath, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                    }
                }
                catch
                {
                    // Ignore permission errors
                }
            }
        }
        else if (_requireOffline)
        {
            throw new InvalidOperationException(
                "Offline mode requires a pre-built layout. Call WithLayout() to specify a runtime layout path.");
        }
        else
        {
            // Download runtime
            using var http = new HttpClient();
            var downloader = new RuntimeDownloader(http, paths, _requireOffline);
            runtimePath = await downloader.EnsureRuntimeAsync(_runtimeVersion, _runtimeIdentifier, _sharedFrameworks, ct);
            muxerPath = downloader.GetMuxerPath(runtimePath);
        }

        // Handle package restore if packages specified
        string? packageLayoutPath = null;
        if (_packages.Count > 0 || !string.IsNullOrEmpty(_packagesJsonPath))
        {
            packageLayoutPath = await RestorePackagesAsync(paths, ct);
            if (!string.IsNullOrEmpty(packageLayoutPath))
            {
                _probingPaths.Add(packageLayoutPath);
            }
        }

        // Create app directory and copy app files
        var appFileName = Path.GetFileName(appPath);
        var appDir = GetAppDirectory(paths, appPath);
        Directory.CreateDirectory(appDir);

        var appDllDest = Path.Combine(appDir, appFileName);

        // Copy app DLL
        File.Copy(appPath, appDllDest, overwrite: true);

        // Copy runtimeconfig.json
        var runtimeConfigSrc = Path.ChangeExtension(appPath, ".runtimeconfig.json");
        if (File.Exists(runtimeConfigSrc))
        {
            var runtimeConfigDest = Path.Combine(appDir, Path.GetFileName(runtimeConfigSrc));
            File.Copy(runtimeConfigSrc, runtimeConfigDest, overwrite: true);
        }

        // Copy deps.json if exists (skip if probing paths provided - we'll copy DLLs directly)
        var depsSrc = Path.ChangeExtension(appPath, ".deps.json");
        if (File.Exists(depsSrc) && _probingPaths.Count == 0)
        {
            var depsDest = Path.Combine(appDir, Path.GetFileName(depsSrc));
            File.Copy(depsSrc, depsDest, overwrite: true);
        }

        // Copy DLLs from probing paths to app directory (runtime probes app dir without deps.json)
        foreach (var probingPath in _probingPaths)
        {
            if (Directory.Exists(probingPath))
            {
                foreach (var file in Directory.GetFiles(probingPath, "*.dll"))
                {
                    var destFile = Path.Combine(appDir, Path.GetFileName(file));
                    if (!File.Exists(destFile))
                    {
                        File.Copy(file, destFile);
                    }
                }
            }
        }

        // Build assembly paths from probing paths
        var assemblyPaths = BuildAssemblyPaths();

        return new MinRTContext(runtimePath, _runtimeVersion, muxerPath, appDllDest, assemblyPaths, [.. _probingPaths], packageLayoutPath);
    }

    /// <summary>
    /// Restore packages using the embedded minrt tool and create a layout.
    /// </summary>
    private async Task<string?> RestorePackagesAsync(CachePaths paths, CancellationToken ct)
    {
        // Create a unique directory for this package set
        var packageHash = ComputePackageHash();
        var objDir = Path.Combine(paths.Packages, "restore", packageHash, "obj");
        var layoutDir = Path.Combine(paths.Packages, "restore", packageHash, "libs");
        var assetsPath = Path.Combine(objDir, "project.assets.json");

        // Skip if layout already exists
        if (Directory.Exists(layoutDir) && Directory.GetFiles(layoutDir, "*.dll").Length > 0)
        {
            return layoutDir;
        }

        Directory.CreateDirectory(objDir);

        // Build arguments for restore command
        var restoreArgs = new List<string> { "restore", "-o", objDir, "-f", _targetFramework ?? "net9.0" };
        
        foreach (var (id, version) in _packages)
        {
            restoreArgs.Add("-p");
            restoreArgs.Add($"{id} {version}");
        }

        if (!string.IsNullOrEmpty(_packagesJsonPath))
        {
            restoreArgs.Add("-j");
            restoreArgs.Add(_packagesJsonPath);
        }

        // Run restore
        var (exitCode, output, error) = await EmbeddedTool.RunWithOutputAsync(
            paths.Root, 
            [.. restoreArgs], 
            ct);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Package restore failed (exit code {exitCode}): {error}\n{output}");
        }

        // Run layout
        var layoutArgs = new List<string> 
        { 
            "layout", 
            "-a", assetsPath, 
            "-o", layoutDir, 
            "-f", _targetFramework ?? "net9.0" 
        };

        (exitCode, output, error) = await EmbeddedTool.RunWithOutputAsync(
            paths.Root, 
            [.. layoutArgs], 
            ct);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Package layout failed (exit code {exitCode}): {error}\n{output}");
        }

        return layoutDir;
    }

    private string ComputePackageHash()
    {
        // Create a hash from package list for caching
        var content = string.Join(";", _packages.OrderBy(p => p.Id).Select(p => $"{p.Id}:{p.Version}"));
        if (!string.IsNullOrEmpty(_packagesJsonPath))
        {
            content += $";json:{_packagesJsonPath}";
        }
        content += $";tfm:{_targetFramework ?? "net9.0"}";
        return content.GetHashCode().ToString("X8");
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
        _targetFramework ??= "net10.0";
        _runtimeVersion ??= GetDefaultRuntimeVersion(_targetFramework);

        var paths = new CachePaths(_cacheDirectory);
        paths.EnsureDirectoriesExist();

        using var http = new HttpClient();
        var downloader = new RuntimeDownloader(http, paths);

        // Download runtime to cache (includes muxer)
        var runtimePath = await downloader.EnsureRuntimeAsync(_runtimeVersion, _runtimeIdentifier, _sharedFrameworks, ct);

        // Copy to output directory
        Directory.CreateDirectory(outputPath);
        CopyDirectory(runtimePath, outputPath);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
            
            // Preserve execute permissions on Unix
            if (!OperatingSystem.IsWindows() && File.Exists(file))
            {
                try
                {
                    var mode = File.GetUnixFileMode(file);
                    File.SetUnixFileMode(destFile, mode);
                }
                catch
                {
                    // Ignore permission errors
                }
            }
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
