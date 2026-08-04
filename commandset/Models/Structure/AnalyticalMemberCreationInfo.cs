using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Models.Structure;

/// <summary>
/// Batch request for analytical members (analytical beams, columns and braces).
/// </summary>
public class AnalyticalMemberCreationInfo
{
    [JsonProperty("data")]
    public List<AnalyticalMemberItem> Data { get; set; } = new List<AnalyticalMemberItem>();

    public bool Validate(out string errorMessage)
    {
        if (Data == null || Data.Count == 0)
        {
            errorMessage = "data must contain at least one analytical member";
            return false;
        }

        for (int i = 0; i < Data.Count; i++)
        {
            if (Data[i] == null)
            {
                errorMessage = $"data[{i}] is null";
                return false;
            }

            if (Data[i].StartPoint == null || Data[i].EndPoint == null)
            {
                errorMessage = $"data[{i}] requires both startPoint and endPoint";
                return false;
            }
        }

        errorMessage = string.Empty;
        return true;
    }
}

/// <summary>
/// A single analytical member. Coordinates are in millimetres.
/// </summary>
public class AnalyticalMemberItem
{
    [JsonProperty("startPoint")]
    public JZPoint StartPoint { get; set; }

    [JsonProperty("endPoint")]
    public JZPoint EndPoint { get; set; }

    /// <summary>
    /// AnalyticalStructuralRole name, e.g. StructuralRoleBeam, StructuralRoleColumn,
    /// StructuralRoleMember (brace), StructuralRoleGirder.
    /// </summary>
    [JsonProperty("structuralRole")]
    public string StructuralRole { get; set; }

    /// <summary>
    /// AnalyzeAs name, e.g. Gravity, Lateral, GravityLateral, NotForAnalysis.
    /// </summary>
    [JsonProperty("analyzeAs")]
    public string AnalyzeAs { get; set; }

    /// <summary>
    /// Element id of the family symbol supplying the cross section.
    /// </summary>
    [JsonProperty("sectionTypeId")]
    public long? SectionTypeId { get; set; }

    /// <summary>
    /// Family symbol name supplying the cross section, used when no id is given.
    /// </summary>
    [JsonProperty("sectionTypeName")]
    public string SectionTypeName { get; set; }

    /// <summary>
    /// Structural material name.
    /// </summary>
    [JsonProperty("materialName")]
    public string MaterialName { get; set; }

    /// <summary>
    /// Cross-section rotation in degrees.
    /// </summary>
    [JsonProperty("crossSectionRotation")]
    public double CrossSectionRotation { get; set; }
}
