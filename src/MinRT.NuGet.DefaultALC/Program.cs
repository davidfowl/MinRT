using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Humanizer;  // <-- Statically compiled against, but NOT in output!
using Microsoft.Extensions.Logging;
using MinRT.NuGet;

Console.WriteLine("=== MinRT.NuGet Static Compile Test ===\n");
Console.WriteLine("This app compiles against Humanizer but the DLL is NOT");
Console.WriteLine("in the output folder. The ModuleInitializer downloads it from");
Console.WriteLine("NuGet before Main() runs, so it's available when needed.\n");

// At this point, Humanizer has been downloaded and registered
// by the ModuleInitializer. Now we can use it with full IntelliSense!

// This line would crash without the NuGet ALC - Humanizer.dll isn't deployed!
var text = "the quick brown fox jumps over the lazy dog";
var titleCase = text.Titleize();

Console.WriteLine($"Original: {text}");
Console.WriteLine($"Titleized: {titleCase}");

var number = 42;
var ordinal = number.Ordinalize();
Console.WriteLine($"\n{number} -> {ordinal}");

var words = "PascalCaseIdentifier".Humanize();
Console.WriteLine($"\nHumanized: {words}");

Console.WriteLine("\n=== Test passed! ===");
return 0;

/// <summary>
/// Downloads NuGet packages before any other code runs.
/// This makes statically-referenced packages available at runtime.
/// </summary>
static class NuGetResolver
{
    private static NuGetAssemblyLoadContext? _alc;

    [ModuleInitializer]
    public static void Initialize()
    {
        Console.WriteLine("[ModuleInitializer] Downloading dependencies from NuGet...\n");

        var packagesDir = Path.Combine(AppContext.BaseDirectory, "packages");
        
        // Simple console logger for debugging
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        var logger = loggerFactory.CreateLogger("NuGetLoader");

        // Download the same packages we compiled against
        // UseDefaultNuGetConfig() uses standard nuget.config resolution like dotnet restore
        _alc = NuGetAssemblyLoader.CreateBuilder()
            .AddPackage("Humanizer.Core", "2.14.1")
            .WithTargetFramework("net9.0")
            .WithPackagesDirectory(packagesDir)
            .UseDefaultNuGetConfig()  // Uses nuget.config from directory tree + user config
            .WithLogger(logger)
            .BuildAsync()
            .GetAwaiter()
            .GetResult();

        Console.WriteLine($"[ModuleInitializer] Downloaded {_alc.AssemblyPaths.Count} assemblies\n");

        // Register resolver - chains with default ALC
        AssemblyLoadContext.Default.Resolving += ResolveFromNuGet;
    }

    private static Assembly? ResolveFromNuGet(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name != null && _alc?.AssemblyPaths.TryGetValue(name, out var path) == true)
        {
            Console.WriteLine($"[Resolving] {name} from NuGet cache");
            return _alc.LoadFromAssemblyPath(path);
        }
        return null; // Let default ALC handle it
    }
}
