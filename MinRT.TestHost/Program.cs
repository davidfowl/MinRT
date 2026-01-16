// MinRT.TestHost - Test harness for MinRT
// Usage: MinRT.TestHost <path-to-dll> [--aspnet] [args...]

using MinRT.Core;

if (args.Length == 0)
{
    Console.WriteLine("Usage: MinRT.TestHost <path-to-dll> [--aspnet] [args...]");
    Console.WriteLine();
    Console.WriteLine("Example: MinRT.TestHost ./hello.dll");
    Console.WriteLine("Example: MinRT.TestHost ./hello-web.dll --aspnet");
    return 1;
}

var dllPath = Path.GetFullPath(args[0]);
var includeAspNet = args.Contains("--aspnet");
var appArgs = args.Skip(1).Where(a => a != "--aspnet").ToArray();
if (appArgs.Length == 0) appArgs = null;

// Use local cache in project folder
var cacheDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".minrt-cache"));

Console.WriteLine($"App: {dllPath}");
Console.WriteLine($"ASP.NET Core: {includeAspNet}");
Console.WriteLine($"Cache: {cacheDir}");
Console.WriteLine();

Console.WriteLine("Building MinRT context...");
var builder = new MinRTBuilder()
    .WithAppPath(dllPath)
    .WithTargetFramework("net10.0")
    .WithRuntimeVersion("10.0.0")
    .WithCacheDirectory(cacheDir);

if (includeAspNet)
{
    builder.WithAspNetCore();
}

var context = await builder.BuildAsync();

Console.WriteLine($"Runtime: {context.RuntimePath}");
Console.WriteLine($"AppHost: {context.AppHostPath}");
Console.WriteLine("---");

var exitCode = context.Run(appArgs);

Console.WriteLine("---");
Console.WriteLine($"Exit code: {exitCode}");

return exitCode;
