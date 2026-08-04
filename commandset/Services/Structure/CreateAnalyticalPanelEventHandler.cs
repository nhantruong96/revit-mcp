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
    /// Creates analytical panels - the analytical floors, walls and foundation slabs of the
    /// standalone analytical model.
    /// </summary>
    public class CreateAnalyticalPanelEventHandler
        : StructuralCreationEventHandlerBase<AnalyticalPanelCreationInfo>
    {
        public override string GetName() => "Create Analytical Panel";

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

            return CreateBatch("Create Analytical Panels", "AnalyticalPanel", Parameters.Data, CreatePanel);
        }

        private (Element, string) CreatePanel(AnalyticalPanelItem item)
        {
            CurveLoop loop = StructuralAnalysisUtils.BuildCurveLoop(item.Boundary);

            var panel = AnalyticalPanel.Create(doc, loop);
            var notes = new List<string>();

            if (item.Thickness.HasValue && item.Thickness.Value > 0)
                panel.Thickness = StructuralAnalysisUtils.MmToFeet(item.Thickness.Value);

            if (!string.IsNullOrWhiteSpace(item.StructuralRole))
            {
                var role = StructuralAnalysisUtils.ParseEnum(item.StructuralRole, AnalyticalStructuralRole.Unset);

                if (panel.IsValidStructuralRole(role))
                    panel.StructuralRole = role;
                else
                    notes.Add($"structuralRole '{item.StructuralRole}' is not valid for this panel; left as {panel.StructuralRole}");
            }

            if (!string.IsNullOrWhiteSpace(item.AnalyzeAs))
            {
                var analyzeAs = StructuralAnalysisUtils.ParseEnum(item.AnalyzeAs, AnalyzeAs.SlabTwoWay);

                if (panel.IsValidAnalyzeAs(analyzeAs))
                    panel.AnalyzeAs = analyzeAs;
                else
                    notes.Add($"analyzeAs '{item.AnalyzeAs}' is not valid for this panel; left as {panel.AnalyzeAs}");
            }

            ElementId materialId = StructuralAnalysisUtils.ResolveMaterial(doc, item.MaterialName);
            if (materialId != ElementId.InvalidElementId)
                panel.MaterialId = materialId;

            return (panel, notes.Count == 0 ? null : string.Join("; ", notes));
        }
#endif
    }
}
