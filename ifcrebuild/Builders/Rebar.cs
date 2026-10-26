using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace IfcRebuild;

/// <summary>
/// Native Rebar, one Rebar element per bar set of the IFC (IfcReinforcingBar), hosted in the concrete element whose
/// IfcGUID is the host GlobalId. Input JSON (lengths in m):
/// {"hosts": {"host name": {"global_id": "...", "sets": [{"mark": "1", "shape": "...", "role": "...", "hook": 135 | null,
///   "stirrup": false, "diameter_mm": 12, "grade": "B500B", "centerline": [[x,y,z], ...], "count": 10, "spacing_m": 0.2,
///   "distribution": [x,y,z] | null, "total_length_m": 12.5}]}}}.
/// The shape is the centre line of the first bar; the layout is a fixed number along the distribution vector. Bar types
/// are found or made by name (config rebarBarTypeName). A 135 degree hook: the centre line holds both hook legs; they
/// are taken off the line, the bar gets the hook type of the config at both ends with the hook length of the legs, and
/// the termination orientation is searched by comparing the hook ends Revit draws with the leg ends. The orientation that
/// fits is remembered per shape and role and used for the next bar of that kind without searching again.
/// filter "hosts=a,b" | "max=n" | "range=a-b" (hosts in input order); mode "probe" | "apply". The pairs (bar set id
/// "host-Tmark-kk", UniqueId) go to mapPath as JSON lines.
/// </summary>
internal static class Rebars
{
    const double M = 1 / 0.3048;
    static XYZ P(JsonElement a) => new XYZ(a[0].GetDouble() * M, a[1].GetDouble() * M, a[2].GetDouble() * M);
    static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

    static RebarBarType BarType(Document doc, Dictionary<string, RebarBarType> cache, int d, string grade)
    {
        var o = RebuildConfig.Current;
        var name = o.RebarBarTypeName.Replace("{d}", d.ToString()).Replace("{grade}", grade).Trim();
        if (cache.TryGetValue(name, out var t)) return t;
        t = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().FirstOrDefault(x => x.Name == name);
        if (t == null)
        {
            t = RebarBarType.Create(doc); t.Name = name;
            t.BarNominalDiameter = d / 304.8; t.BarModelDiameter = d / 304.8;
            t.StandardBendDiameter = o.RebarBendDiameterFactor * d / 304.8; t.StirrupTieBendDiameter = o.RebarBendDiameterFactor * d / 304.8;
        }
        return cache[name] = t;
    }

    static XYZ PlaneNormal(List<XYZ> pts, XYZ dist)
    {
        for (int i = 1; i < pts.Count - 1; i++)
        {
            var n = (pts[i] - pts[0]).CrossProduct(pts[i + 1] - pts[0]);
            if (n.GetLength() > 1e-9) return n.Normalize();
        }
        var a = (pts[^1] - pts[0]).Normalize();
        if (dist != null && dist.GetLength() > 1e-9 && Math.Abs(dist.Normalize().DotProduct(a)) < 0.99) return (dist - a * dist.DotProduct(a)).Normalize();
        var up = Math.Abs(a.Z) < 0.9 ? XYZ.BasisZ : XYZ.BasisX;
        return a.CrossProduct(up).Normalize();
    }

    public static string Run(Document doc, string jsonPath, string filter, string mode, string mapPath, string reportPath)
    {
        var t0 = DateTime.Now; var sb = new StringBuilder();
        var root = JsonDocument.Parse(File.ReadAllText(jsonPath)).RootElement.GetProperty("hosts");
        var hosts = Batch.Pick(root.EnumerateObject().ToList(), filter.StartsWith("hosts=") ? "names=" + filter[6..] : filter, h => h.Name);
        var byGuid = GuidTools.ByGuid(doc);
        var cache = new Dictionary<string, RebarBarType>(); var maps = new List<string>();
        var hookName = RebuildConfig.Current.RebarHookType;
        var hook135 = new FilteredElementCollector(doc).OfClass(typeof(RebarHookType)).Cast<RebarHookType>().FirstOrDefault(x => x.Name == hookName);
        var orient = new Dictionary<string, (RebarTerminationOrientation, RebarTerminationOrientation)>();
        const double tailOk = 40 / 304.8;                        // accepted offset of a hook end (bend of 4 d), per end
        var hookErr = new List<double>();                        // hook end offset of Revit against the input centre line (mm, both ends)
        var made_ = new List<(ElementId id, string name, string shape, double lenJson)>();   // checked afterwards: sets Revit deleted while resolving shape errors
        int made = 0, bars = 0, noHost = 0, badHost = 0, fail = 0, k0 = 0; var errs = new List<string>();
        using var tg = new TransactionGroup(doc, "Rebar"); tg.Start();
        var (st, log) = FailureLog.Run(doc, "Rebar", () =>
        {
            foreach (var h in hosts)
            {
                if (++k0 % 10 == 0) Progress.Report($"Rebar host {k0}/{hosts.Count}, sets {made}");
                var gid = h.Value.GetProperty("global_id").GetString();
                if (!byGuid.TryGetValue(gid, out var host)) { noHost++; continue; }
                if (!RebarHostData.IsValidHost(host)) { badHost++; if (errs.Count < 15) errs.Add("host cannot take rebar: " + h.Name + " " + host.Category?.Name); continue; }
                int k = 0;
                foreach (var s in h.Value.GetProperty("sets").EnumerateArray())
                {
                    k++;
                    var setId = $"{h.Name}-T{S(s, "mark")}-{k:00}";
                    try
                    {
                        var pts = s.GetProperty("centerline").EnumerateArray().Select(P).ToList();
                        var kindKey = S(s, "shape") + "|" + S(s, "role");
                        // 135 degree hook (closed stirrup, cross tie): the centre line holds both hook legs; Revit wants the bar body and a hook type at both ends
                        bool hooked = s.TryGetProperty("hook", out var hv) && hv.ValueKind == JsonValueKind.Number && Math.Abs(hv.GetDouble() - 135) < 1 && pts.Count >= 4;
                        if (hooked && hook135 == null) throw new InvalidOperationException($"rebar hook type '{hookName}' not in the project (config rebarHookType)");
                        XYZ tailA = hooked ? pts[0] : null, tailB = hooked ? pts[^1] : null;
                        int dmm = s.GetProperty("diameter_mm").GetInt32(); string grade = S(s, "grade");
                        if (hooked)
                        {
                            pts = pts.GetRange(1, pts.Count - 2);
                            var tailL = (tailA.DistanceTo(pts[0]) + tailB.DistanceTo(pts[^1])) / 2;      // straight hook leg (feet) from the centre line
                            var btH = BarType(doc, cache, dmm, grade);
                            if (btH.GetAutoCalcHookLengths(hook135.Id) || Math.Abs(btH.GetHookLength(hook135.Id) - tailL) > 1e-4)
                            { btH.SetAutoCalcHookLengths(hook135.Id, false); btH.SetHookLength(hook135.Id, tailL); }
                        }
                        var curves = new List<Curve>();
                        for (int i = 0; i < pts.Count - 1; i++) if (pts[i].DistanceTo(pts[i + 1]) > 1e-6) curves.Add(Line.CreateBound(pts[i], pts[i + 1]));
                        XYZ dist = s.TryGetProperty("distribution", out var dv) && dv.ValueKind == JsonValueKind.Array ? new XYZ(dv[0].GetDouble(), dv[1].GetDouble(), dv[2].GetDouble()) : null;
                        var nrm = PlaneNormal(pts, dist);
                        bool stirrup = hooked || (s.TryGetProperty("stirrup", out var sv) && sv.ValueKind == JsonValueKind.True);
                        var style = stirrup ? RebarStyle.StirrupTie : RebarStyle.Standard;
                        var bt = BarType(doc, cache, dmm, grade);
                        Rebar rb = null;
                        if (!hooked) rb = Rebar.CreateFromCurves(doc, style, bt, host, nrm, curves, new BarTerminationsData(doc), true, true);
                        else if (orient.TryGetValue(kindKey, out var kn))
                        {                                                       // same kind of bar measured before: made directly, no search
                            var td = new BarTerminationsData(doc) { HookTypeIdAtStart = hook135.Id, HookTypeIdAtEnd = hook135.Id, TerminationOrientationAtStart = kn.Item1, TerminationOrientationAtEnd = kn.Item2 };
                            rb = Rebar.CreateFromCurves(doc, style, bt, host, nrm, curves, td, true, true);
                        }
                        else
                        {
                            double bestE = double.MaxValue;
                            var combos = Enum.GetValues<RebarTerminationOrientation>().SelectMany(x => Enum.GetValues<RebarTerminationOrientation>().Select(y => (x, y))).ToList();
                            foreach (var (oa, ob) in combos)
                            {
                                if (bestE < 2 * tailOk) break;
                                Rebar r = null;
                                try
                                {
                                    var td = new BarTerminationsData(doc) { HookTypeIdAtStart = hook135.Id, HookTypeIdAtEnd = hook135.Id, TerminationOrientationAtStart = oa, TerminationOrientationAtEnd = ob };
                                    r = Rebar.CreateFromCurves(doc, style, bt, host, nrm, curves, td, true, true);
                                    doc.Regenerate();
                                    var cl = r.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
                                    XYZ e0 = cl[0].GetEndPoint(0), e1 = cl[cl.Count - 1].GetEndPoint(1);
                                    double er = Math.Min(e0.DistanceTo(tailA) + e1.DistanceTo(tailB), e0.DistanceTo(tailB) + e1.DistanceTo(tailA));
                                    if (er < bestE) { if (rb != null) doc.Delete(rb.Id); rb = r; bestE = er; r = null; orient[kindKey] = (oa, ob); }
                                }
                                catch { }
                                if (r != null && r.IsValidObject) doc.Delete(r.Id);
                            }
                            if (rb == null) throw new InvalidOperationException("no 135 degree hooked bar could be made");
                            hookErr.Add(bestE * 304.8);
                        }
                        int n = s.GetProperty("count").GetInt32();
                        if (n > 1 && dist != null)
                        {
                            double step = s.GetProperty("spacing_m").GetDouble() * M;
                            bool side = dist.DotProduct(nrm) > 0;
                            rb.GetShapeDrivenAccessor().SetLayoutAsFixedNumber(n, step * (n - 1), side, true, true);
                        }
                        rb.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(setId);
                        made++; bars += n;
                        made_.Add((rb.Id, setId, S(s, "shape") + " | " + S(s, "role"),
                                   s.TryGetProperty("total_length_m", out var tl) && tl.ValueKind == JsonValueKind.Number ? tl.GetDouble() : 0));
                        maps.Add("{\"gid\":\"" + setId + "\",\"uid\":\"" + rb.UniqueId + "\",\"role\":\"REBAR\"}");
                    }
                    catch (Exception ex) { fail++; if (errs.Count < 25) errs.Add(setId + ": " + ex.Message); }
                }
            }
        });
        sb.AppendLine($"Rebar {mode} {DateTime.Now:HH:mm:ss}: {st} | sets {made} bars {bars} | host not found {noHost} | host takes no rebar {badHost} | errors {fail} | {(DateTime.Now - t0).TotalSeconds:F0} s");
        var gone = made_.Where(x => doc.GetElement(x.id) == null).ToList(); var alive = made_.Where(x => doc.GetElement(x.id) != null).ToList();
        double lenR = alive.Sum(x => (doc.GetElement(x.id) as Rebar).TotalLength * 0.3048), lenJ = alive.Sum(x => x.lenJson);
        sb.AppendLine($"kept {alive.Count}/{made_.Count} sets | deleted by Revit {gone.Count} | total length Revit {lenR:F1} m, input {lenJ:F1} m ({(lenJ > 0 ? (lenR - lenJ) / lenJ * 100 : 0):F2} %)");
        if (hookErr.Count > 0) sb.AppendLine($"135 hooks: {hookErr.Count} sets searched, hook end offset median {hookErr.OrderBy(x => x).ElementAt(hookErr.Count / 2):F0} mm, max {hookErr.Max():F0} mm");
        foreach (var g in gone.GroupBy(x => x.shape).OrderByDescending(g => g.Count()).Take(12)) sb.AppendLine($"  deleted {g.Count()}: {g.Key} (e.g. {g.First().name})");
        var goneNames = gone.Select(x => "\"gid\":\"" + x.name + "\"").ToList();
        maps.RemoveAll(m => goneNames.Any(m.Contains));
        foreach (var l in log.Lines.Take(10)) sb.AppendLine("  " + l);
        foreach (var e in errs) sb.AppendLine("ERR " + e);
        if (mode == "apply") { tg.Assimilate(); if (!string.IsNullOrEmpty(mapPath) && maps.Count > 0) File.AppendAllLines(mapPath, maps); }
        else tg.RollBack();
        if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, sb.ToString());
        return sb.ToString();
    }
}
