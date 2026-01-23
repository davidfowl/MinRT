// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinRT.Core;

/// <summary>
/// Client for the official .NET release metadata API.
/// https://builds.dotnet.microsoft.com/dotnet/release-metadata/
/// </summary>
public sealed class ReleaseMetadataClient
{
    private const string ReleasesIndexUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json";
    
    private readonly HttpClient _http;

    public ReleaseMetadataClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Gets the runtime download info for a specific version and RID.
    /// </summary>
    public async Task<RuntimeDownloadInfo> GetRuntimeDownloadInfoAsync(
        string version,
        string rid,
        CancellationToken ct = default)
    {
        // Parse major.minor from version (e.g., "10.0.2" -> "10.0")
        var channel = GetChannel(version);
        
        // Fetch releases index to get the releases.json URL
        var releasesJsonUrl = await GetReleasesJsonUrlAsync(channel, ct);
        
        // Fetch releases.json for this channel
        var releases = await FetchReleasesAsync(releasesJsonUrl, ct);
        
        // Find the specific release
        var release = releases.Releases.FirstOrDefault(r => r.ReleaseVersion == version)
            ?? throw new InvalidOperationException($"Release {version} not found in channel {channel}");
        
        // Find the runtime file for this RID
        var archiveExt = GetArchiveExtension(rid);
        
        var file = release.Runtime.Files.FirstOrDefault(f => 
            f.Rid == rid && f.Name.StartsWith("dotnet-runtime-") && f.Name.EndsWith(archiveExt))
            ?? throw new InvalidOperationException($"Runtime file not found for RID {rid} in release {version}");
        
        return new RuntimeDownloadInfo(file.Url, file.Hash, file.Name);
    }

    /// <summary>
    /// Gets the ASP.NET Core runtime download info for a specific version and RID.
    /// </summary>
    public async Task<RuntimeDownloadInfo> GetAspNetCoreDownloadInfoAsync(
        string version,
        string rid,
        CancellationToken ct = default)
    {
        var channel = GetChannel(version);
        var releasesJsonUrl = await GetReleasesJsonUrlAsync(channel, ct);
        var releases = await FetchReleasesAsync(releasesJsonUrl, ct);
        
        var release = releases.Releases.FirstOrDefault(r => r.ReleaseVersion == version)
            ?? throw new InvalidOperationException($"Release {version} not found in channel {channel}");
        
        var archiveExt = GetArchiveExtension(rid);
        
        var file = release.AspNetCoreRuntime?.Files.FirstOrDefault(f => 
            f.Rid == rid && f.Name.StartsWith("aspnetcore-runtime-") && !f.Name.Contains("composite") && f.Name.EndsWith(archiveExt))
            ?? throw new InvalidOperationException($"ASP.NET Core runtime file not found for RID {rid} in release {version}");
        
        return new RuntimeDownloadInfo(file.Url, file.Hash, file.Name);
    }

    /// <summary>
    /// Gets the latest version for a channel (e.g., "10.0" -> "10.0.2").
    /// </summary>
    public async Task<string> GetLatestVersionAsync(string channel, CancellationToken ct = default)
    {
        var index = await FetchReleasesIndexAsync(ReleasesIndexUrl, ct);
        var entry = index.ReleasesIndex.FirstOrDefault(e => e.ChannelVersion == channel)
            ?? throw new InvalidOperationException($"Channel {channel} not found");
        return entry.LatestRuntime;
    }

    private async Task<string> GetReleasesJsonUrlAsync(string channel, CancellationToken ct)
    {
        var index = await FetchReleasesIndexAsync(ReleasesIndexUrl, ct);
        var entry = index.ReleasesIndex.FirstOrDefault(e => e.ChannelVersion == channel)
            ?? throw new InvalidOperationException($"Channel {channel} not found in releases index");
        return entry.ReleasesJsonUrl;
    }

    private async Task<ReleasesIndexJson> FetchReleasesIndexAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize(json, ReleaseMetadataJsonContext.Default.ReleasesIndexJson)
            ?? throw new InvalidOperationException($"Failed to parse JSON from {url}");
    }

    private async Task<ReleasesJson> FetchReleasesAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize(json, ReleaseMetadataJsonContext.Default.ReleasesJson)
            ?? throw new InvalidOperationException($"Failed to parse JSON from {url}");
    }

    private static string GetChannel(string version)
    {
        var parts = version.Split('.');
        if (parts.Length < 2)
            throw new ArgumentException($"Invalid version format: {version}");
        return $"{parts[0]}.{parts[1].Split('-')[0]}"; // Handle preview versions like "10.0.0-preview.1"
    }

    private static string GetArchiveExtension(string rid)
    {
        return rid.StartsWith("win") ? ".zip" : ".tar.gz";
    }
}

/// <summary>
/// Information about a runtime download.
/// </summary>
public record RuntimeDownloadInfo(string Url, string Sha512Hash, string FileName);

#region JSON Models

internal class ReleasesIndexJson
{
    [JsonPropertyName("releases-index")]
    public List<ReleasesIndexEntry> ReleasesIndex { get; set; } = [];
}

internal class ReleasesIndexEntry
{
    [JsonPropertyName("channel-version")]
    public string ChannelVersion { get; set; } = "";
    
    [JsonPropertyName("latest-runtime")]
    public string LatestRuntime { get; set; } = "";
    
    [JsonPropertyName("releases.json")]
    public string ReleasesJsonUrl { get; set; } = "";
}

internal class ReleasesJson
{
    [JsonPropertyName("releases")]
    public List<ReleaseEntry> Releases { get; set; } = [];
}

internal class ReleaseEntry
{
    [JsonPropertyName("release-version")]
    public string ReleaseVersion { get; set; } = "";
    
    [JsonPropertyName("runtime")]
    public RuntimeInfo Runtime { get; set; } = new();
    
    [JsonPropertyName("aspnetcore-runtime")]
    public RuntimeInfo? AspNetCoreRuntime { get; set; }
}

internal class RuntimeInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
    
    [JsonPropertyName("files")]
    public List<RuntimeFile> Files { get; set; } = [];
}

internal class RuntimeFile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    
    [JsonPropertyName("rid")]
    public string Rid { get; set; } = "";
    
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
    
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";
}

#endregion

[JsonSerializable(typeof(ReleasesIndexJson))]
[JsonSerializable(typeof(ReleasesJson))]
internal partial class ReleaseMetadataJsonContext : JsonSerializerContext
{
}
