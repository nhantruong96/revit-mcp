using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Structure;
using RevitMCPCommandSet.Services.Structure;

namespace RevitMCPCommandSet.Commands.Structure
{
    /// <summary>
    /// MCP command creating analytical panels (analytical floors, walls, foundation slabs).
    /// </summary>
    public class CreateAnalyticalPanelCommand
        : StructuralAnalysisCommandBase<CreateAnalyticalPanelEventHandler, AnalyticalPanelCreationInfo, List<StructuralCreationResult>>
    {
        public override string CommandName => "create_analytical_panel";

        protected override string OperationName => "Create analytical panels";

        public CreateAnalyticalPanelCommand(UIApplication uiApp)
            : base(new CreateAnalyticalPanelEventHandler(), uiApp)
        {
        }
    }
}
