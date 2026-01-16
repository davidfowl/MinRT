using Microsoft.Extensions.Logging;
using MinRT.NuGet;

// Set up logging
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .SetMinimumLevel(LogLevel.Debug)
        .AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
});

var logger = loggerFactory.CreateLogger<NuGetAssemblyLoader>();

Console.WriteLine("=== MinRT.NuGet Test ===\n");

// Test 1: Simple package (Newtonsoft.Json)
Console.WriteLine("Test 1: Loading Newtonsoft.Json...\n");

var packagesDir = Path.Combine(AppContext.BaseDirectory, "packages");

var alc = await NuGetAssemblyLoader.CreateBuilder()
    .AddPackage("Newtonsoft.Json", "13.0.3")
    .WithTargetFramework("net9.0")
    .WithPackagesDirectory(packagesDir)
    .WithLogger(loggerFactory)
    .BuildAsync();

Console.WriteLine("\nResolved assemblies:");
foreach (var (name, path) in alc.AssemblyPaths)
{
    Console.WriteLine($"  {name} -> {Path.GetFileName(path)}");
}

// Actually use the loaded assembly
var jsonAssembly = alc.LoadAssembly("Newtonsoft.Json");
Console.WriteLine($"\nLoaded: {jsonAssembly.FullName}");

// Use reflection to call JsonConvert.SerializeObject
var jsonConvertType = jsonAssembly.GetType("Newtonsoft.Json.JsonConvert")!;
var serializeMethod = jsonConvertType.GetMethod("SerializeObject", [typeof(object)])!;
var testObj = new { Message = "Hello from MinRT.NuGet!", Time = DateTime.Now };
var json = (string)serializeMethod.Invoke(null, [testObj])!;
Console.WriteLine($"Serialized: {json}\n");

// Test 2: Package with transitive dependencies
Console.WriteLine("Test 2: Loading Microsoft.Extensions.Logging (has transitive deps)...\n");

var alc2 = await NuGetAssemblyLoader.CreateBuilder()
    .AddPackage("Microsoft.Extensions.Logging", "9.0.0")
    .WithTargetFramework("net9.0")
    .WithPackagesDirectory(packagesDir)
    .WithLogger(loggerFactory)
    .BuildAsync();

Console.WriteLine("\nResolved assemblies:");
foreach (var (name, path) in alc2.AssemblyPaths.OrderBy(x => x.Key))
{
    Console.WriteLine($"  {name}");
}

var loggingAssembly = alc2.LoadAssembly("Microsoft.Extensions.Logging");
Console.WriteLine($"\nLoaded: {loggingAssembly.FullName}");

Console.WriteLine("\n=== All tests passed! ===");
