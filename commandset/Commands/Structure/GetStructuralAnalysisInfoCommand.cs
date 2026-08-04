using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Structure;
using RevitMCPCommandSet.Services.Structure;

namespace RevitMCPCommandSet.Commands.Structure
{
    /// <summary>
    /// MCP command reading the analytical model inventory: analytical elements, hubs,
    /// supports, load cases, combinations and the load symbols available.
    /// </summary>
    public class GetStructuralAnalysisInfoCommand
        : StructuralAnalysisCommandBase<GetStructuralAnalysisInfoEventHandler, StructuralAnalysisQueryInfo, StructuralAnalysisInfo>
    {
        public override string CommandName => "get_structural_analysis_info";

        protected override string OperationName => "Read the structural analysis model";

        public GetStructuralAnalysisInfoCommand(UIApplication uiApp)
            : base(new GetStructuralAnalysisInfoEventHandler(), uiApp)
        {
        }
    }
}
