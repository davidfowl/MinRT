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

var packagesDir = Path.Combine(AppContext.BaseDirectory, "packages");
var testsPassed = 0;
var testsFailed = 0;

// Test 1: Simple package (Newtonsoft.Json)
await RunTestAsync("Simple package (Newtonsoft.Json)", async () =>
{
    var alc = await NuGetAssemblyLoader.CreateBuilder()
        .AddPackage("Newtonsoft.Json", "13.0.3")
        .WithTargetFramework("net9.0")
        .WithPackagesDirectory(packagesDir)
        .WithLogger(loggerFactory)
        .BuildAsync();

    AssertEqual(1, alc.AssemblyPaths.Count, "Assembly count");
    AssertTrue(alc.AssemblyPaths.ContainsKey("Newtonsoft.Json"), "Has Newtonsoft.Json");

    var assembly = alc.LoadAssembly("Newtonsoft.Json");
    AssertNotNull(assembly, "Loaded assembly");
    
    // Use the assembly
    var jsonConvertType = assembly.GetType("Newtonsoft.Json.JsonConvert")!;
    var serializeMethod = jsonConvertType.GetMethod("SerializeObject", [typeof(object)])!;
    var json = (string)serializeMethod.Invoke(null, [new { test = true }])!;
    AssertTrue(json.Contains("test"), "Serialization works");
});

// Test 2: Package with transitive dependencies
await RunTestAsync("Transitive dependencies (Microsoft.Extensions.Logging)", async () =>
{
    var alc = await NuGetAssemblyLoader.CreateBuilder()
        .AddPackage("Microsoft.Extensions.Logging", "9.0.0")
        .WithTargetFramework("net9.0")
        .WithPackagesDirectory(packagesDir)
        .WithLogger(loggerFactory)
        .BuildAsync();

    // Should have multiple assemblies from transitive deps
    AssertTrue(alc.AssemblyPaths.Count >= 3, $"Expected >= 3 assemblies, got {alc.AssemblyPaths.Count}");
    AssertTrue(alc.AssemblyPaths.ContainsKey("Microsoft.Extensions.Logging"), "Has Logging");
    AssertTrue(alc.AssemblyPaths.ContainsKey("Microsoft.Extensions.Logging.Abstractions"), "Has Abstractions");

    var assembly = alc.LoadAssembly("Microsoft.Extensions.Logging");
    AssertNotNull(assembly, "Loaded assembly");
});

// Test 3: UseDefaultNuGetConfig (uses system nuget.config)
await RunTestAsync("UseDefaultNuGetConfig resolution", async () =>
{
    var alc = await NuGetAssemblyLoader.CreateBuilder()
        .AddPackage("Humanizer.Core", "2.14.1")
        .WithTargetFramework("net9.0")
        .WithPackagesDirectory(packagesDir)
        .UseDefaultNuGetConfig()
        .WithLogger(loggerFactory)
        .BuildAsync();

    AssertEqual(1, alc.AssemblyPaths.Count, "Assembly count");
    AssertTrue(alc.AssemblyPaths.ContainsKey("Humanizer"), "Has Humanizer");
});

// Test 4: Multiple packages at once
await RunTestAsync("Multiple packages", async () =>
{
    var alc = await NuGetAssemblyLoader.CreateBuilder()
        .AddPackage("Newtonsoft.Json", "13.0.3")
        .AddPackage("Humanizer.Core", "2.14.1")
        .WithTargetFramework("net9.0")
        .WithPackagesDirectory(packagesDir)
        .WithLogger(loggerFactory)
        .BuildAsync();

    AssertTrue(alc.AssemblyPaths.ContainsKey("Newtonsoft.Json"), "Has Newtonsoft.Json");
    AssertTrue(alc.AssemblyPaths.ContainsKey("Humanizer"), "Has Humanizer");
});

// Test 5: Version range (minimum version)
await RunTestAsync("Version range (>= 13.0.0)", async () =>
{
    var alc = await NuGetAssemblyLoader.CreateBuilder()
        .AddPackage("Newtonsoft.Json", "13.0.0", allowNewer: true)
        .WithTargetFramework("net9.0")
        .WithPackagesDirectory(packagesDir)
        .WithLogger(loggerFactory)
        .BuildAsync();

    AssertTrue(alc.AssemblyPaths.ContainsKey("Newtonsoft.Json"), "Has Newtonsoft.Json");
    
    var assembly = alc.LoadAssembly("Newtonsoft.Json");
    var version = assembly.GetName().Version;
    AssertTrue(version >= new Version(13, 0, 0), $"Version {version} >= 13.0.0");
});

// Test 6: Caching - second load should be faster
await RunTestAsync("Package caching", async () =>
{
    // First load (may download)
    var sw1 = System.Diagnostics.Stopwatch.StartNew();
    var alc1 = await NuGetAssemblyLoader.CreateBuilder()
        .AddPackage("Polly.Core", "8.5.2")
        .WithTargetFramework("net9.0")
        .WithPackagesDirectory(packagesDir)
        .WithLogger(loggerFactory)
        .BuildAsync();
    sw1.Stop();

    // Second load (should use cache)
    var sw2 = System.Diagnostics.Stopwatch.StartNew();
    var alc2 = await NuGetAssemblyLoader.CreateBuilder()
        .AddPackage("Polly.Core", "8.5.2")
        .WithTargetFramework("net9.0")
        .WithPackagesDirectory(packagesDir)
        .WithLogger(loggerFactory)
        .BuildAsync();
    sw2.Stop();

    Console.WriteLine($"    First load: {sw1.ElapsedMilliseconds}ms, Second load: {sw2.ElapsedMilliseconds}ms");
    AssertTrue(alc2.AssemblyPaths.ContainsKey("Polly.Core"), "Has Polly.Core");
});

// Test 7: Package not found error
await RunTestAsync("Package not found error", async () =>
{
    try
    {
        await NuGetAssemblyLoader.CreateBuilder()
            .AddPackage("This.Package.Does.Not.Exist.MinRT.Test", "1.0.0")
            .WithTargetFramework("net9.0")
            .WithPackagesDirectory(packagesDir)
            .WithLogger(loggerFactory)
            .BuildAsync();
        
        throw new Exception("Expected InvalidOperationException");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Package not found"))
    {
        // Expected
        Console.WriteLine($"    Got expected error: {ex.Message}");
    }
});

// Test 8: Different target framework
await RunTestAsync("Target framework net8.0", async () =>
{
    var alc = await NuGetAssemblyLoader.CreateBuilder()
        .AddPackage("Newtonsoft.Json", "13.0.3")
        .WithTargetFramework("net8.0")
        .WithPackagesDirectory(packagesDir)
        .WithLogger(loggerFactory)
        .BuildAsync();

    AssertTrue(alc.AssemblyPaths.ContainsKey("Newtonsoft.Json"), "Has Newtonsoft.Json");
});

// Summary
Console.WriteLine($"\n=== Results: {testsPassed} passed, {testsFailed} failed ===");
return testsFailed > 0 ? 1 : 0;

// Test helpers
async Task RunTestAsync(string name, Func<Task> test)
{
    Console.WriteLine($"\nTest: {name}");
    try
    {
        await test();
        Console.WriteLine($"  ✓ PASSED");
        testsPassed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ✗ FAILED: {ex.Message}");
        testsFailed++;
    }
}

void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{message}: expected {expected}, got {actual}");
}

void AssertTrue(bool condition, string message)
{
    if (!condition)
        throw new Exception($"{message}: condition was false");
}

void AssertNotNull(object? obj, string message)
{
    if (obj == null)
        throw new Exception($"{message}: was null");
}
