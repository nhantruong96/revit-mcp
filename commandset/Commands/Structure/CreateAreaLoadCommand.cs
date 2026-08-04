using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Structure;
using RevitMCPCommandSet.Services.Structure;

namespace RevitMCPCommandSet.Commands.Structure
{
    /// <summary>
    /// MCP command creating area loads.
    /// </summary>
    public class CreateAreaLoadCommand
        : StructuralAnalysisCommandBase<CreateAreaLoadEventHandler, AreaLoadCreationInfo, List<StructuralCreationResult>>
    {
        public override string CommandName => "create_area_load";

        protected override string OperationName => "Create area loads";

        public CreateAreaLoadCommand(UIApplication uiApp)
            : base(new CreateAreaLoadEventHandler(), uiApp)
        {
        }
    }
}
