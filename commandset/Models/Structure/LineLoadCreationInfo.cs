using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Models.Structure;

/// <summary>
/// Batch request for line loads. Forces are in kN/m, moments in kN*m/m.
/// </summary>
public class LineLoadCreationInfo
{
    [JsonProperty("data")]
    public List<LineLoadItem> Data { get; set; } = new List<LineLoadItem>();

    public bool Validate(out string errorMessage)
    {
        if (Data == null || Data.Count == 0)
        {
            errorMessage = "data must contain at least one line load";
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

            if (item.StartPoint == null || item.EndPoint == null)
            {
                errorMessage = $"data[{i}] requires both startPoint and endPoint";
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
/// A single line load. Points are in millimetres, force in kN/m, moment in kN*m/m.
/// Supplying forceEnd or momentEnd makes the load trapezoidal.
/// </summary>
public class LineLoadItem : LoadItemBase
{
    [JsonProperty("startPoint")]
    public JZPoint StartPoint { get; set; }

    [JsonProperty("endPoint")]
    public JZPoint EndPoint { get; set; }

    /// <summary>
    /// Force per unit length at the start, in kN/m. Downward gravity load is negative Z.
    /// </summary>
    [JsonProperty("force")]
    public LoadVector Force { get; set; }

    /// <summary>
    /// Force per unit length at the end, in kN/m. Omit for a uniform load.
    /// </summary>
    [JsonProperty("forceEnd")]
    public LoadVector ForceEnd { get; set; }

    /// <summary>
    /// Moment per unit length at the start, in kN*m/m.
    /// </summary>
    [JsonProperty("moment")]
    public LoadVector Moment { get; set; }

    /// <summary>
    /// Moment per unit length at the end, in kN*m/m. Omit for a uniform load.
    /// </summary>
    [JsonProperty("momentEnd")]
    public LoadVector MomentEnd { get; set; }

    /// <summary>
    /// Project the load onto the host instead of applying it along the true length.
    /// </summary>
    [JsonProperty("isProjected")]
    public bool IsProjected { get; set; }
}
