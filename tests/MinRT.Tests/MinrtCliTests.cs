using System.Diagnostics;

namespace MinRT.Tests;

/// <summary>
/// Tests for the minrt CLI tool (restore and layout commands).
/// </summary>
public class MinrtCliTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _minrtExePath;

    public MinrtCliTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"minrt-cli-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // Find the published minrt.exe
        var repoRoot = FindRepoRoot();
        _minrtExePath = Path.Combine(repoRoot, "tools", "minrt", "bin", "Release", "net10.0", "win-x64", "publish", "minrt.exe");
        
        if (!File.Exists(_minrtExePath))
        {
            // Fall back to Debug build via dotnet run
            _minrtExePath = "dotnet";
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task Restore_SinglePackage_CreatesAssetsFile()
    {
        // Arrange
        var outputDir = Path.Combine(_tempDir, "restore-single");
        Directory.CreateDirectory(outputDir);

        // Act
        var (exitCode, output, _) = await RunMinrtAsync("restore", "-p", "Newtonsoft.Json 13.0.3", "-o", outputDir);

        // Assert
        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(outputDir, "project.assets.json")));
    }

    [Fact]
    public async Task Restore_MultiplePackages_ResolvesTransitive()
    {
        // Arrange
        var outputDir = Path.Combine(_tempDir, "restore-multi");
        Directory.CreateDirectory(outputDir);

        // Act
        var (exitCode, output, _) = await RunMinrtAsync(
            "restore",
            "-p", "Microsoft.Extensions.Logging 9.0.0",
            "-p", "Microsoft.Extensions.Options 9.0.0",
            "-o", outputDir);

        // Assert
        Assert.Equal(0, exitCode);
        var assetsPath = Path.Combine(outputDir, "project.assets.json");
        Assert.True(File.Exists(assetsPath));
        
        // Verify transitive dependencies were resolved
        var assetsContent = await File.ReadAllTextAsync(assetsPath);
        Assert.Contains("Microsoft.Extensions.Primitives", assetsContent);
        Assert.Contains("Microsoft.Extensions.DependencyInjection.Abstractions", assetsContent);
    }

    [Fact]
    public async Task Layout_FromAssetsFile_CopiesDlls()
    {
        // Arrange - first restore
        var restoreDir = Path.Combine(_tempDir, "layout-test-restore");
        var layoutDir = Path.Combine(_tempDir, "layout-test-output");
        Directory.CreateDirectory(restoreDir);

        await RunMinrtAsync("restore", "-p", "Newtonsoft.Json 13.0.3", "-o", restoreDir);
        var assetsPath = Path.Combine(restoreDir, "project.assets.json");

        // Act
        var (exitCode, output, _) = await RunMinrtAsync("layout", "-a", assetsPath, "-o", layoutDir);

        // Assert
        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(layoutDir, "Newtonsoft.Json.dll")));
    }

    [Fact]
    public async Task Layout_IncludesTransitiveDeps()
    {
        // Arrange - restore a package with dependencies
        var restoreDir = Path.Combine(_tempDir, "layout-transitive-restore");
        var layoutDir = Path.Combine(_tempDir, "layout-transitive-output");
        Directory.CreateDirectory(restoreDir);

        await RunMinrtAsync("restore", "-p", "Microsoft.Extensions.Logging 9.0.0", "-o", restoreDir);
        var assetsPath = Path.Combine(restoreDir, "project.assets.json");

        // Act
        var (exitCode, _, _) = await RunMinrtAsync("layout", "-a", assetsPath, "-o", layoutDir);

        // Assert
        Assert.Equal(0, exitCode);
        var dlls = Directory.GetFiles(layoutDir, "*.dll");
        Assert.True(dlls.Length > 1, "Should have multiple DLLs including transitive deps");
        Assert.Contains(dlls, d => Path.GetFileName(d).Contains("Logging"));
    }

    [Fact]
    public async Task Restore_InvalidPackage_ExitCode1()
    {
        // Arrange
        var outputDir = Path.Combine(_tempDir, "restore-invalid");
        Directory.CreateDirectory(outputDir);

        // Act
        var (exitCode, _, error) = await RunMinrtAsync("restore", "-p", "NonExistentPackage.ThatDoesNotExist 99.99.99", "-o", outputDir);

        // Assert
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task Layout_InvalidAssets_ExitCode2()
    {
        // Arrange
        var layoutDir = Path.Combine(_tempDir, "layout-invalid");
        var invalidAssets = Path.Combine(_tempDir, "nonexistent.json");

        // Act
        var (exitCode, _, _) = await RunMinrtAsync("layout", "-a", invalidAssets, "-o", layoutDir);

        // Assert
        Assert.NotEqual(0, exitCode);
    }

    private async Task<(int ExitCode, string Output, string Error)> RunMinrtAsync(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _minrtExePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // If using dotnet run, add project path
        if (_minrtExePath == "dotnet")
        {
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(Path.Combine(FindRepoRoot(), "tools", "minrt", "minrt.csproj"));
            psi.ArgumentList.Add("--");
        }

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, output, error);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MinRT.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Could not find repo root");
    }
}
