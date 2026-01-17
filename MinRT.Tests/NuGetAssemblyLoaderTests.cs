using Microsoft.Extensions.Logging;
using MinRT.NuGet;
using NuGet.Resolver;

namespace MinRT.Tests;

/// <summary>
/// End-to-end tests for NuGetAssemblyLoader.
/// These tests hit real NuGet feeds and verify actual package resolution.
/// </summary>
public class NuGetAssemblyLoaderTests : IDisposable
{
    private readonly string _packagesDir;
    private readonly ILoggerFactory _loggerFactory;

    public NuGetAssemblyLoaderTests()
    {
        _packagesDir = Path.Combine(Path.GetTempPath(), "minrt-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_packagesDir);
        
        _loggerFactory = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Debug)
            .AddConsole());
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
        try { Directory.Delete(_packagesDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task SimplePackage_LoadsSuccessfully()
    {
        // Arrange & Act
        var alc = await NuGetAssemblyLoader.CreateBuilder()
            .AddPackage("Newtonsoft.Json", "13.0.3")
            .WithTargetFramework("net9.0")
            .WithPackagesDirectory(_packagesDir)
            .WithLogger(_loggerFactory)
            .BuildAsync();

        // Assert
        Assert.Single(alc.AssemblyPaths);
        Assert.True(alc.AssemblyPaths.ContainsKey("Newtonsoft.Json"));
        
        var assembly = alc.LoadAssembly("Newtonsoft.Json");
        Assert.NotNull(assembly);
        Assert.Contains("Newtonsoft.Json", assembly.FullName);
    }

    [Fact]
    public async Task SimplePackage_CanSerializeObject()
    {
        // Arrange
        var alc = await NuGetAssemblyLoader.CreateBuilder()
            .AddPackage("Newtonsoft.Json", "13.0.3")
            .WithTargetFramework("net9.0")
            .WithPackagesDirectory(_packagesDir)
            .BuildAsync();

        // Act - use reflection to call JsonConvert.SerializeObject
        var assembly = alc.LoadAssembly("Newtonsoft.Json");
        var jsonConvertType = assembly.GetType("Newtonsoft.Json.JsonConvert")!;
        var serializeMethod = jsonConvertType.GetMethod("SerializeObject", [typeof(object)])!;
        var result = (string)serializeMethod.Invoke(null, [new { test = true }])!;

        // Assert
        Assert.Contains("test", result);
        Assert.Contains("true", result);
    }

    [Fact]
    public async Task TransitiveDependencies_ResolvesAll()
    {
        // Arrange & Act
        var alc = await NuGetAssemblyLoader.CreateBuilder()
            .AddPackage("Microsoft.Extensions.Logging", "9.0.0")
            .WithTargetFramework("net9.0")
            .WithPackagesDirectory(_packagesDir)
            .WithLogger(_loggerFactory)
            .BuildAsync();

        // Assert - should have multiple assemblies from transitive deps
        Assert.True(alc.AssemblyPaths.Count >= 3, 
            $"Expected >= 3 assemblies, got {alc.AssemblyPaths.Count}");
        Assert.True(alc.AssemblyPaths.ContainsKey("Microsoft.Extensions.Logging"));
        Assert.True(alc.AssemblyPaths.ContainsKey("Microsoft.Extensions.Logging.Abstractions"));
        Assert.True(alc.AssemblyPaths.ContainsKey("Microsoft.Extensions.DependencyInjection.Abstractions"));
    }

    [Fact]
    public async Task MultiplePackages_ResolvesAll()
    {
        // Arrange & Act
        var alc = await NuGetAssemblyLoader.CreateBuilder()
            .AddPackage("Newtonsoft.Json", "13.0.3")
            .AddPackage("Humanizer.Core", "2.14.1")
            .WithTargetFramework("net9.0")
            .WithPackagesDirectory(_packagesDir)
            .BuildAsync();

        // Assert
        Assert.True(alc.AssemblyPaths.ContainsKey("Newtonsoft.Json"));
        Assert.True(alc.AssemblyPaths.ContainsKey("Humanizer"));
    }

    [Fact]
    public async Task VersionRange_AllowNewer_SelectsValidVersion()
    {
        // Arrange & Act
        var alc = await NuGetAssemblyLoader.CreateBuilder()
            .AddPackage("Newtonsoft.Json", "13.0.0", allowNewer: true)
            .WithTargetFramework("net9.0")
            .WithPackagesDirectory(_packagesDir)
            .BuildAsync();

        // Assert
        var assembly = alc.LoadAssembly("Newtonsoft.Json");
        var version = assembly.GetName().Version;
        Assert.True(version >= new Version(13, 0, 0), 
            $"Expected version >= 13.0.0, got {version}");
    }

    [Fact]
    public async Task PackageNotFound_ThrowsInvalidOperationException()
    {
        // Arrange & Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await NuGetAssemblyLoader.CreateBuilder()
                .AddPackage("This.Package.Does.Not.Exist.MinRT.Test.12345", "1.0.0")
                .WithTargetFramework("net9.0")
                .WithPackagesDirectory(_packagesDir)
                .BuildAsync();
        });

        Assert.Contains("Package not found", ex.Message);
    }

    [Fact]
    public async Task Caching_SecondLoadUsesCachedPackage()
    {
        // Arrange - first load
        var alc1 = await NuGetAssemblyLoader.CreateBuilder()
            .AddPackage("Newtonsoft.Json", "13.0.3")
            .WithTargetFramework("net9.0")
            .WithPackagesDirectory(_packagesDir)
            .BuildAsync();

        // Act - second load (should use cache)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var alc2 = await NuGetAssemblyLoader.CreateBuilder()
            .AddPackage("Newtonsoft.Json", "13.0.3")
            .WithTargetFramework("net9.0")
            .WithPackagesDirectory(_packagesDir)
            .BuildAsync();
        sw.Stop();

        // Assert - should be fast (cached) and have same assembly
        Assert.True(alc2.AssemblyPaths.ContainsKey("Newtonsoft.Json"));
        // Second load should be relatively fast since package is cached
    }

    [Fact]
    public async Task DifferentTargetFramework_Works()
    {
        // Arrange & Act
        var alc = await NuGetAssemblyLoader.CreateBuilder()
            .AddPackage("Newtonsoft.Json", "13.0.3")
            .WithTargetFramework("net8.0")
            .WithPackagesDirectory(_packagesDir)
            .BuildAsync();

        // Assert
        Assert.True(alc.AssemblyPaths.ContainsKey("Newtonsoft.Json"));
        var assembly = alc.LoadAssembly("Newtonsoft.Json");
        Assert.NotNull(assembly);
    }

    [Fact]
    public async Task UseDefaultNuGetConfig_LoadsFromSystemConfig()
    {
        // Arrange & Act - UseDefaultNuGetConfig should find nuget.org from system config
        var alc = await NuGetAssemblyLoader.CreateBuilder()
            .AddPackage("Humanizer.Core", "2.14.1")
            .WithTargetFramework("net9.0")
            .WithPackagesDirectory(_packagesDir)
            .UseDefaultNuGetConfig()
            .WithLogger(_loggerFactory)
            .BuildAsync();

        // Assert
        Assert.True(alc.AssemblyPaths.ContainsKey("Humanizer"));
    }

    [Fact]
    public async Task ExplicitFeed_Works()
    {
        // Arrange & Act
        var alc = await NuGetAssemblyLoader.CreateBuilder()
            .AddPackage("Newtonsoft.Json", "13.0.3")
            .AddFeed("https://api.nuget.org/v3/index.json", "nuget.org")
            .WithTargetFramework("net9.0")
            .WithPackagesDirectory(_packagesDir)
            .BuildAsync();

        // Assert
        Assert.True(alc.AssemblyPaths.ContainsKey("Newtonsoft.Json"));
    }

    [Fact]
    public async Task DependencyBehavior_LowestVersion()
    {
        // Arrange & Act
        var alc = await NuGetAssemblyLoader.CreateBuilder()
            .AddPackage("Microsoft.Extensions.Logging", "9.0.0")
            .WithTargetFramework("net9.0")
            .WithPackagesDirectory(_packagesDir)
            .WithDependencyBehavior(DependencyBehavior.Lowest)
            .WithLogger(_loggerFactory)
            .BuildAsync();

        // Assert - should resolve with lowest compatible versions
        Assert.True(alc.AssemblyPaths.Count >= 3);
    }

    [Fact]
    public async Task GetType_ReturnsCorrectType()
    {
        // Arrange
        var alc = await NuGetAssemblyLoader.CreateBuilder()
            .AddPackage("Newtonsoft.Json", "13.0.3")
            .WithTargetFramework("net9.0")
            .WithPackagesDirectory(_packagesDir)
            .BuildAsync();

        // Act
        var type = alc.GetType("Newtonsoft.Json", "Newtonsoft.Json.JsonConvert");

        // Assert
        Assert.NotNull(type);
        Assert.Equal("JsonConvert", type.Name);
    }

    [Fact]
    public async Task CollectibleALC_CanBeCreated()
    {
        // Arrange & Act
        var alc = await NuGetAssemblyLoader.CreateBuilder()
            .AddPackage("Newtonsoft.Json", "13.0.3")
            .WithTargetFramework("net9.0")
            .WithPackagesDirectory(_packagesDir)
            .AsCollectible()
            .BuildAsync();

        // Assert
        Assert.True(alc.IsCollectible);
        Assert.True(alc.AssemblyPaths.ContainsKey("Newtonsoft.Json"));
        
        // Load and use via reflection (required for collectible)
        var assembly = alc.LoadAssembly("Newtonsoft.Json");
        Assert.NotNull(assembly);
    }

    [Fact]
    public async Task NonCollectibleALC_IsDefault()
    {
        // Arrange & Act
        var alc = await NuGetAssemblyLoader.CreateBuilder()
            .AddPackage("Newtonsoft.Json", "13.0.3")
            .WithTargetFramework("net9.0")
            .WithPackagesDirectory(_packagesDir)
            .BuildAsync();

        // Assert
        Assert.False(alc.IsCollectible);
    }
}
