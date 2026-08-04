using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Models.Structure;

/// <summary>
/// Batch request for analytical links, the rigid or released connectors that join two
/// analytical hubs.
/// </summary>
public class AnalyticalLinkCreationInfo
{
    [JsonProperty("data")]
    public List<AnalyticalLinkItem> Data { get; set; } = new List<AnalyticalLinkItem>();

    public bool Validate(out string errorMessage)
    {
        if (Data == null || Data.Count == 0)
        {
            errorMessage = "data must contain at least one analytical link";
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

            bool hasStart = item.StartHubId.HasValue || item.StartPoint != null;
            bool hasEnd = item.EndHubId.HasValue || item.EndPoint != null;

            if (!hasStart || !hasEnd)
            {
                errorMessage =
                    $"data[{i}] requires a start and an end, given either as startHubId/endHubId or as startPoint/endPoint";
                return false;
            }
        }

        errorMessage = string.Empty;
        return true;
    }
}

/// <summary>
/// A single analytical link. Hubs can be addressed by element id, or located by the
/// point they sit at (in millimetres).
/// </summary>
public class AnalyticalLinkItem
{
    /// <summary>
    /// Element id of the hub at the link start.
    /// </summary>
    [JsonProperty("startHubId")]
    public long? StartHubId { get; set; }

    /// <summary>
    /// Element id of the hub at the link end.
    /// </summary>
    [JsonProperty("endHubId")]
    public long? EndHubId { get; set; }

    /// <summary>
    /// Location of the start hub, used when no id is given.
    /// </summary>
    [JsonProperty("startPoint")]
    public JZPoint StartPoint { get; set; }

    /// <summary>
    /// Location of the end hub, used when no id is given.
    /// </summary>
    [JsonProperty("endPoint")]
    public JZPoint EndPoint { get; set; }

    /// <summary>
    /// How far from the given point a hub may sit and still match, in millimetres.
    /// </summary>
    [JsonProperty("searchToleranceMm")]
    public double SearchToleranceMm { get; set; } = 25.0;

    /// <summary>
    /// Element id of the AnalyticalLinkType to use.
    /// </summary>
    [JsonProperty("linkTypeId")]
    public long? LinkTypeId { get; set; }

    /// <summary>
    /// AnalyticalLinkType name, used when no id is given. Defaults to the first one
    /// in the model.
    /// </summary>
    [JsonProperty("linkTypeName")]
    public string LinkTypeName { get; set; }
}
