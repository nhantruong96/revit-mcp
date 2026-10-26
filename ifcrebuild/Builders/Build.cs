using System.Globalization;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace IfcRebuild;

/// <summary>
/// Loadable families and instances from plan.json. Own families: the template of the category (file name in the record,
/// folder from the config), solids (extrusions, round sweeps) in family mm coordinates, a type material parameter
/// (config materialParameter), instance depth parameters; line families from the Beams and Braces template with their end
/// faces locked to Member Left / Right; columns with bottom and top faces locked to the levels; nested shared parts (one
/// bolt, one cleat) so that schedules count every one. Library families: loaded from the RFA with a type per parameter
/// set. Instances placed by point (rotation, mirror), by line (justification Origin, no extensions, no end joins) or as
/// columns between levels; Mark = IfcGUID = GlobalId. The result is checked against the IFC box and volume of the
/// geometry file (config geometryFile, next to the plan). mode "probe" rolls everything back.
/// filter: "phase=a-b" | "cls=IfcPile" | "kind=line" | "names=a,b" | "prefix=X" (several joined by ';'); max = instances (0 = all).
/// </summary>
internal static class Build
{
    const double MM = 1 / 304.8;
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    class Overwrite : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = true; return true; }
        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        { source = FamilySource.Family; overwriteParameterValues = true; return true; }
    }

    static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static double D(JsonElement e, string n, double def = 0) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;
    static XYZ P3(JsonElement a) => new XYZ(a[0].GetDouble(), a[1].GetDouble(), a[2].GetDouble());
    static XYZ Pmm(JsonElement a) => P3(a) * MM;

    /// <summary>IFC volume (m3) and box (mm) per GlobalId from the geometry file next to the plan; empty when there is none.</summary>
    internal static Dictionary<string, (double v, double[] bb)> IfcGeometry(string planPath)
    {
        var d = new Dictionary<string, (double v, double[] bb)>();
        var path = Path.Combine(Path.GetDirectoryName(planPath), RebuildConfig.Current.GeometryFile);
        if (!File.Exists(path)) return d;
        using var gj = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var p in gj.RootElement.EnumerateObject())
            d[p.Name] = (p.Value.GetProperty("v").GetDouble(), p.Value.GetProperty("bb").EnumerateArray().Select(x => x.GetDouble() * 1000).ToArray());
        return d;
    }

    // ---------------------------------------------------------------- geometry in a family document
    static CurveArray Loop(JsonElement lp, XYZ o, XYZ u, XYZ v, double tol)
    {
        var ca = new CurveArray();
        if (lp.TryGetProperty("circle", out var c))
        {
            var cc = o + u * c[0].GetDouble() * MM + v * c[1].GetDouble() * MM; double r = c[2].GetDouble() * MM;
            ca.Append(Arc.Create(cc + u * r, cc - u * r, cc + v * r));
            ca.Append(Arc.Create(cc - u * r, cc + u * r, cc - v * r));
            return ca;
        }
        var pts = lp.GetProperty("pts").EnumerateArray().Select(q => o + u * q[0].GetDouble() * MM + v * q[1].GetDouble() * MM).ToList();
        // duplicate / too short edges dropped
        var clean = new List<XYZ>();
        foreach (var p in pts) if (clean.Count == 0 || clean[^1].DistanceTo(p) > tol) clean.Add(p);
        if (clean.Count > 2 && clean[0].DistanceTo(clean[^1]) <= tol) clean.RemoveAt(clean.Count - 1);
        if (clean.Count < 3) throw new InvalidOperationException("degenerate loop");
        for (int i = 0; i < clean.Count; i++) ca.Append(Line.CreateBound(clean[i], clean[(i + 1) % clean.Count]));
        return ca;
    }

    /// <summary>One solid of the plan into the family document; shift = family offset (line and column families). Returns the form.</summary>
    static GenericForm Solid(Document fd, JsonElement s, XYZ shift, double tol, bool columnY = false)
    {
        if (S(s, "k") == "sweep")
        {
            var path = s.GetProperty("path").EnumerateArray().Select(q => Pmm(q) + shift).ToList();
            double r = D(s, "r") * MM, ri = D(s, "ri") * MM;
            try
            {
                var pa = new CurveArray();
                for (int i = 0; i + 1 < path.Count; i++) pa.Append(Line.CreateBound(path[i], path[i + 1]));
                var d0 = (path[1] - path[0]).Normalize();
                XYZ nrm = path.Count >= 3 ? d0.CrossProduct((path[2] - path[1]).Normalize()) : d0.CrossProduct(Math.Abs(d0.Z) < 0.9 ? XYZ.BasisZ : XYZ.BasisX);
                if (nrm.GetLength() < 1e-6) nrm = d0.CrossProduct(Math.Abs(d0.Z) < 0.9 ? XYZ.BasisZ : XYZ.BasisX);
                nrm = nrm.Normalize();
                var pathPlane = SketchPlane.Create(fd, Plane.CreateByNormalAndOrigin(nrm, path[0]));
                // the profile is drawn in the family XY plane about the origin and Revit moves it onto the profile plane at
                // the start of the path; a profile drawn in the real profile plane gives an empty sweep
                var prof = new CurveArrArray();
                foreach (var rad in ri > 1e-9 ? new[] { r, ri } : new[] { r })
                {
                    var ca = new CurveArray();
                    ca.Append(Arc.Create(new XYZ(rad, 0, 0), new XYZ(-rad, 0, 0), new XYZ(0, rad, 0)));
                    ca.Append(Arc.Create(new XYZ(-rad, 0, 0), new XYZ(rad, 0, 0), new XYZ(0, -rad, 0)));
                    prof.Append(ca);
                }
                var sp = fd.Application.Create.NewCurveLoopsProfile(prof);
                return fd.FamilyCreate.NewSweep(true, pa, pathPlane, sp, 0, ProfilePlaneLocation.Start);
            }
            catch
            {
                // fallback: a cylinder per segment (the overlap at a corner is a few mm3)
                GenericForm last = null;
                for (int i = 0; i + 1 < path.Count; i++)
                {
                    var ax = (path[i + 1] - path[i]).Normalize();
                    var u = ax.CrossProduct(Math.Abs(ax.Z) < 0.9 ? XYZ.BasisZ : XYZ.BasisX).Normalize(); var v = ax.CrossProduct(u);
                    var prof = new CurveArrArray(); var ca = new CurveArray();
                    ca.Append(Arc.Create(path[i] + u * r, path[i] - u * r, path[i] + v * r)); ca.Append(Arc.Create(path[i] - u * r, path[i] + u * r, path[i] - v * r));
                    prof.Append(ca);
                    last = fd.FamilyCreate.NewExtrusion(true, prof, SketchPlane.Create(fd, Plane.CreateByNormalAndOrigin(ax, path[i])), path[i].DistanceTo(path[i + 1]));
                }
                return last;
            }
        }
        var o = Pmm(s.GetProperty("o")) + shift;
        var eu = P3(s.GetProperty("u")).Normalize(); var ev = P3(s.GetProperty("v")).Normalize();
        var n = eu.CrossProduct(ev).Normalize();
        double d = D(s, "d") * MM;
        // column family: a vertical extrusion of a rectangle (flange, web) is drawn as an extrusion along Y of its x-z
        // rectangle instead. Vertical extrusions locked to the Upper Ref Level ignore the top offset in the project and
        // end at the family height; solids drawn in the x-z plane follow base and top correctly.
        var loopsEl = s.GetProperty("loops");
        if (columnY && Math.Abs(Math.Abs(n.Z) - 1) < 1e-6 && loopsEl.GetArrayLength() == 1 && loopsEl[0].TryGetProperty("pts", out var ptsEl) && ptsEl.GetArrayLength() == 4)
        {
            var P = ptsEl.EnumerateArray().Select(q => o + eu * (q[0].GetDouble() * MM) + ev * (q[1].GetDouble() * MM)).ToList();
            var xs = P.Select(q => Math.Round(q.X / MM, 1)).Distinct().ToList(); var ys = P.Select(q => Math.Round(q.Y / MM, 1)).Distinct().ToList();
            if (xs.Count == 2 && ys.Count == 2)
            {
                double x0 = P.Min(q => q.X), x1 = P.Max(q => q.X), y0 = P.Min(q => q.Y), y1 = P.Max(q => q.Y);
                double z0 = n.Z > 0 ? o.Z : o.Z - d, z1 = z0 + d;
                var prof = new CurveArrArray(); var ca = new CurveArray();
                var c0 = new XYZ(x0, y0, z0); var c1 = new XYZ(x1, y0, z0); var c2 = new XYZ(x1, y0, z1); var c3 = new XYZ(x0, y0, z1);
                ca.Append(Line.CreateBound(c0, c1)); ca.Append(Line.CreateBound(c1, c2)); ca.Append(Line.CreateBound(c2, c3)); ca.Append(Line.CreateBound(c3, c0));
                prof.Append(ca);
                return fd.FamilyCreate.NewExtrusion(true, prof, SketchPlane.Create(fd, Plane.CreateByNormalAndOrigin(XYZ.BasisY, c0)), y1 - y0);
            }
        }
        var profile = new CurveArrArray();
        foreach (var lp in loopsEl.EnumerateArray()) profile.Append(Loop(lp, o, eu, ev, tol));
        return fd.FamilyCreate.NewExtrusion(true, profile, SketchPlane.Create(fd, Plane.CreateByNormalAndOrigin(n, o)), d);
    }

    static Material FamilyMaterial(Document fd, Material projMat, string name)
    {
        var m = new FilteredElementCollector(fd).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(x => x.Name == name);
        if (m != null) return m;
        m = (Material)fd.GetElement(Material.Create(fd, name));
        if (projMat != null) { m.Color = projMat.Color; m.Transparency = projMat.Transparency; m.MaterialClass = projMat.MaterialClass; }
        m.UseRenderAppearanceForShading = false;
        return m;
    }

    /// <summary>Family parameter driving the material of the solids: the structural material when the template has one, else the config's.</summary>
    static FamilyParameter MaterialParameter(FamilyManager fm, bool structural)
    {
        var name = RebuildConfig.Current.MaterialParameter;
        return (structural ? fm.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM) : null)
               ?? fm.GetParameters().FirstOrDefault(p => p.Definition.Name == name)
               ?? fm.AddParameter(name, GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
    }

    static readonly Dictionary<string, string> PartFiles = new();

    static string SafeName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>Shared part family (one bolt, one cleat, one rod piece): the solid about the origin, extruded along +z,
    /// saved as an RFA (cached per session) so that the parent family can load it. Schedules then count every nested
    /// shared instance. The part record may give "export_as" (e.g. IfcMechanicalFastener).</summary>
    static string PartFile(Document doc, JsonElement part, string cat, string material, Dictionary<string, Material> mats, StringBuilder sb)
    {
        string name = S(part, "name"), type = S(part, "type");
        string key = name + "|" + type + "|" + material + "|" + part.GetProperty("solid").GetRawText();
        if (PartFiles.TryGetValue(key, out var path) && File.Exists(path)) return path;
        var dir = RebuildConfig.Current.PartDir();
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, SafeName(name + " " + type) + ".rfa");
        // face based: a nested instance placed on a reference plane of the parent takes the plane's orientation
        // (a plain Generic Model made "Work Plane-Based" through the API stays upright)
        var fd = doc.Application.NewFamilyDocument(RebuildConfig.Current.Template(doc.Application, "Metric Generic Model face based.rft"));
        try
        {
            var (fst, flog) = FailureLog.Run(fd, "part family", () =>
            {
                var want = fd.Settings.Categories.Cast<Category>().FirstOrDefault(c => c.Name == cat);
                if (want != null && fd.OwnerFamily.FamilyCategory.Id != want.Id) try { fd.OwnerFamily.FamilyCategory = want; } catch { }
                // the face host of the template cannot be deleted (and is not shown in the project); other template forms go
                foreach (var id in new FilteredElementCollector(fd).OfClass(typeof(GenericForm)).ToElementIds().ToList()) try { fd.Delete(id); } catch { }
                fd.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.Set(1);
                // work plane based: placed on a reference plane of the parent, so that its +z follows the bolt / plate normal
                fd.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_WORK_PLANE_BASED)?.Set(1);
                var fm = fd.FamilyManager;
                var g = Solid(fd, part.GetProperty("solid"), XYZ.Zero, fd.Application.ShortCurveTolerance * 1.05);
                fd.Regenerate();
                double pv = 0; foreach (var o in g.get_Geometry(new Options()) ?? Enumerable.Empty<GeometryObject>()) if (o is Autodesk.Revit.DB.Solid so) pv += so.Volume;
                sb.AppendLine($"    part solid volume {pv * Math.Pow(304.8, 3) / 1e3:F2} cm3, category {fd.OwnerFamily.FamilyCategory.Name}, shared {fd.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger()}, workplane {fd.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_WORK_PLANE_BASED)?.AsInteger()}, placement {fd.OwnerFamily.FamilyPlacementType}");
                if (fm.CurrentType == null) fm.NewType(type); else if (fm.CurrentType.Name != type) fm.RenameCurrentType(type);
                if (!string.IsNullOrEmpty(material))
                {
                    var pm = MaterialParameter(fm, false);
                    var fmat = FamilyMaterial(fd, mats.TryGetValue(material, out var pmat) ? pmat : null, material);
                    fm.Set(pm, fmat.Id);
                    var p = g.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                    if (p != null && !p.IsReadOnly) try { fm.AssociateElementParameterToFamilyParameter(p, pm); } catch { }
                }
                var expAs = S(part, "export_as");
                var exp = fm.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_TYPE_AS);
                if (exp != null && expAs != null) try { fm.Set(exp, expAs); } catch { }
                fd.Regenerate();
            });
            if (fst != TransactionStatus.Committed) throw new InvalidOperationException($"part family {fst}: {flog.Summary().Split('\n')[0]}");
            if (File.Exists(path)) File.Delete(path);
            fd.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
        }
        finally { fd.Close(false); }
        PartFiles[key] = path;
        sb.AppendLine($"    part family {name} {type} -> {Path.GetFileName(path)}");
        return path;
    }

    /// <summary>Own family from the plan record; loaded into doc; returns the (single) symbol.</summary>
    static FamilySymbol MakeFamily(Document doc, JsonElement f, Dictionary<string, Material> mats, StringBuilder sb)
    {
        string kind = S(f, "kind"), cat = S(f, "category"), famName = S(f, "name"), typeName = S(f, "type") ?? "Standard";
        var fd = doc.Application.NewFamilyDocument(RebuildConfig.Current.Template(doc.Application, S(f, "template")));
        try
        {
            var (fst, flog) = FailureLog.Run(fd, "family geometry", () =>
            {
                var want = fd.Settings.Categories.Cast<Category>().FirstOrDefault(c => c.Name == cat);
                if (want != null && fd.OwnerFamily.FamilyCategory.Id != want.Id)
                    try { fd.OwnerFamily.FamilyCategory = want; } catch { sb.AppendLine($"    {famName}: category {cat} refused, stays {fd.OwnerFamily.FamilyCategory.Name}"); }
                foreach (var id in new FilteredElementCollector(fd).OfClass(typeof(GenericForm)).ToElementIds().ToList()) fd.Delete(id);
                double tol = fd.Application.ShortCurveTolerance * 1.05;
                var fm = fd.FamilyManager;
                var forms = new List<(GenericForm g, int i)>();
                XYZ shift = XYZ.Zero;
                ReferencePlane left = null, right = null; Level lower = null, upper = null; View planView = null, elevView = null;
                var rps = new FilteredElementCollector(fd).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
                planView = new FilteredElementCollector(fd).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(v => !v.IsTemplate);
                elevView = new FilteredElementCollector(fd).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.Elevation);
                double L = D(f, "L"), H = D(f, "H");
                if (kind == "line")
                {
                    // On placing, the family origin (Center) goes to the midpoint of the line and unconstrained geometry moves
                    // with it; faces within about 40 mm of a reference plane are constrained to that plane and follow it. So:
                    // System Length = L (Left / Right at +-L/2), Member Left / Right and the Stick Symbol planes moved to +-L/2,
                    // geometry drawn centred (x - L/2): every face then lands exactly and the end faces follow the line ends.
                    var pSys = fm.GetParameters().FirstOrDefault(p => p.Definition.Name == "System Length");
                    if (pSys != null) fm.Set(pSys, L * MM);
                    fd.Regenerate();
                    foreach (var (nm, x) in new[] { ("Member Left", -L * MM / 2), ("Member Right", L * MM / 2), ("Stick Symbol Left", -L * MM / 2), ("Stick Symbol Right", L * MM / 2) })
                    {
                        var rp = rps.FirstOrDefault(q => q.Name == nm);
                        if (rp != null && Math.Abs(rp.BubbleEnd.X - x) > 1e-9) ElementTransformUtils.MoveElement(fd, rp.Id, new XYZ(x - rp.BubbleEnd.X, 0, 0));
                    }
                    fd.Regenerate();
                    rps = new FilteredElementCollector(fd).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
                    left = rps.First(q => q.Name == "Member Left"); right = rps.First(q => q.Name == "Member Right");
                    shift = new XYZ(-L * MM / 2, 0, 0);
                }
                else if (kind == "column")
                {
                    var lvls = new FilteredElementCollector(fd).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                    lower = lvls.First(); upper = lvls.Last();
                    upper.Elevation = lower.Elevation + H * MM;
                    shift = new XYZ(0, 0, lower.Elevation);
                    fd.Regenerate();
                }
                int i = 0;
                foreach (var s in f.GetProperty("solids").EnumerateArray())
                {
                    try { forms.Add((Solid(fd, s, shift, tol, kind == "column"), i)); }
                    catch (Exception ex) { sb.AppendLine($"    {famName}: solid {i} {S(s, "k")} failed: {ex.Message.Split('\n')[0]}"); }
                    i++;
                }
                // nested shared parts (bolts, cleats, rod pieces): one instance per placement, oriented by its frame
                int nParts = 0;
                if (f.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    foreach (var part in parts.EnumerateArray())
                    {
                        string pf = PartFile(doc, part, cat, S(f, "material"), mats, sb);
                        if (!fd.LoadFamily(pf, new Overwrite(), out var pfam)) { sb.AppendLine($"    {famName}: part {S(part, "name")} not loaded"); continue; }
                        var psym = (FamilySymbol)fd.GetElement(pfam.GetFamilySymbolIds().First());
                        if (!psym.IsActive) psym.Activate();
                        // one reference plane per orientation (normal, x direction); the parts of that orientation sit on it
                        var planes = new Dictionary<string, ReferencePlane>();
                        foreach (var pl in part.GetProperty("places").EnumerateArray())
                        {
                            var o = Pmm(pl.GetProperty("o")) + shift;
                            var pu = P3(pl.GetProperty("u")).Normalize(); var pv = P3(pl.GetProperty("v")).Normalize(); var pn = pu.CrossProduct(pv).Normalize();
                            string pk = $"{pn.X:F4},{pn.Y:F4},{pn.Z:F4}|{pu.X:F4},{pu.Y:F4},{pu.Z:F4}|{o.DotProduct(pn):F5}";
                            if (!planes.TryGetValue(pk, out var rp))
                            {
                                // a plane through three points keeps its orientation (NewReferencePlane in a plan view makes it vertical)
                                rp = fd.FamilyCreate.NewReferencePlane2(o, o + pu * (500 * MM), o + pv * (500 * MM), planView);
                                planes[pk] = rp;
                            }
                            var inst = fd.FamilyCreate.NewFamilyInstance(rp.GetReference(), o, pu, psym);
                            // the frame Revit gives the instance on the plane may be mirrored against (u, v, n): fixed by mirroring
                            fd.Regenerate();
                            var itr = inst.GetTransform();
                            if (itr.BasisZ.DotProduct(pn) < 0) ElementTransformUtils.MirrorElements(fd, new List<ElementId> { inst.Id }, Plane.CreateByNormalAndOrigin(pn, o), false);
                            itr = inst.GetTransform();
                            if (itr.BasisY.DotProduct(pv) < 0) ElementTransformUtils.MirrorElements(fd, new List<ElementId> { inst.Id }, Plane.CreateByNormalAndOrigin(pv, o), false);
                            itr = inst.GetTransform();
                            if (itr.BasisX.DotProduct(pu) < 0) ElementTransformUtils.MirrorElements(fd, new List<ElementId> { inst.Id }, Plane.CreateByNormalAndOrigin(pu, o), false);
                            nParts++;
                        }
                        if (planes.Count > 0) sb.AppendLine($"    {famName}: {planes.Count} reference planes for {S(part, "name")}");
                    }
                if (forms.Count == 0 && nParts == 0) throw new InvalidOperationException("no geometry");
                if (nParts > 0) sb.AppendLine($"    {famName}: {nParts} nested part instances");
                fd.Regenerate();
                // locks: line families end faces to Member Left / Right; columns bottom / top faces to the levels
                int locks = 0;
                var opt = new Options { ComputeReferences = true, View = planView };
                foreach (var (g, _) in forms)
                {
                    IEnumerable<PlanarFace> Faces() => g.get_Geometry(opt).OfType<Autodesk.Revit.DB.Solid>().SelectMany(so => so.Faces.Cast<Face>()).OfType<PlanarFace>();
                    try
                    {
                        if (kind == "line" && f.TryGetProperty("lockable", out var lk) && lk.ValueKind == JsonValueKind.True)
                        {
                            foreach (var pf in Faces().Where(pf => Math.Abs(Math.Abs(pf.FaceNormal.X) - 1) < 1e-6))
                            {
                                var rp = pf.Origin.X > 0 ? right : left;
                                fd.FamilyCreate.NewAlignment(planView, rp.GetReference(), pf.Reference).IsLocked = true; locks++;
                            }
                        }
                        else if (kind == "column" && elevView != null)
                        {
                            foreach (var pf in Faces().Where(pf => Math.Abs(Math.Abs(pf.FaceNormal.Z) - 1) < 1e-6))
                            {
                                double z = pf.Origin.Z;
                                if (Math.Abs(z - lower.Elevation) < 1e-6) { fd.FamilyCreate.NewAlignment(elevView, lower.GetPlaneReference(), pf.Reference).IsLocked = true; locks++; }
                                else if (Math.Abs(z - upper.Elevation) < 1e-6) { fd.FamilyCreate.NewAlignment(elevView, upper.GetPlaneReference(), pf.Reference).IsLocked = true; locks++; }
                            }
                        }
                    }
                    catch (Exception ex) { sb.AppendLine($"    {famName}: lock failed: {ex.Message.Split('\n')[0]}"); }
                }
                // type: name, material parameter associated to every form
                if (fm.CurrentType == null) fm.NewType(typeName); else if (fm.CurrentType.Name != typeName) fm.RenameCurrentType(typeName);
                var matName = S(f, "material");
                if (!string.IsNullOrEmpty(matName))
                {
                    var pm = MaterialParameter(fm, true);
                    var fmat = FamilyMaterial(fd, mats.TryGetValue(matName, out var pmat) ? pmat : null, matName);
                    fm.Set(pm, fmat.Id);
                    foreach (var (g, _) in forms)
                    {
                        var p = g.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                        if (p != null && !p.IsReadOnly) try { fm.AssociateElementParameterToFamilyParameter(p, pm); } catch { }
                    }
                }
                // instance depth parameters (hanging rods): extrusion end associated to a family parameter
                if (f.TryGetProperty("dparams", out var dps) && dps.ValueKind == JsonValueKind.Object && kind != "line")
                    foreach (var dp in dps.EnumerateObject())
                    {
                        var g = forms.FirstOrDefault(x => x.i == int.Parse(dp.Name)).g as Extrusion;
                        if (g == null) continue;
                        var fp = fm.GetParameters().FirstOrDefault(p => p.Definition.Name == dp.Value.GetString())
                                 ?? fm.AddParameter(dp.Value.GetString(), GroupTypeId.Geometry, SpecTypeId.Length, true);
                        try
                        {
                            double d0 = g.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM).AsDouble();
                            fm.Set(fp, d0);
                            fm.AssociateElementParameterToFamilyParameter(g.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), fp);
                            fm.Set(fp, d0);
                        }
                        catch (Exception ex) { sb.AppendLine($"    {famName}: depth parameter {dp.Value.GetString()} not associated: {ex.Message.Split('\n')[0]}"); }
                    }
                // IFC export class of the family type when the Revit category differs
                var exp = S(f, "export_as");
                if (exp != null)
                {
                    var p = fm.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_TYPE_AS);
                    if (p != null) try { fm.Set(p, exp); } catch { }
                }
                var pre = S(f, "predefined");
                if (pre != null)
                {
                    var p = fm.get_Parameter(BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE_TYPE);
                    if (p != null) try { fm.Set(p, pre); } catch { }
                }
                fd.Regenerate();
                if (locks > 0) sb.AppendLine($"    {famName}: {forms.Count} forms, {locks} locks");
            });
            if (fst != TransactionStatus.Committed) throw new InvalidOperationException($"family geometry {fst}: {flog.Summary().Split('\n')[0]}");
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

    /// <summary>Library family: loaded once; a type per parameter set (name from the plan).</summary>
    static FamilySymbol LibSymbol(Document doc, JsonElement lib, Dictionary<string, Family> loaded, StringBuilder sb)
    {
        string file = S(lib, "file"), type = S(lib, "type");
        string famName = Path.GetFileNameWithoutExtension(file);
        if (!loaded.TryGetValue(famName, out var fam))
        {
            fam = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(x => x.Name == famName);
            if (fam == null)
            {
                using var t = new Transaction(doc, "load " + famName); t.Start();
                if (!doc.LoadFamily(file, new Overwrite(), out fam)) throw new InvalidOperationException("cannot load " + file);
                t.Commit();
            }
            loaded[famName] = fam;
        }
        var syms = fam.GetFamilySymbolIds().Select(id => (FamilySymbol)doc.GetElement(id)).ToList();
        var sym = syms.FirstOrDefault(x => x.Name == type);
        if (sym != null) return sym;
        using (var t = new Transaction(doc, "type " + type))
        {
            t.Start();
            sym = (FamilySymbol)syms[0].Duplicate(type);
            if (lib.TryGetProperty("params", out var prms) && prms.ValueKind == JsonValueKind.Object)
                foreach (var p in prms.EnumerateObject())
                {
                    var par = sym.LookupParameter(p.Name);
                    if (par == null || par.IsReadOnly) { sb.AppendLine($"    {famName}: type parameter {p.Name} missing"); continue; }
                    par.Set(p.Value.GetDouble() * MM);
                }
            t.Commit();
        }
        return sym;
    }

    internal static bool Pass(JsonElement r, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        foreach (var part in filter.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=');
            switch (kv[0])
            {
                case "phase":
                    var ab = kv[1].Split('-'); int ph = r.TryGetProperty("phase", out var pv) ? pv.GetInt32() : 0;
                    if (ph < int.Parse(ab[0]) || ph > int.Parse(ab.Length > 1 ? ab[1] : ab[0])) return false; break;
                case "cls": if (S(r, "cls") != kv[1]) return false; break;
                case "kind": if (S(r, "kind") != kv[1]) return false; break;
                case "names": if (!kv[1].Split(',').Contains(S(r, "name"))) return false; break;
                case "prefix": if (!(S(r, "name") ?? "").StartsWith(kv[1])) return false; break;
                default: throw new ArgumentException($"filter key '{kv[0]}' is not supported (phase, cls, kind, names, prefix)");
            }
        }
        return true;
    }

    public static string Run(Document doc, string planPath, string filter, string mode, int max, string reportPath)
    {
        var sb = new StringBuilder();
        void Flush() { if (!string.IsNullOrEmpty(reportPath)) try { File.WriteAllText(reportPath, sb.ToString()); } catch { } }
        var t0 = DateTime.Now;
        using var js = JsonDocument.Parse(File.ReadAllText(planPath));
        var root = js.RootElement;
        var fams = root.GetProperty("families");
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToDictionary(l => l.Name, l => l);
        var mats = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.First());
        var have = new FilteredElementCollector(doc).WhereElementIsNotElementType().Select(e => e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString())
            .Where(m => !string.IsNullOrEmpty(m)).ToHashSet();
        var todo = root.GetProperty("instances").EnumerateArray().Where(r => Pass(r, filter) && !have.Contains(S(r, "guid"))).ToList();
        if (max > 0) todo = todo.Take(max).ToList();
        sb.AppendLine($"to place: {todo.Count} (filter '{filter}', already in the model {have.Count})");
        var geo = IfcGeometry(planPath);
        using var tg = new TransactionGroup(doc, "IfcRebuild: build " + filter);
        tg.Start();
        // families first
        var syms = new Dictionary<string, FamilySymbol>();
        var loaded = new Dictionary<string, Family>();
        var ownKeys = todo.Where(r => r.TryGetProperty("fam", out _)).Select(r => S(r, "fam")).Distinct().ToList();
        var existing = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First());
        int nf = 0;
        foreach (var key in ownKeys)
        {
            var f = fams.GetProperty(key);
            try
            {
                if (existing.TryGetValue(S(f, "name"), out var ex)) { syms[key] = (FamilySymbol)doc.GetElement(ex.GetFamilySymbolIds().First()); continue; }
                syms[key] = MakeFamily(doc, f, mats, sb); nf++;
            }
            catch (Exception exn) { sb.AppendLine($"  FAMILY FAILED {S(f, "name")}: {exn.Message.Split('\n')[0]}"); }
            if (nf % 20 == 0) { Flush(); Progress.Report($"Build families {nf}/{ownKeys.Count}"); }
        }
        sb.AppendLine($"own families made {nf} of {ownKeys.Count} ({(DateTime.Now - t0).TotalSeconds:F0} s)");
        Flush();
        // instances in batches
        int placed = 0, failed = 0, batch = 250;
        var placedIds = new List<(JsonElement r, ElementId id)>();
        for (int b0 = 0; b0 < todo.Count; b0 += batch)
        {
            Progress.Report($"Build instances {b0}/{todo.Count}");
            var chunk = todo.Skip(b0).Take(batch).ToList();
            // library symbols of the chunk (their own transactions)
            foreach (var r in chunk.Where(r => r.TryGetProperty("lib", out _)))
            {
                var lib = r.GetProperty("lib"); var k = S(lib, "file") + "|" + S(lib, "type");
                if (!syms.ContainsKey(k)) try { syms[k] = LibSymbol(doc, lib, loaded, sb); } catch (Exception ex) { sb.AppendLine($"  LIB FAILED {k}: {ex.Message.Split('\n')[0]}"); }
            }
            var (st, log) = FailureLog.Run(doc, "IfcRebuild: place", () =>
            {
                foreach (var r in chunk)
                {
                    try
                    {
                        FamilySymbol sym;
                        if (r.TryGetProperty("lib", out var lib)) { if (!syms.TryGetValue(S(lib, "file") + "|" + S(lib, "type"), out sym)) throw new InvalidOperationException("no library symbol"); }
                        else if (!syms.TryGetValue(S(r, "fam"), out sym)) throw new InvalidOperationException("no family");
                        if (!sym.IsActive) sym.Activate();
                        var id = Place(doc, r, sym, levels);
                        placedIds.Add((r, id)); placed++;
                    }
                    catch (Exception ex) { failed++; if (failed <= 40) sb.AppendLine($"  FAILED {S(r, "name")} ({S(r, "kind")}): {ex.Message.Split('\n')[0]}"); }
                }
            });
            if (st != TransactionStatus.Committed) sb.AppendLine($"  batch {b0 / batch} commit={st} {log.Summary().Split('\n')[0]}");
            else if (log.Lines.Count > 0) sb.AppendLine($"  batch {b0 / batch}: {log.Summary().Split('\n')[0]}");
            Flush();
        }
        sb.AppendLine($"placed {placed}, failed {failed} ({(DateTime.Now - t0).TotalSeconds:F0} s)");
        // check: solid box against the IFC box (or the plan box), volume against the IFC
        Progress.Report("Build check");
        int ok = 0, nchk = 0; var miss = new List<string>(); var bands = new int[4];
        foreach (var (r, id) in placedIds)
        {
            var e = doc.GetElement(id); if (e == null) continue;
            nchk++;
            var bx = SolidBox(e); var pb = r.GetProperty("bb");
            var hasGeo = geo.TryGetValue(S(r, "guid"), out var g);
            var want = hasGeo ? g.bb
                : new[] { pb[0][0].GetDouble(), pb[0][1].GetDouble(), pb[0][2].GetDouble(), pb[1][0].GetDouble(), pb[1][1].GetDouble(), pb[1][2].GetDouble() };
            double dmax = bx == null ? 1e9 : Enumerable.Range(0, 6).Max(i => Math.Abs(bx[i] - want[i]));
            double vol = SolidVolume(e) * Math.Pow(0.3048, 3), iv = hasGeo ? g.v : -1;
            double dv = iv > 0 ? (vol - iv) / iv : 0;
            bool pass = dmax <= 10 && Math.Abs(dv) <= 0.01;
            bands[Math.Abs(dv) <= 0.01 ? 0 : Math.Abs(dv) <= 0.02 ? 1 : Math.Abs(dv) <= 0.05 ? 2 : 3]++;
            if (pass) ok++; else if (miss.Count < 40) miss.Add($"  MISS {S(r, "name")} {S(r, "kind")}: box {dmax:F0} mm [{(bx == null ? "no solid" : string.Join(",", Enumerable.Range(0, 6).Select(i => (bx[i] - want[i]).ToString("F0", CI))))}] vol {vol:F5}/{iv:F5} m3 ({dv * 100:F1}%)");
        }
        sb.AppendLine($"check: {ok} / {nchk} match (box 10 mm, volume 1%); volume bands <=1% {bands[0]}, <=2% {bands[1]}, <=5% {bands[2]}, >5% {bands[3]}" + (geo.Count == 0 ? " (no geometry file: plan boxes only)" : ""));
        foreach (var m in miss) sb.AppendLine(m);
        if (mode == "apply") tg.Assimilate(); else tg.RollBack();
        sb.AppendLine(mode == "apply" ? "APPLIED" : "probe: rolled back");
        sb.AppendLine($"total {(DateTime.Now - t0).TotalSeconds:F0} s");
        Flush();
        return sb.ToString();
    }

    static ElementId Place(Document doc, JsonElement r, FamilySymbol sym, Dictionary<string, Level> levels)
    {
        string kind = S(r, "kind"), guid = S(r, "guid");
        FamilyInstance inst;
        var cat = sym.Family.FamilyCategory;
        if (kind == "line")
        {
            var a = Pmm(r.GetProperty("p0")); var b = Pmm(r.GetProperty("p1"));
            var lv = levels[S(r, "level")];
            var stype = S(r, "structural") == "Brace" ? StructuralType.Brace : StructuralType.Beam;
            inst = doc.Create.NewFamilyInstance(Line.CreateBound(a, b), sym, lv, stype);
            inst.get_Parameter(BuiltInParameter.Y_JUSTIFICATION)?.Set((int)YJustification.Origin);
            inst.get_Parameter(BuiltInParameter.Z_JUSTIFICATION)?.Set((int)ZJustification.Origin);
            inst.get_Parameter(BuiltInParameter.START_EXTENSION)?.Set(0.0);
            inst.get_Parameter(BuiltInParameter.END_EXTENSION)?.Set(0.0);
            inst.get_Parameter(BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE)?.Set(0.0);
            try { StructuralFramingUtils.DisallowJoinAtEnd(inst, 0); StructuralFramingUtils.DisallowJoinAtEnd(inst, 1); } catch { }
            // vertical member: Revit's cross-section orientation differs from the plan frame (family z = world X for a
            // vertical axis), so a vertical brace comes out turned 90 degrees; Cross-Section Rotation turns the section
            // about the axis until the family z matches
            var ex = (b - a).Normalize();
            if (Math.Abs(ex.Z) > 0.999)
            {
                var want = (XYZ.BasisX - ex * XYZ.BasisX.DotProduct(ex)).Normalize();
                var pAng = inst.get_Parameter(BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE);
                if (pAng != null && !pAng.IsReadOnly)
                {
                    doc.Regenerate();
                    var have = inst.GetTransform().BasisZ;
                    double ang = Math.Atan2(have.CrossProduct(want).DotProduct(ex), have.DotProduct(want));
                    if (Math.Abs(ang) > 1e-4)
                    {
                        pAng.Set(ang); doc.Regenerate();
                        if (inst.GetTransform().BasisZ.DotProduct(want) < 0.999) { pAng.Set(-ang); doc.Regenerate(); }
                    }
                }
            }
        }
        else if (kind == "column")
        {
            var o = Pmm(r.GetProperty("o"));
            var baseLv = levels[S(r, "base_level")];
            var topName = S(r, "top_level");
            var topLv = topName != null && levels.ContainsKey(topName) ? levels[topName] : baseLv;
            double z0 = D(r, "z0") * MM, z1 = D(r, "z1") * MM;
            inst = doc.Create.NewFamilyInstance(new XYZ(o.X, o.Y, baseLv.Elevation), sym, baseLv, StructuralType.Column);
            inst.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.Set(baseLv.Id);
            inst.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(topLv.Id);
            inst.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM)?.Set(z0 - baseLv.Elevation);
            inst.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.Set(z1 - topLv.Elevation);
            double ang = D(r, "ang") * Math.PI / 180;
            if (Math.Abs(ang) > 1e-9) ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateBound(o, o + XYZ.BasisZ), ang);
        }
        else
        {
            var o = Pmm(r.GetProperty("o"));
            var lv = levels[S(r, "level")];
            var stype = cat?.Id.Value == (long)BuiltInCategory.OST_StructuralFoundation ? StructuralType.Footing : StructuralType.NonStructural;
            // the Z of the location is taken as the offset from the level
            inst = doc.Create.NewFamilyInstance(new XYZ(o.X, o.Y, o.Z - lv.Elevation), sym, lv, stype);
            double ang = D(r, "ang") * Math.PI / 180;
            if (Math.Abs(ang) > 1e-9) ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateBound(o, o + XYZ.BasisZ), ang);
            if (r.TryGetProperty("mirror", out var mv) && mv.ValueKind == JsonValueKind.True)
            {
                var n = new XYZ(-Math.Sin(ang), Math.Cos(ang), 0);
                ElementTransformUtils.MirrorElements(doc, new List<ElementId> { inst.Id }, Plane.CreateByNormalAndOrigin(n, o), false);
            }
            if (r.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Object)
                foreach (var p in ps.EnumerateObject()) inst.LookupParameter(p.Name)?.Set(p.Value.GetDouble() * MM);
        }
        inst.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(guid);
        GuidTools.Set(inst, guid);
        var exp = S(r, "export_as");
        if (exp != null) inst.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_AS)?.Set(exp);
        var pre = S(r, "ptype");
        if (pre != null) inst.get_Parameter(BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE)?.Set(pre);
        return inst.Id;
    }

    /// <summary>Deletes the model elements whose Mark is a plan GlobalId matching the filter, then the families left without
    /// instances among the families they used. mode "probe" | "apply".</summary>
    public static string Delete(Document doc, string planPath, string filter, string mode)
    {
        using var js = JsonDocument.Parse(File.ReadAllText(planPath));
        var root = js.RootElement;
        IEnumerable<JsonElement> List(string key) => root.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();
        var guids = List("instances").Concat(List("system")).Concat(List("doors")).Where(r => Pass(r, filter)).Select(r => S(r, "guid")).ToHashSet();
        var els = new FilteredElementCollector(doc).WhereElementIsNotElementType().Where(e => guids.Contains(e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")).ToList();
        var fams = els.OfType<FamilyInstance>().Select(fi => fi.Symbol.Family.Id).Distinct().ToList();
        int nf = 0;
        using var tg = new TransactionGroup(doc, "IfcRebuild: delete " + filter); tg.Start();
        var (st, log) = FailureLog.Run(doc, "delete", () =>
        {
            doc.Delete(els.Select(e => e.Id).ToList());
            doc.Regenerate();
            foreach (var fid in fams)
            {
                var fam = doc.GetElement(fid) as Family; if (fam == null) continue;
                bool used = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Any(fi => fi.Symbol.Family.Id == fid);
                if (!used) { doc.Delete(fid); nf++; }
            }
        });
        if (mode == "apply") tg.Assimilate(); else tg.RollBack();
        return $"deleted {els.Count} elements and {nf} families ({filter}) commit={st} {(mode == "apply" ? "APPLIED" : "rolled back")}";
    }

    /// <summary>Nested shared families are elements of their own in the project: the parent's geometry is taken with its sub-components.</summary>
    internal static IEnumerable<Element> WithSubs(Element e)
    {
        yield return e;
        if (e is FamilyInstance fi)
            foreach (var id in fi.GetSubComponentIds())
            {
                var sub = e.Document.GetElement(id);
                if (sub != null) foreach (var x in WithSubs(sub)) yield return x;
            }
    }

    internal static double SolidVolume(Element e0)
    {
        double v = 0;
        foreach (var e in WithSubs(e0)) v += SolidVolumeOne(e);
        return v;
    }

    static double SolidVolumeOne(Element e)
    {
        double v = 0;
        var g = e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
        if (g == null) return 0;
        void Walk(GeometryElement ge)
        {
            foreach (var o in ge)
                if (o is Autodesk.Revit.DB.Solid s && s.Faces.Size > 2) v += s.Volume;
                else if (o is GeometryInstance gi) Walk(gi.GetInstanceGeometry());
        }
        Walk(g);
        return v;
    }

    /// <summary>Box (mm, world) of the solids of an element, null without solids.</summary>
    internal static double[] SolidBox(Element e0)
    {
        double[] b = { 1e12, 1e12, 1e12, -1e12, -1e12, -1e12 };
        foreach (var e in WithSubs(e0))
        {
            var bb = SolidBoxOne(e); if (bb == null) continue;
            for (int i = 0; i < 3; i++) { b[i] = Math.Min(b[i], bb[i]); b[i + 3] = Math.Max(b[i + 3], bb[i + 3]); }
        }
        return b[0] > 1e11 ? null : b;
    }

    static double[] SolidBoxOne(Element e)
    {
        double[] b = { 1e12, 1e12, 1e12, -1e12, -1e12, -1e12 };
        void Add(Autodesk.Revit.DB.Solid s)
        {
            if (s.Volume <= 1e-12 || s.Faces.Size <= 2) return;     // light source spheres of lighting fixtures left out
            foreach (Edge ed in s.Edges)
                foreach (var p in ed.Tessellate())
                { b[0] = Math.Min(b[0], p.X); b[1] = Math.Min(b[1], p.Y); b[2] = Math.Min(b[2], p.Z); b[3] = Math.Max(b[3], p.X); b[4] = Math.Max(b[4], p.Y); b[5] = Math.Max(b[5], p.Z); }
        }
        var g = e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
        if (g == null) return null;
        void Walk(GeometryElement ge)
        {
            foreach (var o in ge)
                if (o is Autodesk.Revit.DB.Solid s) Add(s);
                else if (o is GeometryInstance gi) Walk(gi.GetInstanceGeometry());
        }
        Walk(g);
        if (b[0] > 1e11) return null;
        return b.Select(x => x / MM).ToArray();
    }
}
