using System.Reflection;

namespace MinRT.Core;

/// <summary>
/// Manages extraction and invocation of the embedded minrt tool.
/// </summary>
public static class EmbeddedTool
{
    private const string ResourcePrefix = "MinRT.Core.minrt.";
    private const string VersionFileName = ".minrt-version";

    /// <summary>
    /// Gets the path to the extracted minrt.dll, extracting all tool files if necessary.
    /// </summary>
    public static async Task<string> GetToolPathAsync(string cacheDirectory, CancellationToken ct = default)
    {
        var toolsDir = Path.Combine(cacheDirectory, "tools", "minrt");
        var toolPath = Path.Combine(toolsDir, "minrt.dll");
        var versionPath = Path.Combine(toolsDir, VersionFileName);

        // Check if extraction is needed
        if (NeedsExtraction(toolPath, versionPath))
        {
            await ExtractToolAsync(toolsDir, versionPath, ct);
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

    private static async Task ExtractToolAsync(string toolsDir, string versionPath, CancellationToken ct)
    {
        var assembly = typeof(EmbeddedTool).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToList();

        if (resourceNames.Count == 0)
        {
            throw new InvalidOperationException(
                "Embedded minrt tool not found. Available resources: " + 
                string.Join(", ", assembly.GetManifestResourceNames()));
        }

        // Ensure directory exists
        Directory.CreateDirectory(toolsDir);

        // Extract all embedded files
        foreach (var resourceName in resourceNames)
        {
            var fileName = resourceName[ResourcePrefix.Length..]; // Remove prefix
            var filePath = Path.Combine(toolsDir, fileName);

            using var resourceStream = assembly.GetManifestResourceStream(resourceName);
            if (resourceStream == null) continue;

            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await resourceStream.CopyToAsync(fileStream, ct);
        }

        // Write version file
        await File.WriteAllTextAsync(versionPath, GetCurrentVersion(), ct);
    }

    private static string GetCurrentVersion()
    {
        var assembly = typeof(EmbeddedTool).Assembly;
        return assembly.GetName().Version?.ToString() ?? "1.0.0.0";
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
        process.StartInfo.FileName = "dotnet";
        process.StartInfo.ArgumentList.Add(toolPath);
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
        process.StartInfo.FileName = "dotnet";
        process.StartInfo.ArgumentList.Add(toolPath);
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
