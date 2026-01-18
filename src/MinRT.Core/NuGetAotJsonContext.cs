// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace MinRT.Core;

/// <summary>
/// AOT-compatible JSON serialization context for NuGet v3 API models.
/// </summary>
[JsonSerializable(typeof(ServiceIndex))]
[JsonSerializable(typeof(RegistrationIndex))]
[JsonSerializable(typeof(RegistrationPage))]
[JsonSerializable(typeof(RegistrationLeaf))]
[JsonSerializable(typeof(CatalogEntry))]
[JsonSerializable(typeof(PackageDependencyGroup))]
[JsonSerializable(typeof(PackageDependencyInfo))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class NuGetAotJsonContext : JsonSerializerContext
{
}

// ============ NuGet v3 Service Index ============

public record ServiceIndex(
    [property: JsonPropertyName("resources")] ServiceResource[] Resources
);

public record ServiceResource(
    [property: JsonPropertyName("@id")] string Id,
    [property: JsonPropertyName("@type")] string Type
);

// ============ NuGet v3 Registration Index ============

public record RegistrationIndex(
    [property: JsonPropertyName("items")] RegistrationPage[] Items
);

public record RegistrationPage(
    [property: JsonPropertyName("@id")] string Id,
    [property: JsonPropertyName("lower")] string? Lower,
    [property: JsonPropertyName("upper")] string? Upper,
    [property: JsonPropertyName("items")] RegistrationLeaf[]? Items
);

public record RegistrationLeaf(
    [property: JsonPropertyName("@id")] string Id,
    [property: JsonPropertyName("catalogEntry")] CatalogEntry CatalogEntry,
    [property: JsonPropertyName("packageContent")] string? PackageContent
);

public record CatalogEntry(
    [property: JsonPropertyName("@id")] string Id,
    [property: JsonPropertyName("id")] string PackageId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("dependencyGroups")] PackageDependencyGroup[]? DependencyGroups
);

public record PackageDependencyGroup(
    [property: JsonPropertyName("targetFramework")] string? TargetFramework,
    [property: JsonPropertyName("dependencies")] PackageDependencyInfo[]? Dependencies
);

public record PackageDependencyInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("range")] string? Range
);
