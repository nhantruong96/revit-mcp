using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Structure;
using RevitMCPCommandSet.Services.Structure;

namespace RevitMCPCommandSet.Commands.Structure
{
    /// <summary>
    /// MCP command creating load cases and, where needed, their load natures.
    /// </summary>
    public class CreateLoadCaseCommand
        : StructuralAnalysisCommandBase<CreateLoadCaseEventHandler, LoadCaseCreationInfo, List<StructuralCreationResult>>
    {
        public override string CommandName => "create_load_case";

        protected override string OperationName => "Create load cases";

        public CreateLoadCaseCommand(UIApplication uiApp)
            : base(new CreateLoadCaseEventHandler(), uiApp)
        {
        }
    }
}
