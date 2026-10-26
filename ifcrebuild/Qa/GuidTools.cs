using Autodesk.Revit.DB;

namespace IfcRebuild;

/// <summary>
/// IfcGUID (BuiltInParameter.IFC_GUID) = IFC GlobalId, one to one. Builders call Set when they create an element.
/// Mark may hold the GlobalId as a working key while building; ClearMarks empties it at the end wherever it equals
/// the IfcGUID (other Marks are left). Coverage and the one-to-one check come from the ExportQa listing.
/// </summary>
internal static class GuidTools
{
    /// <summary>Throws when Revit refuses the value (an Opening returns false from Set without an error).</summary>
    internal static void Set(Element e, string guid)
    {
        var p = e.get_Parameter(BuiltInParameter.IFC_GUID);
        if (p == null || p.IsReadOnly || !p.Set(guid)) throw new InvalidOperationException($"IfcGUID not writable on {e.Id}");
    }

    internal static string Get(Element e) => e.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString();

    /// <summary>First element per IfcGUID among the elements of the collector (default: every element that is not a type).</summary>
    internal static Dictionary<string, Element> ByGuid(Document doc, FilteredElementCollector source = null)
    {
        var d = new Dictionary<string, Element>();
        foreach (var e in (source ?? new FilteredElementCollector(doc).WhereElementIsNotElementType()).ToElements())
        {
            var g = Get(e);
            if (!string.IsNullOrEmpty(g)) d.TryAdd(g, e);
        }
        return d;
    }

    /// <summary>Project, site and building GUIDs live on Project Information; storeys on their Level.
    /// pairs: "PROJECT=gid;SITE=gid;BUILDING=gid;level name=gid;..." (mode "probe" rolls back).</summary>
    public static string SetSpatial(Document doc, string pairs, string mode)
    {
        var map = pairs.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=')).ToDictionary(x => x[0], x => x[1]);
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();   // fixed before changes
        var pi = doc.ProjectInformation;
        int done = 0;
        var missed = new List<string>();
        using var tg = new TransactionGroup(doc, "IfcRebuild: spatial GUIDs");
        tg.Start();
        var (st, log) = FailureLog.Run(doc, "IfcRebuild: spatial GUIDs", () =>
        {
            foreach (var (k, g) in map)
            {
                Parameter p = k switch
                {
                    "PROJECT" => pi.get_Parameter(BuiltInParameter.IFC_PROJECT_GUID),
                    "SITE" => pi.get_Parameter(BuiltInParameter.IFC_SITE_GUID),
                    "BUILDING" => pi.get_Parameter(BuiltInParameter.IFC_BUILDING_GUID),
                    _ => levels.FirstOrDefault(l => l.Name == k)?.get_Parameter(BuiltInParameter.IFC_GUID),
                };
                if (p != null && !p.IsReadOnly && p.Set(g)) done++; else missed.Add(k);
            }
        });
        if (mode == "apply") tg.Assimilate(); else tg.RollBack();
        return $"commit={st} set={done} missed=[{string.Join(",", missed)}] {(mode == "apply" ? "applied" : "rolled back")}\n{log.Summary()}";
    }

    public static string ClearMarks(Document doc, string mode)
    {
        var hits = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements()
            .Select(e => (e, p: e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)))
            .Where(x => x.p != null && !x.p.IsReadOnly && !string.IsNullOrEmpty(x.p.AsString()) && x.p.AsString() == Get(x.e)).ToList();
        if (mode != "apply") return $"probe: {hits.Count} Marks equal to the IfcGUID would be cleared";
        int done = 0, errors = 0;
        var (st, log) = FailureLog.Run(doc, "IfcRebuild: clear Mark", () => { foreach (var (_, p) in hits) { if (p.Set("")) done++; else errors++; } });
        return $"commit={st} cleared={done} errors={errors} warnings={log.Lines.Count}";
    }
}
