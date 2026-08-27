using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.FamilyAuthoring;

/// <summary>
/// Declares family parameters in bulk, either as shared parameters pulled from a shared
/// parameter file or as plain family parameters.
///
/// Bulk matters here. A production family runs to dozens of parameters, and declaring them
/// one round trip at a time is both slow and a good way to end up with a family that is
/// half-declared because something failed in the middle. This does the whole list inside one
/// transaction and reports per-parameter outcomes.
///
/// Shared parameters are matched by name against the file, and the GUID that ends up in the
/// family is the file's. That is the point of using shared parameters at all: two families
/// that declare the same name from the same file end up sharing an identity, and schedules,
/// tags and IFC mapping can rely on it.
/// </summary>
public class FamilyAddParametersEventHandler : FamilyEventHandlerBase
{
    public override string GetName() => "Family Add Parameters";

    protected override object Run(UIApplication app)
    {
        Document document = FamilyDocumentRegistry.Resolve(Text("documentHandle"), app);
        if (!document.IsFamilyDocument)
            throw new InvalidOperationException("This command only works on a family document.");

        var requests = Parameters?["parameters"] as JArray;
        if (requests == null || requests.Count == 0)
            throw new ArgumentException("'parameters' must be a non-empty array.");

        FamilyManager manager = document.FamilyManager;
        string sharedFile = Text("sharedParameterFile");

        var created = new List<object>();
        var skipped = new List<object>();
        var failed = new List<object>();
        var messages = new List<string>();

        // The shared parameter file is application-wide state that belongs to the user, so
        // it is put back exactly as it was even when this call throws.
        string previousSharedFile = app.Application.SharedParametersFilename;

        try
        {
            DefinitionFile definitionFile = null;
            if (!string.IsNullOrWhiteSpace(sharedFile))
            {
                if (!File.Exists(sharedFile))
                    throw new System.IO.FileNotFoundException($"No shared parameter file at '{sharedFile}'.");

                app.Application.SharedParametersFilename = sharedFile;
                definitionFile = app.Application.OpenSharedParameterFile();
                if (definitionFile == null)
                    throw new InvalidOperationException(
                        $"Revit could not read '{sharedFile}' as a shared parameter file.");
            }

            using (var transaction = new Transaction(document, "Add family parameters"))
            {
                transaction.Start();
                McpFailurePreprocessor failures = transaction.SuppressRevitDialogs();

                // Setting a value needs a type to hold it. A template family starts with none.
                if (manager.Types.IsEmpty)
                {
                    manager.NewType(Text("defaultTypeName", "Standard"));
                }

                foreach (JToken token in requests)
                {
                    string name = token["name"]?.ToString();
                    try
                    {
                        if (string.IsNullOrWhiteSpace(name))
                            throw new ArgumentException("Every entry needs a 'name'.");

                        FamilyParameter existing = FindParameter(manager, name);
                        if (existing != null)
                        {
                            skipped.Add(new { name, reason = "already declared" });
                            continue;
                        }

                        bool isInstance = token["isInstance"]?.ToObject<bool>() ?? false;
                        ForgeTypeId group = FamilyTypeIdResolver.ResolveGroup(token["group"]?.ToString());
                        bool useShared = token["shared"]?.ToObject<bool>() ?? definitionFile != null;

                        FamilyParameter parameter;
                        string guid = null;

                        if (useShared)
                        {
                            if (definitionFile == null)
                                throw new InvalidOperationException(
                                    $"'{name}' asks for a shared parameter but no sharedParameterFile was given.");

                            ExternalDefinition definition = FindExternalDefinition(definitionFile, name);
                            if (definition == null)
                                throw new InvalidOperationException(
                                    $"'{name}' is not in the shared parameter file.");

                            parameter = manager.AddParameter(definition, group, isInstance);
                            guid = definition.GUID.ToString();
                        }
                        else
                        {
                            ForgeTypeId spec = FamilyTypeIdResolver.ResolveSpec(token["spec"]?.ToString());
                            parameter = manager.AddParameter(name, group, spec, isInstance);
                        }

                        object applied = ApplyDefault(manager, parameter, token);

                        created.Add(new
                        {
                            name,
                            isInstance,
                            shared = useShared,
                            guid,
                            defaultApplied = applied,
                        });
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new { name, error = ex.Message });
                    }
                }

                TransactionStatus status = transaction.Commit();
                messages.AddRange(failures.Messages);

                if (status != TransactionStatus.Committed)
                    throw new InvalidOperationException(
                        $"Revit rolled the transaction back ({status}); no parameter was kept. "
                        + string.Join(" | ", failures.Messages));
            }
        }
        finally
        {
            app.Application.SharedParametersFilename = previousSharedFile;
        }

        return new
        {
            success = failed.Count == 0,
            createdCount = created.Count,
            skippedCount = skipped.Count,
            failedCount = failed.Count,
            created,
            skipped,
            failed,
            revitMessages = messages,
        };
    }

    /// <summary>Applies an optional starting value. Lengths arrive in millimetres.</summary>
    private static object ApplyDefault(FamilyManager manager, FamilyParameter parameter, JToken token)
    {
        if (parameter.IsDeterminedByFormula || parameter.IsReadOnly) return null;

        JToken mm = token["defaultValueMm"];
        if (mm != null && mm.Type != JTokenType.Null)
        {
            double value = mm.ToObject<double>();
            manager.Set(parameter, FamilyTypeIdResolver.MmToInternal(value));
            return new { millimetres = value };
        }

        JToken number = token["defaultValue"];
        if (number != null && number.Type != JTokenType.Null)
        {
            if (number.Type == JTokenType.Boolean)
            {
                int flag = number.ToObject<bool>() ? 1 : 0;
                manager.Set(parameter, flag);
                return new { integer = flag };
            }

            if (number.Type == JTokenType.Integer)
            {
                int flag = number.ToObject<int>();
                manager.Set(parameter, flag);
                return new { integer = flag };
            }

            double value = number.ToObject<double>();
            manager.Set(parameter, value);
            return new { number = value };
        }

        JToken text = token["defaultText"];
        if (text != null && text.Type != JTokenType.Null)
        {
            string value = text.ToString();
            manager.Set(parameter, value);
            return new { text = value };
        }

        return null;
    }

    internal static FamilyParameter FindParameter(FamilyManager manager, string name)
    {
        foreach (FamilyParameter parameter in manager.Parameters)
        {
            if (string.Equals(parameter.Definition?.Name, name, StringComparison.Ordinal))
                return parameter;
        }
        return null;
    }

    private static ExternalDefinition FindExternalDefinition(DefinitionFile file, string name)
    {
        foreach (DefinitionGroup group in file.Groups)
        {
            foreach (Definition definition in group.Definitions)
            {
                if (definition is ExternalDefinition external
                    && string.Equals(external.Name, name, StringComparison.Ordinal))
                    return external;
            }
        }
        return null;
    }
}
