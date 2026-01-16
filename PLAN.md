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
├── MinRT.Core/
│   ├── MinRTBuilder.cs          # Fluent builder API
│   ├── MinRTContext.cs          # Runs app via patched apphost
│   ├── AppHostPatcher.cs        # Binary patching
│   ├── RuntimeDownloader.cs     # Downloads runtime + apphost
│   ├── NuGetDownloader.cs       # AOT-safe NuGet client
│   └── RuntimeIdentifierHelper.cs
├── MinRT.TestHost/              # Test harness
├── hello/                       # Console test app
├── hello-web/                   # ASP.NET Core test app
└── test-artifacts/              # Published test apps
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

### Part 2: NuGet AssemblyLoadContext (Managed) 🔲
- [ ] Design and implement

---

## Part 2: NuGet AssemblyLoadContext

### Overview

A managed library that provides runtime NuGet package resolution and loading via a custom `AssemblyLoadContext`. This runs inside .NET (bootstrapped by MinRT) and handles dynamic package loading without deps.json.

### Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  MinRT.Core (Native AOT)                                        │
│  - Downloads .NET runtime                                       │
│  - Downloads managed host package                               │
│  - Executes host.dll                                            │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  Managed Host (runs in downloaded .NET)                         │
│  - Uses NuGetLoadContext                                        │
│  - Full NuGet resolution (NuGet.Protocol)                       │
│  - Downloads and resolves packages                              │
│  - Creates AssemblyLoadContext with custom resolver             │
│  - Loads and runs the actual application                        │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  Application (loaded via ALC)                                   │
│  - All deps resolved at runtime                                 │
│  - No build-time dependency resolution needed                   │
└─────────────────────────────────────────────────────────────────┘
```

### Why Two Tiers?

| Concern | MinRT.Core | NuGetLoadContext |
|---------|------------|------------------|
| AOT Compatible | ✅ Required | ❌ Not needed |
| NuGet Resolution | ❌ Too complex | ✅ Full support |
| Dependencies | Zero | Can use NuGet.Protocol |
| Runs in | Native process | .NET runtime |

MinRT.Core stays minimal and AOT-compatible. Complex NuGet resolution moves to managed code where we have full .NET capabilities.

### API (Sketch)

```csharp
// In the managed host application
var context = new NuGetLoadContext()
    .WithFeed("https://api.nuget.org/v3/index.json")
    .WithPackage("Aspire.Hosting", "9.0.0")
    .WithPackage("Aspire.Hosting.AppHost", "9.0.0")
    .WithTargetFramework("net9.0")
    .WithCacheDirectory(".nuget-cache");

await context.ResolveAsync();  // Download + resolve transitive deps

// Load assembly from resolved packages
var assembly = context.LoadFromPackage("Aspire.Hosting.AppHost");

// Or run an entry point
context.Run("Aspire.Hosting.AppHost", args);
```

### Key Components

```
MinRT/
├── MinRT.Core/                    # Part 1 (existing, AOT)
├── MinRT.NuGet/                   # Part 2 (new, managed)
│   ├── NuGetLoadContext.cs        # Custom AssemblyLoadContext
│   ├── NuGetLoadContextBuilder.cs # Fluent builder API
│   └── PackageResolver.cs         # NuGet dependency resolution + download
```

### How It Works

1. **Resolve** - Use NuGet.Protocol to resolve dependency graph
2. **Download** - Download all packages to local cache
3. **Map** - Build assembly name → DLL path mapping from packages
4. **Load** - Custom ALC intercepts `Assembly.Load()` and resolves from map

### The ALC Resolver

```csharp
public class NuGetLoadContext : AssemblyLoadContext
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

- [x] Create MinRT.NuGet project
- [x] Implement PackageResolver (NuGet.Protocol)
- [x] Implement NuGetLoadContext
- [ ] Test with simple package
- [ ] Test with transitive dependencies
- [ ] Test with Aspire packages
