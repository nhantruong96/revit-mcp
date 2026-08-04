using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Structure;
using RevitMCPCommandSet.Services.Structure;

namespace RevitMCPCommandSet.Commands.Structure
{
    /// <summary>
    /// MCP command creating analytical links between analytical hubs.
    /// </summary>
    public class CreateAnalyticalLinkCommand
        : StructuralAnalysisCommandBase<CreateAnalyticalLinkEventHandler, AnalyticalLinkCreationInfo, List<StructuralCreationResult>>
    {
        public override string CommandName => "create_analytical_link";

        protected override string OperationName => "Create analytical links";

        public CreateAnalyticalLinkCommand(UIApplication uiApp)
            : base(new CreateAnalyticalLinkEventHandler(), uiApp)
        {
        }
    }
}
