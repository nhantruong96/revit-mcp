using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.FamilyAuthoring;

/// <summary>
/// Flexes a family: pushes a set of values in, regenerates, and reports what came back out
/// along with every warning the document is now carrying.
///
/// Flexing is how a parametric family is tested, and doing it by hand is why it often is not
/// done at all. The three things worth knowing after a flex are whether the values took,
/// whether the formulas that depend on them produced what they should, and whether the
/// geometry survived without constraint warnings. All three come back from one call.
///
/// Warnings are counted before and after so a flex that adds one is distinguishable from a
/// flex that merely inherited it. Warnings accumulate quietly in a family and then multiply
/// by every instance placed in a project.
/// </summary>
public class FamilyFlexEventHandler : FamilyEventHandlerBase
{
    public override string GetName() => "Family Flex";

    protected override object Run(UIApplication app)
    {
        Document document = FamilyDocumentRegistry.Resolve(Text("documentHandle"), app);
        if (!document.IsFamilyDocument)
            throw new InvalidOperationException("This command only works on a family document.");

        FamilyManager manager = document.FamilyManager;
        var values = Parameters?["values"] as JArray;
        string typeName = Text("typeName");

        int warningsBefore = document.GetWarnings()?.Count ?? 0;

        var writes = new List<PendingWrite>();
        var failed = new List<object>();
        var messages = new List<string>();

        using (var transaction = new Transaction(document, "Flex family"))
        {
            transaction.Start();
            McpFailurePreprocessor failures = transaction.SuppressRevitDialogs();

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                FamilyType target = null;
                foreach (FamilyType type in manager.Types)
                {
                    if (string.Equals(type.Name, typeName, StringComparison.Ordinal)) { target = type; break; }
                }

                if (target == null)
                    throw new InvalidOperationException($"This family has no type named '{typeName}'.");

                manager.CurrentType = target;
            }

            if (values != null)
            {
                foreach (JToken token in values)
                {
                    string name = token["name"]?.ToString();
                    try
                    {
                        if (string.IsNullOrWhiteSpace(name))
                            throw new ArgumentException("Every entry needs a 'name'.");

                        FamilyParameter parameter = FamilyAddParametersEventHandler.FindParameter(manager, name);
                        if (parameter == null)
                            throw new InvalidOperationException($"'{name}' is not declared in this family.");

                        if (parameter.IsDeterminedByFormula)
                            throw new InvalidOperationException(
                                $"'{name}' is driven by a formula, so it cannot be set directly. Set what the formula reads instead.");

                        PendingWrite write = SetValue(manager, parameter, token);
                        write.Name = name;
                        writes.Add(write);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new { name, error = ex.Message });
                    }
                }
            }

            // Regenerate inside the transaction so any constraint failure surfaces through
            // the preprocessor rather than as a dialog after the fact.
            document.Regenerate();

            TransactionStatus status = transaction.Commit();
            messages.AddRange(failures.Messages);

            if (status != TransactionStatus.Committed)
                throw new InvalidOperationException(
                    $"Revit rolled the transaction back ({status}); the flex changed nothing. "
                    + string.Join(" | ", failures.Messages));
        }

        // Verify every write by reading it back. Revit does not always refuse a write it is
        // going to ignore: setting a parameter that a formula drives can return without error
        // and simply leave the old value in place. The IsDeterminedByFormula flag is not a
        // reliable guard either - it has been observed reading false on a parameter that
        // demonstrably still had its formula. Reading back is the only account worth trusting.
        var applied = new List<object>();
        var ignored = new List<object>();

        foreach (PendingWrite write in writes)
        {
            object actual = ReadOne(manager, write.Name);
            bool tookEffect = Matches(write, actual);

            var record = new
            {
                name = write.Name,
                requested = write.Requested,
                actual,
                tookEffect,
            };

            if (tookEffect) applied.Add(record);
            else ignored.Add(record);
        }

        var readBack = ReadBack(manager, Parameters?["readBack"] as JArray);
        var warnings = CollectWarnings(document);

        return new
        {
            success = failed.Count == 0 && ignored.Count == 0,
            currentTypeName = manager.CurrentType?.Name,
            appliedCount = applied.Count,
            ignoredCount = ignored.Count,
            failedCount = failed.Count,
            applied,
            ignored,
            ignoredNote = ignored.Count == 0
                ? null
                : "Revit accepted these writes without error but the value did not change. The usual cause is a formula still driving the parameter.",
            failed,
            readBack,
            warningsBefore,
            warningsAfter = warnings.Count,
            warningsAdded = warnings.Count - warningsBefore,
            warnings,
            revitMessages = messages,
        };
    }

    /// <summary>What was asked for, kept so the write can be checked after the commit.</summary>
    private sealed class PendingWrite
    {
        public string Name;
        public string Kind;      // length | number | integer | text
        public double Numeric;   // millimetres for length, the raw value otherwise
        public string Text;

        public object Requested => Kind switch
        {
            "length" => new { millimetres = Numeric },
            "integer" => new { integer = (int)Numeric },
            "number" => new { number = Numeric },
            _ => new { text = Text },
        };
    }

    private static PendingWrite SetValue(FamilyManager manager, FamilyParameter parameter, JToken token)
    {
        JToken mm = token["valueMm"];
        if (mm != null && mm.Type != JTokenType.Null)
        {
            double value = mm.ToObject<double>();
            manager.Set(parameter, FamilyTypeIdResolver.MmToInternal(value));
            return new PendingWrite { Kind = "length", Numeric = value };
        }

        JToken number = token["value"];
        if (number != null && number.Type != JTokenType.Null)
        {
            if (number.Type == JTokenType.Boolean)
            {
                int flag = number.ToObject<bool>() ? 1 : 0;
                manager.Set(parameter, flag);
                return new PendingWrite { Kind = "integer", Numeric = flag };
            }

            if (parameter.StorageType == StorageType.Integer)
            {
                int flag = number.ToObject<int>();
                manager.Set(parameter, flag);
                return new PendingWrite { Kind = "integer", Numeric = flag };
            }

            double value = number.ToObject<double>();
            manager.Set(parameter, value);
            return new PendingWrite { Kind = "number", Numeric = value };
        }

        JToken text = token["text"];
        if (text != null && text.Type != JTokenType.Null)
        {
            string value = text.ToString();
            manager.Set(parameter, value);
            return new PendingWrite { Kind = "text", Text = value };
        }

        throw new ArgumentException("Give one of 'valueMm', 'value' or 'text'.");
    }

    /// <summary>Reads one parameter back in the same shape the write was expressed in.</summary>
    private static object ReadOne(FamilyManager manager, string name)
    {
        FamilyParameter parameter = FamilyAddParametersEventHandler.FindParameter(manager, name);
        FamilyType current = manager.CurrentType;
        if (parameter == null || current == null) return null;

        try
        {
            return parameter.StorageType switch
            {
                StorageType.Double => current.AsDouble(parameter) is double d
                    ? new { millimetres = FamilyTypeIdResolver.InternalToMm(d), raw = d }
                    : null,
                StorageType.Integer => current.AsInteger(parameter) is int i ? new { integer = i } : (object)null,
                StorageType.String => new { text = current.AsString(parameter) },
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when what came back matches what went in.</summary>
    private static bool Matches(PendingWrite write, object actual)
    {
        if (actual == null) return false;

        switch (write.Kind)
        {
            case "length":
            {
                double? mm = ReadProperty(actual, "millimetres");
                // A tenth of a micron is far below anything a drawing cares about, and well
                // above the noise of converting through Revit's internal feet.
                return mm.HasValue && Math.Abs(mm.Value - write.Numeric) < 0.0001;
            }

            case "integer":
            {
                double? value = ReadProperty(actual, "integer");
                return value.HasValue && (int)value.Value == (int)write.Numeric;
            }

            case "number":
            {
                double? raw = ReadProperty(actual, "raw");
                return raw.HasValue && Math.Abs(raw.Value - write.Numeric) < 1e-9;
            }

            default:
            {
                object text = actual.GetType().GetProperty("text")?.GetValue(actual);
                return string.Equals(text as string, write.Text, StringComparison.Ordinal);
            }
        }
    }

    private static double? ReadProperty(object source, string propertyName)
    {
        object value = source.GetType().GetProperty(propertyName)?.GetValue(source);
        return value == null ? null : Convert.ToDouble(value);
    }

    /// <summary>
    /// Reads named parameters back after the regenerate. With no list given it reports every
    /// formula-driven parameter, which is usually exactly what a flex is checking.
    /// </summary>
    private static List<object> ReadBack(FamilyManager manager, JArray requested)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        if (requested != null)
        {
            foreach (JToken token in requested)
            {
                string name = token?.ToString();
                if (!string.IsNullOrWhiteSpace(name)) wanted.Add(name);
            }
        }

        FamilyType current = manager.CurrentType;
        var results = new List<object>();
        if (current == null) return results;

        foreach (FamilyParameter parameter in manager.Parameters)
        {
            string name = parameter.Definition?.Name;
            if (name == null) continue;

            bool include = wanted.Count > 0 ? wanted.Contains(name) : parameter.IsDeterminedByFormula;
            if (!include) continue;

            try
            {
                object value = parameter.StorageType switch
                {
                    StorageType.Double => current.AsDouble(parameter) is double d
                        ? new { millimetres = FamilyTypeIdResolver.InternalToMm(d), raw = d }
                        : null,
                    StorageType.Integer => current.AsInteger(parameter) is int i ? new { integer = i } : (object)null,
                    StorageType.String => new { text = current.AsString(parameter) },
                    _ => null,
                };

                results.Add(new { name, formula = parameter.Formula, value });
            }
            catch (Exception ex)
            {
                results.Add(new { name, error = ex.Message });
            }
        }

        return results;
    }

    private static List<object> CollectWarnings(Document document)
    {
        var results = new List<object>();
        IList<FailureMessage> warnings = document.GetWarnings();
        if (warnings == null) return results;

        foreach (FailureMessage warning in warnings)
        {
            results.Add(new
            {
                severity = warning.GetSeverity().ToString(),
                description = warning.GetDescriptionText(),
                elementIds = warning.GetFailingElements()?.Select(id => id.ToString()).ToList(),
            });
        }

        return results;
    }
}
