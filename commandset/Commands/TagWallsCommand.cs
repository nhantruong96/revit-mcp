using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands
{
    public class TagWallsCommand : ExternalEventCommandBase
    {
        private TagWallsEventHandler _handler => (TagWallsEventHandler)Handler;

        /// <summary>
        /// 命令名称
        /// </summary>
        public override string CommandName => "tag_walls";

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="uiApp">Revit UIApplication</param>
        public TagWallsCommand(UIApplication uiApp)
            : base(new TagWallsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                // 解析参数
                bool useLeader = false;
                if (parameters["useLeader"] != null)
                {
                    useLeader = parameters["useLeader"].ToObject<bool>();
                }

                string tagTypeId = null;
                if (parameters["tagTypeId"] != null)
                {
                    tagTypeId = parameters["tagTypeId"].ToString();
                }

                // 设置标记参数
                _handler.SetParameters(useLeader, tagTypeId);

                // 触发外部事件并等待完成
                if (RaiseAndWaitForCompletion(30000))
                {
                    return _handler.TaggingResults;
                }
                else
                {
                    throw new TimeoutException("Timed out tagging walls. Revit did not run the request within the time limit, usually because its UI was busy (a view change, a dialog, or a long redraw). The request may still complete on its own, so check the model before retrying.");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to tag walls: {ex.Message}");
            }
        }
    }
}