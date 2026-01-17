namespace MinRT.NuGet;

/// <summary>
/// Provides runtime identifier (RID) fallback chain resolution.
/// The fallback data is generated from Microsoft.NETCore.Platforms runtime.json.
/// </summary>
public static partial class RidGraph
{
    /// <summary>
    /// Gets the full fallback chain for a runtime identifier.
    /// For example, "alpine-x64" returns ["alpine-x64", "alpine", "linux-musl-x64", "linux-musl", "linux-x64", "linux", "unix", "any"].
    /// </summary>
    /// <param name="rid">The runtime identifier to resolve.</param>
    /// <returns>Ordered list of RIDs from most specific to most general, always ending with "any".</returns>
    public static IReadOnlyList<string> GetFallbackChain(string rid)
    {
        if (string.IsNullOrEmpty(rid))
            return ["any"];

        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        CollectFallbacks(rid, result, visited);
        
        // Ensure "any" is always at the end
        if (result.Count == 0 || !result[^1].Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            if (!visited.Contains("any"))
                result.Add("any");
        }
        
        return result;
    }

    private static void CollectFallbacks(string rid, List<string> result, HashSet<string> visited)
    {
        if (!visited.Add(rid))
            return;
            
        result.Add(rid);
        
        if (GetRidImports().TryGetValue(rid, out var imports))
        {
            foreach (var import in imports)
            {
                CollectFallbacks(import, result, visited);
            }
        }
    }

    /// <summary>
    /// Checks if a specific RID is compatible with a target RID.
    /// For example, "linux-x64" is compatible with target "linux" or "unix".
    /// </summary>
    public static bool IsCompatible(string rid, string targetRid)
    {
        if (string.IsNullOrEmpty(rid) || string.IsNullOrEmpty(targetRid))
            return false;
            
        if (rid.Equals(targetRid, StringComparison.OrdinalIgnoreCase))
            return true;
            
        var chain = GetFallbackChain(rid);
        return chain.Any(r => r.Equals(targetRid, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the RID to parent imports mapping. Implemented in RidGraph.Generated.cs.
    /// </summary>
    private static partial IReadOnlyDictionary<string, string[]> GetRidImports();
}
