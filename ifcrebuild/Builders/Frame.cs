using System.Globalization;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace IfcRebuild;

/// <summary>
/// Steel framing with library families, placed the way a modeller does it: beams and braces on their axis (centre
/// justification, no offsets, no end joins), columns between their levels. Input JSON (mm):
/// {"families": {"key": {"file": "x.rfa", "name": "family name", "types": {"type": null | {"parameter": mm}}}},
///  "members": [{"name", "guid", "fam", "type", "kind": "line" | "column", "p0", "p1", "level", "structural": "Beam" | "Brace",
///               "o", "z0", "z1", "base_level", "top_level", "depth": [x,y,z], "bb": [[min],[max]], "sig": [16], "ax": [2], "tol_ax"}]}.
/// A type with null parameters is loaded from the family's type catalog, otherwise duplicated and set. The cross-section
/// rotation comes from the IFC section depth direction and is then checked against the IFC section signature "sig"
/// (or the box "bb" when there is none); rotations by 90 degrees, a mirrored angle and a reversed axis are tried until it
/// fits. The shared parameters and the structural material of the element being replaced (same IfcGUID) are copied
/// over, then that element is deleted. Every pair (IFC GlobalId, UniqueId) is appended to mapPath as a JSON line.
/// mode "probe" | "apply" | "refit" (moves and re-rotates the members already placed, found through mapPath).
/// </summary>
internal static class Frame
{
    const double MM = 1 / 304.8;
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    class Overwrite : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = false; return true; }
        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        { source = FamilySource.Family; overwriteParameterValues = false; return true; }
    }

    static XYZ P(JsonElement a) => new XYZ(a[0].GetDouble() * MM, a[1].GetDouble() * MM, a[2].GetDouble() * MM);
    static XYZ V(JsonElement a) => new XYZ(a[0].GetDouble(), a[1].GetDouble(), a[2].GetDouble());

    static Family LoadFamily(Document doc, string file, string name)
    {
        var f = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(x => x.Name == name);
        if (f != null) return f;
        if (!doc.LoadFamily(file, new Overwrite(), out f)) throw new Exception("cannot load " + file);
        return f;
    }

    static FamilySymbol GetType(Document doc, Family fam, string file, string typeName, JsonElement prms)
    {
        var sym = fam.GetFamilySymbolIds().Select(i => (FamilySymbol)doc.GetElement(i)).FirstOrDefault(s => s.Name == typeName);
        if (sym != null) return sym;
        if (prms.ValueKind != JsonValueKind.Object)
        {
            if (doc.LoadFamilySymbol(file, typeName, new Overwrite(), out sym)) return sym;
            throw new Exception($"no type {typeName} in the catalog of {file}");
        }
        var baseSym = (FamilySymbol)doc.GetElement(fam.GetFamilySymbolIds().First());
        sym = (FamilySymbol)baseSym.Duplicate(typeName);
        foreach (var p in prms.EnumerateObject())
        {
            var par = sym.LookupParameter(p.Name);
            if (par == null || par.IsReadOnly) continue;
            par.Set(par.StorageType == StorageType.Double ? p.Value.GetDouble() * MM : p.Value.GetDouble());
        }
        return sym;
    }

    static Level LevelOf(Document doc, string name) =>
        new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == name)
        ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();

    static double BoxErr(Element e, JsonElement bb)
    {
        var b = e.get_BoundingBox(null); if (b == null) return 1e9;
        var lo = bb[0]; var hi = bb[1];
        double[] a = { b.Min.X / MM - lo[0].GetDouble(), b.Min.Y / MM - lo[1].GetDouble(), b.Min.Z / MM - lo[2].GetDouble(),
                       b.Max.X / MM - hi[0].GetDouble(), b.Max.Y / MM - hi[1].GetDouble(), b.Max.Z / MM - hi[2].GetDouble() };
        return a.Max(Math.Abs);
    }

    static (XYZ x, XYZ a, XYZ b) Basis(XYZ x)
    {
        x = x.Normalize(); var a = (Math.Abs(x.Z) < 0.9 ? x.CrossProduct(XYZ.BasisZ) : x.CrossProduct(XYZ.BasisX)).Normalize();
        return (x, a, x.CrossProduct(a));
    }
    static void Collect(GeometryElement ge, List<XYZ> pts)
    {
        foreach (var o in ge)
        {
            if (o is Solid s && s.Volume > 1e-9) { foreach (Edge ed in s.Edges) pts.AddRange(ed.Tessellate()); }
            else if (o is GeometryInstance gi) Collect(gi.GetInstanceGeometry(), pts);
        }
    }
    /// <summary>Section signature (mm) in the frame (x axis, a, b) of <see cref="Basis"/>: box centre (a, b), half extents,
    /// a-range of the upper / lower half, b-range of the right / left half, then the axial range along x. The input "sig"
    /// must be computed the same way from the IFC body, so that an open C faces the right side, a Z runs the right way
    /// and an angle sits in the right corner.</summary>
    static double[] Sig(Element e, XYZ x, XYZ a, XYZ b)
    {
        var pts = new List<XYZ>(); var ge = e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
        if (ge != null) Collect(ge, pts);
        if (pts.Count == 0) return null;
        var A = pts.Select(p => p.DotProduct(a) / MM).ToArray(); var B = pts.Select(p => p.DotProduct(b) / MM).ToArray(); var T = pts.Select(p => p.DotProduct(x) / MM).ToArray();
        double ca = (A.Min() + A.Max()) / 2, cb = (B.Min() + B.Max()) / 2;
        (double, double) Rng(IEnumerable<int> idx, double[] v, double c) { var l = idx.Select(i => v[i] - c).ToList(); return l.Count == 0 ? (0, 0) : (l.Min(), l.Max()); }
        var I = Enumerable.Range(0, A.Length).ToList();
        var up = Rng(I.Where(i => B[i] - cb > 1), A, ca); var lo = Rng(I.Where(i => B[i] - cb < -1), A, ca);
        var ri = Rng(I.Where(i => A[i] - ca > 1), B, cb); var le = Rng(I.Where(i => A[i] - ca < -1), B, cb);
        return new[] { ca, cb, A.Min() - ca, B.Min() - cb, A.Max() - ca, B.Max() - cb, up.Item1, up.Item2, lo.Item1, lo.Item2, ri.Item1, ri.Item2, le.Item1, le.Item2, T.Min(), T.Max() };
    }
    /// <summary>(section error, axial error beyond tolerance), mm.</summary>
    static (double sec, double ax) SigErr(Element e, JsonElement m, (XYZ x, XYZ a, XYZ b) bs)
    {
        var s = Sig(e, bs.x, bs.a, bs.b); if (s == null) return (1e9, 1e9);
        var j = m.GetProperty("sig"); double sec = 0;
        for (int i = 0; i < 14; i++) sec = Math.Max(sec, Math.Abs(s[i] - j[i].GetDouble()));
        var ax = m.GetProperty("ax"); double tol = m.TryGetProperty("tol_ax", out var tv) ? tv.GetDouble() : 10;
        return (sec, Math.Max(0, Math.Max(Math.Abs(s[14] - ax[0].GetDouble()), Math.Abs(s[15] - ax[1].GetDouble())) - tol));
    }
    static (XYZ x, XYZ a, XYZ b) BasisOf(JsonElement m) => m.GetProperty("kind").GetString() == "line" ? Basis(P(m.GetProperty("p1")) - P(m.GetProperty("p0"))) : Basis(XYZ.BasisZ);

    /// <summary>Signed angle from the default beam up-vector (world Z made normal to the axis) to the section depth vector, about the axis.</summary>
    static double BeamAngle(XYZ p0, XYZ p1, XYZ depth)
    {
        var x = (p1 - p0).Normalize();
        var up = Math.Abs(x.Z) < 0.999 ? XYZ.BasisZ : XYZ.BasisX;
        var z = (up - x * up.DotProduct(x)).Normalize(); var y = z.CrossProduct(x);
        var d = depth - x * depth.DotProduct(x);
        return Math.Atan2(d.DotProduct(y), d.DotProduct(z));
    }

    static void CopyShared(Element from, Element to)
    {
        foreach (Parameter p in from.Parameters)
        {
            if (!p.IsShared || !p.HasValue || p.Definition == null) continue;
            var q = to.get_Parameter(p.GUID); if (q == null || q.IsReadOnly) continue;
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String: q.Set(p.AsString()); break;
                    case StorageType.Double: q.Set(p.AsDouble()); break;
                    case StorageType.Integer: q.Set(p.AsInteger()); break;
                }
            }
            catch { }
        }
    }

    public static string Run(Document doc, string jsonPath, string filter, string mode, string mapPath, string reportPath)
    {
        var root = JsonDocument.Parse(File.ReadAllText(jsonPath)).RootElement;
        var fams = root.GetProperty("families");
        var members = Batch.Pick(root.GetProperty("members").EnumerateArray().ToList(), filter, m => m.GetProperty("name").GetString(),
            (k, v, m) => k == "fam" ? m.GetProperty("fam").GetString() == v : throw new ArgumentException($"filter key '{k}' is not supported by Frame"));
        bool sourceGuid = RebuildConfig.Current.GuidPolicy != "revit";
        // elements being replaced, by IfcGUID (= IFC GlobalId)
        var old = GuidTools.ByGuid(doc, new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)));
        var rep = new StringBuilder(); int made = 0, refit = 0, bad = 0, missingOld = 0; var errs = new List<string>(); var maps = new List<string>();
        var worst = new List<(double err, string name)>();
        using var tg = new TransactionGroup(doc, "Frame steel"); tg.Start();
        var created = new List<(JsonElement m, FamilyInstance fi, double ang)>();
        // refit: members placed earlier (found through the map file: gid -> uid) are moved to the new data, not recreated,
        // so that the parameters copied from the replaced elements stay
        var refitMode = mode == "refit"; var mapUid = new Dictionary<string, string>();
        if (refitMode && File.Exists(mapPath))
            foreach (var ln in File.ReadAllLines(mapPath))
            {
                try { var j = JsonDocument.Parse(ln).RootElement; if (j.TryGetProperty("uid", out var u)) mapUid[j.GetProperty("gid").GetString()] = u.GetString(); } catch { }
            }
        int k0 = 0;
        var (st, log) = refitMode ? FailureLog.Run(doc, "Frame: move members", () =>
        {
            foreach (var m in members)
            {
                if (++k0 % 50 == 0) Progress.Report($"Frame refit {k0}/{members.Count}");
                if (!mapUid.TryGetValue(m.GetProperty("guid").GetString(), out var uid) || doc.GetElement(uid) is not FamilyInstance fi) { missingOld++; continue; }
                double ang = 0;
                if (m.GetProperty("kind").GetString() == "line")
                {
                    var p0 = P(m.GetProperty("p0")); var p1 = P(m.GetProperty("p1"));
                    ((LocationCurve)fi.Location).Curve = Line.CreateBound(p0, p1);
                    if (m.TryGetProperty("depth", out var dv)) ang = BeamAngle(p0, p1, V(dv));
                    fi.get_Parameter(BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE)?.Set(ang);
                }
                created.Add((m, fi, ang));
            }
        }) : FailureLog.Run(doc, "Frame: place members", () =>
        {
            var syms = new Dictionary<string, FamilySymbol>();
            foreach (var m in members)
            {
                if (++k0 % 50 == 0) Progress.Report($"Frame place {k0}/{members.Count}");
                try
                {
                    var fk = m.GetProperty("fam").GetString(); var tn = m.GetProperty("type").GetString(); var fe = fams.GetProperty(fk);
                    var file = fe.GetProperty("file").GetString();
                    if (!syms.TryGetValue(fk + "|" + tn, out var sym))
                    {
                        var fam = LoadFamily(doc, file, fe.GetProperty("name").GetString());
                        sym = GetType(doc, fam, file, tn, fe.GetProperty("types").GetProperty(tn));
                        if (!sym.IsActive) sym.Activate();
                        syms[fk + "|" + tn] = sym;
                    }
                    FamilyInstance fi; double ang = 0;
                    if (m.GetProperty("kind").GetString() == "line")
                    {
                        var p0 = P(m.GetProperty("p0")); var p1 = P(m.GetProperty("p1"));
                        var stype = m.GetProperty("structural").GetString() == "Brace" ? StructuralType.Brace : StructuralType.Beam;
                        fi = doc.Create.NewFamilyInstance(Line.CreateBound(p0, p1), sym, LevelOf(doc, m.GetProperty("level").GetString()), stype);
                        StructuralFramingUtils.DisallowJoinAtEnd(fi, 0); StructuralFramingUtils.DisallowJoinAtEnd(fi, 1);
                        fi.get_Parameter(BuiltInParameter.Y_JUSTIFICATION)?.Set((int)YJustification.Center);
                        fi.get_Parameter(BuiltInParameter.Z_JUSTIFICATION)?.Set((int)ZJustification.Center);
                        foreach (var bip in new[] { BuiltInParameter.Y_OFFSET_VALUE, BuiltInParameter.Z_OFFSET_VALUE, BuiltInParameter.START_Y_OFFSET_VALUE,
                                                    BuiltInParameter.END_Y_OFFSET_VALUE, BuiltInParameter.START_Z_OFFSET_VALUE, BuiltInParameter.END_Z_OFFSET_VALUE })
                            { var pp = fi.get_Parameter(bip); if (pp != null && !pp.IsReadOnly) pp.Set(0.0); }
                        if (m.TryGetProperty("depth", out var dv)) ang = BeamAngle(p0, p1, V(dv));
                        fi.get_Parameter(BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE)?.Set(ang);
                    }
                    else
                    {
                        var o = m.GetProperty("o"); var z0 = m.GetProperty("z0").GetDouble(); var z1 = m.GetProperty("z1").GetDouble();
                        var bl = LevelOf(doc, m.GetProperty("base_level").GetString()); var tl = LevelOf(doc, m.GetProperty("top_level").GetString());
                        fi = doc.Create.NewFamilyInstance(new XYZ(o[0].GetDouble() * MM, o[1].GetDouble() * MM, bl.Elevation), sym, bl, StructuralType.Column);
                        fi.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM).Set(bl.Id);
                        fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM).Set(tl.Id);
                        fi.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM).Set(z0 * MM - bl.Elevation);
                        fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM).Set(z1 * MM - tl.Elevation);
                        if (m.TryGetProperty("depth", out var dv)) ang = Math.Atan2(dv[1].GetDouble(), dv[0].GetDouble()) - Math.PI / 2;
                        if (Math.Abs(ang) > 1e-9)
                            ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(new XYZ(o[0].GetDouble() * MM, o[1].GetDouble() * MM, 0), new XYZ(o[0].GetDouble() * MM, o[1].GetDouble() * MM, 1)), ang);
                    }
                    created.Add((m, fi, ang));
                }
                catch (Exception ex) { errs.Add(m.GetProperty("name").GetString() + ": " + ex.Message); }
            }
        });
        rep.AppendLine($"placed: {st} {created.Count}/{members.Count} | errors {errs.Count}");
        // match the section signature (open side of a C, direction of a Z, corner of an L) and the axial range; try other
        // rotations and a reversed axis when it is off. A rotation that fits is remembered per family type and tried first.
        var rule = new Dictionary<string, (bool rev, int k, bool neg)>(); var errOf = new Dictionary<ElementId, (double sec, double ax)>();
        int k1 = 0;
        FailureLog.Run(doc, "Frame: match sections", () =>
        {
            doc.Regenerate();
            foreach (var (m, fi, ang) in created)
            {
                if (++k1 % 50 == 0) Progress.Report($"Frame match {k1}/{created.Count}");
                var nm = m.GetProperty("name").GetString();
                if (!m.TryGetProperty("sig", out _)) { var be = BoxErr(fi, m.GetProperty("bb")); errOf[fi.Id] = (be, 0); if (be > 10) bad++; worst.Add((be, nm)); continue; }
                var bs = BasisOf(m); var e0 = SigErr(fi, m, bs); var tk = m.GetProperty("fam").GetString() + "|" + m.GetProperty("type").GetString();
                bool line = m.GetProperty("kind").GetString() == "line";
                if (e0.sec > 10 || e0.ax > 0)
                {
                    XYZ q0 = line ? P(m.GetProperty("p0")) : null, q1 = line ? P(m.GetProperty("p1")) : null; double rot = 0;
                    var cands = new List<(bool rev, int k, bool neg)>();
                    if (rule.TryGetValue(tk, out var r0)) cands.Add(r0);
                    foreach (var rv in line ? new[] { false, true } : new[] { false })
                        for (int k = 0; k < 4; k++)
                            foreach (var ng in line ? new[] { false, true } : new[] { false })
                                if (!cands.Contains((rv, k, ng))) cands.Add((rv, k, ng));
                    (bool rev, int k, bool neg) bestC = (false, 0, false); var best = e0;
                    foreach (var c in cands)
                    {
                        if (line)
                        {
                            ((LocationCurve)fi.Location).Curve = c.rev ? Line.CreateBound(q1, q0) : Line.CreateBound(q0, q1);
                            fi.get_Parameter(BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE).Set((c.neg ? -ang : ang) + c.k * Math.PI / 2);
                        }
                        else { var lp = ((LocationPoint)fi.Location).Point; ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(lp, lp + XYZ.BasisZ), c.k * Math.PI / 2 - rot); rot = c.k * Math.PI / 2; }
                        doc.Regenerate(); var ec = SigErr(fi, m, bs);
                        if (Math.Max(ec.sec, ec.ax) < Math.Max(best.sec, best.ax) - 0.5) { best = ec; bestC = c; }
                        if (ec.sec <= 2 && ec.ax <= 0) break;
                    }
                    if (line)
                    {
                        ((LocationCurve)fi.Location).Curve = bestC.rev ? Line.CreateBound(q1, q0) : Line.CreateBound(q0, q1);
                        fi.get_Parameter(BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE).Set((bestC.neg ? -ang : ang) + bestC.k * Math.PI / 2);
                    }
                    else { var lp = ((LocationPoint)fi.Location).Point; ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(lp, lp + XYZ.BasisZ), bestC.k * Math.PI / 2 - rot); }
                    if (best.sec <= 10 && best.ax <= 0) rule[tk] = bestC;
                    refit++; e0 = best;
                }
                errOf[fi.Id] = e0;
                if (e0.sec > 10 || e0.ax > 0) bad++;
                worst.Add((Math.Max(e0.sec, e0.ax), nm));
            }
        });
        // copy parameters, delete the replaced elements, record the GUID pairs
        if (!refitMode) FailureLog.Run(doc, "Frame: replace elements", () =>
        {
            foreach (var (m, fi, _) in created)
            {
                var g = m.GetProperty("guid").GetString();
                if (old.TryGetValue(g, out var oe))
                {
                    CopyShared(oe, fi);
                    var mat = oe.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM)?.AsElementId();
                    if (mat != null && mat != ElementId.InvalidElementId) fi.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM)?.Set(mat);
                    doc.Delete(oe.Id);
                }
                else missingOld++;
                fi.get_Parameter(BuiltInParameter.IFC_GUID)?.Set(sourceGuid ? g : "");
                maps.Add($"{{\"gid\":\"{g}\",\"uid\":\"{fi.UniqueId}\",\"role\":\"\"}}");
                made++;
            }
        });
        worst.Sort((a, b) => b.err.CompareTo(a.err));
        if (!string.IsNullOrEmpty(reportPath))      // box offsets per member: dx0 dy0 dz0 dx1 dy1 dz1 (mm, Revit - IFC)
            File.WriteAllLines(reportPath + ".csv", new[] { "name,fam,type,dx0,dy0,dz0,dx1,dy1,dz1,sec,ax" }.Concat(created.Select(c =>
            {
                var b = c.fi.get_BoundingBox(null); var lo = c.m.GetProperty("bb")[0]; var hi = c.m.GetProperty("bb")[1];
                string d(double v, JsonElement r) => (v / MM - r.GetDouble()).ToString("F1", CI);
                return b == null ? c.m.GetProperty("name").GetString() + ",,,,,,,," : string.Join(",", c.m.GetProperty("name").GetString(), c.m.GetProperty("fam").GetString(), c.m.GetProperty("type").GetString(),
                    d(b.Min.X, lo[0]), d(b.Min.Y, lo[1]), d(b.Min.Z, lo[2]), d(b.Max.X, hi[0]), d(b.Max.Y, hi[1]), d(b.Max.Z, hi[2]),
                    errOf.TryGetValue(c.fi.Id, out var ee) ? ee.sec.ToString("F1", CI) : "",
                    errOf.TryGetValue(c.fi.Id, out var e2) ? e2.ax.ToString("F1", CI) : "");
            })));
        rep.AppendLine($"made {made} | rotation retried {refit} | section off > 10 mm or axis beyond tolerance {bad} | no element to replace {missingOld}");
        rep.AppendLine("largest offsets: " + string.Join("; ", worst.Take(15).Select(w => $"{w.name} {w.err:F0}")));
        foreach (var e in errs.Take(30)) rep.AppendLine("ERR " + e);
        if (mode == "apply" || refitMode)
        {
            tg.Assimilate();
            if (!string.IsNullOrEmpty(mapPath) && maps.Count > 0) File.AppendAllLines(mapPath, maps);
        }
        else tg.RollBack();
        rep.Insert(0, $"Frame {mode} {DateTime.Now:HH:mm:ss}\n");
        if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, rep.ToString());
        return rep.ToString();
    }
}
