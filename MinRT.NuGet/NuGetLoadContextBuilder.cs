using System.Reflection;

namespace MinRT.NuGet;

/// <summary>
/// Builder for creating a NuGetLoadContext with resolved packages.
/// </summary>
public class NuGetLoadContextBuilder
{
    private readonly List<string> _feeds = [];
    private readonly List<(string Id, string Version)> _packages = [];
    private string _targetFramework = "net9.0";
    private string _cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minrt-nuget");

    /// <summary>
    /// Adds a NuGet feed URL.
    /// </summary>
    public NuGetLoadContextBuilder WithFeed(string feedUrl)
    {
        _feeds.Add(feedUrl);
        return this;
    }

    /// <summary>
    /// Adds a package to resolve and load.
    /// </summary>
    public NuGetLoadContextBuilder WithPackage(string id, string version)
    {
        _packages.Add((id, version));
        return this;
    }

    /// <summary>
    /// Sets the target framework (e.g., "net9.0").
    /// </summary>
    public NuGetLoadContextBuilder WithTargetFramework(string tfm)
    {
        _targetFramework = tfm;
        return this;
    }

    /// <summary>
    /// Sets the cache directory for downloaded packages.
    /// </summary>
    public NuGetLoadContextBuilder WithCacheDirectory(string path)
    {
        _cacheDirectory = path;
        return this;
    }

    /// <summary>
    /// Resolves all packages and creates a NuGetLoadContext.
    /// </summary>
    public async Task<NuGetLoadContext> BuildAsync(CancellationToken ct = default)
    {
        var resolver = new PackageResolver(_targetFramework, _cacheDirectory);

        foreach (var feed in _feeds)
        {
            resolver.WithFeed(feed);
        }

        foreach (var (id, version) in _packages)
        {
            resolver.WithPackage(id, version);
        }

        var resolved = await resolver.ResolveAsync(ct);

        var context = new NuGetLoadContext();

        // Add all resolved assemblies
        foreach (var (name, path) in resolved.AssemblyPaths)
        {
            context.AddAssembly(name, path);
        }

        // Add package directories as probing paths for native libs
        foreach (var (_, path) in resolved.PackagePaths)
        {
            context.AddProbingPath(path);
        }

        return context;
    }

    /// <summary>
    /// Resolves packages, creates context, and loads an assembly.
    /// </summary>
    public async Task<Assembly> LoadAssemblyAsync(string assemblyName, CancellationToken ct = default)
    {
        var context = await BuildAsync(ct);
        return context.LoadFromAssemblyName(new AssemblyName(assemblyName));
    }

    /// <summary>
    /// Resolves packages, creates context, loads assembly, and invokes entry point.
    /// </summary>
    public async Task<int> RunAsync(string assemblyName, string[]? args = null, CancellationToken ct = default)
    {
        var assembly = await LoadAssemblyAsync(assemblyName, ct);
        var entryPoint = assembly.EntryPoint 
            ?? throw new InvalidOperationException($"Assembly {assemblyName} has no entry point");

        var parameters = entryPoint.GetParameters();
        object? result;

        if (parameters.Length == 0)
        {
            result = entryPoint.Invoke(null, null);
        }
        else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string[]))
        {
            result = entryPoint.Invoke(null, [args ?? []]);
        }
        else
        {
            throw new InvalidOperationException($"Unsupported entry point signature");
        }

        // Handle async entry points
        if (result is Task<int> taskInt)
        {
            return await taskInt;
        }
        if (result is Task task)
        {
            await task;
            return 0;
        }
        if (result is int exitCode)
        {
            return exitCode;
        }

        return 0;
    }
}
