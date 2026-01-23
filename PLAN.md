# MinRT - Implementation Plan

## Overview

MinRT is a minimal .NET runtime bootstrapper that downloads the runtime from NuGet and executes managed applications without requiring a pre-installed .NET SDK.

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│  MinRT.Core                                                             │
│                                                                         │
│  MinRTBuilder                                                           │
│  ├── WithAppPath("myapp.dll")         ← App to run                      │
│  ├── WithRuntimeVersion("10.0.0")     ← Runtime version                 │
│  ├── WithAspNetCore()                 ← Include ASP.NET Core            │
│  ├── WithCacheDirectory("...")        ← Cache location                  │
│  └── BuildAsync()                                                       │
│      │                                                                  │
│      ├── 1. Download Runtime from NuGet                                 │
│      │   └── Microsoft.NETCore.App.Runtime.{rid}                        │
│      │                                                                  │
│      ├── 2. Download Shared Frameworks (optional)                       │
│      │   └── Microsoft.AspNetCore.App.Runtime.{rid}                     │
│      │                                                                  │
│      ├── 3. Download AppHost from NuGet                                 │
│      │   └── Microsoft.NETCore.App.Host.{rid}                           │
│      │                                                                  │
│      ├── 4. Patch AppHost with app path                                 │
│      │   └── Replace placeholder hash with "myapp.dll"                  │
│      │                                                                  │
│      └── 5. Return MinRTContext                                         │
│                                                                         │
│  MinRTContext                                                           │
│  ├── RuntimePath      → Downloaded runtime location                     │
│  ├── AppHostPath      → Patched apphost executable                      │
│  └── Run(args)        → Spawn process with DOTNET_ROOT set              │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

## Cache Layout

```
{cacheDirectory}/                        # Default: ~/.minrt
├── runtimes/                            # Downloaded .NET runtimes
│   └── 10.0.0-win-x64/
│       ├── host/fxr/10.0.0/hostfxr.dll
│       └── shared/
│           ├── Microsoft.NETCore.App/10.0.0/
│           └── Microsoft.AspNetCore.App/10.0.0/
├── packages/                            # Extracted NuGet packages  
│   ├── microsoft.netcore.app.runtime.win-x64/10.0.0/
│   ├── microsoft.netcore.app.host.win-x64/10.0.0/
│   └── microsoft.aspnetcore.app.runtime.win-x64/10.0.0/
├── apphosts/                            # Patched apphost executables
│   └── {hash}/
│       ├── myapp.exe                    # Patched apphost
│       ├── myapp.dll                    # App assembly (copied)
│       └── myapp.runtimeconfig.json
└── downloads/                           # Temp .nupkg files
```

## AppHost Patching

The apphost binary contains a placeholder that gets replaced with the app path:

```
Placeholder: c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2 (64 chars)
Replacement: myapp.dll\0\0\0\0\0\0\0... (null-padded to 64 chars)
```

This is the same technique used by the .NET SDK.

## Assembly Resolution Behavior

**Key findings from runtime host code (D:\dev\git\runtime):**

1. **Without deps.json**: All assemblies in the app directory become part of TPA (Trusted Platform Assemblies). The runtime probes the app base directory only.

2. **With deps.json + additionalProbingPaths**: The runtime uses `to_library_package_path()` which constructs:
   ```
   {probingPath}/{library_name}/{library_version}/{asset.relative_path}
   ```
   Example: `C:\cache\Newtonsoft.Json\13.0.1\lib\net6.0\Newtonsoft.Json.dll`
   
   This **requires NuGet package layout structure**, NOT flat folders.

3. **Our approach**: For apps with dependencies, use `AddProbingPath()` which copies DLLs directly into the app directory. This works because:
   - Without deps.json, all DLLs in app dir are used
   - Simpler than maintaining NuGet layout structure
   - Works for published (flat) app layouts

## Files

```
MinRT/
├── src/MinRT.Core/
│   ├── MinRTBuilder.cs          # Fluent builder API
│   ├── MinRTContext.cs          # Runs app via patched apphost
│   ├── AppHostPatcher.cs        # Binary patching
│   ├── RuntimeDownloader.cs     # Downloads runtime + apphost
│   ├── NuGetDownloader.cs       # AOT-safe NuGet client
│   └── RuntimeIdentifierHelper.cs
├── tests/MinRT.TestHost/        # Test harness
├── tests/apps/hello/            # Console test app
├── tests/apps/hello-web/        # ASP.NET Core test app
└── tests/apps/test-artifacts/   # Published test apps
```

## API

```csharp
// Download runtime and run app
var context = await new MinRTBuilder()
    .WithAppPath("myapp.dll")
    .WithRuntimeVersion("10.0.0")
    .BuildAsync();

var exitCode = context.Run(args);

// Include ASP.NET Core
var context = await new MinRTBuilder()
    .WithAppPath("webapp.dll")
    .WithRuntimeVersion("10.0.0")
    .WithAspNetCore()
    .WithCacheDirectory(".minrt-cache")
    .BuildAsync();

var exitCode = context.Run(args);

// Create a portable runtime layout (for distribution)
await new MinRTBuilder()
    .WithRuntimeVersion("10.0.0")
    .WithAspNetCore()
    .CreateLayoutAsync("./my-runtime");

// Use a pre-existing layout (no download)
var context = await new MinRTBuilder()
    .WithAppPath("myapp.dll")
    .WithLayout("./my-runtime")
    .BuildAsync();

context.Run();
```

## Runtime Layout

When you create a layout, it produces a self-contained runtime:

```
my-runtime/
├── apphost.exe           # Template apphost (will be patched)
├── host/
│   └── fxr/10.0.0/
│       └── hostfxr.dll
└── shared/
    ├── Microsoft.NETCore.App/10.0.0/
    └── Microsoft.AspNetCore.App/10.0.0/  # if WithAspNetCore()
```

This layout can be:
- Shipped alongside your application
- Downloaded on-demand
- Cached locally for subsequent runs

## Status

### Part 1: MinRT.Core (Native AOT Bootstrapper) ✅
- [x] Download runtime from NuGet
- [x] Download apphost from NuGet
- [x] Patch apphost with app path
- [x] Execute via DOTNET_ROOT
- [x] ASP.NET Core shared framework
- [x] Cross-platform (Windows, Linux)
- [x] Create portable runtime layout
- [x] Use pre-existing layout

### Part 2: NuGet AssemblyLoadContext (Managed) ✅
- [x] Design API
- [x] Implement NuGetAssemblyLoader
- [x] Test with simple package
- [x] Test with transitive dependencies
- [x] UseDefaultNuGetConfig() for dotnet restore-like config resolution
- [x] ModuleInitializer + default ALC chaining pattern
- [x] Comprehensive test suite (8 tests)

### Part 3: NuGet Restore CLI (minrt-nuget) ✅
- [x] Add NuGetRestorer class to MinRT.NuGet
- [x] Use RestoreRunner.RunAsync with PackageSpec (same as dotnet restore)
- [x] Output project.assets.json matching SDK format
- [x] CLI with --package, --json, --output, --framework options
- [x] Transitive dependency resolution

---

## Part 2: NuGet AssemblyLoadContext

### Overview

A managed library that provides runtime NuGet package resolution and loading via a custom `AssemblyLoadContext`. Works like `dotnet restore` but programmatic - no MSBuild, no project files, just packages.

### Why This Exists

| Concern | MinRT.Core | MinRT.NuGet |
|---------|------------|-------------|
| AOT Compatible | ✅ Required | ❌ Not needed |
| NuGet Resolution | ❌ Too complex | ✅ Full support |
| Dependencies | Zero | NuGet.Protocol, NuGet.Resolver |
| Runs in | Native process | .NET runtime |

MinRT.Core stays minimal and AOT-compatible. Complex NuGet resolution moves to managed code.

### API

```csharp
// Simple usage
var alc = await NuGetAssemblyLoader.CreateBuilder()
    .AddPackage("Newtonsoft.Json", "13.0.3")
    .WithTargetFramework("net9.0")
    .BuildAsync();

var assembly = alc.LoadAssembly("Newtonsoft.Json");

// Full-featured usage
var alc = await NuGetAssemblyLoader.CreateBuilder()
    // Package references
    .AddPackage("Microsoft.Extensions.Logging", "9.0.0")
    .AddPackage("Serilog", "4.0.0")
    .AddPackageRange("Newtonsoft.Json", "[13.0.0, 14.0.0)")
    
    // Feed configuration
    .AddFeed("https://api.nuget.org/v3/index.json")
    .AddFeed("https://pkgs.dev.azure.com/org/_packaging/feed/nuget/v3/index.json", 
             "MyFeed", username, password)
    .WithNuGetConfig("./nuget.config")  // Or load from config file
    
    // Resolution settings
    .WithTargetFramework("net9.0")
    .WithDependencyBehavior(DependencyBehavior.Lowest)  // Like NuGet default
    .WithPackagesDirectory("./packages")
    
    // Diagnostics
    .WithLogger(loggerFactory)
    
    .BuildAsync();

// Use the loaded assemblies
var type = alc.GetType("Newtonsoft.Json", "Newtonsoft.Json.JsonConvert");
var instance = alc.CreateInstance("MyLib", "MyLib.MyClass", arg1, arg2);
```

### Resolution Algorithm

Matches SDK/NuGet restore behavior:

1. **Collect Dependencies** - BFS traversal of dependency graph
   - Query each feed for package metadata
   - Collect all transitive dependencies
   - Respect version ranges from each package

2. **Resolve Conflicts** - Using NuGet.Resolver
   - `DependencyBehavior.Lowest` (default) - lowest version that satisfies all constraints
   - `DependencyBehavior.HighestMinor/HighestPatch` - for updates
   - Nearest wins for diamond dependencies

3. **Download & Extract** - Cache packages locally
   - Standard NuGet folder layout: `{packages}/{id}/{version}/`
   - Skip if already cached

4. **Select Assets** - TFM-based selection
   - Use `NuGetFrameworkUtility.GetNearest()` for best `lib/` folder
   - Only managed assemblies (`.dll` in `lib/`)

5. **Create ALC** - Map assembly names to paths
   - Lazy loading on first `Assembly.Load()`
   - Falls back to default context for framework assemblies

### Cache Layout

```
{packagesDirectory}/
├── newtonsoft.json/
│   └── 13.0.3/
│       ├── lib/
│       │   ├── net6.0/
│       │   │   └── Newtonsoft.Json.dll
│       │   └── netstandard2.0/
│       │       └── Newtonsoft.Json.dll
│       └── newtonsoft.json.nuspec
├── microsoft.extensions.logging/
│   └── 9.0.0/
│       └── ...
```

### Key Components

```
src/MinRT.NuGet/
├── NuGetAssemblyLoader.cs      # Builder API + resolution logic
│   ├── CreateBuilder()         # Entry point
│   ├── AddPackage()            # Add package reference
│   ├── AddFeed()               # Add package source
│   ├── WithNuGetConfig()       # Load feeds from config
│   ├── WithTargetFramework()   # Set TFM
│   ├── WithPackagesDirectory() # Set cache location
│   ├── WithLogger()            # Enable diagnostics
│   └── BuildAsync()            # Resolve + download + create ALC
│
├── NuGetAssemblyLoadContext.cs # Custom ALC
│   ├── LoadAssembly()          # Load by name
│   ├── GetType()               # Get type from assembly
│   ├── CreateInstance()        # Create instance of type
│   └── Load()                  # Override for resolution
```

### Diagnostics

With `ILogger` enabled:

```
info: NuGetAssemblyLoader[0]
      Starting NuGet package resolution for 2 packages targeting net9.0
dbug: NuGetAssemblyLoader[0]
      Using 2 package sources
dbug: NuGetAssemblyLoader[0]
        Source: nuget.org (https://api.nuget.org/v3/index.json)
        Source: MyFeed (https://pkgs.dev.azure.com/...)
info: NuGetAssemblyLoader[0]
      Resolving dependency graph...
info: NuGetAssemblyLoader[0]
      Found 15 packages in dependency graph
info: NuGetAssemblyLoader[0]
      Resolving version conflicts using Lowest strategy...
info: NuGetAssemblyLoader[0]
      Resolved to 12 packages
dbug: NuGetAssemblyLoader[0]
        Microsoft.Extensions.Logging 9.0.0
        Microsoft.Extensions.Logging.Abstractions 9.0.0
        ...
info: NuGetAssemblyLoader[0]
      Downloading packages to ./packages...
dbug: NuGetAssemblyLoader[0]
      Package Microsoft.Extensions.Logging 9.0.0 already cached
dbug: NuGetAssemblyLoader[0]
      Downloading Serilog 4.0.0...
info: NuGetAssemblyLoader[0]
      Loaded 12 assemblies
info: NuGetAssemblyLoader[0]
      NuGet assembly loader ready
```

### Limitations (By Design)

- **No RID-specific assets** - Only portable `lib/` assemblies
- **No native libraries** - Managed assemblies only
- **No lock files** - Always resolves fresh (caches packages)
- **No central package management** - Direct package references only
- **No conditional dependencies** - Simple TFM matching only

### The ALC Resolver

```csharp
public class NuGetAssemblyLoadContext : AssemblyLoadContext
{
    private readonly Dictionary<string, string> _assemblyPaths;

    protected override Assembly? Load(AssemblyName name)
    {
        if (_assemblyPaths.TryGetValue(name.Name!, out var path))
        {
            return LoadFromAssemblyPath(path);
        }
        return null; // Fall back to default
    }
}
```

### Status

- [x] Design API  
- [x] Implement NuGetAssemblyLoader
- [x] Test with simple package (Newtonsoft.Json)
- [x] Test with transitive dependencies (Microsoft.Extensions.Logging - 6 packages)
- [x] UseDefaultNuGetConfig() - dotnet restore-like config resolution
- [x] ModuleInitializer + default ALC test (Humanizer)
- [x] Version ranges and allowNewer
- [x] Package caching verification
- [x] Error handling (package not found)
- [x] Target framework variations
