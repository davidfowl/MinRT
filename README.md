# MinRT - Minimal .NET Runtime Host

A zero-dependency .NET runtime bootstrapper. Downloads the .NET runtime from NuGet and executes your app - no pre-installed .NET required.

## Quick Start

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

## API

### MinRTBuilder

```csharp
var context = await new MinRTBuilder()
    .WithAppPath("myapp.dll")              // Required: your app
    .WithRuntimeVersion("10.0.0")          // Runtime version to use
    .WithCacheDirectory(".minrt-cache")    // Cache location (default: ~/.minrt)
    .WithRuntimeIdentifier("linux-x64")    // Override RID (auto-detected)
    .BuildAsync();
```

### MinRTContext

```csharp
// Properties
context.RuntimePath   // Downloaded runtime location
context.AppHostPath   // Patched apphost executable  
context.RuntimeVersion

// Execute
int exitCode = context.Run(args);
int exitCode = await context.RunAsync(args);
```

## How It Works

1. **Downloads runtime** from `Microsoft.NETCore.App.Runtime.{rid}` NuGet package
2. **Downloads apphost** from `Microsoft.NETCore.App.Host.{rid}` NuGet package
3. **Patches apphost** binary to embed your app path (same as .NET SDK)
4. **Executes** with `DOTNET_ROOT` pointing to downloaded runtime

## Cache Layout

```
~/.minrt/                                    # Default cache (or WithCacheDirectory)
├── runtimes/
│   └── 10.0.0-win-x64/                      # Downloaded runtime
│       ├── host/fxr/10.0.0/
│       │   └── hostfxr.dll
│       └── shared/
│           ├── Microsoft.NETCore.App/10.0.0/
│           │   ├── coreclr.dll
│           │   ├── System.*.dll
│           │   └── Microsoft.NETCore.App.runtimeconfig.json
│           └── Microsoft.AspNetCore.App/10.0.0/    # If WithAspNetCore()
│               └── Microsoft.AspNetCore.*.dll
├── packages/                                # Extracted NuGet packages
│   ├── microsoft.netcore.app.runtime.win-x64/10.0.0/
│   ├── microsoft.netcore.app.host.win-x64/10.0.0/
│   └── microsoft.aspnetcore.app.runtime.win-x64/10.0.0/
├── apphosts/                                # Patched apphosts (per app)
│   └── {hash}/                              # Hash of source app path
│       ├── myapp.exe                        # Patched apphost
│       ├── myapp.dll                        # Copied from source
│       ├── myapp.runtimeconfig.json         # Copied from source
│       └── myapp.deps.json                  # Copied from source
└── downloads/                               # Temp .nupkg files
```

The app DLL and config files are copied into the apphost directory because the apphost binary has a 64-character limit for the embedded path. Each unique source path gets its own directory (based on path hash).

## Runtime Sizes

Approximate sizes for .NET 10 (win-x64):

| | Unzipped | Zipped |
|---|---|---|
| Base runtime only | ~77 MB | ~35 MB |
| + ASP.NET Core | +29 MB | +13 MB |
| **Total with ASP.NET** | **~105 MB** | **~48 MB** |

## Requirements

- None! That's the point.

## Use Cases

- Bootstrapping .NET apps without requiring users to install .NET
- Isolated runtime environments
- Self-updating applications that download their own runtime

## License

MIT
