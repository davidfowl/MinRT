using System.Diagnostics;
using System.Text.Json;
using MinRT.NuGet;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace MinRT.Tests;

/// <summary>
/// Tests that verify NuGetRestorer produces the same results as dotnet restore.
/// </summary>
public class NuGetRestorerDeltaTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Theory]
    [InlineData("Newtonsoft.Json", "13.0.3", "net10.0")]
    [InlineData("Microsoft.Extensions.Logging", "9.0.0", "net10.0")]
    [InlineData("Humanizer.Core", "2.14.1", "net10.0")]
    public async Task RestoreAsync_MatchesDotnetRestore(string packageId, string version, string tfm)
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), $"minrt-delta-{Guid.NewGuid():N}");
        var minrtDir = Path.Combine(testDir, "minrt");
        var sdkDir = Path.Combine(testDir, "sdk");
        
        Directory.CreateDirectory(minrtDir);
        Directory.CreateDirectory(sdkDir);

        try
        {
            // Act - Run minrt-nuget restore
            _output.WriteLine($"Testing {packageId} {version} targeting {tfm}");
            
            var restorer = NuGetRestorer.CreateBuilder()
                .AddPackage(packageId, version)
                .WithTargetFramework(tfm)
                .WithOutputPath(minrtDir)
                .UseDefaultNuGetConfig();

            await restorer.RestoreAsync();
            var minrtAssetsPath = Path.Combine(minrtDir, "project.assets.json");

            // Act - Run dotnet restore
            var csproj = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>{tfm}</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""{packageId}"" Version=""{version}"" />
  </ItemGroup>
</Project>";
            var csprojPath = Path.Combine(sdkDir, "test.csproj");
            await File.WriteAllTextAsync(csprojPath, csproj);

            var psi = new ProcessStartInfo("dotnet", $"restore \"{csprojPath}\" --verbosity quiet")
            {
                WorkingDirectory = sdkDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(psi)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);

            var sdkAssetsPath = Path.Combine(sdkDir, "obj", "project.assets.json");

            // Assert - Both files exist
            Assert.True(File.Exists(minrtAssetsPath), $"MinRT assets file not found: {minrtAssetsPath}");
            Assert.True(File.Exists(sdkAssetsPath), $"SDK assets file not found: {sdkAssetsPath}");

            // Parse both files
            var minrtJson = JsonDocument.Parse(await File.ReadAllTextAsync(minrtAssetsPath));
            var sdkJson = JsonDocument.Parse(await File.ReadAllTextAsync(sdkAssetsPath));

            // Compare version
            Assert.Equal(
                sdkJson.RootElement.GetProperty("version").GetInt32(),
                minrtJson.RootElement.GetProperty("version").GetInt32());

            // Compare resolved packages
            var sdkTargets = sdkJson.RootElement.GetProperty("targets").GetProperty(tfm);
            var minrtTargets = minrtJson.RootElement.GetProperty("targets").GetProperty(tfm);

            var sdkPackages = sdkTargets.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToList();
            var minrtPackages = minrtTargets.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToList();

            _output.WriteLine($"SDK resolved {sdkPackages.Count} packages");
            _output.WriteLine($"MinRT resolved {minrtPackages.Count} packages");

            // Check for missing packages
            var missingInMinrt = sdkPackages.Except(minrtPackages).ToList();
            var extraInMinrt = minrtPackages.Except(sdkPackages).ToList();

            if (missingInMinrt.Count > 0)
            {
                _output.WriteLine($"Missing in MinRT: {string.Join(", ", missingInMinrt)}");
            }
            if (extraInMinrt.Count > 0)
            {
                _output.WriteLine($"Extra in MinRT: {string.Join(", ", extraInMinrt)}");
            }

            Assert.Empty(missingInMinrt);
            Assert.Empty(extraInMinrt);
            Assert.Equal(sdkPackages.Count, minrtPackages.Count);

            // Compare each package's dependencies and assets
            foreach (var packageName in sdkPackages)
            {
                var sdkPkg = sdkTargets.GetProperty(packageName);
                var minrtPkg = minrtTargets.GetProperty(packageName);

                // Compare type
                Assert.Equal(
                    sdkPkg.GetProperty("type").GetString(),
                    minrtPkg.GetProperty("type").GetString());

                // Compare dependencies if present
                if (sdkPkg.TryGetProperty("dependencies", out var sdkDeps))
                {
                    Assert.True(minrtPkg.TryGetProperty("dependencies", out var minrtDeps),
                        $"Package {packageName} missing dependencies in MinRT");

                    var sdkDepNames = sdkDeps.EnumerateObject().Select(d => d.Name).OrderBy(x => x).ToList();
                    var minrtDepNames = minrtDeps.EnumerateObject().Select(d => d.Name).OrderBy(x => x).ToList();

                    Assert.Equal(sdkDepNames, minrtDepNames);
                }

                // Compare compile assets if present
                if (sdkPkg.TryGetProperty("compile", out var sdkCompile))
                {
                    Assert.True(minrtPkg.TryGetProperty("compile", out var minrtCompile),
                        $"Package {packageName} missing compile assets in MinRT");

                    var sdkCompileAssets = sdkCompile.EnumerateObject().Select(a => a.Name).OrderBy(x => x).ToList();
                    var minrtCompileAssets = minrtCompile.EnumerateObject().Select(a => a.Name).OrderBy(x => x).ToList();

                    Assert.Equal(sdkCompileAssets, minrtCompileAssets);
                }

                // Compare runtime assets if present
                if (sdkPkg.TryGetProperty("runtime", out var sdkRuntime))
                {
                    Assert.True(minrtPkg.TryGetProperty("runtime", out var minrtRuntime),
                        $"Package {packageName} missing runtime assets in MinRT");

                    var sdkRuntimeAssets = sdkRuntime.EnumerateObject().Select(a => a.Name).OrderBy(x => x).ToList();
                    var minrtRuntimeAssets = minrtRuntime.EnumerateObject().Select(a => a.Name).OrderBy(x => x).ToList();

                    Assert.Equal(sdkRuntimeAssets, minrtRuntimeAssets);
                }
            }

            // Compare libraries section
            var sdkLibraries = sdkJson.RootElement.GetProperty("libraries");
            var minrtLibraries = minrtJson.RootElement.GetProperty("libraries");

            var sdkLibNames = sdkLibraries.EnumerateObject().Select(l => l.Name).OrderBy(x => x).ToList();
            var minrtLibNames = minrtLibraries.EnumerateObject().Select(l => l.Name).OrderBy(x => x).ToList();

            Assert.Equal(sdkLibNames, minrtLibNames);

            // Compare SHA512 hashes for each library
            foreach (var libName in sdkLibNames)
            {
                var sdkLib = sdkLibraries.GetProperty(libName);
                var minrtLib = minrtLibraries.GetProperty(libName);

                if (sdkLib.TryGetProperty("sha512", out var sdkHash))
                {
                    Assert.True(minrtLib.TryGetProperty("sha512", out var minrtHash),
                        $"Library {libName} missing sha512 in MinRT");
                    Assert.Equal(sdkHash.GetString(), minrtHash.GetString());
                }
            }

            _output.WriteLine($"✓ All {sdkPackages.Count} packages match!");
        }
        finally
        {
            // Cleanup
            try { Directory.Delete(testDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RestoreAsync_ProjectFileDependencyGroups_Match()
    {
        var testDir = Path.Combine(Path.GetTempPath(), $"minrt-delta-deps-{Guid.NewGuid():N}");
        var minrtDir = Path.Combine(testDir, "minrt");
        var sdkDir = Path.Combine(testDir, "sdk");
        
        Directory.CreateDirectory(minrtDir);
        Directory.CreateDirectory(sdkDir);

        try
        {
            const string packageId = "Microsoft.Extensions.Logging";
            const string version = "9.0.0";
            const string tfm = "net10.0";

            // MinRT restore
            var restorer = NuGetRestorer.CreateBuilder()
                .AddPackage(packageId, version)
                .WithTargetFramework(tfm)
                .WithOutputPath(minrtDir)
                .UseDefaultNuGetConfig();

            await restorer.RestoreAsync();

            // SDK restore
            var csproj = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>{tfm}</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""{packageId}"" Version=""{version}"" />
  </ItemGroup>
</Project>";
            await File.WriteAllTextAsync(Path.Combine(sdkDir, "test.csproj"), csproj);

            var psi = new ProcessStartInfo("dotnet", "restore --verbosity quiet")
            {
                WorkingDirectory = sdkDir,
                UseShellExecute = false
            };
            using var process = Process.Start(psi)!;
            await process.WaitForExitAsync();

            // Parse
            var minrtJson = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(minrtDir, "project.assets.json")));
            var sdkJson = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sdkDir, "obj", "project.assets.json")));

            // Compare projectFileDependencyGroups
            var sdkDepGroups = sdkJson.RootElement.GetProperty("projectFileDependencyGroups").GetProperty(tfm);
            var minrtDepGroups = minrtJson.RootElement.GetProperty("projectFileDependencyGroups").GetProperty(tfm);

            var sdkDeps = sdkDepGroups.EnumerateArray().Select(d => d.GetString()).OrderBy(x => x).ToList();
            var minrtDeps = minrtDepGroups.EnumerateArray().Select(d => d.GetString()).OrderBy(x => x).ToList();

            Assert.Equal(sdkDeps, minrtDeps);
            _output.WriteLine($"✓ projectFileDependencyGroups match: {string.Join(", ", sdkDeps)}");
        }
        finally
        {
            try { Directory.Delete(testDir, recursive: true); } catch { }
        }
    }
}
