using System.Globalization;
using System.Text.Json;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace IfcRebuild;

/// <summary>
/// Stairs and ramps as the native system families (input in m, world = project coordinates):
/// {"stairs": [{"name", "guid", "level", "base", "top", "risers", "tread", "waist", "landing_thk", "width", "material",
///              "run": [[x,y], [x,y]], "landing": [[x,y], ...], "ptype", "max_riser"?, "min_tread"?, "min_width"?, "keep_railings"?}],
///  "ramps": [{"name", "guid", "level", "base", "top", "width", "thickness", "material", "run": [[x,y], [x,y]], "ptype", "keep_railings"?}],
///  "seed_ramp": "ElementId of a ramp drawn by hand"}.
/// Stairs: StairsEditScope, one straight monolithic run and one sketched landing; the run, landing and stair types are
/// duplicated from the config's source types and named from the config typeNames.
/// Ramps: the API cannot create a ramp, so one seed ramp is drawn with the Ramp tool (Architecture > Circulation) and
/// every ramp is a copy of it whose sketch lines (Run, Riser, Boundary) are rewritten in an ordinary transaction;
/// SketchEditScope rejects ramp sketches ("Sketch does not support editing"). The railings Revit adds are deleted unless
/// the record keeps them. Elements already carrying the IfcGUID (stand-ins) are deleted first. mode "probe" | "apply".
/// </summary>
internal static class Circulation
{
    const double M = 1 / 0.3048;
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static double D(JsonElement e, string n, double def) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;
    static bool Keep(JsonElement e) => e.TryGetProperty("keep_railings", out var v) && v.ValueKind == JsonValueKind.True;
    static double[] Arr(JsonElement a) => a.EnumerateArray().Select(x => x.GetDouble()).ToArray();
    static XYZ P(JsonElement a, double z = 0) { var v = Arr(a); return new XYZ(v[0] * M, v[1] * M, (v.Length > 2 ? v[2] : z) * M); }

    static Material Mat(Document doc, string name) =>
        new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(x => x.Name == name)
        ?? (Material)doc.GetElement(Material.Create(doc, name));

    static T Dup<T>(Document doc, BuiltInCategory cat, string from, string name) where T : ElementType
    {
        var all = new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsElementType().Cast<ElementType>().ToList();
        var have = all.FirstOrDefault(t => t.Name == name);
        if (have != null) return (T)have;
        var src = all.FirstOrDefault(t => t.Name == from && t is T) ?? throw new InvalidOperationException($"source type '{from}' not in the project (config stair*TypeSource)");
        return (T)src.Duplicate(name);
    }

    static void Set(Element e, string name, double v) => (e.LookupParameter(name) ?? throw new InvalidOperationException(name + " on " + e.Name)).Set(v);

    static int DeleteByGuid(Document doc, IEnumerable<string> guids)
    {
        var set = new HashSet<string>(guids);
        var ids = new FilteredElementCollector(doc).WhereElementIsNotElementType()
            .Where(e => e.Category != null && set.Contains(GuidTools.Get(e) ?? "")).Select(e => e.Id).ToList();
        if (ids.Count > 0) doc.Delete(ids);
        return ids.Count;
    }

    /// <summary>Solid volume and box of an element (m3, m).</summary>
    static string Geo(Element e)
    {
        double v = 0; var lo = new XYZ(1e9, 1e9, 1e9); var hi = -lo;
        void Walk(GeometryElement g)
        {
            foreach (var o in g)
                if (o is Solid s && s.Volume > 1e-9) { v += s.Volume; var b = s.GetBoundingBox(); foreach (var q in new[] { b.Transform.OfPoint(b.Min), b.Transform.OfPoint(b.Max) }) { lo = new XYZ(Math.Min(lo.X, q.X), Math.Min(lo.Y, q.Y), Math.Min(lo.Z, q.Z)); hi = new XYZ(Math.Max(hi.X, q.X), Math.Max(hi.Y, q.Y), Math.Max(hi.Z, q.Z)); } }
                else if (o is GeometryInstance gi) Walk(gi.GetInstanceGeometry());
        }
        Walk(e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }));
        string F(XYZ p) => string.Join(",", new[] { p.X, p.Y, p.Z }.Select(c => (c * 0.3048).ToString("F4", CI)));
        return $"vol={v * 0.0283168466:F4} lo={F(lo)} hi={F(hi)}";
    }

    /// <summary>Unique (along, z) vertices of the solids of an element, along = distance on the run axis from o.</summary>
    static string Profile(Element e, XYZ o, XYZ ax)
    {
        var pts = new SortedSet<(double, double)>();
        void Walk(GeometryElement g)
        {
            foreach (var obj in g)
                if (obj is Solid s && s.Volume > 1e-9) { foreach (Edge ed in s.Edges) foreach (var q in ed.Tessellate()) pts.Add((Math.Round((q - o).DotProduct(ax) * 0.3048, 4), Math.Round(q.Z * 0.3048, 4))); }
                else if (obj is GeometryInstance gi) Walk(gi.GetInstanceGeometry());
        }
        Walk(e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }));
        return string.Join(" ", pts.Select(p => $"({p.Item1.ToString(CI)},{p.Item2.ToString(CI)})"));
    }

    public static string Run(Document doc, string jsonPath, string what, string mode, string filter)
    {
        var root = JsonDocument.Parse(File.ReadAllText(jsonPath)).RootElement;
        var sb = new StringBuilder();
        var names = string.IsNullOrEmpty(filter) ? null : new HashSet<string>(filter.Split(','));
        bool Pick(JsonElement r) => names == null || names.Contains(S(r, "name"));
        using var tg = new TransactionGroup(doc, "IfcRebuild: " + what);
        tg.Start();
        if (what == "stairs") foreach (var r in root.GetProperty("stairs").EnumerateArray().Where(Pick)) sb.AppendLine(Stair(doc, r, mode == "probe"));
        else if (what == "ramps") foreach (var r in root.GetProperty("ramps").EnumerateArray().Where(Pick)) sb.AppendLine(Ramp(doc, r, long.Parse(S(root, "seed_ramp") ?? throw new InvalidOperationException("seed_ramp missing: draw one ramp with the Ramp tool and give its ElementId"))));
        else sb.AppendLine($"what '{what}': expected stairs or ramps");
        if (mode == "apply") tg.Assimilate(); else tg.RollBack();
        return sb.ToString();
    }

    static Level LevelOf(Document doc, string name) => new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First(l => l.Name == name);

    /// <summary>One straight monolithic run (first riser at run[0], N risers, last riser onto the landing) and a sketched landing.</summary>
    static string Stair(Document doc, JsonElement r, bool dump)
    {
        string name = S(r, "name"), guid = S(r, "guid");
        var lv = LevelOf(doc, S(r, "level"));
        double zb = r.GetProperty("base").GetDouble(), zt = r.GetProperty("top").GetDouble();
        int n = r.GetProperty("risers").GetInt32();
        double tread = r.GetProperty("tread").GetDouble(), waist = r.GetProperty("waist").GetDouble(), land = r.GetProperty("landing_thk").GetDouble();
        string mat = S(r, "material");
        var o = RebuildConfig.Current;
        string stairTypeName = o.TypeName("stair", mat, 0);
        ElementId sid; double runTop = 0;
        var (st0, log0) = FailureLog.Run(doc, "types " + name, () =>
        {
            DeleteByGuid(doc, new[] { guid });
            var runT = Dup<StairsRunType>(doc, BuiltInCategory.OST_StairsRuns, o.StairRunTypeSource, o.TypeName("stairRun", mat, waist * 1000));
            runT.StructuralDepth = waist * M; runT.LookupParameter("Monolithic Material")?.Set(Mat(doc, mat).Id);
            var landT = Dup<StairsLandingType>(doc, BuiltInCategory.OST_StairsLandings, o.StairLandingTypeSource, o.TypeName("stairLanding", mat, land * 1000));
            landT.Thickness = land * M; landT.LookupParameter("Monolithic Material")?.Set(Mat(doc, mat).Id);
            var stT = Dup<StairsType>(doc, BuiltInCategory.OST_Stairs, o.StairTypeSource, stairTypeName);
            stT.RunType = runT.Id; stT.LandingType = landT.Id;
            stT.MaxRiserHeight = D(r, "max_riser", 0.25) * M; stT.MinTreadDepth = D(r, "min_tread", 0.25) * M; stT.MinRunWidth = D(r, "min_width", 1.0) * M;
            stT.get_Parameter(BuiltInParameter.FUNCTION_PARAM)?.Set(1);                     // Exterior
        });
        if (st0 != TransactionStatus.Committed) return $"{name}: types {st0} {log0.Summary().Split('\n')[0]}";
        var stT = new FilteredElementCollector(doc).OfClass(typeof(StairsType)).Cast<StairsType>().First(t => t.Name == stairTypeName);
        var log = new FailureLog();
        using (var scope = new StairsEditScope(doc, "Stairs " + name))
        {
            // Start needs a higher top level; the top is moved back onto the base level with offsets below
            var up = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Where(l => l.Elevation > lv.Elevation).OrderBy(l => l.Elevation).FirstOrDefault()
                     ?? throw new InvalidOperationException($"no level above {lv.Name}: StairsEditScope.Start needs one");
            sid = scope.Start(lv.Id, up.Id);
            using (var t = new Transaction(doc, "run " + name))
            {
                var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(log); fo.SetForcedModalHandling(false); t.SetFailureHandlingOptions(fo);
                t.Start();
                var st = (Stairs)doc.GetElement(sid);
                st.ChangeTypeId(stT.Id);
                st.get_Parameter(BuiltInParameter.STAIRS_TOP_LEVEL_PARAM).Set(lv.Id);
                st.get_Parameter(BuiltInParameter.STAIRS_BASE_OFFSET).Set(zb * M);
                st.get_Parameter(BuiltInParameter.STAIRS_TOP_OFFSET).Set(zt * M);
                st.DesiredRisersNumber = n;
                st.ActualTreadDepth = tread * M;
                var run = r.GetProperty("run");
                var a = P(run[0], lv.Elevation * 0.3048 + zb); var b = P(run[1], lv.Elevation * 0.3048 + zb);
                var sr = StairsRun.CreateStraightRun(doc, sid, Line.CreateBound(a, b), StairsRunJustification.Center);
                sr.ActualRunWidth = r.GetProperty("width").GetDouble() * M;
                // the run ends with the riser onto the landing and its top is the landing top; left alone, Revit rounds the
                // run top to its own riser height, and EndsWithRiser = false drops the last riser
                sr.BeginsWithRiser = true; sr.EndsWithRiser = true; sr.TopElevation = (zt - zb) * M;
                var loop = new CurveLoop();
                var lp = r.GetProperty("landing").EnumerateArray().Select(q => P(q, lv.Elevation * 0.3048 + zt)).ToList();
                for (int i = 0; i < lp.Count; i++) loop.Append(Line.CreateBound(lp[i], lp[(i + 1) % lp.Count]));
                doc.Regenerate();
                runTop = sr.TopElevation;
                StairsLanding.CreateSketchedLanding(doc, sid, loop, sr.TopElevation);   // relative to the stairs base
                t.Commit(fo);
            }
            scope.Commit(log);
        }
        var stairs = (Stairs)doc.GetElement(sid);
        var (st1, log1) = FailureLog.Run(doc, "tag " + name, () =>
        {
            GuidTools.Set(stairs, guid);
            if (S(r, "ptype") is string pt) stairs.get_Parameter(BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE)?.Set(pt);
            if (!Keep(r))
            {
                var rails = stairs.GetAssociatedRailings();
                if (rails.Count > 0) doc.Delete(rails);
            }
        });
        var res = $"{name}: runTop {runTop * 304.8:F1}, risers {stairs.ActualRisersNumber} x {stairs.ActualRiserHeight * 304.8:F1}, tread {stairs.ActualTreadDepth * 304.8:F1}, " +
                  $"H {stairs.Height * 304.8:F1}; {Geo(stairs)}; {log0.Lines.Count + log.Lines.Count + log1.Lines.Count} failures {string.Join(" | ", log.Lines.Concat(log0.Lines).Concat(log1.Lines).Take(4))}";
        if (dump)
        {
            var run = r.GetProperty("run"); var a = P(run[0]); var ax = (P(run[1]) - a).Normalize();
            foreach (var id in stairs.GetStairsRuns()) res += $"\n  run {Geo(doc.GetElement(id))}\n   {Profile(doc.GetElement(id), a, ax)}";
            foreach (var id in stairs.GetStairsLandings()) res += $"\n  landing {Geo(doc.GetElement(id))}";
        }
        return res;
    }

    /// <summary>Sketch lines of a ramp grouped by line style suffix: "Run", "Riser", "Boundary".</summary>
    static Dictionary<string, List<ModelCurve>> RampLines(Document doc, Element ramp)
    {
        var d = new Dictionary<string, List<ModelCurve>> { ["Run"] = new(), ["Riser"] = new(), ["Boundary"] = new() };
        foreach (var id in ramp.GetDependentElements(new ElementClassFilter(typeof(CurveElement))))
            if (doc.GetElement(id) is ModelCurve mc)
                foreach (var k in d.Keys) if ((mc.LineStyle?.Name ?? "").EndsWith(k + ">")) d[k].Add(mc);
        return d;
    }

    /// <summary>Copy of the seed ramp: straight rectangular run from run[0] (low end) to run[1] (high end), width,
    /// surface elevations base (low end) and top (high end) on the level, type thickness.</summary>
    static string Ramp(Document doc, JsonElement r, long seedId)
    {
        string name = S(r, "name"), guid = S(r, "guid");
        var lv = LevelOf(doc, S(r, "level"));
        double w = r.GetProperty("width").GetDouble(), thk = r.GetProperty("thickness").GetDouble();
        Element ramp = null;
        var (st, log) = FailureLog.Run(doc, "ramp " + name, () =>
        {
            DeleteByGuid(doc, new[] { guid });
            var seed = doc.GetElement(new ElementId(seedId)) ?? throw new InvalidOperationException($"seed ramp {seedId} not in the document");
            var tn = RebuildConfig.Current.TypeName("ramp", S(r, "material"), thk * 1000);
            var rt = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Ramps).WhereElementIsElementType().FirstOrDefault(t => t.Name == tn)
                     ?? ((ElementType)doc.GetElement(seed.GetTypeId())).Duplicate(tn);
            Set(rt, "Thickness", thk * M);
            Set(rt, "Ramp Max Slope (1/x)", 2.0);                   // the drawings fix the slope; the type limit only gates it
            Set(rt, "Maximum Incline Length", 30 * M);
            rt.get_Parameter(BuiltInParameter.FUNCTION_PARAM)?.Set(1);
            rt.LookupParameter("Ramp Material")?.Set(Mat(doc, S(r, "material")).Id);
            var id = ElementTransformUtils.CopyElement(doc, seed.Id, XYZ.Zero).First(i => doc.GetElement(i).Category?.Id.Value == (long)BuiltInCategory.OST_Ramps);
            ramp = doc.GetElement(id);
            ramp.ChangeTypeId(rt.Id);
        });
        if (ramp == null) return $"{name}: copy of the seed ramp failed {st} {log.Summary().Split('\n')[0]}";
        var lines = RampLines(doc, ramp);
        if (lines["Run"].Count != 1 || lines["Riser"].Count != 2 || lines["Boundary"].Count != 2)
            return $"{name}: unexpected sketch run {lines["Run"].Count} riser {lines["Riser"].Count} boundary {lines["Boundary"].Count}";
        var run = r.GetProperty("run");
        double z = lines["Run"][0].GeometryCurve.GetEndPoint(0).Z;
        var a = P(run[0], z / M); var b = P(run[1], z / M);
        var dir = (b - a).Normalize(); var nrm = new XYZ(-dir.Y, dir.X, 0) * (w * M / 2);
        var s0 = lines["Run"][0].GeometryCurve.GetEndPoint(0);
        var rs = lines["Riser"].OrderBy(c => c.GeometryCurve.Evaluate(0.5, true).DistanceTo(s0)).ToList();
        var (st2, log2) = FailureLog.Run(doc, "ramp sketch " + name, () =>
        {
            lines["Run"][0].SetGeometryCurve(Line.CreateBound(a, b), true);
            rs[0].SetGeometryCurve(Line.CreateBound(a + nrm, a - nrm), true);
            rs[1].SetGeometryCurve(Line.CreateBound(b + nrm, b - nrm), true);
            lines["Boundary"][0].SetGeometryCurve(Line.CreateBound(a + nrm, b + nrm), true);
            lines["Boundary"][1].SetGeometryCurve(Line.CreateBound(a - nrm, b - nrm), true);
        });
        var (st3, log3) = FailureLog.Run(doc, "ramp params " + name, () =>
        {
            ramp.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM).Set(lv.Id);
            ramp.get_Parameter(BuiltInParameter.STAIRS_TOP_LEVEL_PARAM).Set(lv.Id);
            ramp.get_Parameter(BuiltInParameter.STAIRS_BASE_OFFSET).Set(r.GetProperty("base").GetDouble() * M);
            ramp.get_Parameter(BuiltInParameter.STAIRS_TOP_OFFSET).Set(r.GetProperty("top").GetDouble() * M);
            GuidTools.Set(ramp, guid);
            if (S(r, "ptype") is string pt) ramp.get_Parameter(BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE)?.Set(pt);
            if (!Keep(r))
            {
                var rails = new FilteredElementCollector(doc).OfClass(typeof(Railing)).Cast<Railing>().Where(x => x.HostId == ramp.Id).Select(x => x.Id).ToList();
                if (rails.Count > 0) doc.Delete(rails);
            }
        });
        return $"{name}: {st2}/{st3} {Geo(ramp)}; failures {log.Lines.Count + log2.Lines.Count + log3.Lines.Count} {string.Join(" | ", log.Lines.Concat(log2.Lines).Concat(log3.Lines).Take(4))}";
    }
}
