using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RevitMCPCommandSet.Utils;

/// <summary>
/// Swallows the modal warning dialogs Revit raises during a transaction - for example
/// "Room is not in a properly enclosed region" - and records them instead.
///
/// This matters more for MCP than for an interactive add-in: a modal dialog blocks the
/// external event queue with nobody there to click it, so the calling tool waits out its
/// whole timeout and reports failure for an operation that actually succeeded.
///
/// Suppressed messages are kept so the tool can report them. Hiding a dialog and then
/// staying silent about it would just move the problem somewhere harder to see.
/// </summary>
public class McpFailurePreprocessor : IFailuresPreprocessor
{
    private readonly List<string> _messages = new List<string>();

    /// <summary>Warnings that were suppressed, and errors that were resolved or rolled back.</summary>
    public IReadOnlyList<string> Messages => _messages;

    /// <summary>True when an unresolvable error forced the transaction to roll back.</summary>
    public bool RolledBack { get; private set; }

    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        IList<FailureMessageAccessor> failures = failuresAccessor.GetFailureMessages();
        if (failures.Count == 0) return FailureProcessingResult.Continue;

        bool mustRollBack = false;

        foreach (FailureMessageAccessor failure in failures)
        {
            FailureSeverity severity = failure.GetSeverity();
            string description = failure.GetDescriptionText();

            if (severity == FailureSeverity.Warning)
            {
                _messages.Add($"Revit warning (suppressed): {description}");
                failuresAccessor.DeleteWarning(failure);
            }
            else if (severity == FailureSeverity.Error)
            {
                // Only resolve when Revit actually offers a resolution; calling
                // ResolveFailure on an unresolvable error throws.
                if (failure.HasResolutions())
                {
                    _messages.Add($"Revit error (auto-resolved): {description}");
                    failuresAccessor.ResolveFailure(failure);
                }
                else
                {
                    _messages.Add($"Revit error (not resolvable, changes rolled back): {description}");
                    mustRollBack = true;
                }
            }
        }

        if (mustRollBack)
        {
            RolledBack = true;
            return FailureProcessingResult.ProceedWithRollBack;
        }

        return FailureProcessingResult.ProceedWithCommit;
    }
}

public static class TransactionFailureExtensions
{
    /// <summary>
    /// Route this transaction's failures through <see cref="McpFailurePreprocessor"/> so no
    /// modal dialog can stall the external event. Call after Start(); keep the returned
    /// object and append its Messages to whatever the tool reports back.
    /// </summary>
    public static McpFailurePreprocessor SuppressRevitDialogs(this Transaction transaction)
    {
        var preprocessor = new McpFailurePreprocessor();

        FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(preprocessor);
        options.SetClearAfterRollback(true);
        // Mini-warnings are deferred UI; there is no user to read them here.
        options.SetDelayedMiniWarnings(false);
        // Without this Revit may still insist on showing a modal dialog from an
        // external event, which is exactly what we are trying to avoid.
        options.SetForcedModalHandling(false);
        transaction.SetFailureHandlingOptions(options);

        return preprocessor;
    }
}

/// <summary>
/// Catches modal dialogs raised outside a transaction, such as the
/// "There is no open view that shows any of the highlighted elements" prompt that
/// UIDocument.ShowElements raises. Deliberately scoped to a using block around the call
/// that can trigger one, so the user's own dialogs are never hijacked while they work.
/// </summary>
public sealed class DialogSuppressionScope : IDisposable
{
    private readonly UIApplication _uiApp;
    private readonly List<string> _dismissed = new List<string>();
    private bool _disposed;

    public DialogSuppressionScope(UIApplication uiApp)
    {
        _uiApp = uiApp;
        if (_uiApp != null) _uiApp.DialogBoxShowing += OnDialogBoxShowing;
    }

    /// <summary>Dialogs that were dismissed while the scope was open.</summary>
    public IReadOnlyList<string> Dismissed => _dismissed;

    private void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
    {
        string detail = e is TaskDialogShowingEventArgs taskDialog && !string.IsNullOrEmpty(taskDialog.Message)
            ? taskDialog.Message
            : e.DialogId;

        _dismissed.Add(detail ?? "(unnamed dialog)");

        // Cancel rather than confirm. The prompt this exists for warns that searching
        // closed views "could take a long time", and a long freeze here would blow the
        // command's timeout for no benefit.
        try
        {
            e.OverrideResult((int)TaskDialogResult.Cancel);
        }
        catch
        {
            // Not every dialog accepts every result; leaving it alone is better than throwing.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_uiApp != null) _uiApp.DialogBoxShowing -= OnDialogBoxShowing;
    }
}
