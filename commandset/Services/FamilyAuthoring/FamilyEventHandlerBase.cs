using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.FamilyAuthoring;

/// <summary>
/// Shared plumbing for the family authoring handlers: the wait handle the command blocks
/// on, and a single place where an exception turns into a reported failure instead of a
/// silent timeout.
///
/// Every handler in this group runs against a document resolved from
/// <see cref="Utils.FamilyDocumentRegistry"/>, not against the active document, so nothing
/// here touches <c>ActiveUIDocument</c>.
/// </summary>
public abstract class FamilyEventHandlerBase : IExternalEventHandler, IWaitableExternalEventHandler
{
    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    /// <summary>What the command hands back to the caller.</summary>
    public object Result { get; private set; }

    /// <summary>The JSON payload the tool was called with.</summary>
    public Newtonsoft.Json.Linq.JObject Parameters { get; set; }

    public bool WaitForCompletion(int timeoutMilliseconds = 10000)
    {
        _resetEvent.Reset();
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        try
        {
            Result = Run(app);
        }
        catch (Exception ex)
        {
            // Report the failure rather than letting the command time out: a timeout tells
            // the caller nothing about what went wrong, and invites the retry that the
            // "never re-run a timed-out command" rule exists to prevent.
            Result = new
            {
                success = false,
                error = ex.Message,
                errorType = ex.GetType().Name,
            };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    /// <summary>Does the actual work. Anything thrown here is reported as a failed result.</summary>
    protected abstract object Run(UIApplication app);

    public abstract string GetName();

    /// <summary>Reads an optional string from the payload.</summary>
    protected string Text(string key, string fallback = null)
    {
        Newtonsoft.Json.Linq.JToken token = Parameters?[key];
        if (token == null || token.Type == Newtonsoft.Json.Linq.JTokenType.Null) return fallback;
        return token.ToString();
    }

    /// <summary>Reads an optional flag from the payload.</summary>
    protected bool Flag(string key, bool fallback = false)
    {
        Newtonsoft.Json.Linq.JToken token = Parameters?[key];
        if (token == null || token.Type == Newtonsoft.Json.Linq.JTokenType.Null) return fallback;
        return token.ToObject<bool>();
    }

    /// <summary>Reads a required string, with a message that says which key is missing.</summary>
    protected string RequiredText(string key)
    {
        string value = Text(key);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"'{key}' is required.");
        return value;
    }
}
