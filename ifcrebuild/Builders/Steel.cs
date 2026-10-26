using System.Globalization;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Steel;
using Autodesk.AdvanceSteel.CADAccess;
using Autodesk.AdvanceSteel.ConstructionTypes;
using Autodesk.AdvanceSteel.Geometry;
using Autodesk.AdvanceSteel.Modelling;
using Plane = Autodesk.AdvanceSteel.Geometry.Plane;
using Autodesk.SteelConnectionsDB;

namespace IfcRebuild;

/// <summary>
/// Steel Connections (Advance Steel) fabrication elements: plates, bolt patterns and anchor patterns, as a modeller makes
/// them with the Steel Connections tools. Input JSON (mm, same coordinates as the Revit model):
/// {"plates": [{"name", "gid", "node", "pieces": [{"pts": [[x,y,z]...], "n": [x,y,z], "t": mm}]}],
///  "bolts": [{"name", "gid", "node", "d", "grade", "grid": {"o", "u", "v", "nu", "nv", "du", "dv"} | null, "axis": [x,y,z], "bolts": [{"p0", "p1"}]}],
///  "anchors": [{"gid", "node", "d", "pts": [[top], ...]}]}.
/// "node" is the GlobalId of the connection node (its assembly is replaced) or null. Bolts are connected to the plates
/// of their node and to the steel framing their shank passes through.
///
/// Two traps, both measured: FabricationTransaction(doc, false, name) - the second argument is isReadOnly; passing true
/// and then writing shuts Revit down. And the transactions Steel Connections opens on its own raise warnings that no
/// preprocessor of ours sees; the job runner deletes them in the FailuresProcessing event, otherwise a modal warning
/// dialog blocks the job.
///
/// Elements already built are found again through the map file (JSON lines {"gid", "as", "role", "uid"}: "as" = Steel
/// Connections id written by Build (BOLTS lines also carry "h", the Steel Connections handle), "uid" = Revit UniqueId of the SteelProxyElement added by GuidMap): DeleteByMap, BoltReport,
/// SetGrip and SetContour work on them in place, so their ids (and the GlobalIds exported from them) stay.
/// </summary>
internal static class Steel
{
    const double FT = 304.8;
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    static Point3d Pt(JsonElement a) => new(a[0].GetDouble(), a[1].GetDouble(), a[2].GetDouble());
    static Vector3d Vc(JsonElement a) => new(a[0].GetDouble(), a[1].GetDouble(), a[2].GetDouble());
    static string Box(Document doc, ElementId id)
    {
        var el = doc.GetElement(id); var bb = el?.get_BoundingBox(null);
        if (bb == null && el != null)                               // SteelProxyElement: no bounding box, taken from the solids
        {
            var pts = new List<XYZ>();
            void Walk(GeometryElement ge) { foreach (var o in ge) { if (o is Solid so) foreach (Edge ed in so.Edges) pts.AddRange(ed.Tessellate()); else if (o is GeometryInstance gi) Walk(gi.GetInstanceGeometry()); } }
            var g0 = el.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }); if (g0 != null) Walk(g0);
            if (pts.Count > 0) bb = new BoundingBoxXYZ { Min = new XYZ(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z)), Max = new XYZ(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z)) };
        }
        if (bb == null) return "nobb";
        return string.Format(CI, "[{0:F1},{1:F1},{2:F1}]-[{3:F1},{4:F1},{5:F1}]", bb.Min.X * FT, bb.Min.Y * FT, bb.Min.Z * FT, bb.Max.X * FT, bb.Max.Y * FT, bb.Max.Z * FT);
    }

    /// <summary>Smallest Steel Connections call: one 200 x 100 x 10 plate at the origin (smoke test on an empty project).</summary>
    public static string Smoke(Document doc)
    {
        using var tr = new FabricationTransaction(doc, false, "Smoke plate");     // false = not read-only
        var pts = new[] { new Point3d(0, 0, 0), new Point3d(200, 0, 0), new Point3d(200, 100, 0), new Point3d(0, 100, 0) };
        var pl = new Plate(new Plane(pts[0], new Vector3d(0, 0, 1)), pts, 10);
        pl.WriteToDb(); tr.Commit();
        return "plate " + pl.GetUniqueId() + " | " + doc.Title;
    }

    /// <summary>Bolt count and spacing of a rectangular pattern (Nx x Ny, Dx, Dy; the constructor alone gives 2 x 2).</summary>
    static void SetGrid(FinitRectScrewBoltPattern bp, int nu, int nv, double du, double dv)
    {
        bp.Nx = Math.Max(nu, 1); bp.Ny = Math.Max(nv, 1);
        if (nu > 1 && du > 0) bp.Dx = du; if (nv > 1 && dv > 0) bp.Dy = dv;
    }

    /// <summary>Filer object of a Revit element (beam, column) so bolts can connect to it.</summary>
    internal static FilerObject Filer(Document doc, Element e)
    {
        SteelElementProperties.AddFabricationInformationForRevitElements(doc, new List<ElementId> { e.Id });
        var g = SteelElementProperties.GetFabricationUniqueID(doc, new Reference(e));
        return g == Guid.Empty ? null : FilerObject.GetFilerObjectByHandle(g.ToString());
    }

    /// <summary>One plate (by name) and one bolt group (by name), rolled back; reports what was made.</summary>
    public static string Probe(Document doc, string jsonPath, string plateName, string boltName)
    {
        var sb = new StringBuilder();
        var root = JsonDocument.Parse(File.ReadAllText(jsonPath)).RootElement;
        JsonElement plate = default, bolt = default;
        foreach (var p in root.GetProperty("plates").EnumerateArray()) if (p.GetProperty("name").GetString() == plateName) plate = p;
        foreach (var b in root.GetProperty("bolts").EnumerateArray()) if (b.GetProperty("name").GetString() == boltName) bolt = b;
        var before = new HashSet<long>(new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElementIds().Select(i => i.Value));
        using var tg = new TransactionGroup(doc, "probe steel"); tg.Start();
        try
        {
            if (plate.ValueKind == JsonValueKind.Object)
                using (var tr = new FabricationTransaction(doc, false, "Probe plate"))
                {
                    foreach (var pc in plate.GetProperty("pieces").EnumerateArray())
                    {
                        var pts = pc.GetProperty("pts").EnumerateArray().Select(Pt).ToArray();
                        var pl = new Plate(new Plane(pts[0], Vc(pc.GetProperty("n"))), pts, pc.GetProperty("t").GetDouble());
                        pl.Portioning = 0; pl.WriteToDb();
                        sb.AppendLine($"plate piece t={pc.GetProperty("t").GetDouble()} portioning={pl.Portioning} thick={pl.Thickness} upperZ={pl.UpperZPos} lowerZ={pl.LowerZPos} handle={pl.Handle}");
                    }
                    tr.Commit();
                }
            else sb.AppendLine($"no plate named {plateName}");
            if (bolt.ValueKind == JsonValueKind.Object && bolt.GetProperty("grid").ValueKind == JsonValueKind.Object)
            {
                var g = bolt.GetProperty("grid");
                var o = Pt(g.GetProperty("o")); var u = Vc(g.GetProperty("u")); var v = Vc(g.GetProperty("v"));
                int nu = g.GetProperty("nu").GetInt32(), nv = g.GetProperty("nv").GetInt32();
                double du = g.GetProperty("du").GetDouble(), dv = g.GetProperty("dv").GetDouble();
                var o2 = o + u * (du * (nu - 1)) + v * (dv * (nv - 1));
                using var tr = new FabricationTransaction(doc, false, "Probe bolts");
                var bp = new FinitRectScrewBoltPattern(o, o2, u, v);
                SetGrid(bp, nu, nv, du, dv);
                bp.ScrewDiameter = bolt.GetProperty("d").GetDouble();
                bp.WriteToDb();
                sb.AppendLine($"bolts nscrews={bp.NumberOfScrews} len={bp.ScrewLength} d={bp.ScrewDiameter} std={bp.Standard} grade={bp.Grade} normal={bp.Normal.x:F3},{bp.Normal.y:F3},{bp.Normal.z:F3}");
                tr.Commit();
            }
            var added = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElementIds().Where(i => !before.Contains(i.Value)).ToList();
            foreach (var id in added.Take(20)) { var e = doc.GetElement(id); sb.AppendLine($"new {id.Value} {e.GetType().Name} cat={e.Category?.Name} {Box(doc, id)}"); }
        }
        catch (Exception ex) { sb.AppendLine("ERROR " + ex); }
        finally { tg.RollBack(); }
        return sb.ToString();
    }

    /// <summary>
    /// Plates, bolt patterns and anchor patterns for every node (or the nodes of the filter "nodes=gid,gid" | "max=n" |
    /// "range=a-b"), replacing the loadable plate and bolt families and the assembly of the node (same IfcGUIDs).
    /// mode "probe" rolls back. The pairs (gid, Steel Connections unique id) go to mapPath as JSON lines.
    /// </summary>
    public static string Build(Document doc, string jsonPath, string filter, string mode, string mapPath, string reportPath)
    {
        var sb = new StringBuilder(); var t0 = DateTime.Now;
        var root = JsonDocument.Parse(File.ReadAllText(jsonPath)).RootElement;
        var plates = root.GetProperty("plates").EnumerateArray().ToList();
        var bolts = root.GetProperty("bolts").EnumerateArray().ToList();
        var anchors = root.TryGetProperty("anchors", out var an) ? an.EnumerateArray().ToList() : new List<JsonElement>();
        string Node(JsonElement x) => x.GetProperty("node").ValueKind == JsonValueKind.String ? x.GetProperty("node").GetString() : "-" + x.GetProperty("gid").GetString();
        var nodes = plates.Select(Node).Concat(bolts.Select(Node)).Concat(anchors.Select(Node)).Distinct().ToList();
        nodes = Batch.Pick(nodes, filter.StartsWith("nodes=") ? "names=" + filter[6..] : filter, n => n);
        var nodeSet = new HashSet<string>(nodes);
        plates = plates.Where(x => nodeSet.Contains(Node(x))).ToList(); bolts = bolts.Where(x => nodeSet.Contains(Node(x))).ToList(); anchors = anchors.Where(x => nodeSet.Contains(Node(x))).ToList();
        var byGuid = GuidTools.ByGuid(doc);
        var frameCats = new List<BuiltInCategory> { BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns };
        var maps = new List<string>(); int np = 0, nb = 0, nbp = 0, na = 0, nconn = 0, nconnFail = 0, nerr = 0, k = 0; var errs = new List<string>();
        var filerCache = new Dictionary<long, FilerObject>();
        FilerObject FrameFiler(Element e)
        {
            if (filerCache.TryGetValue(e.Id.Value, out var f)) return f;
            try { f = Filer(doc, e); } catch { f = null; }
            filerCache[e.Id.Value] = f; return f;
        }
        List<FilerObject> FramesOnSegment(Point3d a, Point3d b)
        {
            var mn = new XYZ(Math.Min(a.x, b.x) / FT, Math.Min(a.y, b.y) / FT, Math.Min(a.z, b.z) / FT) - new XYZ(0.03, 0.03, 0.03);
            var mx = new XYZ(Math.Max(a.x, b.x) / FT, Math.Max(a.y, b.y) / FT, Math.Max(a.z, b.z) / FT) + new XYZ(0.03, 0.03, 0.03);
            var res = new List<FilerObject>();
            foreach (var e in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(frameCats)).WherePasses(new BoundingBoxIntersectsFilter(new Outline(mn, mx))).ToElements())
            {
                var mat = doc.GetElement(e.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId) as Material;
                if (mat != null && mat.MaterialClass == "Concrete") continue;
                var f = FrameFiler(e); if (f != null) res.Add(f);
            }
            return res;
        }
        using var tg = new TransactionGroup(doc, "Steel connections"); tg.Start();
        try
        {
            foreach (var node in nodes)
            {
                if (++k % 10 == 0) Progress.Report($"Steel node {k}/{nodes.Count}, plates {np}, bolts {nb}");
                var nPlates = plates.Where(x => Node(x) == node).ToList(); var nBolts = bolts.Where(x => Node(x) == node).ToList(); var nAnch = anchors.Where(x => Node(x) == node).ToList();
                var plateFilers = new List<(FilerObject fo, string gid)>();
                try
                {
                    using (var tr = new FabricationTransaction(doc, false, "Plates " + node))      // false = not read-only
                    {
                        foreach (var p in nPlates)
                            foreach (var pc in p.GetProperty("pieces").EnumerateArray())
                            {
                                var pts = pc.GetProperty("pts").EnumerateArray().Select(Pt).ToArray();
                                var pl = new Plate(new Plane(pts[0], Vc(pc.GetProperty("n"))), pts, pc.GetProperty("t").GetDouble());
                                pl.Portioning = 1;                       // thickness on the +n side, as the IFC extrusion from its base face; 0 = -n side
                                pl.Denotation = p.GetProperty("name").GetString();
                                pl.WriteToDb(); plateFilers.Add((pl, p.GetProperty("gid").GetString())); np++;
                            }
                        foreach (var b in nBolts)
                        {
                            var d = b.GetProperty("d").GetDouble(); var gd = b.TryGetProperty("grade", out var gv) && gv.ValueKind == JsonValueKind.String ? gv.GetString() : null;
                            var specs = new List<(Point3d o, Point3d o2, Vector3d u, Vector3d v, double du, double dv, int nu, int nv, Point3d a, Point3d e)>();
                            if (b.GetProperty("grid").ValueKind == JsonValueKind.Object)
                            {
                                var g = b.GetProperty("grid"); var o = Pt(g.GetProperty("o")); var u = Vc(g.GetProperty("u")); var v = Vc(g.GetProperty("v"));
                                int nu = g.GetProperty("nu").GetInt32(), nv = g.GetProperty("nv").GetInt32(); double du = g.GetProperty("du").GetDouble(), dv = g.GetProperty("dv").GetDouble();
                                var b0 = b.GetProperty("bolts")[0];
                                specs.Add((o, o + u * (du * (nu - 1)) + v * (dv * (nv - 1)), u, v, du, dv, nu, nv, Pt(b0.GetProperty("p0")), Pt(b0.GetProperty("p1"))));
                            }
                            else
                            {
                                var ax = Vc(b.GetProperty("axis")); var u = ax.GetPerpVector(); u.Normalize(); var v = ax.CrossProduct(u);
                                foreach (var bb in b.GetProperty("bolts").EnumerateArray()) { var o = Pt(bb.GetProperty("p0")); specs.Add((o, o, u, v, 0, 0, 1, 1, o, Pt(bb.GetProperty("p1")))); }
                            }
                            foreach (var sp in specs)
                            {
                                var bp = new FinitRectScrewBoltPattern(sp.o, sp.o2, sp.u, sp.v);
                                SetGrid(bp, sp.nu, sp.nv, sp.du, sp.dv);                  // bolt count and spacing (2 x 2 by default)
                                bp.ScrewDiameter = d; if (!string.IsNullOrEmpty(gd)) { try { bp.Grade = gd; } catch { } }
                                bp.WriteToDb();
                                var objs = plateFilers.Select(x => x.fo).ToList(); objs.AddRange(FramesOnSegment(sp.a, sp.e));
                                try { if (objs.Count > 0) { bp.Connect(objs.ToArray(), AtomicElement.eAssemblyLocation.kOnSite); nconn++; } } catch { nconnFail++; }
                                nbp++; nb += bp.NumberOfScrews;
                                maps.Add("{\"gid\":\"" + b.GetProperty("gid").GetString() + "\",\"as\":\"" + bp.GetUniqueId() + "\",\"h\":\"" + bp.Handle + "\",\"role\":\"BOLTS\"}");
                            }
                        }
                        if (nAnch.Count > 0)
                        {
                            var tops = nAnch.Select(a => Pt(a.GetProperty("pts")[0])).ToList();
                            var xs = tops.Select(q => Math.Round(q.x, 1)).Distinct().OrderBy(q => q).ToList(); var ys = tops.Select(q => Math.Round(q.y, 1)).Distinct().OrderBy(q => q).ToList();
                            var o = new Point3d(xs[0], ys[0], tops.Max(q => q.z));
                            var ap = new AnchorPattern(o, new Point3d(xs[^1], ys[^1], o.z), new Vector3d(1, 0, 0), new Vector3d(0, 1, 0));
                            ap.Nx = xs.Count; ap.Ny = ys.Count; if (xs.Count > 1) ap.Dx = (xs[^1] - xs[0]) / (xs.Count - 1); if (ys.Count > 1) ap.Dy = (ys[^1] - ys[0]) / (ys.Count - 1);
                            ap.ScrewDiameter = nAnch[0].GetProperty("d").GetDouble();
                            ap.WriteToDb();
                            try { if (plateFilers.Count > 0) ap.Connect(plateFilers.Select(x => x.fo).ToArray(), AtomicElement.eAssemblyLocation.kOnSite); } catch { nconnFail++; }
                            na += ap.NumberOfScrews;
                            foreach (var a in nAnch) maps.Add("{\"gid\":\"" + a.GetProperty("gid").GetString() + "\",\"as\":\"" + ap.GetUniqueId() + "\",\"role\":\"ANCHORS\"}");
                        }
                        tr.Commit();
                    }
                    foreach (var (fo, gid) in plateFilers) maps.Add("{\"gid\":\"" + gid + "\",\"as\":\"" + fo.GetUniqueId() + "\",\"role\":\"PLATE\"}");
                    FailureLog.Run(doc, "Delete replaced plates and bolts " + node, () =>
                    {
                        var del = new List<ElementId>();
                        if (!node.StartsWith("-") && byGuid.TryGetValue(node, out var asm)) del.Add(asm.Id);
                        foreach (var x in nPlates.Concat(nBolts).Concat(nAnch)) if (byGuid.TryGetValue(x.GetProperty("gid").GetString(), out var oe)) del.Add(oe.Id);
                        foreach (var id in del) { try { if (doc.GetElement(id) != null) doc.Delete(id); } catch { } }
                    });
                }
                catch (Exception ex) { nerr++; if (errs.Count < 20) errs.Add(node + ": " + ex.Message); }
            }
            sb.AppendLine($"nodes {nodes.Count} | plates {np} | bolt groups {nbp} bolts {nb} (connected {nconn}, connect failed {nconnFail}) | anchors {na} | node errors {nerr} | {(DateTime.Now - t0).TotalSeconds:F0} s");
            foreach (var e in errs) sb.AppendLine("ERR " + e);
            var added = new FilteredElementCollector(doc).OfClass(typeof(SteelProxyElement)).ToElements().OrderByDescending(e => e.Id.Value).Take(10);
            foreach (var e in added) sb.AppendLine($"steel {e.Id.Value} {e.Category?.Name} {Box(doc, e.Id)}");
        }
        catch (Exception ex) { sb.AppendLine("ERROR " + ex); mode = "probe"; }
        if (mode == "apply") { tg.Assimilate(); if (!string.IsNullOrEmpty(mapPath) && maps.Count > 0) File.AppendAllLines(mapPath, maps); }
        else tg.RollBack();
        sb.Insert(0, $"Steel {mode} {DateTime.Now:HH:mm:ss}\n");
        if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, sb.ToString());
        return sb.ToString();
    }

    // ---------------------------------------------------------------- elements already built, found through the map file

    static List<JsonElement> MapRows(string mapPath) =>
        File.ReadAllLines(mapPath).Where(l => l.Trim().Length > 0).Select(l => JsonDocument.Parse(l).RootElement).ToList();
    static string Role(JsonElement r) => r.TryGetProperty("role", out var v) ? v.GetString() : "";
    /// <summary>Steel Connections id as a lookup key: no braces, lower case.</summary>
    static string Key(string asId) => asId.Trim('{', '}').ToLowerInvariant();

    /// <summary>Advance Steel model objects of type T by Key(unique id); call inside a FabricationTransaction.</summary>
    static Dictionary<string, T> AsObjects<T>() where T : FilerObject
    {
        DatabaseManager.GetModelObjectIds(out Autodesk.AdvanceSteel.CADLink.Database.ObjectId[] ids);
        var res = new Dictionary<string, T>();
        foreach (var id in ids) { try { if (DatabaseManager.Open(id) is T x) res[Key(x.GetUniqueId().ToString())] = x; } catch { } }
        return res;
    }

    /// <summary>Read-only count of Advance Steel model objects by type (plates, bolt patterns, anchor patterns, others) and of
    /// SteelProxyElement.</summary>
    public static string Count(Document doc)
    {
        var c = new Dictionary<string, int>();
        using (var tr = new FabricationTransaction(doc, true, "Count Steel Connections objects"))
        {
            DatabaseManager.GetModelObjectIds(out Autodesk.AdvanceSteel.CADLink.Database.ObjectId[] ids);
            foreach (var id in ids) { try { var n = DatabaseManager.Open(id)?.GetType().Name ?? "null"; c[n] = c.GetValueOrDefault(n) + 1; } catch { c["unreadable"] = c.GetValueOrDefault("unreadable") + 1; } }
        }
        return "Steel Connections " + string.Join(", ", c.OrderByDescending(k => k.Value).Select(k => k.Key + " " + k.Value)) +
               " | SteelProxyElement " + new FilteredElementCollector(doc).OfClass(typeof(SteelProxyElement)).GetElementCount();
    }

    /// <summary>Deletes the Revit elements (Steel Connections proxies, framing) of the source GlobalIds listed in gidsPath (one per
    /// line), found through the "uid" of their map lines, so their nodes can be rebuilt. mode probe | apply. The map lines
    /// themselves are left for the caller to remove.</summary>
    public static string DeleteByMap(Document doc, string mapPath, string gidsPath, string mode)
    {
        var t0 = DateTime.Now;
        var want = new HashSet<string>(File.ReadAllLines(gidsPath).Select(s => s.Trim()).Where(s => s.Length > 0));
        var rows = MapRows(mapPath).Where(r => want.Contains(r.GetProperty("gid").GetString())).ToList();
        var ids = new HashSet<ElementId>(); var roles = new Dictionary<string, int>(); int miss = 0;
        foreach (var r in rows)
        {
            var uid = r.TryGetProperty("uid", out var u) ? u.GetString() : null;
            var e = string.IsNullOrEmpty(uid) ? null : doc.GetElement(uid);
            if (e == null) { miss++; continue; }
            ids.Add(e.Id); var ro = Role(r); roles[ro] = roles.GetValueOrDefault(ro) + 1;
        }
        int Proxies() => new FilteredElementCollector(doc).OfClass(typeof(SteelProxyElement)).GetElementCount();
        int p0 = Proxies();
        using var tg = new TransactionGroup(doc, "Delete by map"); tg.Start();
        var (st, _) = FailureLog.Run(doc, "Delete by map", () => { foreach (var id in ids) { try { if (doc.GetElement(id) != null) doc.Delete(id); } catch { } } });
        int p1 = Proxies(); int left = ids.Count(id => doc.GetElement(id) != null);
        if (mode == "apply") tg.Assimilate(); else tg.RollBack();
        return $"DeleteByMap {mode}: gids {want.Count} | map lines {rows.Count} | elements {ids.Count} ({string.Join(", ", roles.Select(k => k.Key + " " + k.Value))}) | not found {miss} | " +
               $"SteelProxyElement {p0} -> {p1} | left after delete {left} | {st} | {(DateTime.Now - t0).TotalSeconds:F0} s";
    }

    /// <summary>Read-only: grip (BindingLength, measured by Advance Steel from the connected parts), its addition, bolt length,
    /// diameter and centre of every bolt pattern in the map lines role BOLTS -> CSV gid,as,n,d,grip,add,length,cx,cy,cz (mm).</summary>
    public static string BoltReport(Document doc, string mapPath, string outPath)
    {
        var t0 = DateTime.Now;
        var rows = MapRows(mapPath).Where(r => Role(r) == "BOLTS").Select(r => (gid: r.GetProperty("gid").GetString(), key: Key(r.GetProperty("as").GetString()))).ToList();
        var lines = new List<string> { "gid,as,n,d,grip,add,length,cx,cy,cz" }; int miss = 0;
        using (var tr = new FabricationTransaction(doc, true, "Read bolt patterns"))
        {
            var byUid = AsObjects<ScrewBoltPattern>();
            foreach (var (gid, key) in rows)
            {
                if (!byUid.TryGetValue(key, out var bp)) { miss++; continue; }
                var c = bp.Center;
                lines.Add(string.Format(CI, "{0},{1},{2},{3:F1},{4:F1},{5:F1},{6:F1},{7:F1},{8:F1},{9:F1}", gid, key, bp.NumberOfScrews, bp.ScrewDiameter, bp.BindingLength, bp.BindingLengthAddition, bp.ScrewLength, c.x, c.y, c.z));
            }
        }
        File.WriteAllLines(outPath, lines);
        return $"BoltReport: BOLTS lines {rows.Count} | read {lines.Count - 1} | not found {miss} | {(DateTime.Now - t0).TotalSeconds:F0} s";
    }

    /// <summary>Grip of bolt patterns through Revit framing that Advance Steel does not measure (L-angle and light-gauge families:
    /// "Connected element not supported"). CSV lines "as,grip_mm,how[,cx,cy,cz]": how "len" sets BindingLength (no connected part
    /// measured), "add" sets BindingLengthAddition = grip - measured grip (the connected plates are measured and would reset
    /// BindingLength); cx, cy, cz = bolt centre in the source model (mm): the pattern is moved along its normal onto it.
    /// Lines whose grip is not a number (a header) are skipped. mode probe | apply.</summary>
    public static string SetGrip(Document doc, string csvPath, string mode)
    {
        var t0 = DateTime.Now; var sb = new StringBuilder();
        var want = File.ReadAllLines(csvPath).Select(l => l.Split(',')).Where(p => p.Length >= 3 && double.TryParse(p[1], NumberStyles.Float, CI, out _)).ToList();
        int ok = 0, miss = 0, moved = 0; var lens = new Dictionary<string, int>(); var errs = new List<string>();
        using var tg = new TransactionGroup(doc, "Set bolt grip"); tg.Start();
        using (var tr = new FabricationTransaction(doc, false, "Set bolt grip"))      // false = not read-only
        {
            var byUid = AsObjects<ScrewBoltPattern>();
            foreach (var p in want)
            {
                if (!byUid.TryGetValue(Key(p[0]), out var bp)) { miss++; continue; }
                try
                {
                    var g = double.Parse(p[1], CI); var g0 = bp.BindingLength;
                    if (p[2].Trim() == "add") bp.BindingLengthAddition = g - g0; else { bp.BindingLengthAddition = 0; bp.BindingLength = g; }
                    double d = 0;
                    if (p.Length >= 6)
                    {
                        var tc = new Point3d(double.Parse(p[3], CI), double.Parse(p[4], CI), double.Parse(p[5], CI)); var n = bp.BoltNormal;
                        var s = (tc - bp.Center).DotProduct(n);
                        if (Math.Abs(s) > 0.5) { bp.RefPoint = bp.RefPoint + n * s; moved++; }
                        d = (tc - bp.Center).GetLength();
                    }
                    ok++;
                    var key = string.Format(CI, "{0} grip {1:F1} (measured {2:F1}) -> {3:F1} + {4:F1} length {5:F0} centre off {6:F0}", p[2].Trim(), g, g0, bp.BindingLength, bp.BindingLengthAddition, bp.ScrewLength, d);
                    lens[key] = lens.GetValueOrDefault(key) + 1;
                }
                catch (Exception ex) { if (errs.Count < 10) errs.Add(p[0] + ": " + ex.Message); }
            }
            tr.Commit();
        }
        sb.AppendLine($"SetGrip {mode}: lines {want.Count} | set {ok} | moved {moved} | not found {miss} | {(DateTime.Now - t0).TotalSeconds:F0} s");
        foreach (var kv in lens.OrderByDescending(x => x.Value).Take(15)) sb.AppendLine($"  {kv.Value} {kv.Key}");
        foreach (var e in errs) sb.AppendLine("ERR " + e);
        if (mode == "apply") tg.Assimilate(); else tg.RollBack();
        return sb.ToString();
    }

    /// <summary>New outline for plates already built, keeping their Steel Connections id (and so the exported GlobalId): JSON
    /// {"gid": {"n": vertex count of the plate to replace, "pts": [[x, y, z], ...] new outline in mm on the same plane}}; among the
    /// plates of that gid (map lines role PLATE) the one whose outline has n vertices gets the new one. mode probe | apply.</summary>
    public static string SetContour(Document doc, string mapPath, string jsonPath, string mode)
    {
        var t0 = DateTime.Now;
        var want = JsonDocument.Parse(File.ReadAllText(jsonPath)).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        var byGid = MapRows(mapPath).Where(r => Role(r) == "PLATE" && want.ContainsKey(r.GetProperty("gid").GetString()))
            .GroupBy(r => r.GetProperty("gid").GetString()).ToDictionary(g => g.Key, g => g.Select(r => Key(r.GetProperty("as").GetString())).ToList());
        int ok = 0, none = 0, many = 0; double dmax = 0; var errs = new List<string>();
        using var tg = new TransactionGroup(doc, "Set plate outline"); tg.Start();
        using (var tr = new FabricationTransaction(doc, false, "Set plate outline"))
        {
            var byUid = AsObjects<Plate>();
            foreach (var (gid, w) in want)
            {
                int n = w.GetProperty("n").GetInt32(); var pts = w.GetProperty("pts").EnumerateArray().Select(Pt).ToArray();
                var cand = (byGid.TryGetValue(gid, out var us) ? us : new List<string>()).Where(byUid.ContainsKey).Select(u => byUid[u])
                    .Where(pl => { pl.GetBaseContourPolygon(0, out Point3d[] c); return c.Length == n; }).ToList();
                if (cand.Count == 0) { none++; continue; }
                if (cand.Count > 1) { many++; continue; }
                try
                {
                    var pl = cand[0]; pl.SetPolygonContour(pts); ok++;
                    pl.GetBaseContourPolygon(0, out Point3d[] c2);
                    foreach (var p in pts) dmax = Math.Max(dmax, c2.Min(q => (q - p).GetLength()));      // farthest new vertex from the outline read back
                }
                catch (Exception ex) { if (errs.Count < 10) errs.Add(gid + ": " + ex.Message); }
            }
            tr.Commit();
        }
        if (mode == "apply") tg.Assimilate(); else tg.RollBack();
        return $"SetContour {mode}: plates {want.Count} | set {ok} | no plate with n vertices {none} | several with n vertices {many} | max vertex deviation {dmax:F1} mm | {(DateTime.Now - t0).TotalSeconds:F0} s"
               + string.Concat(errs.Select(e => "\nERR " + e));
    }

    /// <summary>Plates already built (map lines role PLATE): Portioning = 1, thickness on the +n side as the IFC extrusion (Build
    /// used 0, the -n side, before it was fixed). mode probe | apply.</summary>
    public static string FixPlates(Document doc, string mapPath, string mode)
    {
        var t0 = DateTime.Now;
        var want = new HashSet<string>(MapRows(mapPath).Where(r => Role(r) == "PLATE").Select(r => Key(r.GetProperty("as").GetString())));
        int ok = 0, had = 0;
        using var tg = new TransactionGroup(doc, "Fix plate thickness side"); tg.Start();
        using (var tr = new FabricationTransaction(doc, false, "Fix plate thickness side"))
        {
            foreach (var (k, pl) in AsObjects<Plate>())
            {
                if (!want.Contains(k)) continue;
                if (Math.Abs(pl.Portioning - 1) < 1e-9) { had++; continue; }
                pl.Portioning = 1; ok++;
            }
            tr.Commit();
        }
        if (mode == "apply") tg.Assimilate(); else tg.RollBack();
        return $"FixPlates {mode}: plates in map {want.Count} | fixed {ok} | already right {had} | {(DateTime.Now - t0).TotalSeconds:F0} s";
    }

    /// <summary>Bolt patterns already built (map lines role BOLTS): Nx, Ny, Dx, Dy from the Steel input JSON (Build left the
    /// constructor's 2 x 2 before it was fixed). filter "max=n" | "range=a-b" | "names=gid,.." | "". mode probe | apply.</summary>
    public static string FixBolts(Document doc, string jsonPath, string mapPath, string filter, string mode)
    {
        var t0 = DateTime.Now; var sb = new StringBuilder();
        var groups = JsonDocument.Parse(File.ReadAllText(jsonPath)).RootElement.GetProperty("bolts").EnumerateArray().ToDictionary(b => b.GetProperty("gid").GetString());
        var lines = Batch.Pick(MapRows(mapPath).Where(r => Role(r) == "BOLTS").ToList(), filter, r => r.GetProperty("gid").GetString());
        int ok = 0, miss = 0, screws = 0, expect = 0; var errs = new List<string>();
        using var tg = new TransactionGroup(doc, "Fix bolt patterns"); tg.Start();
        using (var tr = new FabricationTransaction(doc, false, "Fix bolt patterns"))
        {
            var byUid = AsObjects<FinitRectScrewBoltPattern>();
            sb.AppendLine($"rectangular bolt patterns {byUid.Count}");
            foreach (var r in lines)
            {
                var gid = r.GetProperty("gid").GetString();
                if (!groups.TryGetValue(gid, out var b) || !byUid.TryGetValue(Key(r.GetProperty("as").GetString()), out var bp)) { miss++; continue; }
                try
                {
                    if (b.GetProperty("grid").ValueKind == JsonValueKind.Object)
                    {
                        var g = b.GetProperty("grid"); int nu = g.GetProperty("nu").GetInt32(), nv = g.GetProperty("nv").GetInt32();
                        SetGrid(bp, nu, nv, g.GetProperty("du").GetDouble(), g.GetProperty("dv").GetDouble()); expect += nu * nv;
                    }
                    else { SetGrid(bp, 1, 1, 0, 0); expect += 1; }
                    screws += bp.NumberOfScrews; ok++;
                }
                catch (Exception ex) { if (errs.Count < 10) errs.Add(gid + ": " + ex.Message); }
            }
            tr.Commit();
        }
        sb.AppendLine($"FixBolts {mode}: groups {lines.Count} | fixed {ok} | not found {miss} | bolts {screws} (expected {expect}) | {(DateTime.Now - t0).TotalSeconds:F0} s");
        foreach (var e in errs) sb.AppendLine("ERR " + e);
        if (mode == "apply") tg.Assimilate(); else tg.RollBack();
        return sb.ToString();
    }

    /// <summary>Anchor patterns already built (map lines role ANCHORS): ScrewDiameter and ScrewLength (straight part, p0 to p1 of
    /// the input anchor) from the Steel input JSON; reports the values Steel Connections keeps (it snaps to its standard tables).
    /// filter "max=n" | "range=a-b" | "". mode probe | apply.</summary>
    public static string FixAnchors(Document doc, string jsonPath, string mapPath, string filter, string mode)
    {
        var t0 = DateTime.Now; var sb = new StringBuilder();
        var anc = JsonDocument.Parse(File.ReadAllText(jsonPath)).RootElement.GetProperty("anchors").EnumerateArray().ToDictionary(a => a.GetProperty("gid").GetString());
        var byAs = MapRows(mapPath).Where(r => Role(r) == "ANCHORS" && anc.ContainsKey(r.GetProperty("gid").GetString()))
            .GroupBy(r => Key(r.GetProperty("as").GetString())).ToDictionary(g => g.Key, g => anc[g.First().GetProperty("gid").GetString()]);
        var keys = Batch.Pick(byAs.Keys.ToList(), filter, k => k);
        var want = new HashSet<string>(keys); int ok = 0; var res = new List<string>();
        using var tg = new TransactionGroup(doc, "Fix anchor patterns"); tg.Start();
        using (var tr = new FabricationTransaction(doc, false, "Fix anchor patterns"))
        {
            foreach (var (k, ap) in AsObjects<AnchorPattern>())
            {
                if (!want.Contains(k)) continue;
                var a = byAs[k]; var p = a.GetProperty("pts"); var p0 = Pt(p[0]); var p1 = Pt(p[1]);
                double d = a.GetProperty("d").GetDouble(), L = p0.DistanceTo(p1);
                try { ap.ScrewDiameter = d; ap.ScrewLength = L; ok++; } catch (Exception ex) { res.Add("ERR " + ex.Message); }
                if (res.Count < 6) res.Add(string.Format(CI, "asked d {0} L {1:F0} -> d {2} len {3:F0} screwLen {4:F0} std {5} normal z {6:F2} ref z {7:F0} (input top {8:F0})",
                    d, L, ap.ScrewDiameter, ap.Length, ap.ScrewLength, ap.Standard, ap.Normal.z, ap.RefPoint.z, p0.z));
            }
            tr.Commit();
        }
        if (mode == "apply") tg.Assimilate(); else tg.RollBack();
        sb.AppendLine($"FixAnchors {mode}: groups {keys.Count} | fixed {ok} | {(DateTime.Now - t0).TotalSeconds:F0} s");
        foreach (var r in res) sb.AppendLine("  " + r);
        return sb.ToString();
    }

    /// <summary>Read-only: direction, length and standard of the first n anchor patterns (to compare them with the IFC anchors).</summary>
    public static string AnchorInfo(Document doc, string n)
    {
        var sb = new StringBuilder(); int k = 0, max = Batch.Int(n, 10);
        using (var tr = new FabricationTransaction(doc, true, "Read anchor patterns"))
        {
            foreach (var ap in AsObjects<AnchorPattern>().Values)
            {
                var nm = ap.Normal; var bn = ap.BoltNormal; var rp = ap.RefPoint;
                sb.AppendLine(string.Format(CI, "{0} | normal {1:F2},{2:F2},{3:F2} | boltNormal {4:F2},{5:F2},{6:F2} | inverted {7} | ref {8:F0},{9:F0},{10:F0} | len {11:F0} screwLen {12:F0} unfolded {13:F0} | d {14} | std {15} | type {16} | n {17}",
                    ap.GetUniqueId(), nm.x, nm.y, nm.z, bn.x, bn.y, bn.z, ap.IsInverted, rp.x, rp.y, rp.z, ap.Length, ap.ScrewLength, ap.AnchorUnfoldedLength, ap.ScrewDiameter, ap.Standard, ap.AnchorType, ap.NumberOfScrews));
                if (++k >= max) break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Probe, rolled back: on the first anchor pattern with this diameter (mm), sets each ScrewLength of the list
    /// "l1,l2,.." (mm) and reports the values Steel Connections keeps.</summary>
    public static string AnchorTry(Document doc, string diameter, string lengths)
    {
        var sb = new StringBuilder(); double dw = double.Parse(diameter, CI);
        using var tg = new TransactionGroup(doc, "Try anchor lengths"); tg.Start();
        try
        {
            using var tr = new FabricationTransaction(doc, false, "Try anchor lengths");
            var ap = AsObjects<AnchorPattern>().Values.FirstOrDefault(x => Math.Abs(x.ScrewDiameter - dw) < 0.1);
            if (ap == null) return "no anchor pattern with d " + diameter;
            sb.AppendLine($"before: screwLen {ap.ScrewLength:F0} len {ap.Length:F0} unfolded {ap.AnchorUnfoldedLength:F0} thread {ap.ThreadLength:F0} std {ap.Standard} grade {ap.Grade} type {ap.AnchorType} orient {ap.OrientationType}");
            foreach (var l in lengths.Split(',').Select(v => double.Parse(v, CI)))
            {
                try { ap.ScrewLength = l; sb.AppendLine($"  set {l:F0}: screwLen {ap.ScrewLength:F0} len {ap.Length:F0} unfolded {ap.AnchorUnfoldedLength:F0} thread {ap.ThreadLength:F0}"); }
                catch (Exception ex) { sb.AppendLine($"  set {l:F0}: ERROR {ex.Message}"); }
            }
            tr.Commit();
        }
        finally { tg.RollBack(); }
        return sb.ToString();
    }

    /// <summary>Three 200 x 100 x 10 plates on z = 0 (normal +Z) with Portioning 0, 0.5, 1 at x = 0, 300, 600, to see on which
    /// side of the plane the thickness goes (smoke test, best on an empty project).</summary>
    public static string SmokePortioning(Document doc)
    {
        using var tr = new FabricationTransaction(doc, false, "Smoke portioning");
        var sb = new StringBuilder();
        foreach (var (pv, x0) in new[] { (0.0, 0.0), (0.5, 300.0), (1.0, 600.0) })
        {
            var pts = new[] { new Point3d(x0, 0, 0), new Point3d(x0 + 200, 0, 0), new Point3d(x0 + 200, 100, 0), new Point3d(x0, 100, 0) };
            var pl = new Plate(new Plane(pts[0], new Vector3d(0, 0, 1)), pts, 10); pl.Portioning = pv; pl.WriteToDb();
            sb.Append($"portioning {pv} x0 {x0} | ");
        }
        tr.Commit(); return sb.ToString();
    }
}
