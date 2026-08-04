using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Models.Structure;

/// <summary>
/// Batch request for area loads. Forces are in kN/m2.
/// </summary>
public class AreaLoadCreationInfo
{
    [JsonProperty("data")]
    public List<AreaLoadItem> Data { get; set; } = new List<AreaLoadItem>();

    public bool Validate(out string errorMessage)
    {
        if (Data == null || Data.Count == 0)
        {
            errorMessage = "data must contain at least one area load";
            return false;
        }

        for (int i = 0; i < Data.Count; i++)
        {
            var item = Data[i];

            if (item == null)
            {
                errorMessage = $"data[{i}] is null";
                return false;
            }

            if (item.Boundary == null || item.Boundary.Count < 3)
            {
                errorMessage = $"data[{i}].boundary requires at least 3 points";
                return false;
            }

            if (item.Force == null || item.Force.IsZero)
            {
                errorMessage = $"data[{i}].force is required and must be non-zero";
                return false;
            }
        }

        errorMessage = string.Empty;
        return true;
    }
}

/// <summary>
/// A single area load. Boundary points are in millimetres, force in kN/m2.
/// </summary>
public class AreaLoadItem : LoadItemBase
{
    /// <summary>
    /// Ordered, planar outline of the loaded area. Closing point may be omitted.
    /// </summary>
    [JsonProperty("boundary")]
    public List<JZPoint> Boundary { get; set; }

    /// <summary>
    /// Force per unit area in kN/m2. Downward gravity load is negative Z.
    /// </summary>
    [JsonProperty("force")]
    public LoadVector Force { get; set; }

    /// <summary>
    /// Project the load onto the host instead of applying it over the true area.
    /// </summary>
    [JsonProperty("isProjected")]
    public bool IsProjected { get; set; }
}
