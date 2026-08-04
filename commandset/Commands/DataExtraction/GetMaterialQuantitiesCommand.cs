using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    public class GetMaterialQuantitiesCommand : ExternalEventCommandBase
    {
        private GetMaterialQuantitiesEventHandler _handler => (GetMaterialQuantitiesEventHandler)Handler;

        public override string CommandName => "get_material_quantities";

        public GetMaterialQuantitiesCommand(UIApplication uiApp)
            : base(new GetMaterialQuantitiesEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                // Parse parameters
                List<string> categoryFilters = parameters?["categoryFilters"]?.ToObject<List<string>>();
                bool selectedElementsOnly = parameters?["selectedElementsOnly"]?.Value<bool>() ?? false;

                // Set parameters
                _handler.SetParameters(categoryFilters, selectedElementsOnly);

                // Execute and wait
                if (RaiseAndWaitForCompletion(120000)) // 120 second timeout for large projects
                {
                    return _handler.ResultInfo;
                }
                else
                {
                    throw new TimeoutException("Material quantities calculation timed out. Revit did not run the request within the time limit, usually because its UI was busy (a view change, a dialog, or a long redraw). The request may still complete on its own, so check the model before retrying.");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get material quantities: {ex.Message}");
            }
        }
    }
}
