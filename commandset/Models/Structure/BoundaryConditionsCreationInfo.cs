using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Models.Structure;

/// <summary>
/// Batch request for boundary conditions (supports) on analytical elements.
/// </summary>
public class BoundaryConditionsCreationInfo
{
    [JsonProperty("data")]
    public List<BoundaryConditionsItem> Data { get; set; } = new List<BoundaryConditionsItem>();

    public bool Validate(out string errorMessage)
    {
        if (Data == null || Data.Count == 0)
        {
            errorMessage = "data must contain at least one boundary condition";
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

            if (!item.HostElementId.HasValue || item.HostElementId.Value <= 0)
            {
                errorMessage = $"data[{i}].hostElementId is required - a boundary condition must be hosted on an analytical element";
                return false;
            }

            string kind = (item.ConditionType ?? "point").ToLowerInvariant();
            switch (kind)
            {
                case "point":
                    if (item.Point == null)
                    {
                        errorMessage = $"data[{i}] is a point condition and requires point";
                        return false;
                    }
                    break;

                case "line":
                    if (item.StartPoint == null || item.EndPoint == null)
                    {
                        errorMessage = $"data[{i}] is a line condition and requires startPoint and endPoint";
                        return false;
                    }
                    break;

                case "area":
                    if (item.Boundary == null || item.Boundary.Count < 3)
                    {
                        errorMessage = $"data[{i}] is an area condition and requires a boundary of at least 3 points";
                        return false;
                    }
                    break;

                default:
                    errorMessage = $"data[{i}].conditionType must be 'point', 'line' or 'area'";
                    return false;
            }
        }

        errorMessage = string.Empty;
        return true;
    }
}

/// <summary>
/// A single boundary condition. Geometry is in millimetres, spring stiffness in
/// kN/m (point translation), kN*m/deg (point rotation), kN/m/m (line translation),
/// kN*m/deg/m (line rotation) and kN/m/m2 (area translation).
/// </summary>
public class BoundaryConditionsItem
{
    /// <summary>
    /// "point", "line" or "area".
    /// </summary>
    [JsonProperty("conditionType")]
    public string ConditionType { get; set; } = "point";

    /// <summary>
    /// Analytical element the support attaches to.
    /// </summary>
    [JsonProperty("hostElementId")]
    public long? HostElementId { get; set; }

    /// <summary>
    /// Location of a point support.
    /// </summary>
    [JsonProperty("point")]
    public JZPoint Point { get; set; }

    /// <summary>
    /// Start of a line support.
    /// </summary>
    [JsonProperty("startPoint")]
    public JZPoint StartPoint { get; set; }

    /// <summary>
    /// End of a line support.
    /// </summary>
    [JsonProperty("endPoint")]
    public JZPoint EndPoint { get; set; }

    /// <summary>
    /// Outline of an area support.
    /// </summary>
    [JsonProperty("boundary")]
    public List<JZPoint> Boundary { get; set; }

    /// <summary>
    /// Shorthand for the six degrees of freedom: "Fixed", "Pinned", "Roller" or "User".
    /// Anything other than "User" ignores the individual translation/rotation fields.
    /// </summary>
    [JsonProperty("preset")]
    public string Preset { get; set; } = "Fixed";

    /// <summary>TranslationRotationValue: Fixed, Release or Spring.</summary>
    [JsonProperty("xTranslation")]
    public string XTranslation { get; set; } = "Fixed";

    [JsonProperty("yTranslation")]
    public string YTranslation { get; set; } = "Fixed";

    [JsonProperty("zTranslation")]
    public string ZTranslation { get; set; } = "Fixed";

    [JsonProperty("xRotation")]
    public string XRotation { get; set; } = "Fixed";

    [JsonProperty("yRotation")]
    public string YRotation { get; set; } = "Fixed";

    [JsonProperty("zRotation")]
    public string ZRotation { get; set; } = "Fixed";

    /// <summary>Spring stiffness, only read when the matching DOF is "Spring".</summary>
    [JsonProperty("xTranslationSpring")]
    public double XTranslationSpring { get; set; }

    [JsonProperty("yTranslationSpring")]
    public double YTranslationSpring { get; set; }

    [JsonProperty("zTranslationSpring")]
    public double ZTranslationSpring { get; set; }

    [JsonProperty("xRotationSpring")]
    public double XRotationSpring { get; set; }

    [JsonProperty("yRotationSpring")]
    public double YRotationSpring { get; set; }

    [JsonProperty("zRotationSpring")]
    public double ZRotationSpring { get; set; }
}
