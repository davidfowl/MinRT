// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace MinRT.Core;

/// <summary>
/// A built runtime context that can execute .NET entry points.
/// </summary>
public sealed class MinRTContext
{
    private readonly string _runtimePath;
    private readonly string _runtimeVersion;
    private readonly Dictionary<string, string> _assemblyPaths;
    private readonly List<string> _probingPaths;

    internal MinRTContext(
        string runtimePath,
        string runtimeVersion,
        Dictionary<string, string> assemblyPaths,
        List<string> probingPaths)
    {
        _runtimePath = runtimePath;
        _runtimeVersion = runtimeVersion;
        _assemblyPaths = assemblyPaths;
        _probingPaths = probingPaths;
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
    /// Resolved assembly paths (assembly name -> full path)
    /// </summary>
    public IReadOnlyDictionary<string, string> AssemblyPaths => _assemblyPaths;

    /// <summary>
    /// Run a managed entry point
    /// </summary>
    public int Run(string entryAssembly, string[]? args = null)
    {
        var entryPath = ResolveAssemblyPath(entryAssembly);
        return RunWithHostFxr(entryPath, args);
    }

    /// <summary>
    /// Run a managed entry point async
    /// </summary>
    public Task<int> RunAsync(string entryAssembly, string[]? args = null, CancellationToken ct = default)
    {
        // hostfxr_run_app is blocking, so run on thread pool
        return Task.Run(() => Run(entryAssembly, args), ct);
    }

    private string ResolveAssemblyPath(string assemblyName)
    {
        // If it's already a full path, use it
        if (Path.IsPathRooted(assemblyName) && File.Exists(assemblyName))
        {
            return assemblyName;
        }

        // Try exact match in assembly paths
        if (_assemblyPaths.TryGetValue(assemblyName, out var path))
        {
            return path;
        }

        // Try without extension
        var nameWithoutExt = Path.GetFileNameWithoutExtension(assemblyName);
        if (_assemblyPaths.TryGetValue(nameWithoutExt, out path))
        {
            return path;
        }

        // Search probing paths
        foreach (var probingPath in _probingPaths)
        {
            var candidate = Path.Combine(probingPath, assemblyName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            // Try with .dll extension
            if (!assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                candidate = Path.Combine(probingPath, assemblyName + ".dll");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException($"Assembly not found: {assemblyName}");
    }

    private int RunWithHostFxr(string entryPath, string[]? args)
    {
        var hostfxrName = GetHostFxrName();
        var hostfxrPath = Path.Combine(_runtimePath, "host", "fxr", _runtimeVersion, hostfxrName);

        if (!File.Exists(hostfxrPath))
        {
            throw new FileNotFoundException($"hostfxr not found at {hostfxrPath}");
        }

        // Load hostfxr
        NativeLibrary.Load(hostfxrPath);

        // Build argv
        var argv = args is null || args.Length == 0
            ? new[] { entryPath }
            : new[] { entryPath }.Concat(args).ToArray();

        unsafe
        {
            // On Windows: UTF-16, on Unix: UTF-8
            nint hostPathPtr = IntPtr.Zero;
            nint dotnetRootPtr = IntPtr.Zero;

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    hostPathPtr = Marshal.StringToHGlobalUni(_runtimePath);
                    dotnetRootPtr = Marshal.StringToHGlobalUni(_runtimePath);
                }
                else
                {
                    hostPathPtr = Marshal.StringToHGlobalAnsi(_runtimePath);
                    dotnetRootPtr = Marshal.StringToHGlobalAnsi(_runtimePath);
                }

                var parameters = new HostFxrImports.hostfxr_initialize_parameters
                {
                    size = sizeof(HostFxrImports.hostfxr_initialize_parameters),
                    host_path = hostPathPtr,
                    dotnet_root = dotnetRootPtr
                };

                var err = HostFxrImports.Initialize(argv.Length, argv, ref parameters, out var handle);
                if (err < 0)
                {
                    throw new InvalidOperationException($"hostfxr_initialize failed with error code: 0x{err:X8}");
                }

                try
                {
                    return HostFxrImports.Run(handle);
                }
                finally
                {
                    HostFxrImports.Close(handle);
                }
            }
            finally
            {
                if (hostPathPtr != IntPtr.Zero) Marshal.FreeHGlobal(hostPathPtr);
                if (dotnetRootPtr != IntPtr.Zero) Marshal.FreeHGlobal(dotnetRootPtr);
            }
        }
    }

    private static string GetHostFxrName()
    {
        if (OperatingSystem.IsWindows()) return "hostfxr.dll";
        if (OperatingSystem.IsMacOS()) return "libhostfxr.dylib";
        return "libhostfxr.so";
    }
}
