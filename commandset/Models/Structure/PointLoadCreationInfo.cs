using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Models.Structure;

/// <summary>
/// Batch request for point loads. Forces are in kN, moments in kN*m.
/// </summary>
public class PointLoadCreationInfo
{
    [JsonProperty("data")]
    public List<PointLoadItem> Data { get; set; } = new List<PointLoadItem>();

    public bool Validate(out string errorMessage)
    {
        if (Data == null || Data.Count == 0)
        {
            errorMessage = "data must contain at least one point load";
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

            if (item.Point == null)
            {
                errorMessage = $"data[{i}].point is required";
                return false;
            }

            bool hasForce = item.Force != null && !item.Force.IsZero;
            bool hasMoment = item.Moment != null && !item.Moment.IsZero;

            if (!hasForce && !hasMoment)
            {
                errorMessage = $"data[{i}] has neither a force nor a moment - nothing to apply";
                return false;
            }
        }

        errorMessage = string.Empty;
        return true;
    }
}

/// <summary>
/// A single point load. The point is in millimetres, force in kN, moment in kN*m.
/// </summary>
public class PointLoadItem : LoadItemBase
{
    [JsonProperty("point")]
    public JZPoint Point { get; set; }

    /// <summary>
    /// Force vector in kN. Downward gravity load is negative Z.
    /// </summary>
    [JsonProperty("force")]
    public LoadVector Force { get; set; }

    /// <summary>
    /// Moment vector in kN*m.
    /// </summary>
    [JsonProperty("moment")]
    public LoadVector Moment { get; set; }
}
