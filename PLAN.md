# MinRT - Minimal Runtime for Aspire Polyglot Hosting

## End-to-End Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         NATIVE AOT CLI                                  │
│                    (e.g., aspire.cli.exe)                               │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  MinRT.Core (AOT-compatible, embedded in CLI)                           │
│  ┌───────────────────────────────────────────────────────────────────┐  │
│  │                                                                   │  │
│  │  1. .NET Runtime Acquisition                                      │  │
│  │     - Download .NET runtime from NuGet/CDN                        │  │
│  │     - OR embed pre-bundled runtime                                │  │
│  │     - Extract to ~/.minrt/runtimes/{version}/                     │  │
│  │                                                                   │  │
│  │  2. NuGet Package Download (AOT-safe)                             │  │
│  │     - Minimal HTTP client + System.Text.Json                      │  │
│  │     - Resolve dependencies via .nuspec parsing                    │  │
│  │     - Download to ~/.minrt/packages/{id}/{version}/               │  │
│  │                                                                   │  │
│  │  3. Managed Execution                                             │  │
│  │     - Load hostfxr.dll via P/Invoke                               │  │
│  │     - OR spawn: dotnet exec <managed.dll>                         │  │
│  │     - Custom runtime layout (not global dotnet)                   │  │
│  │                                                                   │  │
│  └───────────────────────────────────────────────────────────────────┘  │
│                                                                         │
│  This is the MINIMAL RT we need to build.                               │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                      MANAGED RUNTIME HOST                               │
│                   (MinRT.RuntimeHost.dll)                               │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  Runs on the .NET runtime downloaded/embedded above                     │
│  ┌───────────────────────────────────────────────────────────────────┐  │
│  │                                                                   │  │
│  │  1. Full NuGet.Protocol                                           │  │
│  │     - Download additional packages at runtime                     │  │
│  │     - Full dependency resolution with version ranges              │  │
│  │     - Credentials, source mapping, etc.                           │  │
│  │                                                                   │  │
│  │  2. AssemblyLoadContext                                           │  │
│  │     - Isolated loading of NuGet packages                          │  │
│  │     - Dependency resolution from package graph                    │  │
│  │     - Native library loading                                      │  │
│  │                                                                   │  │
│  │  3. Execute Hosted Application                                    │  │
│  │     - Invoke entry point with args                                │  │
│  │     - Manage lifecycle                                            │  │
│  │                                                                   │  │
│  └───────────────────────────────────────────────────────────────────┘  │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                      HOSTED APPLICATION                                 │
│              (e.g., Aspire.Hosting.RemoteHost)                          │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  Loaded into isolated ALC, full .NET capabilities                       │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### What We're Building

| Component | Runtime | Purpose |
|-----------|---------|---------|
| **MinRT.Core** | Native AOT | Native .NET host - download runtime + packages, execute managed code |
| **MinRT.RuntimeHost** | Managed .NET | Full NuGet, ALC, host applications |

### Key Insight

The native CLI doesn't need full NuGet - just enough to bootstrap. Once managed code is running, we have access to everything.

---

# Part 1: MinRT.Core - Native .NET Host

MinRT.Core is essentially a **native .NET host** that can:
1. Download or embed a .NET runtime
2. Download NuGet packages (AOT-compatible)
3. Execute managed assemblies using hostfxr

See https://github.com/dotnet/runtime/blob/main/docs/design/features/native-hosting.md for the official native hosting documentation.

## Native Hosting Overview

Reference implementation: `D:\dev\git\IIS.NativeAOT` - A Native AOT IIS module that hosts managed .NET applications.

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Native Host (MinRT.Core - AOT)                                         │
│                                                                         │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────────────────────┐  │
│  │   hostfxr   │───▶│ hostpolicy  │───▶│  coreclr + managed code     │  │
│  └─────────────┘    └─────────────┘    └─────────────────────────────┘  │
│                                                                         │
│  hostfxr: Finds and loads the runtime                                   │
│  hostpolicy: Applies runtime configuration                              │
│  coreclr: The actual CLR that runs managed code                         │
└─────────────────────────────────────────────────────────────────────────┘
```

## hostfxr API (from IIS.NativeAOT reference)

```csharp
// HostFxrImports.cs - P/Invoke declarations
public static partial class HostFxrImports
{
    public unsafe struct hostfxr_initialize_parameters
    {
        public nint size;
        public char* host_path;
        public char* dotnet_root;  // Key: points to our custom runtime location
    };

    [LibraryImport("hostfxr", EntryPoint = "hostfxr_initialize_for_dotnet_command_line")]
    public unsafe static partial int Initialize(
        int argc,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] argv,
        ref hostfxr_initialize_parameters parameters,
        out IntPtr host_context_handle);

    [LibraryImport("hostfxr", EntryPoint = "hostfxr_run_app")]
    public static partial int Run(IntPtr host_context_handle);
}

// HostFxr.cs - Initialization logic
public static (string? Error, int? ErrorCode, nint Handle) Initialize(string? dll)
{
    var dotnetRoot = GetDotnetRootPath();  // Can be custom path!
    
    // Find highest version hostfxr
    var allHostFxrDirs = new DirectoryInfo(Path.Combine(dotnetRoot, "host", "fxr"));
    var hostFxrDirectory = allHostFxrDirs.EnumerateDirectories()
        .OrderByDescending(d => FxVer.Parse(d.Name))
        .FirstOrDefault();
    
    // Load hostfxr.dll from our custom location
    NativeLibrary.Load(Path.Combine(hostFxrDirectory.FullName, "hostfxr.dll"));
    
    string[] args = [dll];
    
    unsafe
    {
        fixed (char* hostPathPointer = Environment.CurrentDirectory)
        fixed (char* dotnetRootPointer = dotnetRoot)
        {
            var parameters = new HostFxrImports.hostfxr_initialize_parameters
            {
                size = sizeof(HostFxrImports.hostfxr_initialize_parameters),
                host_path = hostPathPointer,
                dotnet_root = dotnetRootPointer  // Our custom runtime!
            };

            var err = HostFxrImports.Initialize(args.Length, args, ref parameters, out var handle);
            return err < 0 ? ($"Error {err}", err, 0) : (null, null, handle);
        }
    }
}

// CLRHost.cs - Run on background thread
var thread = new Thread(static _ =>
{
    _returnCode = HostFxrImports.Run(_hostContextHandle);
})
{
    IsBackground = true
};
thread.Start();
```

## Key Patterns from IIS.NativeAOT

1. **Custom dotnet_root**: The `hostfxr_initialize_parameters.dotnet_root` points to our downloaded runtime, not global install
2. **Version selection**: `FxVer` class finds highest available hostfxr version
3. **Background thread**: Run managed app on separate thread to not block native host
4. **NativeLibrary.Load**: Explicitly load hostfxr.dll before calling P/Invoke

## MinRT.Core Responsibilities

```
┌─────────────────────────────────────────────────────────────────────────┐
│  MinRT.Core (Native AOT)                                                │
│                                                                         │
│  1. Runtime Acquisition                                                 │
│     ├── Check ~/.minrt/runtimes/{version}/ for cached runtime           │
│     ├── Download from NuGet: Microsoft.NETCore.App.Runtime.{rid}        │
│     ├── Download from NuGet: Microsoft.NETCore.App.Host.{rid}           │
│     └── Extract to cache location                                       │
│                                                                         │
│  2. Package Acquisition (AOT-safe NuGet client)                         │
│     ├── Load NuGet.config (NuGet.Configuration - AOT safe)              │
│     ├── GET {source}/index.json → find PackageBaseAddress               │
│     ├── GET {base}/{id}/{ver}/{id}.{ver}.nupkg                          │
│     ├── Extract ZIP, parse .nuspec for dependencies                     │
│     └── Recursively download transitive dependencies                    │
│                                                                         │
│  3. Native Hosting                                                      │
│     ├── Locate hostfxr in downloaded/embedded runtime                   │
│     ├── Load hostfxr via NativeLibrary.Load()                           │
│     ├── Get function pointers via GetExport()                           │
│     ├── Initialize runtime with custom paths                            │
│     └── Run managed application or get delegate                         │
└─────────────────────────────────────────────────────────────────────────┘
```

## Custom Runtime Layout

Unlike `dotnet exec` which uses the global SDK, MinRT uses a self-contained layout:

```
~/.minrt/
├── runtimes/
│   └── 9.0.0-win-x64/
│       ├── host/
│       │   └── fxr/
│       │       └── 9.0.0/
│       │           └── hostfxr.dll          ◀── Load this first
│       └── shared/
│           └── Microsoft.NETCore.App/
│               └── 9.0.0/
│                   ├── hostpolicy.dll
│                   ├── coreclr.dll
│                   └── System.*.dll
├── packages/
│   └── MinRT.RuntimeHost/
│       └── 1.0.0/
│           └── lib/
│               └── net9.0/
│                   └── MinRT.RuntimeHost.dll  ◀── Run this
└── host/
    └── MinRT.RuntimeHost.runtimeconfig.json   ◀── Points to our runtime
```

### runtimeconfig.json for Custom Layout

```json
{
  "runtimeOptions": {
    "tfm": "net9.0",
    "framework": {
      "name": "Microsoft.NETCore.App",
      "version": "9.0.0"
    },
    "configProperties": {
      "System.Runtime.TieredCompilation": true
    }
  }
}
```

## Implementation Progress

- [x] MinRTBuilder - basic structure
- [x] MinRTContext - basic structure  
- [x] HostFxrImports - P/Invoke declarations
- [x] RuntimeIdentifierHelper - detect current RID
- [x] CachePaths - cache directory layout
- [x] **Test: UseSystemRuntime + Run with hello.dll ✅ WORKS!**
- [ ] NuGetDownloader - download packages from NuGet
- [ ] PackageResolver - resolve dependencies
- [ ] RuntimeDownloader - download .NET runtime from NuGet
- [ ] Test: UseDownloadedRuntime + Run

---

## Cache Layout

All temporary files, downloads, and caches are stored under a single root directory:

```
{cacheDirectory}/                          # Default: ~/.minrt or user-specified
├── runtimes/                              # Downloaded .NET runtimes
│   └── 10.0.0-win-x64/
│       ├── host/
│       │   └── fxr/
│       │       └── 10.0.0/
│       │           └── hostfxr.dll
│       └── shared/
│           └── Microsoft.NETCore.App/
│               └── 10.0.0/
│                   ├── coreclr.dll
│                   └── System.*.dll
├── packages/                              # Extracted NuGet packages
│   ├── newtonsoft.json/
│   │   └── 13.0.3/
│   │       └── lib/net9.0/
│   └── microsoft.extensions.hosting/
│       └── 9.0.0/
│           └── lib/net9.0/
├── downloads/                             # Temporary .nupkg downloads (cleaned up)
│   └── *.nupkg
└── temp/                                  # Other temporary files
```

---

## Managed Builder API (AOT-Compatible)

A fluent API for constructing a runnable .NET context. Fully Native AOT compatible.

```csharp
// Build a context with packages and runtime
var context = await new MinRTBuilder()
    .WithTargetFramework("net9.0")
    .WithRuntimeIdentifier("win-x64")
    .AddPackageReference("Aspire.Hosting", "9.0.0")
    .AddPackageReference("Aspire.Hosting.AppHost", "9.0.0")
    .AddProbingPath(@"C:\Users\davifowl\AppData\Local\Temp\.aspire\hosts\e2645025a1c0")
    .UseSystemRuntime()
    .BuildAsync();

// Execute an entry point
var exitCode = await context.RunAsync("Aspire.Hosting.AppHost.dll", args);
```

### API Design

```csharp
public sealed class MinRTBuilder
{
    private string? _targetFramework;
    private string? _runtimeIdentifier;
    private string? _cacheDirectory;
    private readonly List<PackageReference> _packages = [];
    private readonly List<string> _probingPaths = [];
    private RuntimeMode _runtimeMode = RuntimeMode.Download;
    private string? _runtimeVersion;

    /// <summary>
    /// Target framework (e.g., "net9.0", "net10.0")
    /// </summary>
    public MinRTBuilder WithTargetFramework(string tfm)
    {
        _targetFramework = tfm;
        return this;
    }

    /// <summary>
    /// Runtime identifier (e.g., "win-x64", "linux-x64", "osx-arm64")
    /// </summary>
    public MinRTBuilder WithRuntimeIdentifier(string rid)
    {
        _runtimeIdentifier = rid;
        return this;
    }

    /// <summary>
    /// Add a NuGet package reference
    /// </summary>
    public MinRTBuilder AddPackageReference(string packageId, string version)
    {
        _packages.Add(new PackageReference(packageId, version));
        return this;
    }

    /// <summary>
    /// Add additional probing paths for assembly resolution
    /// </summary>
    public MinRTBuilder AddProbingPath(string path)
    {
        _probingPaths.Add(path);
        return this;
    }

    /// <summary>
    /// Use the system-installed .NET runtime (DOTNET_ROOT or default location)
    /// </summary>
    public MinRTBuilder UseSystemRuntime()
    {
        _runtimeMode = RuntimeMode.System;
        return this;
    }

    /// <summary>
    /// Download runtime from NuGet if not cached (default)
    /// </summary>
    public MinRTBuilder UseDownloadedRuntime(string? version = null)
    {
        _runtimeMode = RuntimeMode.Download;
        _runtimeVersion = version;
        return this;
    }

    /// <summary>
    /// Use runtime at a specific path
    /// </summary>
    public MinRTBuilder UseRuntimeAt(string path)
    {
        _runtimeMode = RuntimeMode.Custom;
        _cacheDirectory = path;
        return this;
    }

    /// <summary>
    /// Directory to cache downloaded packages/runtimes (default: ~/.minrt)
    /// </summary>
    public MinRTBuilder WithCacheDirectory(string path)
    {
        _cacheDirectory = path;
        return this;
    }

    /// <summary>
    /// Build the runtime context - downloads packages and runtime as needed
    /// </summary>
    public async Task<MinRTContext> BuildAsync(CancellationToken ct = default)
    {
        _targetFramework ??= "net9.0";
        _runtimeIdentifier ??= RuntimeIdentifier.Current;
        _cacheDirectory ??= DefaultCacheDirectory;

        // 1. Resolve runtime path
        var runtimePath = await ResolveRuntimeAsync(ct);

        // 2. Download and resolve packages
        var packageResolver = new PackageResolver(_cacheDirectory, _targetFramework, _runtimeIdentifier);
        var resolvedPackages = await packageResolver.ResolveAsync(_packages, ct);

        // 3. Build assembly map from packages + probing paths
        var assemblyPaths = BuildAssemblyPaths(resolvedPackages, _probingPaths);

        return new MinRTContext(runtimePath, _runtimeVersion!, assemblyPaths, _probingPaths);
    }
}

public sealed class MinRTContext
{
    private readonly string _runtimePath;
    private readonly string _runtimeVersion;
    private readonly Dictionary<string, string> _assemblyPaths;
    private readonly List<string> _probingPaths;

    internal MinRTContext(
        string runtimePath,
        string runtimeVersion,
        Dictionary<string, string> assemblyPaths,
        List<string> probingPaths)
    {
        _runtimePath = runtimePath;
        _runtimeVersion = runtimeVersion;
        _assemblyPaths = assemblyPaths;
        _probingPaths = probingPaths;
    }

    /// <summary>
    /// Path to the .NET runtime root
    /// </summary>
    public string RuntimePath => _runtimePath;

    /// <summary>
    /// Resolved assembly paths (assembly name -> full path)
    /// </summary>
    public IReadOnlyDictionary<string, string> AssemblyPaths => _assemblyPaths;

    /// <summary>
    /// Run a managed entry point
    /// </summary>
    public Task<int> RunAsync(string entryAssembly, string[]? args = null, CancellationToken ct = default)
    {
        // Find the entry assembly in our resolved paths
        var entryPath = ResolveAssemblyPath(entryAssembly);
        
        return Task.FromResult(RunWithHostFxr(entryPath, args));
    }

    /// <summary>
    /// Run a managed entry point, specifying the full path
    /// </summary>
    public Task<int> RunAsync(string entryAssemblyPath, string[]? args = null, CancellationToken ct = default)
    {
        return Task.FromResult(RunWithHostFxr(entryAssemblyPath, args));
    }

    private string ResolveAssemblyPath(string assemblyName)
    {
        // Try exact match first
        if (_assemblyPaths.TryGetValue(assemblyName, out var path))
            return path;

        // Try without extension
        var nameWithoutExt = Path.GetFileNameWithoutExtension(assemblyName);
        if (_assemblyPaths.TryGetValue(nameWithoutExt, out path))
            return path;

        // Search probing paths
        foreach (var probingPath in _probingPaths)
        {
            var candidate = Path.Combine(probingPath, assemblyName);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Assembly not found: {assemblyName}");
    }

    private int RunWithHostFxr(string entryPath, string[]? args)
    {
        var hostfxrName = OperatingSystem.IsWindows() ? "hostfxr.dll"
                        : OperatingSystem.IsMacOS() ? "libhostfxr.dylib"
                        : "libhostfxr.so";

        var hostfxrPath = Path.Combine(_runtimePath, "host", "fxr", _runtimeVersion, hostfxrName);
        NativeLibrary.Load(hostfxrPath);

        var argv = args is null ? [entryPath] : [entryPath, .. args];

        unsafe
        {
            fixed (char* hostPath = _runtimePath)
            fixed (char* dotnetRoot = _runtimePath)
            {
                var parameters = new HostFxrImports.hostfxr_initialize_parameters
                {
                    size = sizeof(HostFxrImports.hostfxr_initialize_parameters),
                    host_path = hostPath,
                    dotnet_root = dotnetRoot
                };

                var err = HostFxrImports.Initialize(argv.Length, [.. argv], ref parameters, out var handle);
                if (err < 0) return err;

                err = HostFxrImports.Run(handle);
                HostFxrImports.Close(handle);
                return err;
            }
        }
    }
}

public readonly record struct PackageReference(string Id, string Version);

internal enum RuntimeMode { System, Download, Custom }
```

### Usage Examples

```csharp
// Example 1: Simple - run hello.dll with system runtime
var context = await new MinRTBuilder()
    .WithTargetFramework("net10.0")
    .AddProbingPath("./test-artifacts")
    .UseSystemRuntime()
    .BuildAsync();

await context.RunAsync("hello.dll");

// Example 2: Download packages and runtime from NuGet
var context = await new MinRTBuilder()
    .WithTargetFramework("net9.0")
    .WithRuntimeIdentifier("win-x64")
    .AddPackageReference("Microsoft.Extensions.Hosting", "9.0.0")
    .UseDownloadedRuntime("9.0.0")
    .BuildAsync();

await context.RunAsync("MyApp.dll", ["--environment", "Production"]);

// Example 3: Aspire scenario - mixed probing paths and packages
var context = await new MinRTBuilder()
    .WithTargetFramework("net9.0")
    .WithRuntimeIdentifier("win-x64")
    .AddPackageReference("Aspire.Hosting", "9.0.0")
    .AddPackageReference("Aspire.Hosting.AppHost", "9.0.0")
    .AddProbingPath(@"C:\Users\davifowl\AppData\Local\Temp\.aspire\hosts\e2645025a1c0")
    .UseSystemRuntime()
    .BuildAsync();

await context.RunAsync("Aspire.Hosting.AppHost.dll", ["--app-id", "myapp"]);

// Example 4: Inspect resolved assemblies
var context = await new MinRTBuilder()
    .WithTargetFramework("net9.0")
    .AddPackageReference("Newtonsoft.Json", "13.0.3")
    .UseSystemRuntime()
    .BuildAsync();

foreach (var (name, path) in context.AssemblyPaths)
{
    Console.WriteLine($"{name} -> {path}");
}
```

---

## End-to-End Test Case

### Simplest Test: Download Runtime + Run Hello World

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Goal: Prove we can download .NET from NuGet and execute managed code   │
│                                                                         │
│  Input:                                                                 │
│  - A pre-built "hello.dll" (simple console app)                         │
│  - No .NET installed (or ignored)                                       │
│                                                                         │
│  What MinRT does:                                                       │
│  1. Download Microsoft.NETCore.App.Runtime.win-x64 from NuGet           │
│  2. Download Microsoft.NETCore.App.Host.win-x64 from NuGet              │
│  3. Extract and assemble into runtime layout                            │
│  4. Load hostfxr.dll, call hostfxr_initialize + hostfxr_run_app         │
│  5. hello.dll executes, prints "Hello, World!"                          │
│                                                                         │
│  Output: "Hello, World!" on console                                     │
└─────────────────────────────────────────────────────────────────────────┘
```

### Test Setup

**1. Create hello.dll (one time, check into repo)**

```csharp
// hello/Program.cs
Console.WriteLine("Hello, World!");
Console.WriteLine($"Runtime: {Environment.Version}");
return 0;
```

```powershell
dotnet new console -n hello
cd hello
dotnet publish -c Release -o ../test-artifacts
# Produces: hello.dll, hello.runtimeconfig.json
```

**2. Run MinRT**

```powershell
# MinRT.Core.exe <path-to-dll>
.\MinRT.Core.exe .\test-artifacts\hello.dll

# Expected output:
# Downloading Microsoft.NETCore.App.Runtime.win-x64@9.0.0...
# Downloading Microsoft.NETCore.App.Host.win-x64@9.0.0...
# Extracting runtime...
# Hello, World!
# Runtime: 9.0.0
```

**3. Verify**

```powershell
# Cache should exist
ls ~/.minrt/runtimes/9.0.0-win-x64/

# Second run should be instant (no downloads)
.\MinRT.Core.exe .\test-artifacts\hello.dll
```

---

## Problem Statement

Aspire polyglot hosting needs to:
1. Download `Aspire.Hosting.RemoteHost` and its dependencies from NuGet
2. Run it using a .NET runtime
3. Work from a **Native AOT executable** (`aspire.cli`)

**Challenge**: NuGet.Protocol uses Newtonsoft.Json which is **not AOT-compatible**.

## Solution

Build a minimal, AOT-compatible NuGet client that:
- Uses existing AOT-safe NuGet libraries (Configuration, Packaging, Versioning)
- Replaces NuGet.Protocol's JSON layer with System.Text.Json + source generators
- Downloads packages respecting user's NuGet.config

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  aspire.cli (Native AOT)                                    │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  MinRT.Core (AOT-compatible)                                │
│  ├── NuGet.Configuration  ✅ (feed management, credentials) │
│  ├── NuGet.Packaging      ✅ (extract .nupkg, parse .nuspec)│
│  ├── NuGet.Versioning     ✅ (version parsing/comparison)   │
│  └── NEW: AotNuGetClient  📝 (HTTP + STJ for v3 API)        │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

## What's AOT-Safe (Use As-Is)

| Component | Purpose | AOT Status |
|-----------|---------|------------|
| NuGet.Configuration | Load NuGet.config, get PackageSources | ✅ XDocument only |
| NuGet.Common | Utilities, logging | ✅ No JSON |
| NuGet.Versioning | NuGetVersion, VersionRange | ✅ Pure parsing |
| NuGet.Frameworks | NuGetFramework parsing | ✅ Pure parsing |
| NuGet.Packaging | Extract ZIP, parse .nuspec | ✅ ZipArchive + XDocument |

## What We Must Write

| Component | Purpose | LOC |
|-----------|---------|-----|
| `NuGetAotJsonContext.cs` | STJ source-generated models for v3 API | ~60 |
| `AotNuGetClient.cs` | HTTP client to download packages | ~100 |
| `PackageDownloader.cs` | Integration with NuGet.Configuration | ~50 |
| `DependencyResolver.cs` | Walk .nuspec to resolve transitive deps | ~80 |

**Total: ~300 lines of new code**

## NuGet v3 API Flow

```
1. Load NuGet.config → PackageSource[]     (NuGet.Configuration)
2. GET {source}/index.json → ServiceIndex  (AotNuGetClient + STJ)
3. Find PackageBaseAddress URL             (string match)
4. GET {base}/{id}/{ver}/{id}.{ver}.nupkg  (HttpClient)
5. Extract ZIP → folder                    (ZipFile)
6. Parse .nuspec → dependencies            (NuGet.Packaging)
7. Repeat for transitive deps              (DependencyResolver)
```

## API Design

### Builder Pattern
```csharp
var packages = await new PackageDownloader()
    .WithProjectDirectory(@"C:\MyProject")  // Find NuGet.config here
    .WithPackageCache(@"C:\Users\user\.nuget\packages")
    .WithTargetFramework("net9.0")
    .DownloadWithDependenciesAsync("Aspire.Hosting.RemoteHost", "9.0.0");
```

### Output
```csharp
public record DownloadedPackage(
    string Id,
    string Version,
    string Path,           // Extracted folder
    string[] Assemblies,   // Runtime DLLs
    string[] Dependencies  // Transitive package IDs
);
```

## Files to Create

```
MinRT/
├── MinRT.sln
├── PLAN.md                          ← This file
├── MinRT.Core/
│   ├── MinRT.Core.csproj
│   ├── NuGetAotJsonContext.cs       ✅ Created - STJ models
│   ├── AotNuGetClient.cs            📝 TODO - HTTP download
│   ├── PackageDownloader.cs         📝 TODO - Main API
│   └── DependencyResolver.cs        📝 TODO - Transitive deps
└── MinRT.TestApp/
    ├── MinRT.TestApp.csproj
    └── Program.cs                   📝 TODO - Test harness
```

## Test Scenarios

1. **Basic download**: Download single package by ID+version
2. **With dependencies**: Download package + all transitive deps
3. **Custom feed**: Use private NuGet feed from NuGet.config
4. **Offline**: Use local folder as package source
5. **AOT validation**: Publish as Native AOT, verify no warnings

## Success Criteria

- [ ] Can download `Newtonsoft.Json` 13.0.3 from nuget.org
- [ ] Can resolve transitive dependencies for `Microsoft.Extensions.Hosting`
- [ ] Respects NuGet.config package sources
- [ ] Compiles with `<PublishAot>true</PublishAot>` with no warnings
- [ ] Works in aspire.cli to bootstrap RemoteHost

---

# Part 2: Managed Runtime Host

## Overview

Once the AOT bootstrapper downloads packages, we need a managed .NET runtime to actually load and run them. This involves three distinct components:

### The Three Components

```
┌─────────────────────────────────────────────────────────────────────┐
│  1. AOT Bootstrapper (MinRT.Core)                                   │
│     - Downloads NuGet packages (AOT-safe)                           │
│     - Launches dotnet with the Runtime Host                         │
└─────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│  2. Runtime Host (MinRT.RuntimeHost)                                │
│     - Full NuGet.Protocol (non-AOT, runs on dotnet)                 │
│     - AssemblyLoadContext for isolated plugin loading               │
│     - Can download additional packages at runtime                   │
└─────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│  3. Hosted Application (e.g., Aspire.Hosting.RemoteHost)            │
│     - Loaded into isolated AssemblyLoadContext                      │
│     - Full .NET runtime capabilities                                │
│     - Can be any NuGet package with an entry point                  │
└─────────────────────────────────────────────────────────────────────┘
```

## Component 1: AOT Bootstrapper (MinRT.Core)

**Already planned above** - minimal NuGet client that:
- Downloads the Runtime Host package
- Downloads initial dependencies
- Downloads .NET runtime if not present
- Invokes the runtime to execute the RuntimeHost

### 1a. .NET Runtime Acquisition from NuGet

The .NET shared framework can be acquired directly from NuGet packages. This is the same mechanism used by the SDK.

**NuGet Packages for Runtime:**
```
Microsoft.NETCore.App.Runtime.{rid}     → Runtime binaries (coreclr, BCL)
Microsoft.NETCore.App.Host.{rid}        → Host components (hostfxr, dotnet)
Microsoft.AspNetCore.App.Runtime.{rid}  → ASP.NET Core runtime (if needed)
```

Example: https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.linux-x64

**Reference Implementation:** See `D:\dev\git\dotnet-sdk\src\Cli\dotnet\NugetPackageDownloader\NuGetPackageDownloader.cs`

```csharp
// SDK's approach to downloading and extracting packages
public async Task<string> DownloadPackageAsync(PackageId packageId, NuGetVersion packageVersion, ...)
{
    // 1. Resolve package source and version
    (var source, var resolvedPackageVersion) = await GetPackageSourceAndVersion(...);
    
    // 2. Get the download resource
    SourceRepository repository = GetSourceRepository(source);
    FindPackageByIdResource resource = await repository.GetResourceAsync<FindPackageByIdResource>();
    
    // 3. Download the .nupkg
    var pathResolver = new VersionFolderPathResolver(downloadFolder);
    string nupkgPath = pathResolver.GetPackageFilePath(packageId, resolvedPackageVersion);
    
    using FileStream destinationStream = File.Create(nupkgPath);
    await resource.CopyNupkgToStreamAsync(packageId, resolvedPackageVersion, destinationStream, ...);
    
    return nupkgPath;
}

public async Task<IEnumerable<string>> ExtractPackageAsync(string packagePath, DirectoryPath targetFolder)
{
    await using FileStream packageStream = File.OpenRead(packagePath);
    PackageExtractionContext context = new(PackageSaveMode.Defaultv3, ...);
    
    return await PackageExtractor.ExtractPackageAsync(
        targetFolder, packageStream, packagePathResolver, context, cancellationToken);
}
```

**Package Contents (Microsoft.NETCore.App.Runtime.win-x64):**
```
microsoft.netcore.app.runtime.win-x64/
├── runtimes/
│   └── win-x64/
│       ├── lib/
│       │   └── net9.0/
│       │       ├── System.Private.CoreLib.dll
│       │       ├── System.Runtime.dll
│       │       └── ... (BCL assemblies)
│       └── native/
│           ├── coreclr.dll
│           ├── clrjit.dll
│           └── ... (native components)
└── ... (metadata)
```

**Package Contents (Microsoft.NETCore.App.Host.win-x64):**
```
microsoft.netcore.app.host.win-x64/
├── runtimes/
│   └── win-x64/
│       └── native/
│           ├── dotnet.exe
│           ├── hostfxr.dll
│           ├── hostpolicy.dll
│           └── ... (native host components)
└── ... (metadata)
```

### Assembling a Runtime Layout

After downloading both packages, assemble into the expected dotnet layout:

```
~/.minrt/runtimes/9.0.0-win-x64/
├── dotnet.exe                           ← from Host package/native
├── host/
│   └── fxr/
│       └── 9.0.0/
│           └── hostfxr.dll              ← from Host package/native
└── shared/
    └── Microsoft.NETCore.App/
        └── 9.0.0/
            ├── hostpolicy.dll           ← from Host package/native  
            ├── coreclr.dll              ← from Runtime package/native
            ├── System.Private.CoreLib.dll ← from Runtime package/lib
            └── ...                      ← from Runtime package/lib
```

**runtimeconfig.json Generation:**
```csharp
// Generate runtimeconfig.json for the managed app (see GenerateRuntimeConfigurationFiles.cs)
var config = new {
    runtimeOptions = new {
        tfm = "net9.0",
        framework = new {
            name = "Microsoft.NETCore.App",
            version = "9.0.0"
        }
    }
};
File.WriteAllText(runtimeConfigPath, JsonSerializer.Serialize(config));
```

## Component 2: Runtime Host (MinRT.RuntimeHost)

A managed .NET application that provides full NuGet capabilities and dynamic assembly loading.

### Responsibilities

```
┌─────────────────────────────────────────────────────────────────────┐
│  RuntimeHost Capabilities                                           │
│                                                                     │
│  1. Package Management (Full NuGet.Protocol)                        │
│     - Download additional packages on demand                        │
│     - Resolve transitive dependencies                               │
│     - Handle version conflicts                                      │
│     - Respect NuGet.config feeds and credentials                    │
│                                                                     │
│  2. Assembly Loading (AssemblyLoadContext)                          │
│     - Load assemblies into isolated contexts                        │
│     - Resolve dependencies from downloaded packages                 │
│     - Support unloading (collectible ALCs)                          │
│                                                                     │
│  3. Execution                                                       │
│     - Find and invoke entry points                                  │
│     - Pass arguments to hosted application                          │
│     - Manage application lifecycle                                  │
└─────────────────────────────────────────────────────────────────────┘
```

### Package Resolution Flow

```
┌──────────────────────────────────────────────────────────────────────┐
│ RuntimeHost receives: --package Aspire.Hosting.RemoteHost@9.0.0     │
│                                                                      │
│ 1. Check package cache                                               │
│    └─ ~/.minrt/packages/Aspire.Hosting.RemoteHost/9.0.0/            │
│                                                                      │
│ 2. If not cached, use NuGet.Protocol:                                │
│    var settings = Settings.LoadDefaultSettings(root);                │
│    var sources = SettingsUtility.GetEnabledSources(settings);        │
│    foreach (var source in sources)                                   │
│    {                                                                 │
│        var repo = Repository.Factory.GetCoreV3(source);              │
│        var resource = await repo.GetResourceAsync<DependencyInfo>(); │
│        // Resolve full dependency graph                              │
│    }                                                                 │
│                                                                      │
│ 3. Download all packages in dependency graph                         │
│    └─ Parallel download with deduplication                           │
│                                                                      │
│ 4. Build assembly resolution map                                     │
│    {                                                                 │
│      "Aspire.Hosting.RemoteHost.dll" → "~/.minrt/packages/.../net9.0"│
│      "Microsoft.Extensions.Hosting.dll" → "~/.minrt/packages/..."    │
│      ...                                                             │
│    }                                                                 │
└──────────────────────────────────────────────────────────────────────┘
```

### AssemblyLoadContext Implementation

```csharp
public class PackageLoadContext : AssemblyLoadContext
{
    private readonly Dictionary<string, string> _assemblyPaths;
    private readonly string _basePath;
    
    public PackageLoadContext(
        string basePath, 
        Dictionary<string, string> assemblyPaths) 
        : base(isCollectible: true)
    {
        _basePath = basePath;
        _assemblyPaths = assemblyPaths;
    }
    
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Try resolved package paths first
        if (_assemblyPaths.TryGetValue(assemblyName.Name!, out var path))
        {
            return LoadFromAssemblyPath(path);
        }
        
        // Fall back to probing in base path
        var probePath = Path.Combine(_basePath, $"{assemblyName.Name}.dll");
        if (File.Exists(probePath))
        {
            return LoadFromAssemblyPath(probePath);
        }
        
        // Let default context handle it (shared framework assemblies)
        return null;
    }
    
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        // Handle native dependencies from runtimes/{rid}/native/
        var nativePath = ResolveNativeDll(unmanagedDllName);
        return nativePath != null 
            ? LoadUnmanagedDllFromPath(nativePath) 
            : IntPtr.Zero;
    }
}
```

### Entry Point Discovery

```csharp
public static class EntryPointResolver
{
    public static Func<string[], Task<int>>? FindEntryPoint(Assembly assembly)
    {
        // Strategy 1: Look for IHostedPlugin
        var pluginType = assembly.GetTypes()
            .FirstOrDefault(t => typeof(IHostedPlugin).IsAssignableFrom(t));
        if (pluginType != null)
        {
            var plugin = (IHostedPlugin)Activator.CreateInstance(pluginType)!;
            return args => plugin.RunAsync(args);
        }
        
        // Strategy 2: Convention - Program.Main
        var programType = assembly.GetType("Program") 
                       ?? assembly.GetTypes().FirstOrDefault(t => t.Name == "Program");
        var mainMethod = programType?.GetMethod("Main", 
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        
        if (mainMethod != null)
        {
            return args => InvokeMain(mainMethod, args);
        }
        
        // Strategy 3: Assembly entry point
        var entryPoint = assembly.EntryPoint;
        if (entryPoint != null)
        {
            return args => InvokeMain(entryPoint, args);
        }
        
        return null;
    }
}
```

### RuntimeHost Main Flow

```csharp
// MinRT.RuntimeHost/Program.cs
public static async Task<int> Main(string[] args)
{
    var options = ParseArgs(args);
    
    // 1. Resolve and download packages
    var resolver = new NuGetPackageResolver(options.PackageCache);
    var packages = await resolver.ResolveWithDependenciesAsync(
        options.PackageId, 
        options.Version,
        options.Framework);
    
    // 2. Build assembly map from resolved packages
    var assemblyMap = BuildAssemblyMap(packages, options.Framework);
    
    // 3. Create isolated load context
    var loadContext = new PackageLoadContext(
        packages.First().LibPath, 
        assemblyMap);
    
    // 4. Load entry assembly
    var entryAssemblyPath = packages.First().GetEntryAssembly();
    var assembly = loadContext.LoadFromAssemblyPath(entryAssemblyPath);
    
    // 5. Find and invoke entry point
    var entryPoint = EntryPointResolver.FindEntryPoint(assembly);
    if (entryPoint == null)
        throw new InvalidOperationException($"No entry point found in {assembly.GetName().Name}");
    
    return await entryPoint(options.AppArgs);
}
```

## Component 3: Hosted Application

Any NuGet package that conforms to one of:
- Implements `IHostedPlugin` interface
- Has a `Program.Main` entry point
- Exposes a known factory method

## File Structure (Updated)

```
MinRT/
├── MinRT.sln
├── PLAN.md
├── MinRT.Core/                      # AOT-compatible bootstrapper
│   ├── MinRT.Core.csproj
│   ├── NuGetAotJsonContext.cs       ✅ Created
│   ├── AotNuGetClient.cs            📝 TODO
│   ├── PackageDownloader.cs         📝 TODO
│   └── DependencyResolver.cs        📝 TODO
├── MinRT.RuntimeHost/               # Managed runtime (runs on dotnet)
│   ├── MinRT.RuntimeHost.csproj     📝 TODO
│   ├── Program.cs                   📝 TODO - Entry point
│   ├── PluginLoadContext.cs         📝 TODO - ALC implementation
│   ├── PluginLoader.cs              📝 TODO - Load & invoke plugins
│   └── NuGetService.cs              📝 TODO - Full NuGet.Protocol wrapper
├── MinRT.Abstractions/              # Shared interfaces (optional)
│   ├── MinRT.Abstractions.csproj    📝 TODO
│   └── IHostedPlugin.cs             📝 TODO
└── MinRT.TestApp/
    ├── MinRT.TestApp.csproj
    └── Program.cs                   📝 TODO
```

## End-to-End Flow

```
┌──────────────────────────────────────────────────────────────────────┐
│ aspire.cli (Native AOT)                                              │
│                                                                      │
│ 1. User runs: aspire run --project MyAspireApp                       │
│ 2. Detect need for RemoteHost                                        │
│ 3. Use MinRT.Core to download:                                       │
│    - MinRT.RuntimeHost (if not cached)                               │
│    - Aspire.Hosting.RemoteHost (if not cached)                       │
│ 4. Exec: dotnet MinRT.RuntimeHost.dll                                │
│          --load Aspire.Hosting.RemoteHost                            │
│          --version 9.0.0                                             │
│          -- <app args>                                               │
└──────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────────────┐
│ MinRT.RuntimeHost (Managed .NET)                                     │
│                                                                      │
│ 1. Parse args, find package path                                     │
│ 2. Create PluginLoadContext for Aspire.Hosting.RemoteHost            │
│ 3. Load entry assembly into ALC                                      │
│ 4. Find and invoke entry point                                       │
│ 5. Host keeps running until plugin exits                             │
└──────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────────────┐
│ Aspire.Hosting.RemoteHost (in isolated ALC)                          │
│                                                                      │
│ - Full Aspire hosting capabilities                                   │
│ - Communicates with aspire.cli via gRPC/pipes                        │
│ - Manages distributed application lifecycle                          │
└──────────────────────────────────────────────────────────────────────┘
```

## RuntimeHost CLI Design

```bash
# Basic usage
dotnet MinRT.RuntimeHost.dll --package <id> --version <ver> [-- <args>]

# With explicit path
dotnet MinRT.RuntimeHost.dll --path /path/to/package/lib/net9.0 [-- <args>]

# With NuGet download (uses full NuGet.Protocol)
dotnet MinRT.RuntimeHost.dll --package Aspire.Hosting.RemoteHost \
                              --version 9.0.0 \
                              --framework net9.0 \
                              --download \
                              -- --port 5000
```

## Success Criteria (Part 2)

- [ ] RuntimeHost can load an assembly into isolated ALC
- [ ] RuntimeHost can invoke entry point with args
- [ ] ALC properly resolves transitive dependencies
- [ ] Plugin can be unloaded (collectible ALC)
- [ ] Works with Aspire.Hosting.RemoteHost package

---

## Future Considerations

- **Caching**: Check if package already exists before download
- **Parallel downloads**: Download multiple packages concurrently  
- **Version resolution**: Handle version ranges (currently expects exact)
- **Package source mapping**: Respect which packages come from which feeds
- **Credentials**: Auth for private feeds (NuGet.Configuration handles this)
- **Hot reload**: Unload and reload plugins without restarting host
- **Dependency isolation**: Handle diamond dependency conflicts between plugins

---

## Implementation Status

### Completed ✅

1. **MinRTBuilder** - Fluent builder API with:
   - `WithTargetFramework()`, `WithRuntimeIdentifier()`, `WithCacheDirectory()`
   - `AddPackageReference()`, `AddProbingPath()`
   - `UseSystemRuntime()`, `UseDownloadedRuntime()`
   - `CachePaths` class for cache directory layout

2. **MinRTContext** - Runtime context with hostfxr execution:
   - `RunWithHostFxr()` - Load hostfxr.dll, initialize, run app
   - `hostfxr_initialize_for_dotnet_command_line` + `hostfxr_run_app`

3. **HostFxrImports** - P/Invoke declarations for hostfxr API

4. **RuntimeIdentifierHelper** - Detect current OS/arch RID

5. **NuGetDownloader** - AOT-safe NuGet package download:
   - Parse NuGet v3 service index
   - Download .nupkg from PackageBaseAddress
   - Extract ZIP to cache

6. **RuntimeDownloader** - Download .NET runtime from NuGet:
   - Download `Microsoft.NETCore.App.Runtime.{rid}` package (Host package not needed!)
   - Assemble standard dotnet layout:
     - `host/fxr/{version}/hostfxr.dll`
     - `shared/Microsoft.NETCore.App/{version}/` (all other files)
   - Copy config files (`.deps.json`, `.runtimeconfig.json`)

7. **Native AOT Test Host** - MinRT.TestHost
   - Published as Native AOT executable
   - Uses local cache directory (`.minrt-cache`)
   - ✅ System runtime mode WORKS
   - ✅ **Downloaded runtime mode WORKS!**

8. **Test App** - hello.dll
   - Simple console app for testing
   - Prints "Hello, World!" and runtime version

### Test Results ✅

```
> MinRT.TestHost.exe hello.dll
DLL: D:\dev\git\MinRT\test-artifacts\hello.dll
Probing: D:\dev\git\MinRT\test-artifacts
Cache: D:\dev\git\MinRT\MinRT.TestHost\bin\.minrt-cache

Building MinRT context (downloading runtime if needed)...
Runtime: D:\dev\git\MinRT\MinRT.TestHost\bin\.minrt-cache\runtimes\10.0.0-win-x64
Version: 10.0.0
---
Hello, World!
Runtime: 10.0.0
---
Exit code: 0
```

**SUCCESS!** A Native AOT executable downloaded the .NET 10.0.0 runtime from NuGet and executed a managed assembly!

### Key Learnings from dotnet-sdk

**Source:** `D:\dev\git\dotnet-sdk\src\Tasks\Microsoft.NET.Build.Tasks\ResolveRuntimePackAssets.cs`

1. **RuntimeList.xml Manifest**: Each runtime pack contains `data/RuntimeList.xml` that lists all files with:
   - `Type` attribute: "Managed", "Native", "Resources", "PgoData"
   - `Path` attribute: Relative path within package
   - Used by SDK to enumerate and copy files

2. **Package Structure**:
   ```
   Microsoft.NETCore.App.Runtime.{rid}/
   ├── data/
   │   └── RuntimeList.xml          ← File manifest
   └── runtimes/{rid}/
       ├── native/                  ← Native files (hostfxr, coreclr, etc.)
       └── lib/net{ver}/            ← Managed assemblies + config files
   ```

3. **File Locations** (from RuntimeList.xml):
   - `hostfxr.dll` → `runtimes/win-x64/native/hostfxr.dll` (Type="Native")
   - `hostpolicy.dll` → `runtimes/win-x64/native/hostpolicy.dll` (Type="Native")
   - `coreclr.dll` → `runtimes/win-x64/native/coreclr.dll` (Type="Native")
   - `System.*.dll` → `runtimes/win-x64/lib/net10.0/` (Type="Managed")
   - Config files in lib folder: `Microsoft.NETCore.App.deps.json`, `Microsoft.NETCore.App.runtimeconfig.json`

4. **We don't need the Host package** - Everything is in the Runtime package!
   - `Microsoft.NETCore.App.Host.{rid}` contains apphost.exe templates
   - `Microsoft.NETCore.App.Runtime.{rid}` contains ALL runtime files including hostfxr

### Next Steps

1. [x] Fix RuntimeDownloader to use only Runtime package (skip Host package) ✅
2. [x] Copy `.json` config files to shared dir ✅
3. [x] Test with downloaded runtime ✅
4. [x] Update PLAN with working solution ✅
5. [ ] Implement `AddPackageReference()` - download and resolve NuGet packages
6. [ ] Implement dependency resolution for transitive packages
7. [ ] Add probing paths to assembly resolution
