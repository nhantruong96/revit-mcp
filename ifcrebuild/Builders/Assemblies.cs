using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace IfcRebuild;

/// <summary>
/// One Revit Assembly per node of the JSON {"nodes": [{"guid", "name", "kind", "members": ["GlobalId", ...]}]}: the
/// members (plates, bolt groups, cleats of a steel connection) become members of an AssemblyInstance and keep their own
/// IfcGUID; the assembly carries the node GUID, so that an exported IFC gets a matching IfcElementAssembly. Revit reuses
/// one assembly type for identical nodes; a type created here is named "&lt;kind&gt; &lt;n&gt;". mode "probe" | "apply".
/// </summary>
internal static class Assemblies
{
    static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static string Run(Document doc, string jsonPath, string mode, string reportPath)
    {
        var sb = new StringBuilder();
        void Flush() { if (!string.IsNullOrEmpty(reportPath)) try { File.WriteAllText(reportPath, sb.ToString()); } catch { } }
        using var js = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var nodes = js.RootElement.GetProperty("nodes").EnumerateArray().ToList();
        var byGuid = new Dictionary<string, Element>();
        foreach (var e in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
        {
            if (e.SuperComponent != null) continue;
            var g = e.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString();
            if (!string.IsNullOrEmpty(g) && g.Length == 22) byGuid.TryAdd(g, e);
        }
        var namingCat = new ElementId(BuiltInCategory.OST_StructConnections);
        int made = 0, reused = 0, skipped = 0, missing = 0, failed = 0;
        var typeCount = new Dictionary<string, int>();
        var renamed = new HashSet<ElementId>();
        var typeOf = new List<string>();
        using var tg = new TransactionGroup(doc, "IfcRebuild: assemblies");
        tg.Start();
        int k = 0;
        foreach (var n in nodes)
        {
            k++;
            var ids = new List<ElementId>();
            foreach (var m in n.GetProperty("members").EnumerateArray())
            {
                if (byGuid.TryGetValue(m.GetString(), out var e))
                {
                    if (e.AssemblyInstanceId != ElementId.InvalidElementId) { skipped++; ids.Clear(); break; }
                    ids.Add(e.Id);
                }
                else missing++;
            }
            if (ids.Count < 2) { if (ids.Count > 0) skipped++; continue; }
            string kind = S(n, "kind") ?? "Steel node", guid = S(n, "guid"), name = S(n, "name");
            AssemblyInstance ai = null;
            // the assembly type exists only after the creating transaction is committed
            var (st, log) = FailureLog.Run(doc, "assembly", () => { ai = AssemblyInstance.Create(doc, ids, namingCat); });
            if (st == TransactionStatus.Committed && ai != null) (st, log) = FailureLog.Run(doc, "assembly name", () =>
            {
                var tid = ai.GetTypeId();
                if (!renamed.Contains(tid) && (ai.AssemblyTypeName.StartsWith("Assembly") || ai.AssemblyTypeName.StartsWith("Structural Connections")))
                {
                    typeCount[kind] = typeCount.GetValueOrDefault(kind) + 1;
                    ai.AssemblyTypeName = $"{kind} {typeCount[kind]}";
                    renamed.Add(tid);
                }
                else reused++;
                if (guid != null) ai.get_Parameter(BuiltInParameter.IFC_GUID)?.Set(guid);
                var pc = ai.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (pc != null && !pc.IsReadOnly && name != null) pc.Set(name);
            });
            if (st == TransactionStatus.Committed && ai != null) { made++; typeOf.Add(ai.AssemblyTypeName); }
            else { failed++; if (failed <= 10) sb.AppendLine($"  FAILED {name}: {log.Lines.FirstOrDefault() ?? st.ToString()}"); }
            if (k % 100 == 0) { sb.AppendLine($"  ... {k} / {nodes.Count}"); Flush(); Progress.Report($"Assemblies {k}/{nodes.Count}"); }
        }
        sb.AppendLine($"assemblies made {made} (types reused {reused}), failed {failed}, nodes skipped {skipped}, members missing {missing}");
        sb.AppendLine("types: " + string.Join("; ", typeOf.GroupBy(t => t).OrderByDescending(g => g.Count()).Take(15).Select(g => $"{g.Key} x{g.Count()}")));
        sb.AppendLine($"assembly types total: {typeOf.Distinct().Count()}");
        if (mode == "apply") tg.Assimilate(); else tg.RollBack();
        sb.AppendLine(mode == "apply" ? "APPLIED" : "probe: rolled back");
        Flush();
        return sb.ToString();
    }
}
