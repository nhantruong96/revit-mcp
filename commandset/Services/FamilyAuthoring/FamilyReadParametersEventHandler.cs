using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.FamilyAuthoring;

/// <summary>
/// Reads the whole parameter table back out of a family: name, storage type, group,
/// instance flag, shared flag and GUID, formula, read-only state, and the value held by
/// each type.
///
/// This is the checking half of the pair. Writing a parameter and then trusting the write
/// is how a family ends up with a parameter that is silently a type parameter when it had
/// to be an instance one, or a shared parameter whose GUID does not match the file everyone
/// else used. Reading the table back turns all of that into something a test can assert on.
/// </summary>
public class FamilyReadParametersEventHandler : FamilyEventHandlerBase
{
    public override string GetName() => "Family Read Parameters";

    protected override object Run(UIApplication app)
    {
        Document document = FamilyDocumentRegistry.Resolve(Text("documentHandle"), app);
        if (!document.IsFamilyDocument)
            throw new InvalidOperationException("This command only works on a family document.");

        FamilyManager manager = document.FamilyManager;
        bool includeValues = Flag("includeValues", true);
        string nameFilter = Text("nameContains");

        var parameters = new List<object>();
        int instanceCount = 0;
        int formulaCount = 0;
        int sharedCount = 0;

        foreach (FamilyParameter parameter in manager.Parameters)
        {
            string name = parameter.Definition?.Name;
            if (name == null) continue;
            if (!string.IsNullOrWhiteSpace(nameFilter)
                && name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            if (parameter.IsInstance) instanceCount++;
            if (parameter.IsDeterminedByFormula) formulaCount++;
            if (parameter.IsShared) sharedCount++;

            parameters.Add(new
            {
                name,
                storageType = parameter.StorageType.ToString(),
                spec = SafeSpec(parameter),
                group = SafeGroup(parameter),
                isInstance = parameter.IsInstance,
                isShared = parameter.IsShared,
                guid = parameter.IsShared ? parameter.GUID.ToString() : null,
                formula = parameter.Formula,
                isDeterminedByFormula = parameter.IsDeterminedByFormula,
                isReadOnly = parameter.IsReadOnly,
                value = includeValues ? ReadValue(manager, parameter) : null,
            });
        }

        var typeNames = new List<string>();
        foreach (FamilyType type in manager.Types)
        {
            if (!string.IsNullOrEmpty(type.Name)) typeNames.Add(type.Name);
        }

        return new
        {
            success = true,
            title = document.Title,
            pathName = document.PathName,
            familyCategory = document.OwnerFamily?.FamilyCategory?.Name,
            isSharedFamily = ReadSharedFlag(document),
            parameterCount = parameters.Count,
            instanceCount,
            typeParameterCount = parameters.Count - instanceCount,
            formulaCount,
            sharedCount,
            typeCount = typeNames.Count,
            typeNames,
            currentTypeName = manager.CurrentType?.Name,
            parameters,
        };
    }

    /// <summary>The value the current type holds, in millimetres for lengths.</summary>
    private static object ReadValue(FamilyManager manager, FamilyParameter parameter)
    {
        FamilyType current = manager.CurrentType;
        if (current == null) return null;

        try
        {
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    double? raw = current.AsDouble(parameter);
                    if (raw == null) return null;
                    return new
                    {
                        raw = raw.Value,
                        millimetres = FamilyTypeIdResolver.InternalToMm(raw.Value),
                        display = current.AsValueString(parameter),
                    };

                case StorageType.Integer:
                    int? integer = current.AsInteger(parameter);
                    return integer == null ? null : new { integer = integer.Value };

                case StorageType.String:
                    return new { text = current.AsString(parameter) };

                case StorageType.ElementId:
                    ElementId id = current.AsElementId(parameter);
                    return id == null ? null : new { elementId = id.ToString() };

                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            // One unreadable parameter should not cost the caller the other seventy-nine.
            return new { error = ex.Message };
        }
    }

    private static string SafeSpec(FamilyParameter parameter)
    {
        try
        {
            ForgeTypeId spec = parameter.Definition?.GetDataType();
            return spec == null || spec.Empty() ? null : spec.TypeId;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeGroup(FamilyParameter parameter)
    {
        try
        {
            ForgeTypeId group = parameter.Definition?.GetGroupTypeId();
            return group == null || group.Empty() ? null : group.TypeId;
        }
        catch
        {
            return null;
        }
    }

    private static bool? ReadSharedFlag(Document document)
    {
        try
        {
            Parameter shared = document.OwnerFamily?.get_Parameter(BuiltInParameter.FAMILY_SHARED);
            return shared?.AsInteger() == 1;
        }
        catch
        {
            return null;
        }
    }
}
