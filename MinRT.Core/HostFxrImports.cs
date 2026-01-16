// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace MinRT.Core;

/// <summary>
/// P/Invoke declarations for hostfxr native hosting API.
/// See https://github.com/dotnet/runtime/blob/main/docs/design/features/native-hosting.md
/// 
/// Note: hostfxr uses char_t* which is:
/// - wchar_t* (UTF-16) on Windows
/// - char* (UTF-8) on Unix
/// </summary>
public static partial class HostFxrImports
{
    public struct hostfxr_initialize_parameters
    {
        public nint size;
        public nint host_path;    // char_t* - use IntPtr for cross-platform
        public nint dotnet_root;  // char_t* - use IntPtr for cross-platform
    }

    // Use StringMarshalling.Custom to handle UTF-16 on Windows, UTF-8 on Unix
    // We use separate methods and call the right one at runtime

    [LibraryImport("hostfxr", EntryPoint = "hostfxr_initialize_for_dotnet_command_line", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int InitializeUtf16(
        int argc,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] argv,
        ref hostfxr_initialize_parameters parameters,
        out nint host_context_handle);

    [LibraryImport("hostfxr", EntryPoint = "hostfxr_initialize_for_dotnet_command_line", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int InitializeUtf8(
        int argc,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPUTF8Str)] string[] argv,
        ref hostfxr_initialize_parameters parameters,
        out nint host_context_handle);

    public static int Initialize(
        int argc,
        string[] argv,
        ref hostfxr_initialize_parameters parameters,
        out nint host_context_handle)
    {
        if (OperatingSystem.IsWindows())
        {
            return InitializeUtf16(argc, argv, ref parameters, out host_context_handle);
        }
        else
        {
            return InitializeUtf8(argc, argv, ref parameters, out host_context_handle);
        }
    }

    [LibraryImport("hostfxr", EntryPoint = "hostfxr_run_app")]
    public static partial int Run(nint host_context_handle);

    [LibraryImport("hostfxr", EntryPoint = "hostfxr_close")]
    public static partial int Close(nint host_context_handle);
}
