using System.Globalization;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace IfcRebuild;

/// <summary>
/// Doors hosted in their IFC wall (plan.json "doors": host wall GUID and the cut box of the wall). Family from the Metric
/// Door template: Width / Height = the cut box, the template opening cuts the host; leaf and roller box as extrusions in
/// family coordinates (origin at the bottom centre of the door box, x along the wall, y across it). One family per door
/// geometry (several doors share one). Records: {"name", "guid", "storey", "ptype", "bname", "material"?, "door": {"width",
/// "height"} (m), "bb": [[min],[max]] mm, "host": {"guid", "name", "box": [[x0,x1],[y0,y1],[z0,z1]] m}, "solids": [...]}.
/// mode "probe" | "apply"; filter "names=a,b" | "".
/// </summary>
internal static class Doors
{
    const double MM = 1 / 304.8;
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static double[] Arr(JsonElement a) => a.EnumerateArray().Select(x => x.GetDouble()).ToArray();

    class Overwrite : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = true; return true; }
        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        { source = FamilySource.Family; overwriteParameterValues = true; return true; }
    }

    /// <summary>Door family: the geometry of the IFC door (leaf, roller box) about the opening's bottom centre; the
    /// template opening set to the cut box (width x height, bottom at the family level); host wall of the template as
    /// thick as the real wall.</summary>
    static FamilySymbol MakeDoorFamily(Document doc, string famName, string typeName, List<(XYZ o, XYZ u, XYZ v, List<(double a, double b)> pts, double d)> solids,
                                       double openW, double openH, double openBelow, double hostThick, string material, Dictionary<string, Material> mats, StringBuilder sb)
    {
        var fd = doc.Application.NewFamilyDocument(RebuildConfig.Current.Template(doc.Application, "Metric Door.rft"));
        try
        {
            var (fst, flog) = FailureLog.Run(fd, "door family", () =>
            {
                var fm = fd.FamilyManager;
                // host wall as thick as the real one (a hosted family is regenerated on its host)
                var tw = new FilteredElementCollector(fd).OfClass(typeof(Wall)).Cast<Wall>().FirstOrDefault();
                if (tw != null && Math.Abs(tw.Width - hostThick) > 1e-6)
                {
                    var nt = (WallType)tw.WallType.Duplicate($"Host {Math.Round(hostThick / MM)} mm");
                    var cs = nt.GetCompoundStructure();
                    if (cs.LayerCount == 1) { cs.SetLayerWidth(0, hostThick); nt.SetCompoundStructure(cs); tw.WallType = nt; }
                }
                // an opening taller than the template host wall (about 4 m) is dropped by Revit and the loaded family no
                // longer cuts, so the host wall grows with the opening
                var pTh = tw?.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                if (pTh != null && !pTh.IsReadOnly && pTh.AsDouble() < openH + openBelow + 1000 * MM) pTh.Set(openH + openBelow + 1000 * MM);
                // the same for an opening wider than the template wall (5 m): the wall is lengthened about the family origin
                if (tw?.Location is LocationCurve twl && twl.Curve.Length < openW + 2000 * MM)
                {
                    double half = (openW + 2000 * MM) / 2; var mid = twl.Curve.Evaluate(0.5, true);
                    var dir = (twl.Curve.GetEndPoint(1) - twl.Curve.GetEndPoint(0)).Normalize();
                    var c0 = new XYZ(0, mid.Y, mid.Z);
                    twl.Curve = Line.CreateBound(c0 - dir * half, c0 + dir * half);
                }
                // template geometry (frame, panel, symbolic lines) removed, the opening kept
                foreach (var id in new FilteredElementCollector(fd).OfClass(typeof(GenericForm)).ToElementIds().ToList()) fd.Delete(id);
                foreach (var id in new FilteredElementCollector(fd).OfClass(typeof(FamilyInstance)).ToElementIds().ToList()) fd.Delete(id);
                var pW = fm.get_Parameter(BuiltInParameter.DOOR_WIDTH) ?? fm.GetParameters().First(p => p.Definition.Name == "Width");
                var pH = fm.get_Parameter(BuiltInParameter.DOOR_HEIGHT) ?? fm.GetParameters().First(p => p.Definition.Name == "Height");
                if (fm.CurrentType == null) fm.NewType(typeName); else if (fm.CurrentType.Name != typeName) fm.RenameCurrentType(typeName);
                fm.Set(pW, openW); fm.Set(pH, openH + openBelow);
                fd.Regenerate();
                // the opening of the template starts at the level: the part of the IFC cut below the sill is the extra height,
                // and the family geometry is shifted up by it so that the door bottom centre stays the insertion point
                var shift = new XYZ(0, 0, openBelow);
                double tol = fd.Application.ShortCurveTolerance * 1.05;
                var forms = new List<GenericForm>();
                foreach (var (o, u, v, pts, d) in solids)
                {
                    var ca = new CurveArray(); var clean = new List<XYZ>();
                    foreach (var (a, b) in pts) { var q = o + u * a + v * b + shift; if (clean.Count == 0 || clean[^1].DistanceTo(q) > tol) clean.Add(q); }
                    if (clean.Count > 2 && clean[0].DistanceTo(clean[^1]) <= tol) clean.RemoveAt(clean.Count - 1);
                    for (int i = 0; i < clean.Count; i++) ca.Append(Line.CreateBound(clean[i], clean[(i + 1) % clean.Count]));
                    var prof = new CurveArrArray(); prof.Append(ca);
                    forms.Add(fd.FamilyCreate.NewExtrusion(true, prof, SketchPlane.Create(fd, Plane.CreateByNormalAndOrigin(u.CrossProduct(v).Normalize(), o + shift)), d));
                }
                var pmName = RebuildConfig.Current.MaterialParameter;
                var pm = fm.GetParameters().FirstOrDefault(p => p.Definition.Name == pmName) ?? fm.AddParameter(pmName, GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
                var fmat = new FilteredElementCollector(fd).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(x => x.Name == material);
                if (fmat == null) { fmat = (Material)fd.GetElement(Material.Create(fd, material)); if (mats.TryGetValue(material, out var pmat)) { fmat.Color = pmat.Color; fmat.MaterialClass = pmat.MaterialClass; } fmat.UseRenderAppearanceForShading = false; }
                fm.Set(pm, fmat.Id);
                foreach (var g in forms) { var p = g.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM); if (p != null && !p.IsReadOnly) try { fm.AssociateElementParameterToFamilyParameter(p, pm); } catch { } }
                fd.Regenerate();
            });
            if (fst != TransactionStatus.Committed) throw new InvalidOperationException($"door family {fst}: {flog.Summary().Split('\n')[0]}");
            var fam = fd.LoadFamily(doc, new Overwrite());
            using (var t = new Transaction(doc, "family name"))
            {
                t.Start();
                if (fam.Name != famName && !new FilteredElementCollector(doc).OfClass(typeof(Family)).Any(x => x.Name == famName)) fam.Name = famName;
                t.Commit();
            }
            return (FamilySymbol)doc.GetElement(fam.GetFamilySymbolIds().First());
        }
        finally { fd.Close(false); }
    }

    public static string Run(Document doc, string planPath, string filter, string mode, string reportPath)
    {
        var sb = new StringBuilder();
        void Flush() { if (!string.IsNullOrEmpty(reportPath)) try { File.WriteAllText(reportPath, sb.ToString()); } catch { } }
        using var js = JsonDocument.Parse(File.ReadAllText(planPath));
        var root = js.RootElement;
        var names = string.IsNullOrEmpty(filter) ? null : filter.Replace("names=", "").Split(',').ToHashSet();
        var have = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Select(e => e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString()).Where(m => !string.IsNullOrEmpty(m)).ToHashSet();
        var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => !string.IsNullOrEmpty(w.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString()))
            .GroupBy(w => w.get_Parameter(BuiltInParameter.ALL_MODEL_MARK).AsString()).ToDictionary(g => g.Key, g => g.First());
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToDictionary(l => l.Name, l => l);
        var mats = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.First());
        var geo = Build.IfcGeometry(planPath);
        var todo = root.GetProperty("doors").EnumerateArray().Where(r => !have.Contains(S(r, "guid")) && (names == null || names.Contains(S(r, "name")))).ToList();
        sb.AppendLine($"doors to place: {todo.Count}");
        using var tg = new TransactionGroup(doc, "IfcRebuild: doors");
        tg.Start();
        var syms = new Dictionary<string, FamilySymbol>();
        int ok = 0, n = 0, failed = 0;
        foreach (var r in todo)
        {
            if (n % 10 == 0) Progress.Report($"Doors {n}/{todo.Count}");
            try
            {
                var host = r.GetProperty("host");
                if (!walls.TryGetValue(S(host, "guid"), out var wall)) { sb.AppendLine($"  {S(r, "name")}: host wall {S(host, "name")} not in the model"); failed++; continue; }
                var box = host.GetProperty("box"); var bx = Arr(box[0]); var by = Arr(box[1]); var bz = Arr(box[2]);
                var dbb = r.GetProperty("bb"); var dlo = Arr(dbb[0]); var dhi = Arr(dbb[1]);     // door box, mm
                // wall direction from its location line
                var wl = (wall.Location as LocationCurve).Curve as Line;
                var wdir = (wl.GetEndPoint(1) - wl.GetEndPoint(0)).Normalize();
                bool alongX = Math.Abs(wdir.X) > Math.Abs(wdir.Y);
                // opening along the wall: the cut box in the along direction is the door width
                double a0 = alongX ? bx[0] : by[0], a1 = alongX ? bx[1] : by[1];
                double openW = (a1 - a0) * 1000, openBelow = Math.Max(0, (dlo[2] / 1000 - bz[0]) * 1000);
                // opening top: the cut box top, or the wall top when the box passes it (the wall is cut to its top)
                var wbb = wall.get_BoundingBox(null);
                double wallTop = wbb.Max.Z / MM;
                double openH = Math.Min(bz[1] * 1000, wallTop) - dlo[2];
                // insertion point: bottom centre of the door box, on the wall location line
                var cen = new XYZ((dlo[0] + dhi[0]) / 2, (dlo[1] + dhi[1]) / 2, dlo[2]) * MM;
                var wn = wall.Orientation.Normalize();
                var p0 = wl.GetEndPoint(0);
                var onLine = cen - wn * ((cen - p0).DotProduct(wn));
                // right-handed family frame (x along the wall, z up); the exterior side is set by flipping the facing
                var ex = wdir; var ez = XYZ.BasisZ; var ey = ez.CrossProduct(ex);
                var sol = new List<(XYZ o, XYZ u, XYZ v, List<(double, double)> pts, double d)>();
                foreach (var s in r.GetProperty("solids").EnumerateArray())
                {
                    // plan solids are about the door box bottom centre in world axes (mm): re-expressed about onLine in the family axes
                    var oW = new XYZ(s.GetProperty("o")[0].GetDouble(), s.GetProperty("o")[1].GetDouble(), s.GetProperty("o")[2].GetDouble()) * MM + cen - onLine;
                    XYZ W(XYZ w) => new XYZ(w.DotProduct(ex), w.DotProduct(ey), w.DotProduct(ez));
                    var u = new XYZ(s.GetProperty("u")[0].GetDouble(), s.GetProperty("u")[1].GetDouble(), s.GetProperty("u")[2].GetDouble());
                    var v = new XYZ(s.GetProperty("v")[0].GetDouble(), s.GetProperty("v")[1].GetDouble(), s.GetProperty("v")[2].GetDouble());
                    var pts = s.GetProperty("loops")[0].GetProperty("pts").EnumerateArray().Select(q => (q[0].GetDouble() * MM, q[1].GetDouble() * MM)).ToList();
                    sol.Add((W(oW), W(u).Normalize(), W(v).Normalize(), pts, s.GetProperty("d").GetDouble() * MM));
                }
                string bname = S(r, "bname") ?? "Door"; var dd = r.GetProperty("door");
                string typeName = $"{dd.GetProperty("width").GetDouble() * 1000:F0} x {dd.GetProperty("height").GetDouble() * 1000:F0}mm";
                string key = $"{bname}|{typeName}|{openW:F0}|{openH:F0}|{openBelow:F0}|{wall.Width:F4}|{string.Join(";", sol.Select(x => $"{x.o.X:F3},{x.o.Y:F3},{x.o.Z:F3},{x.d:F3}"))}";
                if (!syms.TryGetValue(key, out var sym))
                {
                    string famName = $"{bname} {typeName}";
                    int k = 2; var fams = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Select(f => f.Name).ToHashSet();
                    string fn = famName; while (fams.Contains(fn)) fn = $"{famName} - {k++}";
                    string matName = S(r, "material")
                        ?? (r.TryGetProperty("shapes", out var shs) ? shs.EnumerateArray().Select(x => S(x, "material")).FirstOrDefault(x => x != null) : null)
                        ?? RebuildConfig.Current.DoorMaterial;
                    sym = MakeDoorFamily(doc, fn, typeName, sol, openW * MM, openH * MM, openBelow * MM, wall.Width, matName, mats, sb);
                    syms[key] = sym;
                }
                double wv0 = Build.SolidVolume(wall);
                FamilyInstance inst = null;
                var (st, log) = FailureLog.Run(doc, "door " + S(r, "name"), () =>
                {
                    if (!sym.IsActive) sym.Activate();
                    var lv = levels[S(r, "storey")];
                    // the family opening starts at the insertion level and the leaf is drawn openBelow above it: insert at the cut bottom
                    inst = doc.Create.NewFamilyInstance(new XYZ(onLine.X, onLine.Y, onLine.Z - openBelow * MM), sym, wall, lv, StructuralType.NonStructural);
                    doc.Regenerate();
                    if (inst.HandOrientation.DotProduct(ex) < 0 && inst.CanFlipHand) inst.flipHand();
                    if (inst.FacingOrientation.DotProduct(ey) < 0 && inst.CanFlipFacing) inst.flipFacing();
                    // the opening does not cut until Revit recalculates the door: flip twice in one committed transaction
                    if (inst.CanFlipFacing) { inst.flipFacing(); inst.flipFacing(); }
                    inst.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(S(r, "guid"));
                    GuidTools.Set(inst, S(r, "guid"));
                    var pt = S(r, "ptype");
                    if (pt != null) inst.get_Parameter(BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE)?.Set(pt);
                });
                n++;
                if (inst == null || st != TransactionStatus.Committed) { failed++; sb.AppendLine($"  FAILED {S(r, "name")}: {st} {log.Summary().Split('\n')[0]}"); continue; }
                if (!geo.TryGetValue(S(r, "guid"), out var g)) { sb.AppendLine($"  placed {S(r, "name")} (no IFC geometry to check against)"); continue; }
                var sbx = Build.SolidBox(inst);
                double dmax = sbx == null ? 1e9 : Enumerable.Range(0, 6).Max(i => Math.Abs(sbx[i] - g.bb[i]));
                double vol = Build.SolidVolume(inst) * Math.Pow(0.3048, 3), dv = (vol - g.v) / g.v;
                double wv1 = Build.SolidVolume(wall);
                bool pass = dmax <= 10 && Math.Abs(dv) <= 0.01;
                if (pass) ok++;
                sb.AppendLine($"  {(pass ? "ok  " : "MISS")} {S(r, "name")} in {S(host, "name")}: box {dmax:F0} mm [{(sbx == null ? "no solid" : string.Join(",", Enumerable.Range(0, 6).Select(i => (sbx[i] - g.bb[i]).ToString("F0", CI))))}] vol {vol:F4}/{g.v:F4} ({dv * 100:F1}%) wall {wv0 * 0.0283168:F3} -> {wv1 * 0.0283168:F3} m3");
            }
            catch (Exception ex) { failed++; sb.AppendLine($"  FAILED {S(r, "name")}: {ex.Message.Split('\n')[0]}"); }
            Flush();
        }
        sb.AppendLine($"doors matching the IFC: {ok} / {n}, failed {failed}");
        if (mode == "apply") tg.Assimilate(); else tg.RollBack();
        sb.AppendLine(mode == "apply" ? "APPLIED" : "probe: rolled back");
        Flush();
        return sb.ToString();
    }
}
