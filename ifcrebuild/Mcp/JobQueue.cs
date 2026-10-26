using System.Reflection;
using System.Runtime.Loader;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace IfcRebuild.Mcp;

/// <summary>One call of an Entry method, run on Revit's thread while the client polls for it.</summary>
internal sealed class Job
{
    public string Id { get; init; }
    public string Method { get; init; }
    public string[] Args { get; init; }
    public string Config { get; init; }
    public string Dll { get; init; }
    public DateTime Queued { get; } = DateTime.Now;
    public DateTime? Started;
    public DateTime? Finished;
    public volatile string State = "queued";          // queued | running | done | failed
    public volatile string ProgressText = "";
    public volatile string Result;
    public volatile string ResultFile;
    public readonly List<string> Warnings = new();
    public readonly ManualResetEventSlim StartedSignal = new(false);
    public readonly ManualResetEventSlim DoneSignal = new(false);
}

/// <summary>
/// Jobs run one after another on Revit's thread through one ExternalEvent; the socket thread only reads their state, so
/// ifcrebuild_status answers while Revit is busy and no request has to stay open for the length of a build (the MCP
/// server gives up on a request after 120 s). While a job runs, warnings Revit raises in transactions nobody
/// preprocesses (Steel Connections opens its own) are deleted in FailuresProcessing and listed on the job; without that
/// a modal warning would block the job.
/// </summary>
internal static class JobQueue
{
    const int Keep = 50;
    static readonly object Sync = new();
    static readonly Queue<Job> Pending = new();
    static readonly List<Job> Recent = new();
    static ExternalEvent _event;
    static bool _hooked;
    static volatile Job _current;

    internal static Job Current => _current;
    internal static int PendingCount { get { lock (Sync) return Pending.Count; } }

    /// <summary>Called by the command constructor, which the plugin runs in an API context (ExternalEvent.Create needs one).
    /// The plugin builds the commands again each time the MCP server is switched on; event and handler are made once.</summary>
    internal static void Init(UIApplication app)
    {
        lock (Sync)
        {
            _event ??= ExternalEvent.Create(new Runner());
            if (!_hooked) { app.Application.FailuresProcessing += OnFailuresProcessing; _hooked = true; }
        }
    }

    internal static Job Enqueue(string method, string[] args, string config, string dll)
    {
        if (_event == null) throw new InvalidOperationException("IfcRebuild is not initialised: switch the MCP server off and on");
        if (!string.IsNullOrEmpty(dll) && !File.Exists(dll)) throw new FileNotFoundException("dll not found", dll);
        if (!string.IsNullOrEmpty(config) && !File.Exists(config)) throw new FileNotFoundException("config not found", config);
        var job = new Job
        {
            Id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6],
            Method = method, Args = args, Config = config, Dll = dll,
        };
        lock (Sync)
        {
            Pending.Enqueue(job);
            Recent.Add(job);
            while (Recent.Count > Keep)
            {
                var old = Recent.FirstOrDefault(j => j.State is "done" or "failed");
                if (old == null) break;
                Recent.Remove(old);
            }
        }
        var r = _event.Raise();
        if (r == ExternalEventRequest.Denied || r == ExternalEventRequest.TimedOut)
        {
            lock (Sync) if (Pending.Contains(job)) { var rest = Pending.Where(j => j != job).ToList(); Pending.Clear(); foreach (var j in rest) Pending.Enqueue(j); }
            job.Result = $"ERROR Revit refused the external event ({r})"; job.State = "failed"; job.StartedSignal.Set(); job.DoneSignal.Set();
        }
        return job;
    }

    internal static Job Find(string id) { lock (Sync) return Recent.FirstOrDefault(j => j.Id == id); }

    internal static List<Job> List() { lock (Sync) return Recent.ToList(); }

    static void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
    {
        var job = _current;
        if (job == null) return;
        var fa = e.GetFailuresAccessor();
        foreach (var m in fa.GetFailureMessages().Where(m => m.GetSeverity() == FailureSeverity.Warning).ToList())
        {
            lock (job.Warnings) job.Warnings.Add(m.GetDescriptionText());
            fa.DeleteWarning(m);
        }
    }

    sealed class Runner : IExternalEventHandler
    {
        public string GetName() => "IfcRebuild job";

        public void Execute(UIApplication app)
        {
            // every queued job in one go: Revit does not go idle in between, so the plugin's dialog guard stays armed
            while (true)
            {
                Job job;
                lock (Sync) { if (Pending.Count == 0) return; job = Pending.Dequeue(); }
                Run(app, job);
            }
        }
    }

    static void Run(UIApplication app, Job job)
    {
        _current = job; job.Started = DateTime.Now; job.State = "running"; job.StartedSignal.Set();
        string result; bool ok = false; RebuildConfig opts = null;
        try
        {
            opts = RebuildConfig.Use(job.Config);
            result = Invoke(app, job);
            ok = true;
        }
        catch (Exception ex)
        {
            var x = ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex;
            result = "ERROR " + x;
        }
        finally { Progress.Sink = null; _current = null; }
        var warn = Warnings(job);
        try
        {
            var dir = (opts ?? RebuildConfig.Load(null)).JobDir();
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, job.Id + ".txt");
            File.WriteAllText(file, $"{job.Method}({string.Join(", ", job.Args)})\nstate {(ok ? "done" : "failed")} | started {job.Started:yyyy-MM-dd HH:mm:ss} | finished {DateTime.Now:HH:mm:ss}\n\n{result}\n"
                                    + (warn.Count > 0 ? "\nwarnings deleted:\n" + string.Join("\n", warn) + "\n" : ""));
            job.ResultFile = file;
        }
        catch { }
        job.Result = result;
        job.Finished = DateTime.Now;
        job.State = ok ? "done" : "failed";
        job.DoneSignal.Set();
    }

    /// <summary>Calls IfcRebuild.Entry.&lt;method&gt; of this assembly, or of a fresh copy of the DLL the job names.</summary>
    static string Invoke(UIApplication app, Job job)
    {
        var asm = typeof(Entry).Assembly;
        if (!string.IsNullOrEmpty(job.Dll))
        {
            // a fresh copy of a separately built builder DLL in a load context of its own: changed code runs without
            // restarting Revit. RevitAPI and the other shared assemblies resolve from the default context.
            var copy = Path.Combine(Path.GetTempPath(), "IfcRebuild_" + DateTime.Now.Ticks + ".dll");
            File.Copy(job.Dll, copy);
            asm = new AssemblyLoadContext("IfcRebuild_" + DateTime.Now.Ticks).LoadFromAssemblyPath(copy);
            // the copy has statics of its own: hand it the config
            asm.GetType("IfcRebuild.RebuildConfig")?.GetMethod("Use")?.Invoke(null, new object[] { job.Config ?? "" });
        }
        asm.GetType("IfcRebuild.Progress")?.GetField("Sink")?.SetValue(null, (Action<string>)(s => job.ProgressText = s));
        var entry = asm.GetType("IfcRebuild.Entry") ?? throw new InvalidOperationException($"{asm.GetName().Name} has no class IfcRebuild.Entry");
        var m = entry.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(x => x.Name == job.Method)
                ?? throw new ArgumentException($"no method '{job.Method}'; run the method Methods for the list");
        var ps = m.GetParameters();
        if (job.Args.Length > ps.Length - 1)
            throw new ArgumentException($"{job.Method} takes {ps.Length - 1} arguments ({string.Join(", ", ps.Skip(1).Select(p => p.Name))}), got {job.Args.Length}");
        var args = new object[ps.Length];
        args[0] = ps[0].ParameterType == typeof(UIApplication) ? app
            : app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("no active document: open one first (OpenActive or NewEmpty)");
        for (int i = 1; i < ps.Length; i++) args[i] = i - 1 < job.Args.Length ? job.Args[i - 1] : "";
        return m.Invoke(null, args)?.ToString() ?? "";
    }

    static List<string> Warnings(Job j)
    {
        lock (j.Warnings) return j.Warnings.GroupBy(w => w).Select(g => $"{g.Count()}x {g.Key}").ToList();
    }

    internal static JObject Describe(Job j, int maxChars)
    {
        var o = new JObject
        {
            ["jobId"] = j.Id,
            ["method"] = j.Method,
            ["args"] = new JArray(j.Args),
            ["state"] = j.State,
            ["queued"] = j.Queued.ToString("s"),
            ["started"] = j.Started?.ToString("s"),
            ["finished"] = j.Finished?.ToString("s"),
            ["elapsedSeconds"] = j.Started == null ? 0 : Math.Round(((j.Finished ?? DateTime.Now) - j.Started.Value).TotalSeconds, 1),
            ["progress"] = j.ProgressText,
        };
        var w = Warnings(j);
        if (w.Count > 0) o["warningsDeleted"] = new JArray(w);
        var res = j.Result;
        if (res != null) o["result"] = res.Length <= maxChars ? res : res[..maxChars] + $"\n... ({res.Length - maxChars} more characters in resultFile)";
        if (j.ResultFile != null) o["resultFile"] = j.ResultFile;
        return o;
    }
}
