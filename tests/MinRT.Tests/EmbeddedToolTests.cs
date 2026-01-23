using MinRT.Core;

namespace MinRT.Tests;

/// <summary>
/// Tests for the embedded tool extraction and execution.
/// </summary>
public class EmbeddedToolTests : IDisposable
{
    private readonly string _tempDir;

    public EmbeddedToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"minrt-embedded-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
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
    public async Task GetToolPath_FirstRun_CreatesToolFile()
    {
        // Arrange
        var cacheDir = Path.Combine(_tempDir, "cache1");
        Directory.CreateDirectory(cacheDir);

        // Act
        var toolPath = await EmbeddedTool.GetToolPathAsync(cacheDir);

        // Assert
        Assert.True(File.Exists(toolPath));
        Assert.Contains("minrt", Path.GetFileName(toolPath));
    }

    [Fact]
    public async Task GetToolPath_SecondCall_ReturnsSamePath()
    {
        // Arrange
        var cacheDir = Path.Combine(_tempDir, "cache2");
        Directory.CreateDirectory(cacheDir);

        // Act
        var toolPath1 = await EmbeddedTool.GetToolPathAsync(cacheDir);
        var modTime1 = File.GetLastWriteTimeUtc(toolPath1);
        
        await Task.Delay(100); // Small delay to detect if file is rewritten
        
        var toolPath2 = await EmbeddedTool.GetToolPathAsync(cacheDir);
        var modTime2 = File.GetLastWriteTimeUtc(toolPath2);

        // Assert
        Assert.Equal(toolPath1, toolPath2);
        Assert.Equal(modTime1, modTime2); // File should not be rewritten
    }

    [Fact]
    public async Task RunAsync_ValidCommand_ReturnsZero()
    {
        // Arrange
        var cacheDir = Path.Combine(_tempDir, "cache3");
        Directory.CreateDirectory(cacheDir);

        // Act
        var exitCode = await EmbeddedTool.RunAsync(cacheDir, ["--help"]);

        // Assert
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunWithOutputAsync_HelpCommand_ContainsExpectedText()
    {
        // Arrange
        var cacheDir = Path.Combine(_tempDir, "cache4");
        Directory.CreateDirectory(cacheDir);

        // Act
        var (exitCode, output, _) = await EmbeddedTool.RunWithOutputAsync(cacheDir, ["--help"]);

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Contains("restore", output);
        Assert.Contains("layout", output);
    }

    [Fact]
    public async Task RunAsync_InvalidCommand_ReturnsNonZero()
    {
        // Arrange
        var cacheDir = Path.Combine(_tempDir, "cache5");
        Directory.CreateDirectory(cacheDir);

        // Act
        var exitCode = await EmbeddedTool.RunAsync(cacheDir, ["invalid-command-that-does-not-exist"]);

        // Assert
        Assert.NotEqual(0, exitCode);
    }
}
