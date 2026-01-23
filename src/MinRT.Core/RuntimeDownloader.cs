// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace MinRT.Core;

/// <summary>
/// Downloads .NET runtime from official release archives.
/// 
/// Uses the official release metadata API at builds.dotnet.microsoft.com
/// to download pre-built runtime archives that include the dotnet muxer.
/// </summary>
public sealed class RuntimeDownloader
{
    private readonly HttpClient _http;
    private readonly CachePaths _paths;
    private readonly ReleaseMetadataClient _releaseClient;
    private readonly bool _requireOffline;

    public RuntimeDownloader(HttpClient http, CachePaths paths, bool requireOffline = false)
    {
        _http = http;
        _paths = paths;
        _requireOffline = requireOffline;
        _releaseClient = new ReleaseMetadataClient(http);
    }

    /// <summary>
    /// Ensure runtime is downloaded and extracted. Returns path to runtime root.
    /// </summary>
    public async Task<string> EnsureRuntimeAsync(
        string version, 
        string rid, 
        IEnumerable<SharedFramework> frameworks,
        CancellationToken ct = default)
    {
        var runtimePath = _paths.GetRuntimePath(version, rid);
        
        // Check if already downloaded
        if (!IsRuntimeComplete(runtimePath, version))
        {
            if (_requireOffline)
            {
                throw new InvalidOperationException(
                    $"Runtime {version} for {rid} not found in cache and offline mode is enabled.");
            }

            // Get download info from release metadata
            var downloadInfo = await _releaseClient.GetRuntimeDownloadInfoAsync(version, rid, ct);
            
            // Download and extract
            await DownloadAndExtractAsync(downloadInfo, runtimePath, ct);
            
            // Set execute permissions on Unix
            if (!OperatingSystem.IsWindows())
            {
                SetExecutePermissions(runtimePath, version);
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
    /// Gets the path to the dotnet muxer executable.
    /// </summary>
    public string GetMuxerPath(string runtimePath)
    {
        var muxerName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        return Path.Combine(runtimePath, muxerName);
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

        if (_requireOffline)
        {
            throw new InvalidOperationException(
                $"ASP.NET Core {version} for {rid} not found in cache and offline mode is enabled.");
        }

        // Get download info from release metadata
        var downloadInfo = await _releaseClient.GetAspNetCoreDownloadInfoAsync(version, rid, ct);
        
        // Download to temp and extract only the shared framework portion
        var tempDir = Path.Combine(_paths.Downloads, $"aspnetcore-{version}-{rid}-{Guid.NewGuid():N}");
        try
        {
            await DownloadAndExtractAsync(downloadInfo, tempDir, ct);
            
            // Copy the shared/Microsoft.AspNetCore.App/{version} folder to runtimePath
            var sourceDir = Path.Combine(tempDir, "shared", "Microsoft.AspNetCore.App", version);
            if (Directory.Exists(sourceDir))
            {
                Directory.CreateDirectory(sharedDir);
                foreach (var file in Directory.GetFiles(sourceDir))
                {
                    File.Copy(file, Path.Combine(sharedDir, Path.GetFileName(file)), overwrite: true);
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private async Task DownloadAndExtractAsync(RuntimeDownloadInfo downloadInfo, string targetPath, CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.Downloads);
        var archivePath = Path.Combine(_paths.Downloads, downloadInfo.FileName);
        
        try
        {
            // Download the archive
            using (var response = await _http.GetAsync(downloadInfo.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                using var fileStream = File.Create(archivePath);
                await response.Content.CopyToAsync(fileStream, ct);
            }

            // Verify SHA512 hash
            var actualHash = await ComputeSha512Async(archivePath, ct);
            if (!string.Equals(actualHash, downloadInfo.Sha512Hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Hash mismatch for {downloadInfo.FileName}. Expected: {downloadInfo.Sha512Hash}, Actual: {actualHash}");
            }

            // Extract archive
            Directory.CreateDirectory(targetPath);
            
            if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archivePath, targetPath, overwriteFiles: true);
            }
            else if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                await ExtractTarGzAsync(archivePath, targetPath, ct);
            }
            else
            {
                throw new NotSupportedException($"Unsupported archive format: {downloadInfo.FileName}");
            }
        }
        finally
        {
            // Clean up downloaded archive
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
        }
    }

    private static async Task<string> ComputeSha512Async(string filePath, CancellationToken ct)
    {
        using var stream = File.OpenRead(filePath);
        var hash = await SHA512.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task ExtractTarGzAsync(string archivePath, string targetPath, CancellationToken ct)
    {
        await using var fileStream = File.OpenRead(archivePath);
        await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzipStream, targetPath, overwriteFiles: true, cancellationToken: ct);
    }

    private static void SetExecutePermissions(string runtimePath, string version)
    {
        // Set +x on dotnet muxer
        var muxerPath = Path.Combine(runtimePath, "dotnet");
        if (File.Exists(muxerPath))
        {
            SetFileExecutable(muxerPath);
        }

        // Set +x on hostfxr
        var fxrDir = Path.Combine(runtimePath, "host", "fxr", version);
        if (Directory.Exists(fxrDir))
        {
            foreach (var file in Directory.GetFiles(fxrDir, "*.so"))
                SetFileExecutable(file);
            foreach (var file in Directory.GetFiles(fxrDir, "*.dylib"))
                SetFileExecutable(file);
        }

        // Set +x on native files in shared dir
        var sharedDir = Path.Combine(runtimePath, "shared", "Microsoft.NETCore.App", version);
        if (Directory.Exists(sharedDir))
        {
            foreach (var file in Directory.GetFiles(sharedDir, "*.so"))
                SetFileExecutable(file);
            foreach (var file in Directory.GetFiles(sharedDir, "*.dylib"))
                SetFileExecutable(file);
        }
    }

    private static void SetFileExecutable(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    private static bool IsRuntimeComplete(string runtimePath, string version)
    {
        // Check for muxer as the primary indicator
        var muxerName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var muxerPath = Path.Combine(runtimePath, muxerName);
        return File.Exists(muxerPath);
    }
}
