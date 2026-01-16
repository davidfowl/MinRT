// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace MinRT.Core;

/// <summary>
/// P/Invoke declarations for hostfxr native hosting API.
/// See https://github.com/dotnet/runtime/blob/main/docs/design/features/native-hosting.md
/// </summary>
public static partial class HostFxrImports
{
    public unsafe struct hostfxr_initialize_parameters
    {
        public nint size;
        public char* host_path;
        public char* dotnet_root;
    }

    [LibraryImport("hostfxr", EntryPoint = "hostfxr_initialize_for_dotnet_command_line")]
    public static unsafe partial int Initialize(
        int argc,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] argv,
        ref hostfxr_initialize_parameters parameters,
        out nint host_context_handle);

    [LibraryImport("hostfxr", EntryPoint = "hostfxr_run_app")]
    public static partial int Run(nint host_context_handle);

    [LibraryImport("hostfxr", EntryPoint = "hostfxr_close")]
    public static partial int Close(nint host_context_handle);
}
