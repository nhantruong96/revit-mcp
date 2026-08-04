using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Structure;

/// <summary>
/// Per-item outcome of a structural analysis creation command. Every analysis command
/// creates a batch, and one bad item must not hide the ones that succeeded, so each
/// entry carries its own success flag and message.
/// </summary>
public class StructuralCreationResult
{
    /// <summary>
    /// Zero-based position of the item in the request, so a caller can match a failure
    /// back to what it sent.
    /// </summary>
    [JsonProperty("index")]
    public int Index { get; set; }

    /// <summary>
    /// Element id of the created element, or -1 when the item failed.
    /// </summary>
    [JsonProperty("elementId")]
    public long ElementId { get; set; } = -1;

    /// <summary>
    /// What was created ("AnalyticalMember", "PointLoad", ...).
    /// </summary>
    [JsonProperty("kind")]
    public string Kind { get; set; }

    /// <summary>
    /// Element name, when the element type carries a meaningful one.
    /// </summary>
    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Failure reason, or a note about anything that was adjusted.
    /// </summary>
    [JsonProperty("message")]
    public string Message { get; set; }
}

/// <summary>
/// A force or moment vector. Units depend on the load: kN and kN*m for point loads,
/// kN/m and kN*m/m for line loads, kN/m2 for area loads.
/// </summary>
public class LoadVector
{
    [JsonProperty("x")] public double X { get; set; }

    [JsonProperty("y")] public double Y { get; set; }

    [JsonProperty("z")] public double Z { get; set; }

    public bool IsZero => X == 0 && Y == 0 && Z == 0;
}

/// <summary>
/// Fields every load shares: which case it belongs to, which symbol it uses, and how
/// its vectors are oriented.
/// </summary>
public abstract class LoadItemBase
{
    /// <summary>
    /// Element id of the load case. Takes precedence over <see cref="LoadCaseName" />.
    /// </summary>
    [JsonProperty("loadCaseId")]
    public long? LoadCaseId { get; set; }

    /// <summary>
    /// Load case name, resolved against the model when no id is given.
    /// </summary>
    [JsonProperty("loadCaseName")]
    public string LoadCaseName { get; set; }

    /// <summary>
    /// Load symbol name. Defaults to the first symbol of the matching kind in the model.
    /// </summary>
    [JsonProperty("loadTypeName")]
    public string LoadTypeName { get; set; }

    /// <summary>
    /// Analytical element the load is hosted on. Omit for a free-standing load.
    /// </summary>
    [JsonProperty("hostElementId")]
    public long? HostElementId { get; set; }

    /// <summary>
    /// LoadOrientTo value: Project, WorkPlane or HostLocalCoordinateSystem.
    /// </summary>
    [JsonProperty("orientTo")]
    public string OrientTo { get; set; }

    /// <summary>
    /// Mark the load as a reaction rather than an applied load.
    /// </summary>
    [JsonProperty("isReaction")]
    public bool IsReaction { get; set; }
}
