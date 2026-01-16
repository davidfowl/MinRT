using System.Reflection;
using System.Runtime.Loader;

namespace MinRT.NuGet;

/// <summary>
/// A custom AssemblyLoadContext that resolves assemblies from NuGet packages.
/// </summary>
public class NuGetLoadContext : AssemblyLoadContext
{
    private readonly Dictionary<string, string> _assemblyPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _probingPaths = [];

    public NuGetLoadContext() : base(isCollectible: true)
    {
    }

    /// <summary>
    /// Adds a mapping from assembly name to file path.
    /// </summary>
    public void AddAssembly(string assemblyName, string path)
    {
        _assemblyPaths[assemblyName] = path;
    }

    /// <summary>
    /// Adds a directory to probe for assemblies.
    /// </summary>
    public void AddProbingPath(string path)
    {
        _probingPaths.Add(path);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null) return null;

        // Check explicit mappings first
        if (_assemblyPaths.TryGetValue(name, out var path) && File.Exists(path))
        {
            return LoadFromAssemblyPath(path);
        }

        // Probe directories
        foreach (var probingPath in _probingPaths)
        {
            var dllPath = Path.Combine(probingPath, $"{name}.dll");
            if (File.Exists(dllPath))
            {
                return LoadFromAssemblyPath(dllPath);
            }
        }

        // Fall back to default context
        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        // Probe for native libraries
        foreach (var probingPath in _probingPaths)
        {
            var nativePath = Path.Combine(probingPath, unmanagedDllName);
            if (File.Exists(nativePath))
            {
                return LoadUnmanagedDllFromPath(nativePath);
            }

            // Try with platform-specific extensions
            if (OperatingSystem.IsWindows())
            {
                nativePath = Path.Combine(probingPath, $"{unmanagedDllName}.dll");
            }
            else if (OperatingSystem.IsLinux())
            {
                nativePath = Path.Combine(probingPath, $"lib{unmanagedDllName}.so");
            }
            else if (OperatingSystem.IsMacOS())
            {
                nativePath = Path.Combine(probingPath, $"lib{unmanagedDllName}.dylib");
            }

            if (File.Exists(nativePath))
            {
                return LoadUnmanagedDllFromPath(nativePath);
            }
        }

        return IntPtr.Zero;
    }
}
