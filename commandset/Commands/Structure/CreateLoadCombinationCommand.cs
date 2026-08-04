using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Structure;
using RevitMCPCommandSet.Services.Structure;

namespace RevitMCPCommandSet.Commands.Structure
{
    /// <summary>
    /// MCP command creating load combinations.
    /// </summary>
    public class CreateLoadCombinationCommand
        : StructuralAnalysisCommandBase<CreateLoadCombinationEventHandler, LoadCombinationCreationInfo, List<StructuralCreationResult>>
    {
        public override string CommandName => "create_load_combination";

        protected override string OperationName => "Create load combinations";

        public CreateLoadCombinationCommand(UIApplication uiApp)
            : base(new CreateLoadCombinationEventHandler(), uiApp)
        {
        }
    }
}
