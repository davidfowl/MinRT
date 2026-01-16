// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Text.Json;

namespace MinRT.Core;

/// <summary>
/// AOT-compatible NuGet package downloader using direct HTTP calls.
/// </summary>
public sealed class NuGetDownloader
{
    private readonly HttpClient _http;
    private readonly CachePaths _paths;

    // Well-known NuGet v3 service types
    private const string PackageBaseAddressType = "PackageBaseAddress/3.0.0";
    private const string DefaultNuGetSource = "https://api.nuget.org/v3/index.json";

    public NuGetDownloader(HttpClient http, CachePaths paths)
    {
        _http = http;
        _paths = paths;
    }

    /// <summary>
    /// Download and extract a package to the cache.
    /// Returns the path to the extracted package.
    /// </summary>
    public async Task<string> DownloadPackageAsync(string packageId, string version, CancellationToken ct = default)
    {
        var packagePath = _paths.GetPackagePath(packageId, version);
        
        // Already cached?
        if (Directory.Exists(packagePath) && Directory.GetFiles(packagePath, "*.dll", SearchOption.AllDirectories).Length > 0)
        {
            return packagePath;
        }

        // Get the package base address from NuGet service index
        var baseAddress = await GetPackageBaseAddressAsync(ct);
        
        // Download the .nupkg
        var nupkgUrl = GetNupkgUrl(baseAddress, packageId, version);
        var nupkgPath = _paths.GetDownloadPath($"{packageId}.{version}.nupkg");
        
        Directory.CreateDirectory(Path.GetDirectoryName(nupkgPath)!);
        
        using (var response = await _http.GetAsync(nupkgUrl, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var fileStream = File.Create(nupkgPath);
            await response.Content.CopyToAsync(fileStream, ct);
        }

        // Extract
        Directory.CreateDirectory(packagePath);
        ZipFile.ExtractToDirectory(nupkgPath, packagePath, overwriteFiles: true);
        
        // Clean up download
        File.Delete(nupkgPath);

        return packagePath;
    }

    /// <summary>
    /// Get the package base address from the NuGet service index.
    /// </summary>
    private async Task<string> GetPackageBaseAddressAsync(CancellationToken ct)
    {
        var response = await _http.GetAsync(DefaultNuGetSource, ct);
        response.EnsureSuccessStatusCode();
        
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var serviceIndex = await JsonSerializer.DeserializeAsync(stream, NuGetAotJsonContext.Default.ServiceIndex, ct);
        
        if (serviceIndex?.Resources is null)
        {
            throw new InvalidOperationException("Failed to parse NuGet service index");
        }

        var baseAddress = serviceIndex.Resources
            .FirstOrDefault(r => r.Type == PackageBaseAddressType)?.Id;

        if (baseAddress is null)
        {
            throw new InvalidOperationException($"Could not find {PackageBaseAddressType} in service index");
        }

        return baseAddress.TrimEnd('/');
    }

    /// <summary>
    /// Build the URL to download a .nupkg file.
    /// Format: {baseAddress}/{id}/{version}/{id}.{version}.nupkg
    /// </summary>
    private static string GetNupkgUrl(string baseAddress, string packageId, string version)
    {
        var id = packageId.ToLowerInvariant();
        var ver = version.ToLowerInvariant();
        return $"{baseAddress}/{id}/{ver}/{id}.{ver}.nupkg";
    }
}
