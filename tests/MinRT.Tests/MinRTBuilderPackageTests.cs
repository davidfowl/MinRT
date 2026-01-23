using MinRT.Core;

namespace MinRT.Tests;

/// <summary>
/// Tests for MinRTBuilder package-related functionality.
/// </summary>
public class MinRTBuilderPackageTests
{
    [Fact]
    public void WithPackage_AddsToPackageList()
    {
        // Arrange & Act - just ensure the API works (no public way to inspect internal state)
        var builder = new MinRTBuilder()
            .WithPackage("Newtonsoft.Json", "13.0.3");

        // Assert - no exception thrown
        Assert.NotNull(builder);
    }

    [Fact]
    public void WithPackage_Multiple_Fluent()
    {
        // Arrange & Act
        var builder = new MinRTBuilder()
            .WithPackage("Newtonsoft.Json", "13.0.3")
            .WithPackage("Microsoft.Extensions.Logging", "9.0.0")
            .WithPackage("Humanizer.Core", "2.14.1");

        // Assert - no exception thrown
        Assert.NotNull(builder);
    }

    [Fact]
    public void WithPackages_BulkAdd()
    {
        // Arrange
        var packages = new List<(string Id, string Version)>
        {
            ("Newtonsoft.Json", "13.0.3"),
            ("Microsoft.Extensions.Logging", "9.0.0")
        };

        // Act
        var builder = new MinRTBuilder()
            .WithPackages(packages);

        // Assert - no exception thrown
        Assert.NotNull(builder);
    }

    [Fact]
    public void WithPackagesFromJson_SetsPath()
    {
        // Arrange & Act
        var builder = new MinRTBuilder()
            .WithPackagesFromJson("packages.json");

        // Assert - no exception thrown
        Assert.NotNull(builder);
    }

    [Fact]
    public void WithTargetFramework_SetsFramework()
    {
        // Arrange & Act
        var builder = new MinRTBuilder()
            .WithTargetFramework("net10.0")
            .WithPackage("Newtonsoft.Json", "13.0.3");

        // Assert - no exception thrown
        Assert.NotNull(builder);
    }

    [Fact]
    public void CombinedWithRuntimeMethods_Works()
    {
        // Arrange & Act - verify package methods work with existing runtime methods
        var builder = new MinRTBuilder()
            .WithTargetFramework("net10.0")
            .WithRuntimeVersion("10.0.0")
            .WithAspNetCore()
            .WithPackage("Newtonsoft.Json", "13.0.3")
            .WithPackages([("Microsoft.Extensions.Logging", "9.0.0")])
            .WithCacheDirectory(Path.GetTempPath());

        // Assert - no exception thrown, all methods chain properly
        Assert.NotNull(builder);
    }
}
