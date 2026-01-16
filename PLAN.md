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

- [x] Download runtime from NuGet
- [x] Download apphost from NuGet
- [x] Patch apphost with app path
- [x] Execute via DOTNET_ROOT
- [x] ASP.NET Core shared framework
- [x] Cross-platform (Windows, Linux)
- [x] Create portable runtime layout
- [x] Use pre-existing layout
- [ ] Additional shared frameworks (Windows Desktop, etc.)
- [ ] NuGet package dependency resolution
