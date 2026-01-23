// MinRT.TestHost - Test harness for MinRT
// Usage: MinRT.TestHost <path-to-dll> [options] [args...]

using MinRT.Core;

if (args.Length == 0)
{
    Console.WriteLine("Usage: MinRT.TestHost <path-to-dll> [options] [args...]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --aspnet                     Include ASP.NET Core framework");
    Console.WriteLine("  --layout <path>              Use existing runtime layout (no download)");
    Console.WriteLine("  --create-layout <path>       Create a runtime layout and exit");
    Console.WriteLine("  --probe <path>               Add a folder of DLLs to probing paths");
    Console.WriteLine("  --package <id> <version>     Add a NuGet package to restore");
    Console.WriteLine("  --offline                    Fail if any download is attempted (use with --layout)");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  MinRT.TestHost ./hello.dll");
    Console.WriteLine("  MinRT.TestHost ./hello-web.dll --aspnet");
    Console.WriteLine("  MinRT.TestHost --create-layout ./my-runtime --aspnet");
    Console.WriteLine("  MinRT.TestHost ./hello.dll --layout ./my-runtime");
    Console.WriteLine("  MinRT.TestHost ./hello.dll --layout ./my-runtime --offline");
    Console.WriteLine("  MinRT.TestHost ./myapp.dll --probe ./libs");
    Console.WriteLine("  MinRT.TestHost ./myapp.dll --package Newtonsoft.Json 13.0.3");
    return 1;
}

// Parse args
var includeAspNet = args.Contains("--aspnet");
var requireOffline = args.Contains("--offline");
var createLayoutIdx = Array.IndexOf(args, "--create-layout");
var layoutIdx = Array.IndexOf(args, "--layout");

// Parse --probe args (can be multiple)
var probePaths = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--probe" && i + 1 < args.Length)
    {
        probePaths.Add(args[i + 1]);
        i += 1;
    }
}

// Parse --package args (can be multiple)
var packages = new List<(string Id, string Version)>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--package" && i + 2 < args.Length)
    {
        packages.Add((args[i + 1], args[i + 2]));
        i += 2;
    }
}

// Use local cache in project folder
var cacheDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".minrt-cache"));

// Mode: Create layout only
if (createLayoutIdx >= 0)
{
    var layoutPath = Path.GetFullPath(args[createLayoutIdx + 1]);
    Console.WriteLine($"Creating layout at: {layoutPath}");
    Console.WriteLine($"ASP.NET Core: {includeAspNet}");

    var builder = new MinRTBuilder()
        .WithRuntimeVersion("10.0.0")
        .WithCacheDirectory(cacheDir);

    if (includeAspNet)
    {
        builder.WithAspNetCore();
    }

    await builder.CreateLayoutAsync(layoutPath);
    Console.WriteLine("Layout created!");
    
    // Show contents
    foreach (var entry in Directory.GetFileSystemEntries(layoutPath))
    {
        var name = Path.GetFileName(entry);
        var isDir = Directory.Exists(entry);
        Console.WriteLine($"  {(isDir ? "[DIR] " : "[FILE]")} {name}");
    }
    return 0;
}

// Mode: Run app
var dllPath = Path.GetFullPath(args[0]);

Console.WriteLine($"App: {dllPath}");
Console.WriteLine($"ASP.NET Core: {includeAspNet}");
Console.WriteLine($"Offline: {requireOffline}");
Console.WriteLine($"Probe paths: {probePaths.Count}");
Console.WriteLine($"Packages: {packages.Count}");
Console.WriteLine($"Cache: {cacheDir}");

var runBuilder = new MinRTBuilder()
    .WithAppPath(dllPath)
    .WithTargetFramework("net10.0")
    .WithRuntimeVersion("10.0.0")
    .WithCacheDirectory(cacheDir);

if (includeAspNet)
{
    runBuilder.WithAspNetCore();
}

if (requireOffline)
{
    runBuilder.RequireOffline();
}

foreach (var probePath in probePaths)
{
    Console.WriteLine($"  Probe: {probePath}");
    runBuilder.AddProbingPath(probePath);
}

foreach (var (id, version) in packages)
{
    Console.WriteLine($"  Package: {id} {version}");
    runBuilder.WithPackage(id, version);
}

if (layoutIdx >= 0)
{
    var layoutPath = Path.GetFullPath(args[layoutIdx + 1]);
    Console.WriteLine($"Layout: {layoutPath}");
    runBuilder.WithLayout(layoutPath);
}

Console.WriteLine();
Console.WriteLine("Building MinRT context...");

var context = await runBuilder.BuildAsync();

Console.WriteLine($"Runtime: {context.RuntimePath}");
Console.WriteLine($"Muxer: {context.MuxerPath}");
Console.WriteLine($"App: {context.AppPath}");
Console.WriteLine("---");

var exitCode = context.Run();

Console.WriteLine("---");
Console.WriteLine($"Exit code: {exitCode}");

return exitCode;
