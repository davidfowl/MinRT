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
    private readonly string _muxerPath;
    private readonly string _appPath;
    private readonly Dictionary<string, string> _assemblyPaths;
    private readonly List<string> _probingPaths;
    private readonly string? _packageLayoutPath;

    internal MinRTContext(
        string runtimePath,
        string runtimeVersion,
        string muxerPath,
        string appPath,
        Dictionary<string, string> assemblyPaths,
        List<string> probingPaths,
        string? packageLayoutPath = null)
    {
        _runtimePath = runtimePath;
        _runtimeVersion = runtimeVersion;
        _muxerPath = muxerPath;
        _appPath = appPath;
        _assemblyPaths = assemblyPaths;
        _probingPaths = probingPaths;
        _packageLayoutPath = packageLayoutPath;
    }

    /// <summary>
    /// Path to the .NET runtime root
    /// </summary>
    public string RuntimePath => _runtimePath;

    /// <summary>
    /// Runtime version (e.g., "10.0.2")
    /// </summary>
    public string RuntimeVersion => _runtimeVersion;

    /// <summary>
    /// Path to the dotnet muxer executable
    /// </summary>
    public string MuxerPath => _muxerPath;

    /// <summary>
    /// Path to the application DLL
    /// </summary>
    public string AppPath => _appPath;

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
    /// Run the application using the dotnet muxer
    /// </summary>
    public int Run(string[]? args = null)
    {
        var psi = CreateProcessStartInfo(args);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start process");

        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>
    /// Run the application async using the dotnet muxer
    /// </summary>
    public async Task<int> RunAsync(string[]? args = null, CancellationToken ct = default)
    {
        var psi = CreateProcessStartInfo(args);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start process");

        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    private ProcessStartInfo CreateProcessStartInfo(string[]? args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _muxerPath,
            UseShellExecute = false,
        };

        // Set DOTNET_ROOT so muxer uses our downloaded runtime
        psi.Environment["DOTNET_ROOT"] = _runtimePath;

        // First argument is the app DLL
        psi.ArgumentList.Add(_appPath);

        // Add user arguments
        if (args is not null)
        {
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
        }

        return psi;
    }
}
