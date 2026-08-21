using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    /// <summary>
    /// Connection test. Answers with what it can see of the Revit session instead of
    /// showing a dialog: a modal dialog blocks the external event until someone clicks
    /// it, which turned the simplest tool in the set into a guaranteed timeout whenever
    /// nobody was watching the Revit window.
    /// </summary>
    public class SayHelloEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public string Message { get; set; } = "Hello MCP!";

        public object Result { get; private set; }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var revit = app.Application;
                var uiDoc = app.ActiveUIDocument;

                Result = new
                {
                    success = true,
                    message = Message,
                    revitVersion = revit.VersionName,
                    revitBuild = revit.VersionBuild,
                    activeDocument = uiDoc?.Document?.Title,
                    activeView = uiDoc?.ActiveView?.Name,
                };
            }
            catch (Exception ex)
            {
                Result = new { success = false, message = Message, error = ex.Message };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        public string GetName()
        {
            return "Say Hello";
        }
    }
}
