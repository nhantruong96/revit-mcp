using System.Text;
using Autodesk.Revit.DB;

namespace IfcRebuild;

/// <summary>
/// Worksharing in one file: worksets per discipline from the config "worksets" rules (category lists), concrete framing
/// and columns to the config concreteWorkset, steel framing, columns and Steel Connections elements to steelWorkset;
/// then the file is saved as central at the same path when the gate passes. mode "probe" only counts; "apply" enables
/// worksharing, assigns and saves.
/// </summary>
internal static class Collab
{
    public static string Run(Document doc, string mode)
    {
        var o = RebuildConfig.Current;
        var sb = new StringBuilder();
        var plan = new Dictionary<string, List<ElementId>>();
        List<ElementId> Ws(string name) => plan.TryGetValue(name, out var l) ? l : plan[name] = new List<ElementId>();
        foreach (var rule in o.Worksets)
        {
            var cats = new List<BuiltInCategory>();
            foreach (var c in rule.Categories)
                if (Enum.TryParse<BuiltInCategory>(c, out var bic)) cats.Add(bic); else sb.AppendLine($"unknown category {c} in workset {rule.Name}");
            if (cats.Count > 0)
                Ws(rule.Name).AddRange(new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType().ToElementIds());
        }
        // framing and columns: concrete to one workset, steel to the other
        foreach (var e in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(new[] { BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns }))
                     .WhereElementIsNotElementType())
        {
            var mat = doc.GetElement(e.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId) as Material;
            Ws(mat != null && mat.MaterialClass == "Concrete" ? o.ConcreteWorkset : o.SteelWorkset).Add(e.Id);
        }
        // Steel Connections elements (plates, bolts, anchors) are SteelProxyElements
        Ws(o.SteelWorkset).AddRange(new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Steel.SteelProxyElement)).ToElementIds());
        foreach (var kv in plan) sb.AppendLine($"{kv.Key}: {kv.Value.Distinct().Count()}");
        if (mode != "apply") return "Collab probe\n" + sb;
        if (string.IsNullOrEmpty(doc.PathName)) return "NOT SAVED: the document has never been saved\n" + sb;
        var g = Gate.Run(doc);
        if (!g.StartsWith("GATE PASS")) return "NOT SAVED " + g + "\n" + sb;
        if (!doc.IsWorkshared) doc.EnableWorksharing(o.LevelsWorkset, o.DefaultWorkset);
        var (st, log) = FailureLog.Run(doc, "Worksets per discipline", () =>
        {
            var have = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToDictionary(w => w.Name, w => w.Id);
            foreach (var kv in plan)
            {
                if (!have.TryGetValue(kv.Key, out var wid)) wid = have[kv.Key] = Workset.Create(doc, kv.Key).Id;
                foreach (var id in kv.Value.Distinct())
                {
                    var p = doc.GetElement(id)?.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (p != null && !p.IsReadOnly) p.Set(wid.IntegerValue);
                }
            }
        });
        sb.AppendLine("worksets assigned: " + st);
        var opt = new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 20 };
        opt.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
        doc.SaveAs(doc.PathName, opt);
        sb.AppendLine("saved as central " + doc.PathName);
        return "Collab apply\n" + sb;
    }
}
