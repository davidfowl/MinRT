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
/// Also handles Microsoft.NETCore.App.Host.{rid} for apphost template
/// and Microsoft.AspNetCore.App.Runtime.{rid} for ASP.NET Core.
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
    public async Task<string> EnsureRuntimeAsync(
        string version, 
        string rid, 
        IEnumerable<SharedFramework> frameworks,
        CancellationToken ct = default)
    {
        var runtimePath = _paths.GetRuntimePath(version, rid);
        
        // Check if already assembled (base runtime)
        if (!IsRuntimeComplete(runtimePath, version))
        {
            // Download base runtime package
            var runtimePackageId = $"Microsoft.NETCore.App.Runtime.{rid}";
            var runtimePackagePath = await _nuget.DownloadPackageAsync(runtimePackageId, version, ct);

            AssembleRuntimeLayout(runtimePath, version, rid, runtimePackagePath, "Microsoft.NETCore.App");

            if (!OperatingSystem.IsWindows())
            {
                SetExecutePermissions(runtimePath, version, "Microsoft.NETCore.App");
            }
        }

        // Download additional shared frameworks
        foreach (var framework in frameworks)
        {
            if (framework == SharedFramework.NetCore) continue; // Already handled

            if (framework == SharedFramework.AspNetCore)
            {
                await EnsureAspNetCoreAsync(runtimePath, version, rid, ct);
            }
        }

        return runtimePath;
    }

    /// <summary>
    /// Download and install ASP.NET Core shared framework.
    /// </summary>
    private async Task EnsureAspNetCoreAsync(string runtimePath, string version, string rid, CancellationToken ct)
    {
        var sharedDir = Path.Combine(runtimePath, "shared", "Microsoft.AspNetCore.App", version);
        
        // Check if already installed
        if (Directory.Exists(sharedDir) && Directory.GetFiles(sharedDir, "*.dll").Length > 0)
        {
            return;
        }

        var packageId = $"Microsoft.AspNetCore.App.Runtime.{rid}";
        var packagePath = await _nuget.DownloadPackageAsync(packageId, version, ct);

        // Install to shared/Microsoft.AspNetCore.App/{version}/
        Directory.CreateDirectory(sharedDir);

        var libDir = FindRuntimeLibDir(packagePath, rid, version);
        if (libDir is not null && Directory.Exists(libDir))
        {
            foreach (var file in Directory.GetFiles(libDir))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".dll" or ".json")
                {
                    File.Copy(file, Path.Combine(sharedDir, Path.GetFileName(file)), overwrite: true);
                }
            }
        }

        // Copy native files if any
        var nativeDir = Path.Combine(packagePath, "runtimes", rid, "native");
        if (Directory.Exists(nativeDir))
        {
            foreach (var file in Directory.GetFiles(nativeDir))
            {
                var dest = Path.Combine(sharedDir, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
                
                if (!OperatingSystem.IsWindows())
                {
                    SetFileExecutable(dest);
                }
            }
        }
    }

    /// <summary>
    /// Gets the path to the apphost template from the Host package.
    /// Downloads the package if not already cached.
    /// </summary>
    public async Task<string> GetAppHostTemplateAsync(string version, string rid, CancellationToken ct = default)
    {
        var hostPackageId = $"Microsoft.NETCore.App.Host.{rid}";
        var hostPackagePath = await _nuget.DownloadPackageAsync(hostPackageId, version, ct);

        var apphostName = OperatingSystem.IsWindows() ? "apphost.exe" : "apphost";
        var apphostPath = Path.Combine(hostPackagePath, "runtimes", rid, "native", apphostName);

        if (!File.Exists(apphostPath))
        {
            throw new FileNotFoundException($"apphost not found at {apphostPath}");
        }

        return apphostPath;
    }

    private static void SetExecutePermissions(string runtimePath, string version, string frameworkName)
    {
        // Set +x on hostfxr (only for base runtime)
        if (frameworkName == "Microsoft.NETCore.App")
        {
            var fxrDir = Path.Combine(runtimePath, "host", "fxr", version);
            if (Directory.Exists(fxrDir))
            {
                foreach (var file in Directory.GetFiles(fxrDir, "*.so"))
                {
                    SetFileExecutable(file);
                }
                foreach (var file in Directory.GetFiles(fxrDir, "*.dylib"))
                {
                    SetFileExecutable(file);
                }
            }
        }

        // Set +x on native files in shared dir
        var sharedDir = Path.Combine(runtimePath, "shared", frameworkName, version);
        if (Directory.Exists(sharedDir))
        {
            foreach (var file in Directory.GetFiles(sharedDir, "*.so"))
            {
                SetFileExecutable(file);
            }
            foreach (var file in Directory.GetFiles(sharedDir, "*.dylib"))
            {
                SetFileExecutable(file);
            }
        }
    }

    private static void SetFileExecutable(string path)
    {
        // Use chmod via File.SetUnixFileMode (available in .NET 7+)
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
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
    ///     └── {frameworkName}/
    ///         └── {version}/
    ///             ├── hostpolicy.dll
    ///             ├── coreclr.dll
    ///             ├── *.deps.json
    ///             ├── *.runtimeconfig.json
    ///             └── *.dll
    /// </summary>
    private static void AssembleRuntimeLayout(
        string runtimePath,
        string version,
        string rid,
        string runtimePackagePath,
        string frameworkName)
    {
        // Create directory structure
        var fxrDir = Path.Combine(runtimePath, "host", "fxr", version);
        var sharedDir = Path.Combine(runtimePath, "shared", frameworkName, version);
        
        Directory.CreateDirectory(fxrDir);
        Directory.CreateDirectory(sharedDir);

        // Source paths in extracted package
        var runtimeNativeDir = Path.Combine(runtimePackagePath, "runtimes", rid, "native");
        var runtimeLibDir = FindRuntimeLibDir(runtimePackagePath, rid, version);

        // hostfxr.dll goes to host/fxr/{version}/ (only for base runtime)
        if (frameworkName == "Microsoft.NETCore.App")
        {
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
