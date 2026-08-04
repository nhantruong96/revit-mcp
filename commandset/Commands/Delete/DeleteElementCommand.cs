using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Delete
{
    public class DeleteElementCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private DeleteElementEventHandler _handler => (DeleteElementEventHandler)Handler;

        public override string CommandName => "delete_element";

        public DeleteElementCommand(UIApplication uiApp)
            : base(new DeleteElementEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    // 解析数组参数
                    var elementIds = parameters?["elementIds"]?.ToObject<string[]>();
                    if (elementIds == null || elementIds.Length == 0)
                    {
                        throw new ArgumentException("The element id list cannot be empty");
                    }

                    // 设置要删除的元素ID数组
                    _handler.ElementIds = elementIds;

                    // 触发外部事件并等待完成
                    if (RaiseAndWaitForCompletion(30000))
                    {
                        if (_handler.IsSuccess)
                        {
                            return new { deleted = true, count = _handler.DeletedCount };
                        }
                        else
                        {
                            throw new Exception("Failed to delete elements");
                        }
                    }
                    else
                    {
                        throw new TimeoutException("Timed out deleting elements. Revit did not run the request within the time limit, usually because its UI was busy (a view change, a dialog, or a long redraw). The request may still complete on its own, so check the model before retrying.");
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to delete elements: {ex.Message}");
                }
            }
        }
    }
}
