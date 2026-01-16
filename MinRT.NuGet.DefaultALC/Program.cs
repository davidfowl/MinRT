using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using MinRT.NuGet;

Console.WriteLine("=== MinRT.NuGet Default ALC Test ===\n");
Console.WriteLine("This test uses a ModuleInitializer to set up the NuGet ALC");
Console.WriteLine("before any code runs, so Assembly.Load works automatically.\n");

// The NuGetResolver.Initialize() was called by ModuleInitializer before Main()
// Now we can just use types from NuGet packages directly

Console.WriteLine("Test 1: Loading Newtonsoft.Json.JsonConvert via Type.GetType...\n");

var jsonConvertType = Type.GetType("Newtonsoft.Json.JsonConvert, Newtonsoft.Json");

if (jsonConvertType == null)
{
    Console.WriteLine("ERROR: Failed to load type via default resolver");
    return 1;
}

Console.WriteLine($"Loaded type: {jsonConvertType.FullName}");
Console.WriteLine($"From assembly: {jsonConvertType.Assembly.FullName}");

// Use the type via reflection
var serializeMethod = jsonConvertType.GetMethod("SerializeObject", [typeof(object)]);
var testObj = new { Message = "Hello from ModuleInitializer!", Source = "MinRT.NuGet" };
var json = (string)serializeMethod!.Invoke(null, [testObj])!;

Console.WriteLine($"Serialized: {json}");

// Test that framework assemblies still resolve (chaining works)
Console.WriteLine("\nTest 2: Verify framework assemblies still resolve (chaining)...\n");

var listType = Type.GetType("System.Collections.Generic.List`1, System.Collections");
Console.WriteLine($"Framework type resolved: {listType?.FullName ?? "FAILED"}");

Console.WriteLine("\n=== All tests passed! ===");

return 0;

/// <summary>
/// Sets up NuGet package resolution before any other code runs.
/// Chains with the default ALC so framework assemblies still resolve normally.
/// </summary>
static class NuGetResolver
{
    private static NuGetAssemblyLoadContext? _alc;

    [ModuleInitializer]
    public static void Initialize()
    {
        Console.WriteLine("[ModuleInitializer] Setting up NuGet ALC...\n");

        var packagesDir = Path.Combine(AppContext.BaseDirectory, "packages");

        // Build the NuGet ALC synchronously (blocking)
        _alc = NuGetAssemblyLoader.CreateBuilder()
            .AddPackage("Newtonsoft.Json", "13.0.3")
            .WithTargetFramework("net9.0")
            .WithPackagesDirectory(packagesDir)
            .BuildAsync()
            .GetAwaiter()
            .GetResult();

        Console.WriteLine($"[ModuleInitializer] Loaded {_alc.AssemblyPaths.Count} assemblies from NuGet\n");

        // Register as resolver for the default context
        // This chains with default resolution - if we can't resolve it, default ALC continues
        AssemblyLoadContext.Default.Resolving += ResolveFromNuGet;
    }

    private static Assembly? ResolveFromNuGet(AssemblyLoadContext defaultContext, AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name == null || _alc == null)
        {
            return null; // Let default ALC handle it
        }

        // Check if we have this assembly from NuGet
        if (_alc.AssemblyPaths.TryGetValue(name, out var path))
        {
            Console.WriteLine($"[Resolving] {name} -> {Path.GetFileName(path)} (from NuGet)");
            return _alc.LoadFromAssemblyPath(path);
        }

        // Not in our NuGet packages - let default ALC chain continue
        // This ensures framework assemblies, app assemblies, etc. still resolve
        return null;
    }
}
