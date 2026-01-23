using System.Reflection;
using System.Runtime.InteropServices;

namespace MinRT.Core;

/// <summary>
/// Manages extraction and invocation of the embedded minrt tool.
/// </summary>
public static class EmbeddedTool
{
    private const string ToolResourceName = "MinRT.Core.minrt";
    private const string VersionFileName = ".minrt-version";

    /// <summary>
    /// Gets the path to the extracted minrt tool, extracting it if necessary.
    /// </summary>
    public static async Task<string> GetToolPathAsync(string cacheDirectory, CancellationToken ct = default)
    {
        var toolsDir = Path.Combine(cacheDirectory, "tools");
        var toolName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "minrt.exe" : "minrt";
        var toolPath = Path.Combine(toolsDir, toolName);
        var versionPath = Path.Combine(toolsDir, VersionFileName);

        // Check if extraction is needed
        if (NeedsExtraction(toolPath, versionPath))
        {
            await ExtractToolAsync(toolPath, versionPath, ct);
        }

        return toolPath;
    }

    private static bool NeedsExtraction(string toolPath, string versionPath)
    {
        // Tool doesn't exist
        if (!File.Exists(toolPath))
        {
            return true;
        }

        // Version file doesn't exist
        if (!File.Exists(versionPath))
        {
            return true;
        }

        // Version mismatch
        var currentVersion = GetCurrentVersion();
        var extractedVersion = File.ReadAllText(versionPath).Trim();
        
        return !string.Equals(currentVersion, extractedVersion, StringComparison.Ordinal);
    }

    private static async Task ExtractToolAsync(string toolPath, string versionPath, CancellationToken ct)
    {
        var assembly = typeof(EmbeddedTool).Assembly;
        
        // Find the embedded resource
        var resourceName = GetResourceName(assembly);
        if (resourceName == null)
        {
            throw new InvalidOperationException(
                "Embedded minrt tool not found. Available resources: " + 
                string.Join(", ", assembly.GetManifestResourceNames()));
        }

        using var resourceStream = assembly.GetManifestResourceStream(resourceName);
        if (resourceStream == null)
        {
            throw new InvalidOperationException($"Could not load embedded resource: {resourceName}");
        }

        // Ensure directory exists
        var toolsDir = Path.GetDirectoryName(toolPath)!;
        Directory.CreateDirectory(toolsDir);

        // Extract the tool
        using var fileStream = new FileStream(toolPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await resourceStream.CopyToAsync(fileStream, ct);

        // Make executable on Unix
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            MakeExecutable(toolPath);
        }

        // Write version file
        await File.WriteAllTextAsync(versionPath, GetCurrentVersion(), ct);
    }

    private static string? GetResourceName(Assembly assembly)
    {
        var names = assembly.GetManifestResourceNames();
        
        // Look for minrt.exe or minrt (without extension on Unix)
        return names.FirstOrDefault(n => 
            n.EndsWith("minrt.exe", StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith("minrt", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetCurrentVersion()
    {
        var assembly = typeof(EmbeddedTool).Assembly;
        return assembly.GetName().Version?.ToString() ?? "1.0.0.0";
    }

    private static void MakeExecutable(string path)
    {
        // Use chmod via Process on Unix-like systems
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "chmod";
            process.StartInfo.Arguments = $"+x \"{path}\"";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            process.WaitForExit();
        }
        catch
        {
            // Best effort - some systems may not need this
        }
    }

    /// <summary>
    /// Runs the embedded minrt tool with the specified arguments.
    /// </summary>
    public static async Task<int> RunAsync(
        string cacheDirectory,
        string[] arguments,
        CancellationToken ct = default)
    {
        var toolPath = await GetToolPathAsync(cacheDirectory, ct);
        
        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = toolPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        
        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        await process.WaitForExitAsync(ct);
        
        return process.ExitCode;
    }

    /// <summary>
    /// Runs the embedded minrt tool with the specified arguments and captures output.
    /// </summary>
    public static async Task<(int ExitCode, string Output, string Error)> RunWithOutputAsync(
        string cacheDirectory,
        string[] arguments,
        CancellationToken ct = default)
    {
        var toolPath = await GetToolPathAsync(cacheDirectory, ct);
        
        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = toolPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        
        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        
        await process.WaitForExitAsync(ct);
        
        return (process.ExitCode, await outputTask, await errorTask);
    }
}
