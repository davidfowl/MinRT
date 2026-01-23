using System.CommandLine;
using System.Text.Json;
using MinRT.NuGet;
using Microsoft.Extensions.Logging;

var packageOption = new Option<string[]>(
    ["--package", "-p"],
    "Package to restore in format 'id version' (can be specified multiple times)")
{
    AllowMultipleArgumentsPerToken = true,
    Arity = ArgumentArity.ZeroOrMore
};

var jsonOption = new Option<FileInfo?>(
    ["--json", "-j"],
    "JSON file containing packages to restore");

var outputOption = new Option<string>(
    ["--output", "-o"],
    () => "./project.assets.json",
    "Output path for project.assets.json");

var frameworkOption = new Option<string>(
    ["--framework", "-f"],
    () => "net10.0",
    "Target framework (e.g., net10.0, net9.0)");

var packagesDirOption = new Option<string?>(
    ["--packages-dir"],
    "Packages cache directory (defaults to ~/.nuget/packages)");

var sourceOption = new Option<string[]>(
    ["--source", "-s"],
    "Package source URL (can be specified multiple times)")
{
    AllowMultipleArgumentsPerToken = true,
    Arity = ArgumentArity.ZeroOrMore
};

var verboseOption = new Option<bool>(
    ["--verbose", "-v"],
    "Enable verbose logging");

var rootCommand = new RootCommand("Minimal NuGet restore without a csproj file")
{
    packageOption,
    jsonOption,
    outputOption,
    frameworkOption,
    packagesDirOption,
    sourceOption,
    verboseOption
};

rootCommand.SetHandler(async (context) =>
{
    var packages = context.ParseResult.GetValueForOption(packageOption) ?? [];
    var jsonFile = context.ParseResult.GetValueForOption(jsonOption);
    var output = context.ParseResult.GetValueForOption(outputOption)!;
    var framework = context.ParseResult.GetValueForOption(frameworkOption)!;
    var packagesDir = context.ParseResult.GetValueForOption(packagesDirOption);
    var sources = context.ParseResult.GetValueForOption(sourceOption) ?? [];
    var verbose = context.ParseResult.GetValueForOption(verboseOption);
    var ct = context.GetCancellationToken();

    try
    {
        var restorer = NuGetRestorer.CreateBuilder()
            .WithTargetFramework(framework)
            .UseDefaultNuGetConfig();

        // Add packages from command line (format: "id version")
        foreach (var pkg in packages)
        {
            var parts = pkg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                Console.Error.WriteLine($"Invalid package format: '{pkg}'. Expected 'id version'.");
                context.ExitCode = 2;
                return;
            }
            restorer.AddPackage(parts[0], parts[1]);
        }

        // Add packages from JSON file
        if (jsonFile != null)
        {
            if (!jsonFile.Exists)
            {
                Console.Error.WriteLine($"JSON file not found: {jsonFile.FullName}");
                context.ExitCode = 2;
                return;
            }

            var jsonContent = await File.ReadAllTextAsync(jsonFile.FullName, ct);
            var config = JsonSerializer.Deserialize<RestoreConfig>(jsonContent, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (config?.Packages != null)
            {
                foreach (var pkg in config.Packages)
                {
                    restorer.AddPackage(pkg.Id, pkg.Version);
                }
            }

            if (!string.IsNullOrEmpty(config?.Framework))
            {
                restorer.WithTargetFramework(config.Framework);
            }

            if (config?.Sources != null)
            {
                foreach (var src in config.Sources)
                {
                    restorer.AddFeed(src);
                }
            }
        }

        // Add sources from command line
        foreach (var src in sources)
        {
            restorer.AddFeed(src);
        }

        // Set packages directory
        if (!string.IsNullOrEmpty(packagesDir))
        {
            restorer.WithPackagesDirectory(packagesDir);
        }

        // Set up logging
        if (verbose)
        {
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });
            restorer.WithLogger(loggerFactory);
        }

        // Run restore
        Console.WriteLine($"Restoring packages to {output}...");
        await restorer.RestoreToFileAsync(output, ct);
        Console.WriteLine("Restore completed successfully.");
        context.ExitCode = 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Restore failed: {ex.Message}");
        if (verbose)
        {
            Console.Error.WriteLine(ex.ToString());
        }
        context.ExitCode = 1;
    }
});

return await rootCommand.InvokeAsync(args);

// JSON input model
record RestoreConfig
{
    public PackageRef[]? Packages { get; init; }
    public string? Framework { get; init; }
    public string[]? Sources { get; init; }
}

record PackageRef
{
    public required string Id { get; init; }
    public required string Version { get; init; }
}
