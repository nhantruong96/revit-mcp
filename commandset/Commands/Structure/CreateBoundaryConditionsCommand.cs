using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Structure;
using RevitMCPCommandSet.Services.Structure;

namespace RevitMCPCommandSet.Commands.Structure
{
    /// <summary>
    /// MCP command creating point, line and area boundary conditions.
    /// </summary>
    public class CreateBoundaryConditionsCommand
        : StructuralAnalysisCommandBase<CreateBoundaryConditionsEventHandler, BoundaryConditionsCreationInfo, List<StructuralCreationResult>>
    {
        public override string CommandName => "create_boundary_conditions";

        protected override string OperationName => "Create boundary conditions";

        public CreateBoundaryConditionsCommand(UIApplication uiApp)
            : base(new CreateBoundaryConditionsEventHandler(), uiApp)
        {
        }
    }
}
