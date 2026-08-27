using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using RevitMCPCommandSet.Services.FamilyAuthoring;

namespace RevitMCPCommandSet.Commands.FamilyAuthoring;

/// <summary>
/// The seven family authoring commands. They are gathered in one file because each is the
/// same twenty lines - hand the payload to the handler, wait, return - and the interesting
/// code all lives in the handlers.
///
/// Timeouts are generous compared with the rest of the command set. Opening a family, adding
/// eighty parameters or exporting a view are each slow enough that a tight limit produces
/// spurious failures, and a timed-out command must never simply be retried: its work may
/// have completed, so a retry would do it twice.
/// </summary>
public abstract class FamilyCommandBase : ExternalEventCommandBase
{
    private readonly object _executionLock = new object();
    private readonly int _timeoutMs;

    protected FamilyCommandBase(FamilyEventHandlerBase handler, UIApplication uiApp, int timeoutMs)
        : base(handler, uiApp)
    {
        _timeoutMs = timeoutMs;
    }

    private FamilyEventHandlerBase TypedHandler => (FamilyEventHandlerBase)Handler;

    public override object Execute(JObject parameters, string requestId)
    {
        lock (_executionLock)
        {
            TypedHandler.Parameters = parameters ?? new JObject();

            if (RaiseAndWaitForCompletion(_timeoutMs))
                return TypedHandler.Result;

            throw new TimeoutException(
                $"{CommandName} timed out after {_timeoutMs / 1000}s. Revit did not finish the request in time, "
                + "usually because its UI was busy. The work may still have completed - read the document state "
                + "before deciding what to do, and do not simply run the command again.");
        }
    }
}

public class FamilyOpenDocumentCommand : FamilyCommandBase
{
    public override string CommandName => "family_open_document";

    public FamilyOpenDocumentCommand(UIApplication uiApp)
        : base(new FamilyDocumentEventHandler(), uiApp, 180000) { }
}

public class FamilyAddParametersCommand : FamilyCommandBase
{
    public override string CommandName => "family_add_parameters";

    public FamilyAddParametersCommand(UIApplication uiApp)
        : base(new FamilyAddParametersEventHandler(), uiApp, 180000) { }
}

public class FamilySetFormulasCommand : FamilyCommandBase
{
    public override string CommandName => "family_set_formulas";

    public FamilySetFormulasCommand(UIApplication uiApp)
        : base(new FamilySetFormulasEventHandler(), uiApp, 120000) { }
}

public class FamilyReadParametersCommand : FamilyCommandBase
{
    public override string CommandName => "family_read_parameters";

    public FamilyReadParametersCommand(UIApplication uiApp)
        : base(new FamilyReadParametersEventHandler(), uiApp, 120000) { }
}

public class FamilyFlexCommand : FamilyCommandBase
{
    public override string CommandName => "family_flex";

    public FamilyFlexCommand(UIApplication uiApp)
        : base(new FamilyFlexEventHandler(), uiApp, 180000) { }
}

public class FamilyMeasureGeometryCommand : FamilyCommandBase
{
    public override string CommandName => "family_measure_geometry";

    public FamilyMeasureGeometryCommand(UIApplication uiApp)
        : base(new FamilyMeasureGeometryEventHandler(), uiApp, 180000) { }
}

public class ViewExportCommand : FamilyCommandBase
{
    public override string CommandName => "view_export";

    public ViewExportCommand(UIApplication uiApp)
        : base(new ViewExportEventHandler(), uiApp, 300000) { }
}
