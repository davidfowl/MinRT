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
var gapTestsPassed = 0;
var gapTestsFailed = 0;

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

// ============================================================================
// GAP TESTS - These tests demonstrate missing features
// They are expected to FAIL until the gaps are fixed
// ============================================================================

Console.WriteLine("\n=== GAP TESTS (Expected to fail until fixed) ===");

// Gap Test 1: RID-specific asset selection
// SQLite.Core has RID-specific managed assemblies in runtimes/{rid}/lib/
await RunGapTestAsync("GAP: RID-specific managed assemblies", async () =>
{
    var alc = await NuGetAssemblyLoader.CreateBuilder()
        .AddPackage("Microsoft.Data.Sqlite.Core", "9.0.0")
        .WithTargetFramework("net9.0")
        .WithPackagesDirectory(packagesDir)
        .WithLogger(loggerFactory)
        .BuildAsync();

    AssertTrue(alc.AssemblyPaths.ContainsKey("Microsoft.Data.Sqlite"), "Has Microsoft.Data.Sqlite");
    
    // Try to actually use SQLite - this requires the correct RID-specific implementation
    var assembly = alc.LoadAssembly("Microsoft.Data.Sqlite");
    var connType = assembly.GetType("Microsoft.Data.Sqlite.SqliteConnection");
    AssertNotNull(connType, "SqliteConnection type found");
    
    // Create a connection - this will fail if RID assets aren't loaded correctly
    var conn = Activator.CreateInstance(connType!, "Data Source=:memory:");
    var openMethod = connType!.GetMethod("Open")!;
    openMethod.Invoke(conn, null);  // This may fail without proper native library
    
    var closeMethod = connType.GetMethod("Close")!;
    closeMethod.Invoke(conn, null);
});

// Gap Test 2: Native library loading (runtimes/{rid}/native/)
// SQLitePCLRaw.bundle_e_sqlite3 has native SQLite binaries
await RunGapTestAsync("GAP: Native library loading", async () =>
{
    var alc = await NuGetAssemblyLoader.CreateBuilder()
        .AddPackage("SQLitePCLRaw.bundle_e_sqlite3", "2.1.10")
        .WithTargetFramework("net9.0")
        .WithPackagesDirectory(packagesDir)
        .WithLogger(loggerFactory)
        .BuildAsync();

    // The package should expose the native library path
    // Currently LoadUnmanagedDll returns IntPtr.Zero
    
    // Try to load the provider - this will fail without native library support
    var providerAssembly = alc.LoadAssembly("SQLitePCLRaw.provider.e_sqlite3");
    AssertNotNull(providerAssembly, "Provider assembly loaded");
    
    // Calling into SQLite will fail because native e_sqlite3.dll isn't loaded
    var batteries = alc.LoadAssembly("SQLitePCLRaw.batteries_v2");
    var initMethod = batteries.GetType("SQLitePCL.Batteries_V2")?.GetMethod("Init");
    initMethod?.Invoke(null, null);  // Will throw if native lib not found
});

// Gap Test 3: Native library paths not collected
// Packages put native libs in runtimes/{rid}/native/ which we don't process
await RunGapTestAsync("GAP: Native library paths not collected", async () =>
{
    // SQLitePCLRaw packages have native libraries in runtimes/{rid}/native/
    var alc = await NuGetAssemblyLoader.CreateBuilder()
        .AddPackage("SQLitePCLRaw.lib.e_sqlite3", "2.1.10")
        .WithTargetFramework("net9.0")
        .WithPackagesDirectory(packagesDir)
        .WithLogger(loggerFactory)
        .BuildAsync();

    // This package only has native libraries, no managed assemblies
    // The AssemblyPaths should be empty or very small
    Console.WriteLine($"    Assembly paths count: {alc.AssemblyPaths.Count}");
    
    // The real gap: we don't expose native library paths at all
    // There should be a NativeLibraryPaths property or similar
    var alcType = alc.GetType();
    var nativePathsProperty = alcType.GetProperty("NativeLibraryPaths");
    
    if (nativePathsProperty == null)
    {
        throw new Exception("No NativeLibraryPaths property - native libs not tracked");
    }
    
    var nativePaths = nativePathsProperty.GetValue(alc) as IReadOnlyDictionary<string, string>;
    AssertTrue(nativePaths?.Count > 0, "Should have native library paths");
});

// Gap Test 4: Lock file support - reproducible resolution
await RunGapTestAsync("GAP: Lock file for reproducible builds", async () =>
{
    // This test demonstrates we don't support lock files
    // Two resolves of floating versions could give different results
    
    var lockFilePath = Path.Combine(packagesDir, "test.lock.json");
    
    // Currently there's no API to:
    // 1. Generate a lock file
    // 2. Use an existing lock file
    
    // We'd want something like:
    // .WithLockFile(lockFilePath)
    // .GenerateLockFile(lockFilePath)
    
    // For now, just verify the API doesn't exist
    var builder = NuGetAssemblyLoader.CreateBuilder();
    var builderType = builder.GetType();
    var lockFileMethod = builderType.GetMethod("WithLockFile");
    
    // This assertion should FAIL once we add lock file support
    AssertTrue(lockFileMethod == null, "WithLockFile method should not exist yet");
    
    // Once fixed, this test should verify lock file works
    throw new Exception("Lock file support not implemented - no WithLockFile() API");
});

// Gap Test 5: Package downgrade detection
await RunGapTestAsync("GAP: Package downgrade warning", async () =>
{
    // Scenario: Direct reference to older version than transitive requires
    // Package A v1.0 depends on B >= 2.0
    // But we directly reference B v1.0
    // SDK would warn about downgrade
    
    // Using a real example: Microsoft.Extensions.Logging 9.0.0 depends on
    // Microsoft.Extensions.Logging.Abstractions 9.0.0
    // If we also add Abstractions 8.0.0, what happens?
    
    var alc = await NuGetAssemblyLoader.CreateBuilder()
        .AddPackage("Microsoft.Extensions.Logging", "9.0.0")
        .AddPackage("Microsoft.Extensions.Logging.Abstractions", "8.0.0")  // Downgrade!
        .WithTargetFramework("net9.0")
        .WithPackagesDirectory(packagesDir)
        .WithLogger(loggerFactory)
        .BuildAsync();

    // Check which version was actually selected
    var abstractionsPath = alc.AssemblyPaths["Microsoft.Extensions.Logging.Abstractions"];
    Console.WriteLine($"    Abstractions path: {abstractionsPath}");
    
    // Currently: No warning about downgrade attempt
    // SDK behavior: Would warn "Detected package downgrade"
    
    // We should have selected 9.0.0 (higher wins) but did we warn?
    // This test passes silently - the gap is the missing warning
    throw new Exception("No downgrade warning was emitted - gap not fixed");
});

// Gap Test 6: Source mapping
await RunGapTestAsync("GAP: Package source mapping", async () =>
{
    // SDK supports packageSourceMapping in nuget.config to restrict
    // which packages can come from which sources (security feature)
    
    // Example nuget.config:
    // <packageSourceMapping>
    //   <packageSource key="nuget.org">
    //     <package pattern="Newtonsoft.*" />
    //   </packageSource>
    //   <packageSource key="internal">
    //     <package pattern="MyCompany.*" />
    //   </packageSource>
    // </packageSourceMapping>
    
    var builder = NuGetAssemblyLoader.CreateBuilder();
    var builderType = builder.GetType();
    var mappingMethod = builderType.GetMethod("WithSourceMapping");
    
    AssertTrue(mappingMethod == null, "WithSourceMapping should not exist yet");
    
    throw new Exception("Source mapping not implemented");
});

// Gap Test 7: Framework references
await RunGapTestAsync("GAP: Framework reference handling", async () =>
{
    // Some packages have FrameworkReference dependencies
    // e.g., Microsoft.AspNetCore.* packages reference Microsoft.AspNetCore.App
    
    // Currently we don't handle FrameworkReference items in packages
    // The SDK resolves these to targeting/runtime packs
    
    var alc = await NuGetAssemblyLoader.CreateBuilder()
        .AddPackage("Microsoft.AspNetCore.Authentication", "2.2.0")
        .WithTargetFramework("net9.0")
        .WithPackagesDirectory(packagesDir)
        .WithLogger(loggerFactory)
        .BuildAsync();

    // This package has a FrameworkReference to Microsoft.AspNetCore.App
    // We should either:
    // 1. Resolve the framework reference
    // 2. Or at least not crash
    
    AssertTrue(alc.AssemblyPaths.Count > 0, "Some assemblies loaded");
    
    // The gap: we don't process frameworkReference items
    throw new Exception("Framework references not handled");
});

// Summary
Console.WriteLine("\n=== Gap Test Summary ===");
Console.WriteLine($"Gap tests passed: {gapTestsPassed} (these gaps have been FIXED)");
Console.WriteLine($"Gap tests failed: {gapTestsFailed} (these gaps still EXIST)");
Console.WriteLine("");

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

// Gap test helper - failures are expected (they represent unfixed gaps)
async Task RunGapTestAsync(string name, Func<Task> test)
{
    Console.WriteLine($"\nGap Test: {name}");
    try
    {
        await test();
        Console.WriteLine($"  ✓ GAP FIXED (test passed)");
        gapTestsPassed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ⚠ GAP EXISTS: {ex.Message}");
        gapTestsFailed++;
    }
}
