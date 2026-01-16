// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace MinRT.Core;

/// <summary>
/// Patches apphost binaries to embed the application path.
/// </summary>
public static class AppHostPatcher
{
    // The placeholder hash that gets replaced with the app path
    // This is a well-known value used by the .NET SDK
    private const string PlaceholderHash = "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";
    private static readonly byte[] PlaceholderBytes = Encoding.UTF8.GetBytes(PlaceholderHash);

    /// <summary>
    /// Creates a patched apphost with the specified app path embedded.
    /// </summary>
    /// <param name="sourceAppHost">Path to the template apphost from the Host package</param>
    /// <param name="destinationPath">Where to write the patched apphost</param>
    /// <param name="appRelativePath">Relative path to the app DLL (e.g., "myapp.dll")</param>
    public static void PatchAppHost(string sourceAppHost, string destinationPath, string appRelativePath)
    {
        if (appRelativePath.Length >= PlaceholderHash.Length)
        {
            throw new ArgumentException(
                $"App path '{appRelativePath}' is too long. Maximum length is {PlaceholderHash.Length - 1} characters.",
                nameof(appRelativePath));
        }

        // Read the template
        var bytes = File.ReadAllBytes(sourceAppHost);

        // Find the placeholder
        int offset = FindPlaceholder(bytes);
        if (offset < 0)
        {
            throw new InvalidOperationException(
                "Could not find placeholder in apphost. The apphost may have already been patched or is invalid.");
        }

        // Create the replacement (null-terminated, same length as placeholder)
        var replacement = new byte[PlaceholderBytes.Length];
        Encoding.UTF8.GetBytes(appRelativePath, replacement);
        // Rest is already zeros (null terminator + padding)

        // Patch in place
        Array.Copy(replacement, 0, bytes, offset, replacement.Length);

        // Write the patched binary
        var destDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        File.WriteAllBytes(destinationPath, bytes);

        // Set executable permission on Unix
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destinationPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static int FindPlaceholder(byte[] bytes)
    {
        for (int i = 0; i <= bytes.Length - PlaceholderBytes.Length; i++)
        {
            bool found = true;
            for (int j = 0; j < PlaceholderBytes.Length; j++)
            {
                if (bytes[i + j] != PlaceholderBytes[j])
                {
                    found = false;
                    break;
                }
            }
            if (found)
            {
                return i;
            }
        }
        return -1;
    }
}
