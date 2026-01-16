// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;

namespace MinRT.Core;

/// <summary>
/// Downloads and assembles .NET runtime from NuGet packages.
/// 
/// Uses Microsoft.NETCore.App.Runtime.{rid} package which contains:
/// - hostfxr.dll (host/fxr/{version}/)
/// - hostpolicy.dll, coreclr.dll, etc. (shared/Microsoft.NETCore.App/{version}/)
/// - System.*.dll managed assemblies
/// - Microsoft.NETCore.App.deps.json and .runtimeconfig.json
/// 
/// Note: Microsoft.NETCore.App.Host.{rid} is NOT needed - it only contains
/// apphost.exe templates used for single-file publish, not runtime hosting.
/// </summary>
public sealed class RuntimeDownloader
{
    private readonly HttpClient _http;
    private readonly CachePaths _paths;
    private readonly NuGetDownloader _nuget;

    public RuntimeDownloader(HttpClient http, CachePaths paths)
    {
        _http = http;
        _paths = paths;
        _nuget = new NuGetDownloader(http, paths);
    }

    /// <summary>
    /// Ensure runtime is downloaded and assembled. Returns path to runtime root.
    /// </summary>
    public async Task<string> EnsureRuntimeAsync(string version, string rid, CancellationToken ct = default)
    {
        var runtimePath = _paths.GetRuntimePath(version, rid);
        
        // Check if already assembled
        if (IsRuntimeComplete(runtimePath, version))
        {
            return runtimePath;
        }

        // Download runtime package (contains everything we need)
        var runtimePackageId = $"Microsoft.NETCore.App.Runtime.{rid}";
        var runtimePackagePath = await _nuget.DownloadPackageAsync(runtimePackageId, version, ct);

        // Assemble into dotnet layout
        AssembleRuntimeLayout(runtimePath, version, rid, runtimePackagePath);

        return runtimePath;
    }

    private static bool IsRuntimeComplete(string runtimePath, string version)
    {
        var hostfxrName = GetHostFxrName();
        var hostfxrPath = Path.Combine(runtimePath, "host", "fxr", version, hostfxrName);
        return File.Exists(hostfxrPath);
    }

    /// <summary>
    /// Assemble extracted NuGet package into standard dotnet layout:
    /// 
    /// {runtimePath}/
    /// ├── host/
    /// │   └── fxr/
    /// │       └── {version}/
    /// │           └── hostfxr.dll
    /// └── shared/
    ///     └── Microsoft.NETCore.App/
    ///         └── {version}/
    ///             ├── hostpolicy.dll
    ///             ├── coreclr.dll
    ///             ├── Microsoft.NETCore.App.deps.json
    ///             ├── Microsoft.NETCore.App.runtimeconfig.json
    ///             └── System.*.dll
    /// </summary>
    private static void AssembleRuntimeLayout(
        string runtimePath,
        string version,
        string rid,
        string runtimePackagePath)
    {
        // Create directory structure
        var fxrDir = Path.Combine(runtimePath, "host", "fxr", version);
        var sharedDir = Path.Combine(runtimePath, "shared", "Microsoft.NETCore.App", version);
        
        Directory.CreateDirectory(fxrDir);
        Directory.CreateDirectory(sharedDir);

        // Source paths in extracted package
        var runtimeNativeDir = Path.Combine(runtimePackagePath, "runtimes", rid, "native");
        var runtimeLibDir = FindRuntimeLibDir(runtimePackagePath, rid, version);

        // hostfxr.dll goes to host/fxr/{version}/
        var hostfxrName = GetHostFxrName();
        var hostfxrSrc = Path.Combine(runtimeNativeDir, hostfxrName);
        if (File.Exists(hostfxrSrc))
        {
            File.Copy(hostfxrSrc, Path.Combine(fxrDir, hostfxrName), overwrite: true);
        }
        else
        {
            throw new FileNotFoundException($"hostfxr not found at {hostfxrSrc}");
        }

        // Copy all native runtime files to shared dir (coreclr, hostpolicy, etc.)
        if (Directory.Exists(runtimeNativeDir))
        {
            foreach (var file in Directory.GetFiles(runtimeNativeDir))
            {
                var fileName = Path.GetFileName(file);
                // hostfxr goes to fxr dir, everything else to shared
                if (!fileName.Equals(hostfxrName, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(file, Path.Combine(sharedDir, fileName), overwrite: true);
                }
            }
        }

        // Copy managed assemblies (System.*.dll) and config files from lib dir
        if (runtimeLibDir is not null && Directory.Exists(runtimeLibDir))
        {
            foreach (var file in Directory.GetFiles(runtimeLibDir))
            {
                var fileName = Path.GetFileName(file);
                var ext = Path.GetExtension(file).ToLowerInvariant();
                // Copy DLLs and JSON config files
                if (ext is ".dll" or ".json")
                {
                    File.Copy(file, Path.Combine(sharedDir, fileName), overwrite: true);
                }
            }
        }
    }

    private static string? FindRuntimeLibDir(string runtimePackagePath, string rid, string version)
    {
        // Try exact TFM match first: runtimes/{rid}/lib/net{major}.0
        var majorVersion = version.Split('.')[0];
        var exactPath = Path.Combine(runtimePackagePath, "runtimes", rid, "lib", $"net{majorVersion}.0");
        if (Directory.Exists(exactPath))
        {
            return exactPath;
        }

        // Fall back to first available lib dir
        var libBase = Path.Combine(runtimePackagePath, "runtimes", rid, "lib");
        if (Directory.Exists(libBase))
        {
            var libDirs = Directory.GetDirectories(libBase);
            if (libDirs.Length > 0)
            {
                return libDirs[0];
            }
        }

        return null;
    }

    private static string GetHostFxrName()
    {
        if (OperatingSystem.IsWindows()) return "hostfxr.dll";
        if (OperatingSystem.IsMacOS()) return "libhostfxr.dylib";
        return "libhostfxr.so";
    }
}
