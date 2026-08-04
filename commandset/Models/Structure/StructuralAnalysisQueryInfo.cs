using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Structure;

/// <summary>
/// Request for the structural analysis inventory. The other analysis commands need
/// element ids - load cases, hubs, load symbols - and this is where they come from.
/// </summary>
public class StructuralAnalysisQueryInfo
{
    /// <summary>
    /// Which sections to return. Empty means everything except hubs, which are
    /// numerous and only needed for analytical links.
    /// </summary>
    [JsonProperty("include")]
    public List<string> Include { get; set; }

    /// <summary>
    /// Maximum number of entries per section.
    /// </summary>
    [JsonProperty("limit")]
    public int Limit { get; set; } = 200;

    public bool Wants(string section)
    {
        if (Include == null || Include.Count == 0)
            return !section.Equals("hubs", StringComparison.OrdinalIgnoreCase);

        return Include.Any(s => s.Equals(section, StringComparison.OrdinalIgnoreCase));
    }
}
