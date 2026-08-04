using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    public class ExportRoomDataCommand : ExternalEventCommandBase
    {
        private ExportRoomDataEventHandler _handler => (ExportRoomDataEventHandler)Handler;

        public override string CommandName => "export_room_data";

        public ExportRoomDataCommand(UIApplication uiApp)
            : base(new ExportRoomDataEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                // Parse optional parameters
                bool includeUnplacedRooms = parameters?["includeUnplacedRooms"]?.Value<bool>() ?? false;
                bool includeNotEnclosedRooms = parameters?["includeNotEnclosedRooms"]?.Value<bool>() ?? false;

                // Set parameters
                _handler.SetParameters(includeUnplacedRooms, includeNotEnclosedRooms);

                // Execute and wait
                if (RaiseAndWaitForCompletion(60000)) // 60 second timeout
                {
                    return _handler.ResultInfo;
                }
                else
                {
                    throw new TimeoutException("Export room data operation timed out. Revit did not run the request within the time limit, usually because its UI was busy (a view change, a dialog, or a long redraw). The request may still complete on its own, so check the model before retrying.");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to export room data: {ex.Message}");
            }
        }
    }
}
