using System.Reflection;
using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils;

/// <summary>
/// Turns the plain names a tool call uses - "Length", "YesNo", "Geometry" - into the
/// <see cref="ForgeTypeId"/> values the Revit API wants.
///
/// The lookup goes through reflection rather than a hard-coded switch on purpose. The
/// identifiers live on <c>SpecTypeId</c> and <c>GroupTypeId</c>, partly on nested classes
/// (<c>SpecTypeId.Int.Integer</c>, <c>SpecTypeId.Boolean.YesNo</c>), and the set grows with
/// every Revit release. Reflection means a name this file never heard of still resolves as
/// long as the running Revit has it, and an unknown name produces a message listing what is
/// actually available instead of a compile-time gap.
/// </summary>
public static class FamilyTypeIdResolver
{
    /// <summary>Short names people actually type, mapped to their real path.</summary>
    private static readonly Dictionary<string, string> _specAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Integer"] = "Int.Integer",
        ["Int"] = "Int.Integer",
        ["Text"] = "String.Text",
        ["String"] = "String.Text",
        ["YesNo"] = "Boolean.YesNo",
        ["Bool"] = "Boolean.YesNo",
        ["Boolean"] = "Boolean.YesNo",
        ["Material"] = "Reference.Material",
        ["Url"] = "String.Url",
        ["MultilineText"] = "String.MultilineText",
    };

    public static ForgeTypeId ResolveSpec(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A parameter needs a spec, for example \"Length\", \"Integer\", \"Number\", \"Text\" or \"YesNo\".");

        string path = _specAliases.TryGetValue(name.Trim(), out string alias) ? alias : name.Trim();

        ForgeTypeId resolved = Lookup(typeof(SpecTypeId), path);
        if (resolved != null) return resolved;

        throw new ArgumentException(
            $"Unknown spec '{name}'. Try one of: {string.Join(", ", SampleNames(typeof(SpecTypeId)))} - or a nested name such as Int.Integer, String.Text, Boolean.YesNo.");
    }

    public static ForgeTypeId ResolveGroup(string name)
    {
        // A parameter with no group is legal; Revit files it under "Other".
        if (string.IsNullOrWhiteSpace(name)) return GroupTypeId.Data;

        ForgeTypeId resolved = Lookup(typeof(GroupTypeId), name.Trim());
        if (resolved != null) return resolved;

        throw new ArgumentException(
            $"Unknown parameter group '{name}'. Try one of: {string.Join(", ", SampleNames(typeof(GroupTypeId)))}.");
    }

    /// <summary>Walks a dotted path over static properties and nested classes.</summary>
    private static ForgeTypeId Lookup(Type root, string path)
    {
        string[] parts = path.Split('.');
        Type current = root;

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            bool last = i == parts.Length - 1;

            if (last)
            {
                PropertyInfo property = current
                    .GetProperties(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(p => p.PropertyType == typeof(ForgeTypeId)
                                         && string.Equals(p.Name, part, StringComparison.OrdinalIgnoreCase));

                return property?.GetValue(null) as ForgeTypeId;
            }

            Type nested = current
                .GetNestedTypes(BindingFlags.Public)
                .FirstOrDefault(t => string.Equals(t.Name, part, StringComparison.OrdinalIgnoreCase));

            if (nested == null) return null;
            current = nested;
        }

        return null;
    }

    private static IEnumerable<string> SampleNames(Type root)
    {
        return root.GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(ForgeTypeId))
            .Select(p => p.Name)
            .OrderBy(n => n)
            .Take(12);
    }

    /// <summary>
    /// Millimetres to Revit's internal feet. Every length crossing the tool boundary is in
    /// millimetres, matching the rest of this command set.
    /// </summary>
    public static double MmToInternal(double millimetres)
        => UnitUtils.ConvertToInternalUnits(millimetres, UnitTypeId.Millimeters);

    public static double InternalToMm(double internalUnits)
        => UnitUtils.ConvertFromInternalUnits(internalUnits, UnitTypeId.Millimeters);

    /// <summary>Cubic feet to cubic millimetres, for the volumes the measure command reports.</summary>
    public static double InternalVolumeToMm3(double internalVolume)
        => internalVolume * 304.8 * 304.8 * 304.8;
}
