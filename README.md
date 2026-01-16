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
    .AddPackageRange("Serilog", "[3.0.0, 4.0.0)")   // Version range
    
    // Feed configuration
    .AddFeed("https://api.nuget.org/v3/index.json")
    .AddFeed("https://pkgs.dev.azure.com/org/feed/nuget/v3/index.json", 
             "MyFeed", username, password)          // Authenticated feed
    .WithNuGetConfig("./nuget.config")              // Load feeds from config
    
    // Resolution settings
    .WithTargetFramework("net9.0")
    .WithPackagesDirectory("./packages")            // Cache location
    .WithDependencyBehavior(DependencyBehavior.Lowest)  // Like NuGet default
    
    // Diagnostics
    .WithLogger(loggerFactory)                      // Rich logging
    
    .BuildAsync();

// Use the loaded assemblies
var assembly = alc.LoadAssembly("Newtonsoft.Json");
var type = alc.GetType("Newtonsoft.Json", "Newtonsoft.Json.JsonConvert");
var instance = alc.CreateInstance("MyLib", "MyLib.MyClass", arg1, arg2);
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
