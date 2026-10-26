using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;

namespace IfcRebuild.Mcp;

/// <summary>
/// ifcrebuild_run: queues an Entry method and answers at once with the job, so a build of any length never meets the
/// request timeout. Parameters: method, args (strings, in order), config (options JSON), dll (a separately built copy of
/// this assembly to run instead, for development), waitMs (0-100000: wait that long after the job starts for it to end,
/// for short calls), maxChars (of the result returned, default 20000).
/// </summary>
public class RunCommand : IRevitCommand
{
    public string CommandName => "ifcrebuild_run";

    public RunCommand(UIApplication uiApp) => JobQueue.Init(uiApp);

    public object Execute(JObject parameters, string requestId)
    {
        var method = parameters["method"]?.ToString();
        if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("Missing required parameter: 'method'");
        var args = (parameters["args"] as JArray)?.Select(t => t.Type == JTokenType.Null ? "" : t.ToString()).ToArray() ?? Array.Empty<string>();
        var job = JobQueue.Enqueue(method, args, parameters["config"]?.ToString(), parameters["dll"]?.ToString());
        // wait for Revit to pick the job up: from then on Revit is busy and does not go idle until the job ends, which
        // keeps the plugin's dialog guard armed after this request has returned
        job.StartedSignal.Wait(5000);
        int wait = Math.Clamp(parameters["waitMs"]?.Value<int>() ?? 0, 0, 100000);
        if (wait > 0) job.DoneSignal.Wait(wait);
        return JobQueue.Describe(job, parameters["maxChars"]?.Value<int>() ?? 20000);
    }
}

/// <summary>
/// ifcrebuild_status: with jobId, the state, progress, deleted warnings and result of that job (waitMs 0-100000 waits for
/// it to end first); without, the running job, the queue length and the recent jobs. Never touches the Revit API, so it
/// answers while a job keeps Revit busy.
/// </summary>
public class StatusCommand : IRevitCommand
{
    public string CommandName => "ifcrebuild_status";

    public object Execute(JObject parameters, string requestId)
    {
        int maxChars = parameters["maxChars"]?.Value<int>() ?? 20000;
        var id = parameters["jobId"]?.ToString();
        if (!string.IsNullOrEmpty(id))
        {
            var job = JobQueue.Find(id) ?? throw new ArgumentException($"no job {id} in this Revit session; the result file of a finished job stays in the jobs folder");
            int wait = Math.Clamp(parameters["waitMs"]?.Value<int>() ?? 0, 0, 100000);
            if (wait > 0) job.DoneSignal.Wait(wait);
            return JobQueue.Describe(job, maxChars);
        }
        var jobs = JobQueue.List();
        jobs.Reverse();
        return new JObject
        {
            ["running"] = JobQueue.Current?.Id,
            ["queued"] = JobQueue.PendingCount,
            ["jobs"] = new JArray(jobs.Take(20).Select(j => new JObject
            {
                ["jobId"] = j.Id,
                ["method"] = j.Method,
                ["state"] = j.State,
                ["progress"] = j.ProgressText,
                ["queued"] = j.Queued.ToString("s"),
                ["finished"] = j.Finished?.ToString("s"),
            })),
        };
    }
}
