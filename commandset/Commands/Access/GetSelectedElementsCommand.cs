using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace RevitMCPCommandSet.Commands.Access
{
    public class GetSelectedElementsCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private GetSelectedElementsEventHandler _handler => (GetSelectedElementsEventHandler)Handler;

        public override string CommandName => "get_selected_elements";

        public GetSelectedElementsCommand(UIApplication uiApp)
            : base(new GetSelectedElementsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    // 解析参数
                    int? limit = parameters?["limit"]?.Value<int>();

                    // 设置数量限制
                    _handler.Limit = limit;

                    // 触发外部事件并等待完成
                    if (RaiseAndWaitForCompletion(30000))
                    {
                        return _handler.ResultElements;
                    }
                    else
                    {
                        throw new TimeoutException("Timed out getting selected elements. Revit did not run the request within the time limit, usually because its UI was busy (a view change, a dialog, or a long redraw). The request may still complete on its own, so check the model before retrying.");
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to get selected elements: {ex.Message}");
                }
            }
        }
    }
}
