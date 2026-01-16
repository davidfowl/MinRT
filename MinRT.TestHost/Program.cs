// MinRT.TestHost - Native AOT test harness for MinRT
// This must be published as Native AOT to avoid "already initialized" error

using MinRT.Core;

if (args.Length == 0)
{
    Console.WriteLine("Usage: MinRT.TestHost <path-to-dll> [args...]");
    Console.WriteLine();
    Console.WriteLine("Example: MinRT.TestHost ./hello.dll");
    return 1;
}

var dllPath = Path.GetFullPath(args[0]);
var probingPath = Path.GetDirectoryName(dllPath)!;
var appArgs = args.Length > 1 ? args[1..] : null;

// Use local cache in project folder
var cacheDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".minrt-cache"));

Console.WriteLine($"DLL: {dllPath}");
Console.WriteLine($"Probing: {probingPath}");
Console.WriteLine($"Cache: {cacheDir}");
Console.WriteLine();

// Use downloaded runtime from NuGet (not system runtime)
Console.WriteLine("Building MinRT context (downloading runtime if needed)...");
var context = await new MinRTBuilder()
    .WithTargetFramework("net10.0")
    .WithCacheDirectory(cacheDir)
    .AddProbingPath(probingPath)
    .UseDownloadedRuntime("10.0.0")  // Download from NuGet
    .BuildAsync();

Console.WriteLine($"Runtime: {context.RuntimePath}");
Console.WriteLine($"Version: {context.RuntimeVersion}");
Console.WriteLine("---");

var exitCode = context.Run(dllPath, appArgs);

Console.WriteLine("---");
Console.WriteLine($"Exit code: {exitCode}");

return exitCode;
