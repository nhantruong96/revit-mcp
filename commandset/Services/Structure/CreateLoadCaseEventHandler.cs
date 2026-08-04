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
    /// Creates load cases, adding the load nature they belong to when it does not exist yet.
    /// </summary>
    public class CreateLoadCaseEventHandler
        : StructuralCreationEventHandlerBase<LoadCaseCreationInfo>
    {
        public override string GetName() => "Create Load Case";

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

            return CreateBatch("Create Load Cases", "LoadCase", Parameters.Data, CreateLoadCase);
        }

        private (Element, string) CreateLoadCase(LoadCaseItem item)
        {
            var category = StructuralAnalysisUtils.ParseEnum(item.Category, LoadCaseCategory.Dead);
            var notes = new List<string>();

            // The nature defaults to the category name, which is what Revit's own
            // structural template uses.
            string natureName = string.IsNullOrWhiteSpace(item.NatureName)
                ? category.ToString()
                : item.NatureName;

            ElementId natureId = ResolveOrCreateNature(natureName, notes);

            if (new FilteredElementCollector(doc)
                    .OfClass(typeof(LoadCase))
                    .Cast<LoadCase>()
                    .Any(lc => lc.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"A load case named '{item.Name}' already exists");
            }

            var loadCase = LoadCase.Create(doc, item.Name, natureId, category);

            if (item.Number.HasValue)
            {
                if (LoadCase.IsNumberUnique(doc, item.Number.Value))
                    loadCase.Number = item.Number.Value;
                else
                    notes.Add($"case number {item.Number.Value} is already in use; Revit assigned {loadCase.Number}");
            }

            return (loadCase, notes.Count == 0 ? null : string.Join("; ", notes));
        }

        /// <summary>
        /// Find the load nature by name, creating it when the model does not have one.
        /// </summary>
        private ElementId ResolveOrCreateNature(string natureName, List<string> notes)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(LoadNature))
                .Cast<LoadNature>()
                .FirstOrDefault(n => n.Name.Equals(natureName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
                return existing.Id;

            var created = LoadNature.Create(doc, natureName);
            notes.Add($"created load nature '{natureName}'");

            return created.Id;
        }
#endif
    }
}
