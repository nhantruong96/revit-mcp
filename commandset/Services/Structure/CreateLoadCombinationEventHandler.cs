using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Models.Structure;

#if REVIT2023_OR_GREATER
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitMCPCommandSet.Utils;
#endif

namespace RevitMCPCommandSet.Services.Structure
{
    /// <summary>
    /// Creates load combinations from factored load cases or nested combinations.
    /// </summary>
    public class CreateLoadCombinationEventHandler
        : StructuralCreationEventHandlerBase<LoadCombinationCreationInfo>
    {
        public override string GetName() => "Create Load Combination";

#if REVIT2023_OR_GREATER
        protected override AIResult<List<StructuralCreationResult>> Run()
        {
            if (!Parameters.Validate(out string validationError))
            {
                return new AIResult<List<StructuralCreationResult>>
                {
                    Success = false,
                    Message = $"Validation failed: {validationError}",
                    Response = null
                };
            }

            return CreateBatch("Create Load Combinations", "LoadCombination", Parameters.Data, CreateCombination);
        }

        private (Element, string) CreateCombination(LoadCombinationItem item)
        {
            var type = StructuralAnalysisUtils.ParseEnum(item.CombinationType, LoadCombinationType.Combination);
            var state = StructuralAnalysisUtils.ParseEnum(item.State, LoadCombinationState.Ultimate);

            if (new FilteredElementCollector(doc)
                    .OfClass(typeof(LoadCombination))
                    .Cast<LoadCombination>()
                    .Any(c => c.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"A load combination named '{item.Name}' already exists");
            }

            var combination = LoadCombination.Create(doc, item.Name, type, state);
            var notes = new List<string>();

            var components = item.Components
                .Select(c => new LoadComponent(ResolveComponentId(c), c.Factor))
                .ToList();

            combination.SetComponents(components);

            if (item.UsageNames != null && item.UsageNames.Count > 0)
            {
                var usageIds = item.UsageNames
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => ResolveOrCreateUsage(n, notes))
                    .ToList();

                if (usageIds.Count > 0)
                    combination.SetUsageIds(usageIds);
            }

            return (combination, notes.Count == 0 ? null : string.Join("; ", notes));
        }

        /// <summary>
        /// A combination term may reference a load case or another combination, so both are
        /// searched before giving up.
        /// </summary>
        private ElementId ResolveComponentId(LoadCombinationComponent component)
        {
            if (component.LoadCaseId.HasValue && component.LoadCaseId.Value > 0)
            {
                ElementId id = StructuralAnalysisUtils.NewElementId(component.LoadCaseId.Value);
                Element element = doc.GetElement(id);

                if (element is not LoadCase && element is not LoadCombination)
                    throw new ArgumentException($"Element id {component.LoadCaseId.Value} is neither a load case nor a load combination");

                return id;
            }

            var loadCase = new FilteredElementCollector(doc)
                .OfClass(typeof(LoadCase))
                .Cast<LoadCase>()
                .FirstOrDefault(lc => lc.Name.Equals(component.Name, StringComparison.OrdinalIgnoreCase));

            if (loadCase != null)
                return loadCase.Id;

            var nested = new FilteredElementCollector(doc)
                .OfClass(typeof(LoadCombination))
                .Cast<LoadCombination>()
                .FirstOrDefault(c => c.Name.Equals(component.Name, StringComparison.OrdinalIgnoreCase));

            if (nested != null)
                return nested.Id;

            throw new ArgumentException($"No load case or load combination named '{component.Name}' exists in the model");
        }

        /// <summary>
        /// Find the load usage by name, creating it when the model does not have one.
        /// </summary>
        private ElementId ResolveOrCreateUsage(string usageName, List<string> notes)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(LoadUsage))
                .Cast<LoadUsage>()
                .FirstOrDefault(u => u.Name.Equals(usageName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
                return existing.Id;

            var created = LoadUsage.Create(doc, usageName);
            notes.Add($"created load usage '{usageName}'");

            return created.Id;
        }
#endif
    }
}
