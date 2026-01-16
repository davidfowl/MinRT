# MinRT - Minimal .NET Runtime Host

A zero-dependency, **Native AOT compatible** .NET runtime bootstrapper. Downloads the .NET runtime from NuGet and executes your app - no pre-installed .NET required.

## Two Libraries

| Library | Purpose | AOT Compatible |
|---------|---------|----------------|
| **MinRT.Core** | Downloads runtime, patches apphost, executes apps | ✅ Yes |
| **MinRT.NuGet** | Runtime NuGet package resolution + AssemblyLoadContext | ❌ No (managed) |

---

## MinRT.Core - Runtime Host

### Quick Start

```csharp
using MinRT.Core;

var context = await new MinRTBuilder()
    .WithAppPath("myapp.dll")
    .WithRuntimeVersion("10.0.0")
    .BuildAsync();

var exitCode = context.Run(args);
```

That's it. MinRT will:
1. Download the .NET runtime from NuGet (cached)
2. Download and patch the apphost
3. Execute your app

### API

```csharp
var context = await new MinRTBuilder()
    .WithAppPath("myapp.dll")              // Required: your app
    .WithRuntimeVersion("10.0.0")          // Runtime version to use
    .WithCacheDirectory(".minrt-cache")    // Cache location (default: ~/.minrt)
    .WithRuntimeIdentifier("linux-x64")    // Override RID (auto-detected)
    .WithAspNetCore()                      // Include ASP.NET Core framework
    .AddProbingPath("./libs")              // Add folder of DLLs to copy
    .BuildAsync();

context.Run(args);
```

### Pre-packaged Runtime Layout

Create a portable runtime layout for distribution:

```csharp
// Create layout (no app - just runtime)
await new MinRTBuilder()
    .WithRuntimeVersion("10.0.0")
    .WithAspNetCore()
    .CreateLayoutAsync("./my-runtime");

// Use existing layout (no download needed)
var context = await new MinRTBuilder()
    .WithAppPath("myapp.dll")
    .WithLayout("./my-runtime")
    .BuildAsync();
```

### Two Deployment Modes

| | Download-on-Demand | Pre-packaged Layout |
|---|---|---|
| Distribution size | Tiny (just your app) | Large (~77-105MB) |
| First run | Slow (downloads runtime) | Fast |
| Requires internet | Yes (first run) | No |
| Offline support | ❌ | ✅ |

---

## MinRT.NuGet - Programmatic NuGet Resolution

Runtime NuGet package resolution and loading via custom `AssemblyLoadContext`. Works like `dotnet restore` but programmatic - no MSBuild, no project files.

### Quick Start

```csharp
using MinRT.NuGet;

var alc = await NuGetAssemblyLoader.CreateBuilder()
    .AddPackage("Newtonsoft.Json", "13.0.3")
    .WithTargetFramework("net9.0")
    .BuildAsync();

var assembly = alc.LoadAssembly("Newtonsoft.Json");
```

### Full API

```csharp
var alc = await NuGetAssemblyLoader.CreateBuilder()
    // Package references
    .AddPackage("Microsoft.Extensions.Logging", "9.0.0")
    .AddPackage("Newtonsoft.Json", "13.0.3")
    .AddPackage("SomePackage", "1.0.0", allowNewer: true)  // >= 1.0.0
    .AddPackageRange("Serilog", "[3.0.0, 4.0.0)")         // Version range
    
    // Feed configuration (choose one approach)
    .UseDefaultNuGetConfig()                              // Like dotnet restore
    .AddFeed("https://api.nuget.org/v3/index.json")
    .AddFeed("https://pkgs.dev.azure.com/org/feed/nuget/v3/index.json", 
             "MyFeed", username, password)                // Authenticated feed
    .WithNuGetConfig("./nuget.config")                    // Specific config file
    
    // Resolution settings
    .WithTargetFramework("net9.0")
    .WithPackagesDirectory("./packages")                  // Cache location
    .WithDependencyBehavior(DependencyBehavior.Lowest)    // Like NuGet default
    .AsCollectible()                                      // Enable unloading (see below)
    
    // Diagnostics
    .WithLogger(loggerFactory)                            // Rich logging
    
    .BuildAsync();

// Use the loaded assemblies
var assembly = alc.LoadAssembly("Newtonsoft.Json");
var type = alc.GetType("Newtonsoft.Json", "Newtonsoft.Json.JsonConvert");
var instance = alc.CreateInstance("MyLib", "MyLib.MyClass", arg1, arg2);
```

### Collectible vs Non-Collectible

| Mode | Use Case | Can Unload? | Works with Default.Resolving? |
|------|----------|-------------|-------------------------------|
| Non-collectible (default) | Static compile pattern, Default ALC integration | ❌ | ✅ |
| Collectible | Plugin systems, hot reload | ✅ | ❌ |

**Non-collectible (default)** - Use when integrating with `AssemblyLoadContext.Default.Resolving`:

```csharp
var alc = await NuGetAssemblyLoader.CreateBuilder()
    .AddPackage("Humanizer.Core", "2.14.1")
    .BuildAsync();

// Works - assemblies can be referenced by Default ALC
AssemblyLoadContext.Default.Resolving += (ctx, name) =>
    alc.AssemblyPaths.TryGetValue(name.Name!, out var path) 
        ? alc.LoadFromAssemblyPath(path) : null;
```

**Collectible** - Use when you need to unload assemblies (plugin systems):

```csharp
var alc = await NuGetAssemblyLoader.CreateBuilder()
    .AddPackage("MyPlugin", "1.0.0")
    .AsCollectible()  // Enable unloading
    .BuildAsync();

// Must use reflection - stay inside the collectible context
var assembly = alc.LoadAssembly("MyPlugin");
var type = assembly.GetType("MyPlugin.Plugin")!;
var instance = Activator.CreateInstance(type);
// ... use via reflection ...

// Later: unload when done
alc.Unload();
```

> ⚠️ **Warning**: Collectible assemblies cannot be referenced by non-collectible assemblies. 
> If you try to use `Default.Resolving` with a collectible ALC, you'll get:
> `NotSupportedException: A non-collectible assembly may not reference a collectible assembly.`

### Feed Configuration Options

| Method | Equivalent To | Description |
|--------|---------------|-------------|
| `UseDefaultNuGetConfig()` | `dotnet restore` | Walks directory tree + user + machine config |
| `UseDefaultNuGetConfig("./src")` | `cd ./src && dotnet restore` | Start from specific directory |
| `WithNuGetConfig("./nuget.config")` | `--configfile ./nuget.config` | Specific config file only |
| `AddFeed(url)` | `--source <url>` | Explicit feed (additive) |
| *(none)* | Fallback | Uses nuget.org |

#### UseDefaultNuGetConfig Behavior

When you call `UseDefaultNuGetConfig()`, MinRT.NuGet loads package sources the same way `dotnet restore` does:

1. **Walk up directory tree** - Starting from root (or current dir), look for `nuget.config` files in each parent
2. **User config** - `%APPDATA%\NuGet\NuGet.Config` (Windows) or `~/.nuget/NuGet/NuGet.Config` (Linux/macOS)
3. **Machine-wide config** - System-level NuGet configuration

This means your existing `nuget.config` files (with private feeds, credentials, etc.) will "just work".

**Example - Using default config like dotnet restore:**

```csharp
// Equivalent to: cd /path/to/project && dotnet restore
var alc = await NuGetAssemblyLoader.CreateBuilder()
    .AddPackage("MyInternalPackage", "1.0.0")
    .WithTargetFramework("net9.0")
    .UseDefaultNuGetConfig("/path/to/project")
    .WithLogger(logger)
    .BuildAsync();
```

### Features

- **Transitive dependency resolution** - Uses NuGet.Resolver with "lowest version wins"
- **nuget.config support** - Load feeds and credentials from config files  
- **Authenticated feeds** - Username/password for private feeds
- **TFM-based asset selection** - Picks best `lib/` folder for your framework
- **Package caching** - Standard NuGet folder layout
- **Rich diagnostics** - ILogger shows HTTP requests, resolution, downloads

### Example Output

```
info: Starting NuGet package resolution for 1 packages targeting net9.0
info: Resolving dependency graph...
info: Found 6 packages in dependency graph
info: Resolving version conflicts using Lowest strategy...
info: Resolved to 6 packages
dbug:   Microsoft.Extensions.DependencyInjection 9.0.0
dbug:   Microsoft.Extensions.DependencyInjection.Abstractions 9.0.0
dbug:   Microsoft.Extensions.Logging 9.0.0
dbug:   Microsoft.Extensions.Logging.Abstractions 9.0.0
dbug:   Microsoft.Extensions.Options 9.0.0
dbug:   Microsoft.Extensions.Primitives 9.0.0
info: Downloading packages to ./packages...
info: Loaded 6 assemblies
info: NuGet assembly loader ready
```

### Static Compile + Runtime Download Pattern

You can compile against a package for IntelliSense but download it at runtime:

```xml
<!-- In .csproj - compile against but don't deploy -->
<PackageReference Include="Humanizer.Core" Version="2.14.1">
  <ExcludeAssets>runtime</ExcludeAssets>
</PackageReference>
```

```csharp
using System.Runtime.CompilerServices;
using Humanizer;  // Full IntelliSense - even though DLL isn't deployed!

// ModuleInitializer downloads before Main() runs
static class NuGetResolver
{
    [ModuleInitializer]
    public static void Initialize()
    {
        var alc = NuGetAssemblyLoader.CreateBuilder()
            .AddPackage("Humanizer.Core", "2.14.1")
            .WithTargetFramework("net9.0")
            .UseDefaultNuGetConfig()
            .BuildAsync()
            .GetAwaiter()
            .GetResult();

        // Register with default ALC
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            if (name.Name != null && alc.AssemblyPaths.TryGetValue(name.Name, out var path))
                return alc.LoadFromAssemblyPath(path);
            return null;
        };
    }
}

// Works at runtime - downloaded from NuGet!
Console.WriteLine("hello world".Titleize());  // "Hello World"
```

---

## How It Works

### MinRT.Core

1. **Downloads runtime** from `Microsoft.NETCore.App.Runtime.{rid}` NuGet package
2. **Downloads apphost** from `Microsoft.NETCore.App.Host.{rid}` NuGet package
3. **Patches apphost** binary to embed your app path (same as .NET SDK)
4. **Executes** with `DOTNET_ROOT` pointing to downloaded runtime

### MinRT.NuGet

1. **Collect dependencies** - BFS traversal of dependency graph
2. **Resolve conflicts** - Using NuGet.Resolver (lowest version wins)
3. **Download packages** - Cache to local folder
4. **Select assets** - Pick best `lib/` folder for target framework
5. **Create ALC** - Custom AssemblyLoadContext with lazy loading

## Cache Layout

```
~/.minrt/                                    # MinRT.Core cache
├── runtimes/
│   └── 10.0.0-win-x64/
│       ├── host/fxr/10.0.0/hostfxr.dll
│       └── shared/
│           ├── Microsoft.NETCore.App/10.0.0/
│           └── Microsoft.AspNetCore.App/10.0.0/
├── packages/
└── apphosts/{hash}/
    ├── myapp.exe                            # Patched apphost
    └── myapp.dll                            # Copied app + deps

./packages/                                  # MinRT.NuGet cache
├── newtonsoft.json/13.0.3/
│   └── lib/net6.0/Newtonsoft.Json.dll
└── microsoft.extensions.logging/9.0.0/
    └── lib/net9.0/Microsoft.Extensions.Logging.dll
```

## Runtime Sizes

Approximate sizes for .NET 10 (win-x64):

| | Unzipped | Zipped |
|---|---|---|
| Base runtime only | ~77 MB | ~35 MB |
| + ASP.NET Core | +29 MB | +13 MB |
| **Total with ASP.NET** | **~105 MB** | **~48 MB** |

## Use Cases

- Bootstrapping .NET apps without requiring users to install .NET
- Runtime plugin loading from NuGet packages
- Self-updating applications that download their own runtime
- Isolated runtime environments
- Offline/air-gapped deployments (with pre-packaged layout)

## License

MIT
