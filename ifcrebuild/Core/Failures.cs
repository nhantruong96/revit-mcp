using Autodesk.Revit.DB;

namespace IfcRebuild;

/// <summary>Preprocessor for the transactions the builders open themselves: no dialogs; warnings deleted, errors resolved
/// with Revit's default resolution; everything logged with the IfcGUIDs of the elements involved.</summary>
internal sealed class FailureLog : IFailuresPreprocessor
{
    public readonly List<string> Lines = new();

    public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
    {
        var doc = fa.GetDocument();
        bool resolved = false;
        foreach (var m in fa.GetFailureMessages())
        {
            var marks = m.GetFailingElementIds().Select(id => doc.GetElement(id)?.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString())
                .Where(x => !string.IsNullOrEmpty(x));
            var line = $"{m.GetSeverity()}: {m.GetDescriptionText()} [{string.Join(",", marks)}]";
            if (m.GetSeverity() == FailureSeverity.Warning) fa.DeleteWarning(m);
            else if (m.HasResolutions()) { fa.ResolveFailure(m); resolved = true; line += " -> resolved"; }
            else line += " -> UNRESOLVED";
            Lines.Add(line);
        }
        return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
    }

    public static (TransactionStatus status, FailureLog log) Run(Document doc, string name, Action body)
    {
        var log = new FailureLog();
        using var t = new Transaction(doc, name);
        var opt = t.GetFailureHandlingOptions();
        opt.SetFailuresPreprocessor(log);
        opt.SetForcedModalHandling(false);
        opt.SetClearAfterRollback(true);
        t.SetFailureHandlingOptions(opt);
        t.Start();
        body();
        return (t.Commit(opt), log);
    }

    public string Summary() => $"failures: {Lines.Count} (errors {Lines.Count(l => l.StartsWith("Error"))})\n" +
        string.Join("\n", Lines.GroupBy(l => l.Split('[')[0]).OrderByDescending(g => g.Count()).Take(12)
            .Select(g => $"  {g.Count()}x {g.Key.Trim()}  e.g. [{g.First().Split('[').Last()}"));
}
