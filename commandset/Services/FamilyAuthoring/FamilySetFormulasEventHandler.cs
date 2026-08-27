using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.FamilyAuthoring;

/// <summary>
/// Assigns formulas to parameters that already exist, in bulk.
///
/// Kept separate from declaring parameters because the order is a hard constraint: a formula
/// can only reference parameters Revit already knows about, so the whole set has to be
/// declared before any formula is applied. Splitting the two steps makes that ordering
/// impossible to get wrong by accident.
///
/// Passing an empty formula clears one, which is the way back out when a formula turns a
/// parameter read-only and it needs to take a value again.
/// </summary>
public class FamilySetFormulasEventHandler : FamilyEventHandlerBase
{
    public override string GetName() => "Family Set Formulas";

    protected override object Run(UIApplication app)
    {
        Document document = FamilyDocumentRegistry.Resolve(Text("documentHandle"), app);
        if (!document.IsFamilyDocument)
            throw new InvalidOperationException("This command only works on a family document.");

        var requests = Parameters?["formulas"] as JArray;
        if (requests == null || requests.Count == 0)
            throw new ArgumentException("'formulas' must be a non-empty array.");

        FamilyManager manager = document.FamilyManager;
        var applied = new List<object>();
        var failed = new List<object>();
        var messages = new List<string>();

        using (var transaction = new Transaction(document, "Set family formulas"))
        {
            transaction.Start();
            McpFailurePreprocessor failures = transaction.SuppressRevitDialogs();

            foreach (JToken token in requests)
            {
                string name = token["name"]?.ToString();
                string formula = token["formula"]?.ToString();

                try
                {
                    if (string.IsNullOrWhiteSpace(name))
                        throw new ArgumentException("Every entry needs a 'name'.");

                    FamilyParameter parameter = FamilyAddParametersEventHandler.FindParameter(manager, name);
                    if (parameter == null)
                        throw new InvalidOperationException(
                            $"'{name}' is not declared in this family. Declare it before giving it a formula.");

                    // Revit's own message for a bad formula names the expression but not the
                    // parameter, which is little help halfway through a list of forty.
                    manager.SetFormula(parameter, string.IsNullOrWhiteSpace(formula) ? null : formula);

                    applied.Add(new
                    {
                        name,
                        formula = string.IsNullOrWhiteSpace(formula) ? null : formula,
                        cleared = string.IsNullOrWhiteSpace(formula),
                    });
                }
                catch (Exception ex)
                {
                    failed.Add(new { name, formula, error = ex.Message });
                }
            }

            TransactionStatus status = transaction.Commit();
            messages.AddRange(failures.Messages);

            if (status != TransactionStatus.Committed)
                throw new InvalidOperationException(
                    $"Revit rolled the transaction back ({status}); no formula was kept. "
                    + string.Join(" | ", failures.Messages));
        }

        return new
        {
            success = failed.Count == 0,
            appliedCount = applied.Count,
            failedCount = failed.Count,
            applied,
            failed,
            revitMessages = messages,
        };
    }
}
