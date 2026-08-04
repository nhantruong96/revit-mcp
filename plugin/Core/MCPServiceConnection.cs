using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace revit_mcp_plugin.Core
{
    [Transaction(TransactionMode.Manual)]
    public class MCPServiceConnection : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // 获取socket服务
                // Obtain socket service.
                SocketService service = SocketService.Instance;
                bool wasRunning = service.IsRunning;

                if (wasRunning)
                {
                    service.Stop();
                }
                else
                {
                    service.Initialize(commandData.Application);
                    service.Start();
                }

                // Read the state back instead of assuming the toggle worked. Start() swallows
                // its exceptions and leaves the service stopped, so a port already taken by
                // another Revit session used to pop up "Open Server" while nothing listened.
                bool isRunning = service.IsRunning;
                Application.SetServiceState(isRunning, service.Port);

                // The button now carries the state, so the old confirmation dialog on every
                // click was just noise. Only speak up when the outcome is not what was asked.
                if (!wasRunning && !isRunning)
                {
                    TaskDialog.Show("Revit MCP",
                        $"The MCP server could not start.\n\nPort {service.Port} is most likely already in use - "
                        + "by another Revit session running this add-in, or another application. "
                        + "Close it and try again.");
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
