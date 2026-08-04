using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Structure;
using RevitMCPCommandSet.Services.Structure;

namespace RevitMCPCommandSet.Commands.Structure
{
    /// <summary>
    /// MCP command creating point loads.
    /// </summary>
    public class CreatePointLoadCommand
        : StructuralAnalysisCommandBase<CreatePointLoadEventHandler, PointLoadCreationInfo, List<StructuralCreationResult>>
    {
        public override string CommandName => "create_point_load";

        protected override string OperationName => "Create point loads";

        public CreatePointLoadCommand(UIApplication uiApp)
            : base(new CreatePointLoadEventHandler(), uiApp)
        {
        }
    }
}
