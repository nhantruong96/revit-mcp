using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Structure;
using RevitMCPCommandSet.Services.Structure;

namespace RevitMCPCommandSet.Commands.Structure
{
    /// <summary>
    /// MCP command creating analytical members (analytical beams, columns, braces).
    /// </summary>
    public class CreateAnalyticalMemberCommand
        : StructuralAnalysisCommandBase<CreateAnalyticalMemberEventHandler, AnalyticalMemberCreationInfo, List<StructuralCreationResult>>
    {
        public override string CommandName => "create_analytical_member";

        protected override string OperationName => "Create analytical members";

        public CreateAnalyticalMemberCommand(UIApplication uiApp)
            : base(new CreateAnalyticalMemberEventHandler(), uiApp)
        {
        }
    }
}
