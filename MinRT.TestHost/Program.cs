// MinRT.TestHost - Test harness for MinRT
// Usage: MinRT.TestHost <path-to-dll> [args...]

using MinRT.Core;

if (args.Length == 0)
{
    Console.WriteLine("Usage: MinRT.TestHost <path-to-dll> [args...]");
    Console.WriteLine();
    Console.WriteLine("Example: MinRT.TestHost ./hello.dll");
    return 1;
}

var dllPath = Path.GetFullPath(args[0]);
var appArgs = args.Length > 1 ? args[1..] : null;

// Use local cache in project folder
var cacheDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".minrt-cache"));

Console.WriteLine($"App: {dllPath}");
Console.WriteLine($"Cache: {cacheDir}");
Console.WriteLine();

Console.WriteLine("Building MinRT context...");
var context = await new MinRTBuilder()
    .WithAppPath(dllPath)
    .WithTargetFramework("net10.0")
    .WithRuntimeVersion("10.0.0")
    .WithCacheDirectory(cacheDir)
    .BuildAsync();

Console.WriteLine($"Runtime: {context.RuntimePath}");
Console.WriteLine($"AppHost: {context.AppHostPath}");
Console.WriteLine("---");

var exitCode = context.Run(appArgs);

Console.WriteLine("---");
Console.WriteLine($"Exit code: {exitCode}");

return exitCode;
