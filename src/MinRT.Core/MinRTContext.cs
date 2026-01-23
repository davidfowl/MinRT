// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace MinRT.Core;

/// <summary>
/// A built runtime context that can execute .NET entry points.
/// </summary>
public sealed class MinRTContext
{
    private readonly string _runtimePath;
    private readonly string _runtimeVersion;
    private readonly string _appHostPath;
    private readonly Dictionary<string, string> _assemblyPaths;
    private readonly List<string> _probingPaths;
    private readonly string? _packageLayoutPath;

    internal MinRTContext(
        string runtimePath,
        string runtimeVersion,
        string appHostPath,
        Dictionary<string, string> assemblyPaths,
        List<string> probingPaths,
        string? packageLayoutPath = null)
    {
        _runtimePath = runtimePath;
        _runtimeVersion = runtimeVersion;
        _appHostPath = appHostPath;
        _assemblyPaths = assemblyPaths;
        _probingPaths = probingPaths;
        _packageLayoutPath = packageLayoutPath;
    }

    /// <summary>
    /// Path to the .NET runtime root
    /// </summary>
    public string RuntimePath => _runtimePath;

    /// <summary>
    /// Runtime version (e.g., "9.0.0")
    /// </summary>
    public string RuntimeVersion => _runtimeVersion;

    /// <summary>
    /// Path to the patched apphost executable
    /// </summary>
    public string AppHostPath => _appHostPath;

    /// <summary>
    /// Resolved assembly paths (assembly name -> full path)
    /// </summary>
    public IReadOnlyDictionary<string, string> AssemblyPaths => _assemblyPaths;

    /// <summary>
    /// Path to the restored package layout directory (contains DLLs from NuGet packages).
    /// Null if no packages were restored.
    /// </summary>
    public string? PackageLayoutPath => _packageLayoutPath;

    /// <summary>
    /// Run the application
    /// </summary>
    public int Run(string[]? args = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _appHostPath,
            UseShellExecute = false,
        };

        // Set DOTNET_ROOT so apphost finds our downloaded runtime
        psi.Environment["DOTNET_ROOT"] = _runtimePath;

        if (args is not null)
        {
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start process");

        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>
    /// Run the application async
    /// </summary>
    public async Task<int> RunAsync(string[]? args = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _appHostPath,
            UseShellExecute = false,
        };

        psi.Environment["DOTNET_ROOT"] = _runtimePath;

        if (args is not null)
        {
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start process");

        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }
}
