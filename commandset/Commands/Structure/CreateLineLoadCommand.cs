using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Structure;
using RevitMCPCommandSet.Services.Structure;

namespace RevitMCPCommandSet.Commands.Structure
{
    /// <summary>
    /// MCP command creating line loads.
    /// </summary>
    public class CreateLineLoadCommand
        : StructuralAnalysisCommandBase<CreateLineLoadEventHandler, LineLoadCreationInfo, List<StructuralCreationResult>>
    {
        public override string CommandName => "create_line_load";

        protected override string OperationName => "Create line loads";

        public CreateLineLoadCommand(UIApplication uiApp)
            : base(new CreateLineLoadEventHandler(), uiApp)
        {
        }
    }
}
