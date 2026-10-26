using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace IfcRebuild;

/// <summary>
/// Family housekeeping and two family template experiments. libDir (optional) is a folder of library .rfa files: a family
/// whose name matches a file there counts as a library family (FamilyStats) and keeps its name (RenameFamiliesByCode).
/// </summary>
internal static class Families
{
    const double MM = 1 / 304.8;
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    class Overwrite : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = true; return true; }
        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        { source = FamilySource.Family; overwriteParameterValues = true; return true; }
    }

    static HashSet<string> Library(string libDir) =>
        string.IsNullOrWhiteSpace(libDir) ? new(StringComparer.OrdinalIgnoreCase)
            : new(Directory.EnumerateFiles(libDir, "*.rfa", SearchOption.AllDirectories).Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);

    /// <summary>Read-only: bounding box (mm) of an element's solids at Coarse, Medium and Fine detail. uid = ElementId or UniqueId.</summary>
    public static string GeoDetail(Document doc, string uid)
    {
        var e = long.TryParse(uid, out var eid) ? doc.GetElement(new ElementId(eid)) : doc.GetElement(uid);
        if (e == null) return "not found " + uid + " | elements " + new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount();
        var sb = new StringBuilder(e.Name + " | " + e.Category?.Name + "\n");
        foreach (var lv in new[] { ViewDetailLevel.Coarse, ViewDetailLevel.Medium, ViewDetailLevel.Fine })
        {
            var pts = new List<XYZ>();
            void W(GeometryElement g) { foreach (var o in g) { if (o is Solid so && so.Volume > 1e-9) foreach (Edge ed in so.Edges) pts.AddRange(ed.Tessellate()); else if (o is GeometryInstance gi) W(gi.GetInstanceGeometry()); } }
            var ge = e.get_Geometry(new Options { DetailLevel = lv }); if (ge != null) W(ge);
            if (pts.Count == 0) { sb.AppendLine(lv + ": no solid"); continue; }
            double F(Func<XYZ, double> f) => (pts.Max(f) - pts.Min(f)) * 304.8;
            sb.AppendLine($"{lv}: dx {F(p => p.X):F1} dy {F(p => p.Y):F1} dz {F(p => p.Z):F1}");
        }
        return sb.ToString();
    }

    /// <summary>Read-only: families loaded, families and types with placed instances, how many of them are library families
    /// (by name, libDir), and families per category.</summary>
    public static string Stats(Document doc, string libDir)
    {
        var lib = Library(libDir);
        var used = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Select(fi => fi.Symbol).Where(s => s != null).ToList();
        var fams = used.Select(s => s.Family.Name).Distinct().ToList(); var types = used.Select(s => s.Id).Distinct().Count();
        var allF = new FilteredElementCollector(doc).OfClass(typeof(Family)).GetElementCount();
        var inLib = fams.Where(lib.Contains).ToList();
        var cats = used.GroupBy(s => s.Family.FamilyCategory?.Name ?? "-").Select(g => (g.Key, g.Select(x => x.Family.Name).Distinct().Count())).OrderByDescending(x => x.Item2).Take(10);
        return $"families loaded {allF} | with instances {fams.Count} | types used {types} | library families {inLib.Count} ({string.Join(", ", inLib.Take(15))})\n" +
               $"families per category: {string.Join("; ", cats.Select(c => c.Key + " " + c.Item2))}";
    }

    /// <summary>Purges unused families and family types (Document.GetUnusedElements, as Purge Unused), nothing else. mode probe | apply.</summary>
    public static string PurgeUnused(Document doc, string mode)
    {
        var ids = doc.GetUnusedElements(new HashSet<ElementId>()).Where(id => doc.GetElement(id) is Family || doc.GetElement(id) is FamilySymbol).ToList();
        int nf = ids.Count(id => doc.GetElement(id) is Family), ns = ids.Count - nf;
        if (mode != "apply") return $"Purge probe: unused families {nf}, unused types {ns}";
        var (st, _) = FailureLog.Run(doc, "Purge unused families", () => { foreach (var id in ids) { try { if (doc.GetElement(id) != null) doc.Delete(id); } catch { } } });
        return $"Purge apply: {st} | deleted families {nf}, types {ns} | families left {new FilteredElementCollector(doc).OfClass(typeof(Family)).GetElementCount()}";
    }

    /// <summary>Families made by the build get a classification code as name prefix, e.g. "B1020 Roof beam ...": the code most
    /// frequent among their instances in tsvPath (lines "UniqueId&lt;TAB&gt;code"), cut to codeLength characters (default 5, a
    /// Uniformat II level 3 code). Library families (libDir) keep their names. mode probe | apply.</summary>
    public static string RenameByCode(Document doc, string tsvPath, string libDir, string mode, string codeLength)
    {
        int len = Batch.Int(codeLength, 5);
        var code = File.ReadAllLines(tsvPath).Select(l => l.Split('\t')).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1].Trim());
        var lib = Library(libDir);
        var plan = new List<(Family f, string name)>(); int noCode = 0;
        foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where(fi => fi.Symbol != null).GroupBy(fi => fi.Symbol.Family.Id))
        {
            var fam = doc.GetElement(g.Key) as Family; if (fam == null || lib.Contains(fam.Name)) continue;
            var c = g.Select(fi => code.TryGetValue(fi.UniqueId, out var v) && v.Length > 0 ? v[..Math.Min(len, v.Length)] : null).Where(v => v != null)
                .GroupBy(v => v).OrderByDescending(x => x.Count()).FirstOrDefault()?.Key;
            if (c == null) { noCode++; continue; }
            if (fam.Name.StartsWith(c)) continue;
            plan.Add((fam, c + " " + fam.Name));
        }
        var sb = new StringBuilder($"RenameFamilies {mode}: to rename {plan.Count} | without code {noCode}");
        foreach (var x in plan.Take(8)) sb.Append(" | " + x.name);
        if (mode != "apply") return sb.ToString();
        int ok = 0;
        var (st, _) = FailureLog.Run(doc, "Family names by code", () => { foreach (var (f, n) in plan) { try { f.Name = n; ok++; } catch { } } });
        return sb + $" | {st}, renamed {ok}";
    }

    /// <summary>Experiment: an L-shaped bolt (r 12, legs 600 and 100 mm) as NewSweep variants and as two cylinders, in a Metric
    /// Generic Model family that is closed unsaved; reports the volumes.</summary>
    public static string TestSweep(Document doc)
    {
        var sb = new StringBuilder();
        var fd = doc.Application.NewFamilyDocument(RebuildConfig.Current.Template(doc.Application, "Metric Generic Model.rft"));
        try
        {
            var path = new List<XYZ> { new XYZ(0, 0, 612 * MM), new XYZ(0, 0, 12 * MM), new XYZ(80 * MM, 60 * MM, 12 * MM) };
            double r = 12 * MM;
            string Vol(GenericForm g) { double v = 0; foreach (var o in g.get_Geometry(new Options())) if (o is Solid so) v += so.Volume; return (v * Math.Pow(304.8, 3) / 1e3).ToString("F2", CI) + " cm3"; }
            var (st, log) = FailureLog.Run(fd, "sweep test", () =>
            {
                var d0 = (path[1] - path[0]).Normalize(); var d1 = (path[2] - path[1]).Normalize();
                var nrm = d0.CrossProduct(d1).Normalize();
                // A: profile at the start of the path, its plane normal to the first leg
                try
                {
                    var pa = new CurveArray(); pa.Append(Line.CreateBound(path[0], path[1])); pa.Append(Line.CreateBound(path[1], path[2]));
                    var pp = SketchPlane.Create(fd, Plane.CreateByNormalAndOrigin(nrm, path[0]));
                    var a1 = nrm; var a2 = d0.CrossProduct(nrm).Normalize();
                    var prof = new CurveArrArray(); var ca = new CurveArray();
                    ca.Append(Arc.Create(path[0] + a1 * r, path[0] - a1 * r, path[0] + a2 * r)); ca.Append(Arc.Create(path[0] - a1 * r, path[0] + a1 * r, path[0] - a2 * r)); prof.Append(ca);
                    var sw = fd.FamilyCreate.NewSweep(true, pa, pp, fd.Application.Create.NewCurveLoopsProfile(prof), 0, ProfilePlaneLocation.Start);
                    fd.Regenerate(); sb.AppendLine("A sweep, profile at the start: " + Vol(sw));
                }
                catch (Exception ex) { sb.AppendLine("A failed: " + ex.Message); }
                // B: profile drawn in the family XY plane at the origin (does Revit move it onto the profile plane?)
                try
                {
                    var pa = new CurveArray(); pa.Append(Line.CreateBound(path[0], path[1])); pa.Append(Line.CreateBound(path[1], path[2]));
                    var pp = SketchPlane.Create(fd, Plane.CreateByNormalAndOrigin(nrm, path[0]));
                    var prof = new CurveArrArray(); var ca = new CurveArray();
                    ca.Append(Arc.Create(new XYZ(r, 0, 0), new XYZ(-r, 0, 0), new XYZ(0, r, 0))); ca.Append(Arc.Create(new XYZ(-r, 0, 0), new XYZ(r, 0, 0), new XYZ(0, -r, 0))); prof.Append(ca);
                    var sw = fd.FamilyCreate.NewSweep(true, pa, pp, fd.Application.Create.NewCurveLoopsProfile(prof), 0, ProfilePlaneLocation.Start);
                    fd.Regenerate(); sb.AppendLine("B sweep, profile in XY: " + Vol(sw));
                }
                catch (Exception ex) { sb.AppendLine("B failed: " + ex.Message); }
                // C: one cylinder per leg
                try
                {
                    double v = 0;
                    for (int i = 0; i + 1 < path.Count; i++)
                    {
                        var ax = (path[i + 1] - path[i]).Normalize();
                        var u = ax.CrossProduct(Math.Abs(ax.Z) < 0.9 ? XYZ.BasisZ : XYZ.BasisX).Normalize(); var vv = ax.CrossProduct(u);
                        var prof = new CurveArrArray(); var ca = new CurveArray();
                        ca.Append(Arc.Create(path[i] + u * r, path[i] - u * r, path[i] + vv * r)); ca.Append(Arc.Create(path[i] - u * r, path[i] + u * r, path[i] - vv * r)); prof.Append(ca);
                        var ex = fd.FamilyCreate.NewExtrusion(true, prof, SketchPlane.Create(fd, Plane.CreateByNormalAndOrigin(ax, path[i])), path[i].DistanceTo(path[i + 1]));
                        fd.Regenerate();
                        foreach (var o in ex.get_Geometry(new Options())) if (o is Solid so) v += so.Volume;
                    }
                    sb.AppendLine($"C cylinders: {v * Math.Pow(304.8, 3) / 1e3:F2} cm3 (sum, overlap not removed)");
                }
                catch (Exception ex) { sb.AppendLine("C failed: " + ex.Message); }
                sb.AppendLine($"analytic legs: {Math.PI * 144 * 700 / 1e3:F2} cm3");
            });
            sb.AppendLine($"commit={st} {log.Summary().Split('\n')[0]}");
        }
        finally { fd.Close(false); }
        return sb.ToString();
    }

    /// <summary>Experiment: what drives Member Left / Right in the Metric Structural Framing - Beams and Braces template, and how a
    /// family of length L (short pieces at Member Left, in the middle, at Member Right) behaves when placed on a line of length
    /// L (mm). The family is loaded and placed, then everything in the project is rolled back.</summary>
    public static string TestTemplate(Document doc, string mmLength)
    {
        var sb = new StringBuilder();
        double L = double.Parse(mmLength, CI) * MM;
        var fd = doc.Application.NewFamilyDocument(RebuildConfig.Current.Template(doc.Application, "Metric Structural Framing - Beams and Braces.rft"));
        Family fam = null;
        using var tg = new TransactionGroup(doc, "Template test"); tg.Start();
        try
        {
            var fm = fd.FamilyManager;
            try
            {
                foreach (var fp in fm.GetParameters().Where(p => p.Definition.Name is "Length" or "Cross-Section Rotation" or "Structural Material"))
                    sb.AppendLine($"param {fp.Definition.Name}: instance={fp.IsInstance} reporting={fp.IsReporting} formula={fp.Formula} readonly={fp.IsReadOnly} value={(fm.CurrentType.HasValue(fp) ? fm.CurrentType.AsDouble(fp) / MM : 0):F0}");
                foreach (var rp in new FilteredElementCollector(fd).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>())
                    sb.AppendLine($"refplane {rp.Name}: x={rp.BubbleEnd.X / MM:F0} y={rp.BubbleEnd.Y / MM:F0} normal {rp.Normal} pinned={rp.Pinned}");
                foreach (var ce in new FilteredElementCollector(fd).OfClass(typeof(CurveElement)).Cast<CurveElement>())
                    sb.AppendLine($"curve {ce.GetType().Name} {ce.Category?.Name}: ({ce.GeometryCurve.GetEndPoint(0).X / MM:F0},{ce.GeometryCurve.GetEndPoint(0).Y / MM:F0})-({ce.GeometryCurve.GetEndPoint(1).X / MM:F0},{ce.GeometryCurve.GetEndPoint(1).Y / MM:F0})");
                foreach (var d in new FilteredElementCollector(fd).OfClass(typeof(Dimension)).Cast<Dimension>())
                { string lk = "?", lb = "?"; try { lk = d.IsLocked.ToString(); } catch { } try { lb = d.FamilyLabel?.Definition.Name; } catch { } sb.AppendLine($"dimension {d.Name} segments={d.NumberOfSegments} value={(d.NumberOfSegments <= 1 ? (d.Value ?? 0) / MM : 0):F0} label={lb} locked={lk} refs={d.References.Size}"); }
                var (st, log) = FailureLog.Run(fd, "flex", () =>
                {
                    foreach (var id in new FilteredElementCollector(fd).OfClass(typeof(GenericForm)).ToElementIds().ToList()) fd.Delete(id);
                    var rps = new FilteredElementCollector(fd).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
                    var right = rps.First(r => r.Name == "Member Right"); var left = rps.First(r => r.Name == "Member Left");
                    foreach (var fp in fm.GetParameters()) sb.AppendLine($"fparam {fp.Definition.Name} inst={fp.IsInstance} rep={fp.IsReporting} ro={fp.IsReadOnly} v={(fm.CurrentType.HasValue(fp) && fp.StorageType == StorageType.Double ? (fm.CurrentType.AsDouble(fp) ?? 0) / MM : 0):F0}");
                    var pSys = fm.GetParameters().FirstOrDefault(p => p.Definition.Name == "System Length");
                    double setback = (rps.First(r => r.Name == "Right").BubbleEnd.X - right.BubbleEnd.X) + (left.BubbleEnd.X - rps.First(r => r.Name == "Left").BubbleEnd.X);
                    sb.AppendLine($"setback total {setback / MM:F0}; System Length param {(pSys == null ? "missing" : "found")}");
                    if (pSys != null) { try { fm.Set(pSys, L); } catch (Exception ex) { sb.AppendLine("set System Length failed " + ex.Message); } }
                    fd.Regenerate();
                    foreach (var (nm, x) in new[] { ("Member Left", -L / 2), ("Member Right", L / 2), ("Stick Symbol Left", -L / 2), ("Stick Symbol Right", L / 2) })
                    { var rp = rps.First(r => r.Name == nm); ElementTransformUtils.MoveElement(fd, rp.Id, new XYZ(x - rp.BubbleEnd.X, 0, 0)); }
                    fd.Regenerate();
                    rps = new FilteredElementCollector(fd).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
                    foreach (var rp in rps) sb.AppendLine($"after move: refplane {rp.Name}: x={rp.BubbleEnd.X / MM:F0}");
                    foreach (var ce in new FilteredElementCollector(fd).OfClass(typeof(CurveElement)).Cast<CurveElement>())
                        sb.AppendLine($"after move: curve {ce.GetType().Name} {ce.Category?.Name}: ({ce.GeometryCurve.GetEndPoint(0).X / MM:F0},{ce.GeometryCurve.GetEndPoint(0).Y / MM:F0})-({ce.GeometryCurve.GetEndPoint(1).X / MM:F0},{ce.GeometryCurve.GetEndPoint(1).Y / MM:F0})");
                    double x0 = left.BubbleEnd.X, x1 = rps.First(r => r.Name == "Member Right").BubbleEnd.X;
                    // pieces 1000 mm long, 100 x 100: at Member Left, 10 mm inside it, middle, near and at Member Right, and two fixed positions
                    foreach (var xs in new[] { x0, x0 + 10 * MM, (x0 + x1) / 2 - 500 * MM, x1 - 1040 * MM, x1 - 1000 * MM, -1250 * MM, 1000 * MM })
                    {
                        var prof = new CurveArrArray(); var ca = new CurveArray();
                        var q = new[] { new XYZ(xs, -50 * MM, -50 * MM), new XYZ(xs + 1000 * MM, -50 * MM, -50 * MM), new XYZ(xs + 1000 * MM, -50 * MM, 50 * MM), new XYZ(xs, -50 * MM, 50 * MM) };
                        for (int i = 0; i < 4; i++) ca.Append(Line.CreateBound(q[i], q[(i + 1) % 4]));
                        prof.Append(ca);
                        fd.FamilyCreate.NewExtrusion(true, prof, SketchPlane.Create(fd, Plane.CreateByNormalAndOrigin(XYZ.BasisY, new XYZ(0, -50 * MM, 0))), 100 * MM);
                    }
                    fd.Regenerate();
                    foreach (var g in new FilteredElementCollector(fd).OfClass(typeof(GenericForm)).Cast<GenericForm>())
                    { var bb = g.get_BoundingBox(null); sb.AppendLine($"form box x {bb.Min.X / MM:F0}..{bb.Max.X / MM:F0}"); }
                });
                sb.AppendLine($"family commit={st} {log.Summary().Split('\n')[0]}");
            }
            catch (Exception ex) { sb.AppendLine("EXC " + ex.Message); }
            fam = fd.LoadFamily(doc, new Overwrite());
        }
        finally { fd.Close(false); }
        FailureLog.Run(doc, "place", () =>
        {
            var sym = (FamilySymbol)doc.GetElement(fam.GetFamilySymbolIds().First()); sym.Activate();
            var lv = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
            var inst = doc.Create.NewFamilyInstance(Line.CreateBound(new XYZ(0, 0, 0), new XYZ(L, 0, 0)), sym, lv, StructuralType.Beam);
            inst.get_Parameter(BuiltInParameter.Y_JUSTIFICATION)?.Set((int)YJustification.Origin);
            inst.get_Parameter(BuiltInParameter.Z_JUSTIFICATION)?.Set((int)ZJustification.Origin);
            inst.get_Parameter(BuiltInParameter.START_EXTENSION)?.Set(0.0); inst.get_Parameter(BuiltInParameter.END_EXTENSION)?.Set(0.0);
            doc.Regenerate();
            foreach (var o in inst.get_Geometry(new Options()))
                if (o is GeometryInstance gi) foreach (var o2 in gi.GetInstanceGeometry()) if (o2 is Solid so && so.Volume > 1e-12)
                { var bb = so.GetBoundingBox(); var mn = bb.Transform.OfPoint(bb.Min); var mx = bb.Transform.OfPoint(bb.Max); sb.AppendLine($"instance solid x {mn.X / MM:F0}..{mx.X / MM:F0} vol {so.Volume * Math.Pow(304.8, 3) / 1e6:F4} dm3"); }
            sb.AppendLine($"instance origin {inst.GetTransform().Origin.X / MM:F0}");
        });
        tg.RollBack();
        return sb.ToString();
    }
}
