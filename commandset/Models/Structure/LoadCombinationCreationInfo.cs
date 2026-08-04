using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Structure;

/// <summary>
/// Batch request for load combinations.
/// </summary>
public class LoadCombinationCreationInfo
{
    [JsonProperty("data")]
    public List<LoadCombinationItem> Data { get; set; } = new List<LoadCombinationItem>();

    public bool Validate(out string errorMessage)
    {
        if (Data == null || Data.Count == 0)
        {
            errorMessage = "data must contain at least one load combination";
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

            if (string.IsNullOrWhiteSpace(item.Name))
            {
                errorMessage = $"data[{i}].name is required";
                return false;
            }

            if (item.Components == null || item.Components.Count == 0)
            {
                errorMessage = $"data[{i}].components must list at least one load case or combination";
                return false;
            }

            for (int j = 0; j < item.Components.Count; j++)
            {
                var component = item.Components[j];

                if (component == null)
                {
                    errorMessage = $"data[{i}].components[{j}] is null";
                    return false;
                }

                if (!component.LoadCaseId.HasValue && string.IsNullOrWhiteSpace(component.Name))
                {
                    errorMessage = $"data[{i}].components[{j}] requires loadCaseId or name";
                    return false;
                }
            }
        }

        errorMessage = string.Empty;
        return true;
    }
}

/// <summary>
/// A single load combination.
/// </summary>
public class LoadCombinationItem
{
    /// <summary>
    /// Combination name, e.g. "1.2DL + 1.6LL".
    /// </summary>
    [JsonProperty("name")]
    public string Name { get; set; }

    /// <summary>
    /// LoadCombinationType: Combination (factored sum) or Envelope.
    /// </summary>
    [JsonProperty("combinationType")]
    public string CombinationType { get; set; } = "Combination";

    /// <summary>
    /// LoadCombinationState: Ultimate, Serviceability, ServiceabilityQuasiPermanent,
    /// ServiceabilityFrequent, ServiceabilityRare, Accidental or Special.
    /// </summary>
    [JsonProperty("state")]
    public string State { get; set; } = "Ultimate";

    /// <summary>
    /// Cases or nested combinations and their factors.
    /// </summary>
    [JsonProperty("components")]
    public List<LoadCombinationComponent> Components { get; set; }

    /// <summary>
    /// Load usage names to attach. Usages that do not exist are created.
    /// </summary>
    [JsonProperty("usageNames")]
    public List<string> UsageNames { get; set; }
}

/// <summary>
/// One factored term of a load combination.
/// </summary>
public class LoadCombinationComponent
{
    /// <summary>
    /// Element id of the load case or nested combination.
    /// </summary>
    [JsonProperty("loadCaseId")]
    public long? LoadCaseId { get; set; }

    /// <summary>
    /// Name of the load case or nested combination, used when no id is given.
    /// </summary>
    [JsonProperty("name")]
    public string Name { get; set; }

    /// <summary>
    /// Multiplier applied to the case, e.g. 1.2.
    /// </summary>
    [JsonProperty("factor")]
    public double Factor { get; set; } = 1.0;
}
