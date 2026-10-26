using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace IfcRebuild;

/// <summary>
/// Join Geometry from the IFC build data: an element that "yields_to" another (its body is the IFC body minus the other's)
/// is joined with it and the other one cuts. One pair per transaction; a pair Revit refuses is logged. Joins Revit made on
/// its own that the IFC does not have are undone. Afterwards every cut element is measured against the IFC volume of the
/// geometry file. mode "probe" | "apply"; filter "phase=a-b" | "names=a,b" | "" (all instances and system elements of
/// the plan with yields_to).
/// </summary>
internal static class Joins
{
    static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int Phase(JsonElement r) => r.TryGetProperty("phase", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    public static string Run(Document doc, string planPath, string filter, string mode, string reportPath)
    {
        var sb = new StringBuilder();
        void Flush() { if (!string.IsNullOrEmpty(reportPath)) try { File.WriteAllText(reportPath, sb.ToString()); } catch { } }
        var t0 = DateTime.Now;
        using var js = JsonDocument.Parse(File.ReadAllText(planPath));
        var root = js.RootElement;
        var byMark = new FilteredElementCollector(doc).WhereElementIsNotElementType()
            .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model)
            .Select(e => (e, m: e.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString()))
            .Where(x => !string.IsNullOrEmpty(x.m) && x.m.Length == 22)
            .GroupBy(x => x.m).ToDictionary(g => g.Key, g => g.First().e);
        var byName = new Dictionary<string, string>();   // IFC name -> guid
        IEnumerable<JsonElement> List(string key) => root.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();
        var recs = List("instances").Concat(List("system")).ToList();
        foreach (var r in recs) if (S(r, "name") != null) byName[S(r, "name")] = S(r, "guid");
        var geo = Build.IfcGeometry(planPath);
        var pairs = new List<(string cut, string cutter, string name)>();
        int ph0 = 0, ph1 = int.MaxValue; HashSet<string> names = null;
        foreach (var part in (filter ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=');
            if (kv[0] == "phase") { var ab = kv[1].Split('-'); ph0 = int.Parse(ab[0]); ph1 = int.Parse(ab.Length > 1 ? ab[1] : ab[0]); }
            else if (kv[0] == "names") names = kv[1].Split(',').ToHashSet();
            else throw new ArgumentException($"filter key '{kv[0]}' is not supported (phase, names)");
        }
        bool InScope(JsonElement r) => Phase(r) >= ph0 && Phase(r) <= ph1 && (names == null || names.Contains(S(r, "name")));
        foreach (var r in recs.Where(InScope))
        {
            if (!r.TryGetProperty("yields_to", out var y)) continue;
            foreach (var yn in y.EnumerateArray().Select(x => x.GetString()).Distinct())
                if (byName.TryGetValue(yn, out var g2)) pairs.Add((S(r, "guid"), g2, S(r, "name") + " <- " + yn));
        }
        sb.AppendLine($"pairs from yields_to: {pairs.Count}");
        int joined = 0, already = 0, refused = 0, missing = 0, switched = 0;
        var fails = new Dictionary<string, int>();
        // apply: one committed transaction per pair and no group, so that an exception late in a long run keeps what was
        // done instead of rolling every join back; probe: a group that is rolled back at the end
        TransactionGroup tg = null;
        if (mode != "apply") { tg = new TransactionGroup(doc, "IfcRebuild: joins"); tg.Start(); }
        int k = 0;
        try
        {
            foreach (var (cut, cutter, nm) in pairs)
            {
                k++;
                if (!byMark.TryGetValue(cut, out var a) || !byMark.TryGetValue(cutter, out var b)) { missing++; continue; }
                try
                {
                    if (JoinGeometryUtils.AreElementsJoined(doc, a, b))
                    {
                        already++;
                        if (!JoinGeometryUtils.IsCuttingElementInJoin(doc, b, a))
                        {
                            var (st0, _) = FailureLog.Run(doc, "switch join", () => JoinGeometryUtils.SwitchJoinOrder(doc, a, b));
                            if (st0 == TransactionStatus.Committed) switched++;
                        }
                        continue;
                    }
                    var (st, log) = FailureLog.Run(doc, "join", () =>
                    {
                        JoinGeometryUtils.JoinGeometry(doc, a, b);
                        if (JoinGeometryUtils.AreElementsJoined(doc, a, b) && !JoinGeometryUtils.IsCuttingElementInJoin(doc, b, a)) JoinGeometryUtils.SwitchJoinOrder(doc, a, b);
                    });
                    if (st == TransactionStatus.Committed && JoinGeometryUtils.AreElementsJoined(doc, a, b)) joined++;
                    else
                    {
                        refused++;
                        var why = log.Lines.FirstOrDefault()?.Split('[')[0].Trim() ?? st.ToString();
                        fails[why] = fails.GetValueOrDefault(why) + 1;
                        if (refused <= 25) sb.AppendLine($"  REFUSED {nm}: {why}");
                    }
                }
                catch (Exception ex)
                {
                    refused++;
                    var why = ex.GetType().Name + ": " + ex.Message.Split('\n')[0].Trim();
                    fails[why] = fails.GetValueOrDefault(why) + 1;
                    if (refused <= 25) sb.AppendLine($"  REFUSED {nm}: {why}");
                }
                if (k % 500 == 0) { sb.AppendLine($"  ... {k} / {pairs.Count} ({(DateTime.Now - t0).TotalSeconds:F0} s)"); Flush(); Progress.Report($"Joins {k}/{pairs.Count}"); }
            }
        }
        catch (Exception ex) { sb.AppendLine($"ABORTED at pair {k}: {ex}"); Flush(); tg?.RollBack(); throw; }
        sb.AppendLine($"joined {joined}, already {already} (order switched {switched}), refused {refused}, element missing {missing} ({(DateTime.Now - t0).TotalSeconds:F0} s)");
        // joins Revit made on its own (concrete beams, columns, foundations, walls, floors join automatically) that the IFC
        // does not have: unjoined, so that only the IFC cuts remain
        Progress.Report("Joins: undoing automatic joins");
        var ifcPairs = new HashSet<string>();
        foreach (var r in recs)
            if (r.TryGetProperty("yields_to", out var y2))
                foreach (var yn in y2.EnumerateArray().Select(x => x.GetString()))
                    if (byName.TryGetValue(yn, out var g2)) { ifcPairs.Add(S(r, "guid") + "|" + g2); ifcPairs.Add(g2 + "|" + S(r, "guid")); }
        int unjoined = 0, unjoinFailed = 0;
        var idToMark = byMark.ToDictionary(kv => kv.Value.Id, kv => kv.Key);
        var scopeGuids = recs.Where(InScope).Select(r => S(r, "guid")).ToHashSet();
        var scope = names == null && ph0 == 0 && ph1 == int.MaxValue ? byMark : byMark.Where(kv => scopeGuids.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (mark, e) in scope)
        {
            foreach (var jid in JoinGeometryUtils.GetJoinedElements(doc, e).ToList())
            {
                if (!idToMark.TryGetValue(jid, out var m2)) continue;
                if (ifcPairs.Contains(mark + "|" + m2)) continue;
                try
                {
                    var (st, _) = FailureLog.Run(doc, "unjoin", () => JoinGeometryUtils.UnjoinGeometry(doc, e, doc.GetElement(jid)));
                    if (st == TransactionStatus.Committed) unjoined++; else unjoinFailed++;
                }
                catch (Exception) { unjoinFailed++; }
            }
        }
        sb.AppendLine($"automatic joins not in the IFC: unjoined {unjoined}, failed {unjoinFailed} ({(DateTime.Now - t0).TotalSeconds:F0} s)"); Flush();
        foreach (var f in fails.OrderByDescending(x => x.Value).Take(10)) sb.AppendLine($"  {f.Value}x {f.Key}");
        // volumes of the cut elements after the joins
        int ok = 0, n = 0; var miss = new List<string>(); var bands = new int[4];
        foreach (var cut in pairs.Select(p => p.cut).Distinct())
        {
            if (!byMark.TryGetValue(cut, out var e) || !geo.TryGetValue(cut, out var g) || g.v <= 0) continue;
            n++;
            double vol; try { vol = Build.SolidVolume(e) * Math.Pow(0.3048, 3); } catch (Exception) { continue; }
            double dv = (vol - g.v) / g.v;
            bands[Math.Abs(dv) <= 0.01 ? 0 : Math.Abs(dv) <= 0.02 ? 1 : Math.Abs(dv) <= 0.05 ? 2 : 3]++;
            if (Math.Abs(dv) <= 0.01) ok++; else if (miss.Count < 40) miss.Add($"  VOL {cut} {e.Category?.Name}: {vol:F4}/{g.v:F4} m3 ({dv * 100:F1}%)");
        }
        sb.AppendLine($"cut elements within 1 %: {ok} / {n}; bands <=1% {bands[0]}, <=2% {bands[1]}, <=5% {bands[2]}, >5% {bands[3]}" + (geo.Count == 0 ? " (no geometry file)" : ""));
        foreach (var m in miss) sb.AppendLine(m);
        if (tg != null) { tg.RollBack(); tg.Dispose(); }
        sb.AppendLine(mode == "apply" ? "APPLIED" : "probe: rolled back");
        Flush();
        return sb.ToString();
    }
}
