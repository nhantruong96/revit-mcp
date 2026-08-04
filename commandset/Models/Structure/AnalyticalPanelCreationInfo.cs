using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Models.Structure;

/// <summary>
/// Batch request for analytical panels (analytical floors, walls and foundation slabs).
/// </summary>
public class AnalyticalPanelCreationInfo
{
    [JsonProperty("data")]
    public List<AnalyticalPanelItem> Data { get; set; } = new List<AnalyticalPanelItem>();

    public bool Validate(out string errorMessage)
    {
        if (Data == null || Data.Count == 0)
        {
            errorMessage = "data must contain at least one analytical panel";
            return false;
        }

        for (int i = 0; i < Data.Count; i++)
        {
            if (Data[i] == null)
            {
                errorMessage = $"data[{i}] is null";
                return false;
            }

            if (Data[i].Boundary == null || Data[i].Boundary.Count < 3)
            {
                errorMessage = $"data[{i}].boundary requires at least 3 points";
                return false;
            }
        }

        errorMessage = string.Empty;
        return true;
    }
}

/// <summary>
/// A single analytical panel. Coordinates and thickness are in millimetres.
/// </summary>
public class AnalyticalPanelItem
{
    /// <summary>
    /// Ordered, planar outline of the panel. Closing point may be omitted.
    /// </summary>
    [JsonProperty("boundary")]
    public List<JZPoint> Boundary { get; set; }

    /// <summary>
    /// Panel thickness in millimetres. Omit to keep Revit's default.
    /// </summary>
    [JsonProperty("thickness")]
    public double? Thickness { get; set; }

    /// <summary>
    /// AnalyticalStructuralRole name, e.g. StructuralRoleFloor, StructuralRoleWall,
    /// StructuralRolePanel.
    /// </summary>
    [JsonProperty("structuralRole")]
    public string StructuralRole { get; set; }

    /// <summary>
    /// AnalyzeAs name, e.g. SlabOneWay, SlabTwoWay, Mat, SlabOnGrade, Lateral.
    /// </summary>
    [JsonProperty("analyzeAs")]
    public string AnalyzeAs { get; set; }

    /// <summary>
    /// Structural material name.
    /// </summary>
    [JsonProperty("materialName")]
    public string MaterialName { get; set; }
}
