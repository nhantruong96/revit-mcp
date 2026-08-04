using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Structure;

/// <summary>
/// Batch request for load cases. Load natures are created on demand when the requested
/// nature does not exist yet.
/// </summary>
public class LoadCaseCreationInfo
{
    [JsonProperty("data")]
    public List<LoadCaseItem> Data { get; set; } = new List<LoadCaseItem>();

    public bool Validate(out string errorMessage)
    {
        if (Data == null || Data.Count == 0)
        {
            errorMessage = "data must contain at least one load case";
            return false;
        }

        for (int i = 0; i < Data.Count; i++)
        {
            if (Data[i] == null)
            {
                errorMessage = $"data[{i}] is null";
                return false;
            }

            if (string.IsNullOrWhiteSpace(Data[i].Name))
            {
                errorMessage = $"data[{i}].name is required";
                return false;
            }
        }

        errorMessage = string.Empty;
        return true;
    }
}

/// <summary>
/// A single load case.
/// </summary>
public class LoadCaseItem
{
    /// <summary>
    /// Load case name, e.g. "DL1" or "Live - Office".
    /// </summary>
    [JsonProperty("name")]
    public string Name { get; set; }

    /// <summary>
    /// LoadCaseCategory name: Dead, Live, Wind, Snow, RoofLive, Accidental,
    /// Temperature or Seismic.
    /// </summary>
    [JsonProperty("category")]
    public string Category { get; set; } = "Dead";

    /// <summary>
    /// Load nature name. Defaults to the category name; created if it does not exist.
    /// </summary>
    [JsonProperty("natureName")]
    public string NatureName { get; set; }

    /// <summary>
    /// Case number. Left to Revit when omitted; a number already in use is skipped
    /// rather than failing the item.
    /// </summary>
    [JsonProperty("number")]
    public int? Number { get; set; }
}
