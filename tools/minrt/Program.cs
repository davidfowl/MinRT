using System.CommandLine;
using System.Text.Json;
using MinRT.NuGet;
using Microsoft.Extensions.Logging;
using NuGet.ProjectModel;

// === RESTORE COMMAND ===
var restorePackageOption = new Option<string[]>(
    ["--package", "-p"],
    "Package to restore in format 'id version' (can be specified multiple times)")
{
    AllowMultipleArgumentsPerToken = true,
    Arity = ArgumentArity.ZeroOrMore
};

var restoreJsonOption = new Option<FileInfo?>(
    ["--json", "-j"],
    "JSON file containing packages to restore");

var restoreOutputOption = new Option<string>(
    ["--output", "-o"],
    () => "./obj",
    "Output directory for project.assets.json");

var restoreFrameworkOption = new Option<string>(
    ["--framework", "-f"],
    () => "net10.0",
    "Target framework (e.g., net10.0, net9.0)");

var restorePackagesDirOption = new Option<string?>(
    ["--packages-dir"],
    "Packages cache directory (defaults to ~/.nuget/packages)");

var restoreSourceOption = new Option<string[]>(
    ["--source", "-s"],
    "Package source URL (can be specified multiple times)")
{
    AllowMultipleArgumentsPerToken = true,
    Arity = ArgumentArity.ZeroOrMore
};

var verboseOption = new Option<bool>(
    ["--verbose", "-v"],
    "Enable verbose logging");

var restoreCommand = new Command("restore", "Restore NuGet packages and create project.assets.json")
{
    restorePackageOption,
    restoreJsonOption,
    restoreOutputOption,
    restoreFrameworkOption,
    restorePackagesDirOption,
    restoreSourceOption,
    verboseOption
};

restoreCommand.SetHandler(async (context) =>
{
    var packages = context.ParseResult.GetValueForOption(restorePackageOption) ?? [];
    var jsonFile = context.ParseResult.GetValueForOption(restoreJsonOption);
    var output = context.ParseResult.GetValueForOption(restoreOutputOption)!;
    var framework = context.ParseResult.GetValueForOption(restoreFrameworkOption)!;
    var packagesDir = context.ParseResult.GetValueForOption(restorePackagesDirOption);
    var sources = context.ParseResult.GetValueForOption(restoreSourceOption) ?? [];
    var verbose = context.ParseResult.GetValueForOption(verboseOption);
    var ct = context.GetCancellationToken();

    try
    {
        var restorer = NuGetRestorer.CreateBuilder()
            .WithTargetFramework(framework)
            .WithOutputPath(output)
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
        var assetsPath = Path.Combine(output, "project.assets.json");
        Console.WriteLine($"Restoring packages to {assetsPath}...");
        await restorer.RestoreAsync(ct);
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

// === LAYOUT COMMAND ===
var layoutAssetsOption = new Option<FileInfo>(
    ["--assets", "-a"],
    "Path to project.assets.json file")
{
    IsRequired = true
};

var layoutOutputOption = new Option<string>(
    ["--output", "-o"],
    "Output directory for DLLs")
{
    IsRequired = true
};

var layoutTfmOption = new Option<string>(
    ["--tfm", "-f"],
    () => "net10.0",
    "Target framework to select from assets");

var layoutPackagesDirOption = new Option<string?>(
    ["--packages-dir"],
    "Packages cache directory (defaults to reading from assets file)");

var layoutCommand = new Command("layout", "Create a flat DLL layout from project.assets.json")
{
    layoutAssetsOption,
    layoutOutputOption,
    layoutTfmOption,
    layoutPackagesDirOption,
    verboseOption
};

layoutCommand.SetHandler(async (context) =>
{
    var assetsFile = context.ParseResult.GetValueForOption(layoutAssetsOption)!;
    var output = context.ParseResult.GetValueForOption(layoutOutputOption)!;
    var tfm = context.ParseResult.GetValueForOption(layoutTfmOption)!;
    var packagesDir = context.ParseResult.GetValueForOption(layoutPackagesDirOption);
    var verbose = context.ParseResult.GetValueForOption(verboseOption);
    var ct = context.GetCancellationToken();

    try
    {
        if (!assetsFile.Exists)
        {
            Console.Error.WriteLine($"Assets file not found: {assetsFile.FullName}");
            context.ExitCode = 2;
            return;
        }

        // Parse the lock file
        var lockFileFormat = new LockFileFormat();
        var lockFile = lockFileFormat.Read(assetsFile.FullName);

        // Find packages directory
        var pkgDir = packagesDir ?? lockFile.PackageFolders.FirstOrDefault()?.Path;
        if (string.IsNullOrEmpty(pkgDir))
        {
            Console.Error.WriteLine("Could not determine packages directory. Use --packages-dir.");
            context.ExitCode = 2;
            return;
        }

        // Find the target for the specified TFM
        var target = lockFile.Targets.FirstOrDefault(t => 
            t.TargetFramework.GetShortFolderName().Equals(tfm, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(t.RuntimeIdentifier));

        if (target == null)
        {
            var available = string.Join(", ", lockFile.Targets
                .Where(t => string.IsNullOrEmpty(t.RuntimeIdentifier))
                .Select(t => t.TargetFramework.GetShortFolderName()));
            Console.Error.WriteLine($"Target framework '{tfm}' not found. Available: {available}");
            context.ExitCode = 2;
            return;
        }

        // Create output directory
        Directory.CreateDirectory(output);

        var copiedCount = 0;
        var skippedCount = 0;

        foreach (var library in target.Libraries)
        {
            if (library.Type != "package" || library.Name is null) continue;

            var packagePath = Path.Combine(pkgDir, library.Name.ToLowerInvariant(), library.Version?.ToString() ?? "0.0.0");

            // Copy runtime assemblies
            foreach (var runtime in library.RuntimeAssemblies)
            {
                // Skip placeholder files
                if (runtime.Path.EndsWith("_._")) 
                {
                    skippedCount++;
                    continue;
                }

                var sourcePath = Path.Combine(packagePath, runtime.Path.Replace('/', Path.DirectorySeparatorChar));
                var destPath = Path.Combine(output, Path.GetFileName(runtime.Path));

                if (File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, destPath, overwrite: true);
                    copiedCount++;
                    if (verbose)
                    {
                        Console.WriteLine($"  Copied: {Path.GetFileName(runtime.Path)}");
                    }
                }
                else if (verbose)
                {
                    Console.WriteLine($"  Warning: Not found: {sourcePath}");
                }
            }
        }

        Console.WriteLine($"Layout created: {copiedCount} DLLs copied to {output}");
        if (skippedCount > 0 && verbose)
        {
            Console.WriteLine($"  Skipped {skippedCount} placeholder files");
        }
        context.ExitCode = 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Layout failed: {ex.Message}");
        if (verbose)
        {
            Console.Error.WriteLine(ex.ToString());
        }
        context.ExitCode = 1;
    }
});

// === ROOT COMMAND ===
var rootCommand = new RootCommand("MinRT - Minimal .NET runtime and package management")
{
    restoreCommand,
    layoutCommand
};

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
