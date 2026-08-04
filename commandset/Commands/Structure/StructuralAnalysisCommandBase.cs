using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Structure;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Structure
{
    /// <summary>
    /// Shared MCP command shell for the structural analysis handlers: deserialize the
    /// request, hand it to the handler, and wait for Revit's API thread to finish.
    /// Request validation lives in the handler so it runs the same way regardless of
    /// how the handler is invoked.
    /// </summary>
    /// <typeparam name="THandler">Handler that does the work.</typeparam>
    /// <typeparam name="TParameters">Deserialized request model.</typeparam>
    /// <typeparam name="TResponse">Payload returned to the MCP client.</typeparam>
    public abstract class StructuralAnalysisCommandBase<THandler, TParameters, TResponse>
        : ExternalEventCommandBase
        where THandler : StructuralAnalysisEventHandlerBase<TParameters, TResponse>
    {
        private THandler TypedHandler => (THandler)Handler;

        protected StructuralAnalysisCommandBase(THandler handler, UIApplication uiApp)
            : base(handler, uiApp)
        {
        }

        /// <summary>
        /// How long to wait for Revit to run the request. Batches can be large, so this is
        /// generous; a busy Revit UI is the usual reason for hitting it.
        /// </summary>
        protected virtual int TimeoutMilliseconds => 30000;

        /// <summary>
        /// Human-readable name of the operation, used in error messages.
        /// </summary>
        protected abstract string OperationName { get; }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                TParameters data = parameters.ToObject<TParameters>();

                if (data == null)
                    throw new ArgumentNullException(nameof(parameters), $"{OperationName} request is empty");

                TypedHandler.SetParameters(data);

                if (RaiseAndWaitForCompletion(TimeoutMilliseconds))
                    return TypedHandler.Result;

                throw new TimeoutException(
                    $"{OperationName} timed out. Revit did not run the request within the time limit, " +
                    "usually because its UI was busy (a view change, a dialog, or a long redraw). " +
                    "The request may still complete on its own, so check the model before retrying.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to {OperationName.ToLowerInvariant()}: {ex.Message}");
            }
        }
    }
}
