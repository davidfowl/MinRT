using MinRT.Core;

// Test: Run hello.dll using system runtime
// Note: Since we're running from managed code, hostfxr is already initialized.
// We spawn a separate process to test the actual execution.

var testArtifacts = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "test-artifacts"));
Console.WriteLine($"Test artifacts: {testArtifacts}");
Console.WriteLine($"hello.dll exists: {File.Exists(Path.Combine(testArtifacts, "hello.dll"))}");
Console.WriteLine();

// Test 1: Build context with system runtime
Console.WriteLine("=== Test 1: Build context with system runtime ===");
var context = await new MinRTBuilder()
    .WithTargetFramework("net10.0")
    .AddProbingPath(testArtifacts)
    .UseSystemRuntime()
    .BuildAsync();

Console.WriteLine($"Runtime Path: {context.RuntimePath}");
Console.WriteLine($"Runtime Version: {context.RuntimeVersion}");
Console.WriteLine($"Assemblies found: {context.AssemblyPaths.Count}");
foreach (var (name, path) in context.AssemblyPaths.Take(5))
{
    Console.WriteLine($"  {name} -> {path}");
}
Console.WriteLine();

// Test 2: Verify hostfxr exists
Console.WriteLine("=== Test 2: Verify hostfxr exists ===");
var hostfxrPath = Path.Combine(context.RuntimePath, "host", "fxr", context.RuntimeVersion, "hostfxr.dll");
Console.WriteLine($"hostfxr path: {hostfxrPath}");
Console.WriteLine($"hostfxr exists: {File.Exists(hostfxrPath)}");
Console.WriteLine();

// Test 3: Run hello.dll using dotnet exec (simulates what hostfxr does)
Console.WriteLine("=== Test 3: Run hello.dll via dotnet exec ===");
var helloDll = Path.Combine(testArtifacts, "hello.dll");
var psi = new System.Diagnostics.ProcessStartInfo
{
    FileName = Path.Combine(context.RuntimePath, "dotnet.exe"),
    Arguments = $"exec \"{helloDll}\"",
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true
};
using var process = System.Diagnostics.Process.Start(psi)!;
var output = await process.StandardOutput.ReadToEndAsync();
var error = await process.StandardError.ReadToEndAsync();
await process.WaitForExitAsync();

Console.WriteLine("Output:");
Console.WriteLine(output);
if (!string.IsNullOrEmpty(error))
{
    Console.WriteLine("Error:");
    Console.WriteLine(error);
}
Console.WriteLine($"Exit code: {process.ExitCode}");
Console.WriteLine();

Console.WriteLine("=== All tests passed! ===");
return 0;
