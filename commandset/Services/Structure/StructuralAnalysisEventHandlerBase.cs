using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Models.Structure;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Structure
{
    /// <summary>
    /// Shared plumbing for the structural analysis handlers: parameter hand-off, the
    /// wait handle the MCP command blocks on, and the Revit-version gate. The analytical
    /// model API these commands drive only exists from Revit 2023 onwards, so on older
    /// releases the handler reports that instead of failing to load.
    /// </summary>
    /// <typeparam name="TParameters">Deserialized request model.</typeparam>
    /// <typeparam name="TResponse">Payload returned to the MCP client.</typeparam>
    public abstract class StructuralAnalysisEventHandlerBase<TParameters, TResponse>
        : IExternalEventHandler, IWaitableExternalEventHandler
    {
        protected UIApplication uiApp;
        protected UIDocument uiDoc => uiApp.ActiveUIDocument;
        protected Document doc => uiDoc.Document;

        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        /// <summary>
        /// Request parameters, set by the command before the external event is raised.
        /// </summary>
        public TParameters Parameters { get; private set; }

        /// <summary>
        /// Execution result, read by the command once the wait handle is signalled.
        /// </summary>
        public AIResult<TResponse> Result { get; protected set; }

        public void SetParameters(TParameters parameters)
        {
            Parameters = parameters;
            _resetEvent.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            uiApp = uiapp;

            try
            {
                Result = Run();
            }
            catch (Exception ex)
            {
                Result = new AIResult<TResponse>
                {
                    Success = false,
                    Message = $"{GetName()} failed: {ex.Message}",
                    Response = default
                };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        /// <summary>
        /// Do the actual work. Runs on Revit's API thread inside the external event.
        /// </summary>
        protected abstract AIResult<TResponse> Run();

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public abstract string GetName();
    }

    /// <summary>
    /// Base for the handlers that create a batch of elements and report per-item results.
    /// </summary>
    public abstract class StructuralCreationEventHandlerBase<TParameters>
        : StructuralAnalysisEventHandlerBase<TParameters, List<StructuralCreationResult>>
    {
        /// <summary>
        /// Create every item in one transaction, isolating each behind a sub-transaction so a
        /// single bad item is reported against its own index and rolled back without taking
        /// the rest of the batch with it.
        /// </summary>
        /// <param name="transactionName">Name shown in Revit's undo stack.</param>
        /// <param name="kind">Element kind reported back to the caller.</param>
        /// <param name="items">Items to create.</param>
        /// <param name="factory">Creates one element, optionally returning a note.</param>
        protected AIResult<List<StructuralCreationResult>> CreateBatch<TItem>(
            string transactionName,
            string kind,
            IList<TItem> items,
            Func<TItem, (Element Element, string Note)> factory)
        {
            var results = new List<StructuralCreationResult>();

            using (var trans = new Transaction(doc, transactionName))
            {
                trans.Start();
                trans.SuppressRevitDialogs();

                for (int i = 0; i < items.Count; i++)
                {
                    var sub = new SubTransaction(doc);
                    sub.Start();

                    try
                    {
                        var (element, note) = factory(items[i]);

                        if (element == null)
                            throw new InvalidOperationException("Revit returned no element");

                        sub.Commit();

                        results.Add(new StructuralCreationResult
                        {
                            Index = i,
                            ElementId = element.Id.GetValue(),
                            Kind = kind,
                            Name = SafeName(element),
                            Success = true,
                            Message = note
                        });
                    }
                    catch (Exception ex)
                    {
                        sub.RollBack();

                        results.Add(new StructuralCreationResult
                        {
                            Index = i,
                            Kind = kind,
                            Success = false,
                            Message = ex.Message
                        });
                    }
                }

                trans.Commit();
            }

            int created = results.Count(r => r.Success);
            int failed = results.Count - created;

            string message = $"Created {created} of {results.Count} {kind}(s)";
            if (failed > 0)
                message += $"; {failed} failed - see the per-item messages";

            return new AIResult<List<StructuralCreationResult>>
            {
                // A batch where nothing at all could be created is a failure, not a
                // successful no-op.
                Success = created > 0,
                Message = message,
                Response = results
            };
        }

        /// <summary>
        /// Element.Name throws for element kinds that have no name; those are still worth
        /// reporting, just without one.
        /// </summary>
        private static string SafeName(Element element)
        {
            try
            {
                return element.Name;
            }
            catch
            {
                return null;
            }
        }
    }
}
