using System.Globalization;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;

namespace IfcRebuild;

/// <summary>
/// System families from plan.json "system": walls (profile walls on the IFC elevation outline), cladding walls (profile
/// with rectangular openings for the cut-outs), floors (sloped by slope arrow), roof sheets (extrusion roofs), pipes,
/// cable trays, rooms. Records (m): {"name", "guid", "cls", "ptype", "storey", "phase", "material", "type"?, "exterior"?,
/// "dn"?, "shapes": [{"t": "box" | "prism_xz" | "prism_yz", ...}], "profile"?}. A missing "type" is named from the config
/// typeNames. mode "probe" | "apply"; what = "walls" | "cladding" | "floors" | "roofs" | "pipes" | "trays" | "rooms" |
/// "all" | a comma list of those | "names=a,b".
/// </summary>
internal static class SystemFamilies
{
    const double MM = 1 / 304.8;
    static bool Debug = false;
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static double[] Arr(JsonElement a) => a.EnumerateArray().Select(x => x.GetDouble()).ToArray();

    /// <summary>Outside diameters (mm) of the standard steel pipe sizes (ISO 65 / ASME B36.10) and their nominal diameter.</summary>
    static readonly (double od, double dn)[] PipeSizes =
    {
        (21.3, 15), (26.9, 20), (33.7, 25), (42.4, 32), (48.3, 40), (60.3, 50), (76.1, 65), (88.9, 80), (114.3, 100),
        (139.7, 125), (168.3, 150), (219.1, 200), (273.0, 250), (323.9, 300), (355.6, 350), (406.4, 400),
    };

    static Material Mat(Document doc, string name)
    {
        var m = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(x => x.Name == name);
        if (m != null) return m;
        m = (Material)doc.GetElement(Material.Create(doc, name)); m.UseRenderAppearanceForShading = false; return m;
    }

    /// <summary>Basic wall type of one layer: thickness, material, function; made from the first basic wall type.</summary>
    static WallType WallTypeFor(Document doc, string name, double thickMm, string material, bool exterior, StringBuilder sb)
    {
        var have = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault(t => t.Name == name);
        if (have != null) return have;
        var basic = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(t => t.Kind == WallKind.Basic);
        var nt = (WallType)basic.Duplicate(name);
        var cs = nt.GetCompoundStructure();
        var mat = Mat(doc, material);
        var layers = new List<CompoundStructureLayer> { new CompoundStructureLayer(thickMm * MM, MaterialFunctionAssignment.Structure, mat.Id) };
        cs.SetLayers(layers);
        cs.StructuralMaterialIndex = 0;
        nt.SetCompoundStructure(cs);
        nt.get_Parameter(BuiltInParameter.FUNCTION_PARAM)?.Set(exterior ? (int)WallFunction.Exterior : (int)WallFunction.Interior);
        sb.AppendLine($"  wall type {name}: {thickMm:F0} mm {material}");
        return nt;
    }

    static FloorType FloorTypeFor(Document doc, string name, double thickMm, string material, StringBuilder sb)
    {
        var have = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().FirstOrDefault(t => t.Name == name);
        if (have != null) return have;
        var basic = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().First(t => !t.IsFoundationSlab && t.GetCompoundStructure() != null);
        var nt = (FloorType)basic.Duplicate(name);
        var cs = nt.GetCompoundStructure();
        cs.SetLayers(new List<CompoundStructureLayer> { new CompoundStructureLayer(thickMm * MM, MaterialFunctionAssignment.Structure, Mat(doc, material).Id) });
        cs.StructuralMaterialIndex = 0;
        nt.SetCompoundStructure(cs);
        sb.AppendLine($"  floor type {name}: {thickMm:F0} mm {material}");
        return nt;
    }

    static RoofType RoofTypeFor(Document doc, string name, double thickMm, string material, StringBuilder sb)
    {
        var have = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().FirstOrDefault(t => t.Name == name);
        if (have != null) return have;
        var basic = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().First(t => t.GetCompoundStructure() != null);
        var nt = (RoofType)basic.Duplicate(name);
        var cs = nt.GetCompoundStructure();
        cs.SetLayers(new List<CompoundStructureLayer> { new CompoundStructureLayer(thickMm * MM, MaterialFunctionAssignment.Structure, Mat(doc, material).Id) });
        cs.StructuralMaterialIndex = 0;
        nt.SetCompoundStructure(cs);
        sb.AppendLine($"  roof type {name}: {thickMm:F1} mm {material}");
        return nt;
    }

    static void Tag(Element e, JsonElement r, string exportAs = null)
    {
        var guid = S(r, "guid");
        e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(guid);
        GuidTools.Set(e, guid);
        if (exportAs != null) e.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_AS)?.Set(exportAs);
        var pt = S(r, "ptype");
        if (pt != null) e.get_Parameter(BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE)?.Set(pt);
    }

    /// <summary>Walls and cladding: profile wall in the plane of the IFC prism (centre of the thickness).</summary>
    static Element BuildWall(Document doc, JsonElement r, Dictionary<string, Level> levels, StringBuilder sb, bool cladding)
    {
        var sh = r.GetProperty("shapes")[0];
        string t = S(sh, "t");
        double thick, pos0, pos1; bool alongX;
        List<XYZ> outer; var holes = new List<List<XYZ>>(); var circles = new List<(XYZ c, double rad)>();
        if (t == "box")
        {
            var x = Arr(sh.GetProperty("x")); var y = Arr(sh.GetProperty("y")); var z = Arr(sh.GetProperty("z"));
            alongX = x[1] - x[0] >= y[1] - y[0];
            (pos0, pos1) = alongX ? (y[0], y[1]) : (x[0], x[1]);
            var a = alongX ? x : y;
            outer = new List<(double, double)> { (a[0], z[0]), (a[1], z[0]), (a[1], z[1]), (a[0], z[1]) }.Select(q => alongX ? new XYZ(q.Item1, (pos0 + pos1) / 2, q.Item2) : new XYZ((pos0 + pos1) / 2, q.Item1, q.Item2)).ToList();
        }
        else
        {
            alongX = t == "prism_xz";
            var pr = Arr(sh.GetProperty(alongX ? "y" : "x")); pos0 = pr[0]; pos1 = pr[1];
            double mid = (pos0 + pos1) / 2;
            XYZ P(double a, double b) => alongX ? new XYZ(a, mid, b) : new XYZ(mid, a, b);
            if (cladding && r.TryGetProperty("profile", out var prof))
            {
                outer = prof.GetProperty("outer").EnumerateArray().Select(q => P(q[0].GetDouble() / 1000, q[1].GetDouble() / 1000)).ToList();
                if (prof.TryGetProperty("holes", out var hs)) foreach (var h in hs.EnumerateArray()) holes.Add(h.EnumerateArray().Select(q => P(q[0].GetDouble() / 1000, q[1].GetDouble() / 1000)).ToList());
                if (prof.TryGetProperty("circles", out var cs)) foreach (var c in cs.EnumerateArray()) circles.Add((P(c[0].GetDouble() / 1000, c[1].GetDouble() / 1000), c[2].GetDouble() / 1000));
            }
            else outer = sh.GetProperty("pts").EnumerateArray().Select(q => P(q[0].GetDouble(), q[1].GetDouble())).ToList();
        }
        thick = (pos1 - pos0) * 1000;
        string mat = S(r, "material");
        bool retaining = S(r, "ptype") == "RETAININGWALL";
        string typeName = S(r, "type") ?? RebuildConfig.Current.TypeName(cladding ? "cladding" : retaining ? "retainingWall" : "wall", mat, thick);
        bool exterior = r.TryGetProperty("exterior", out var ev) ? ev.ValueKind == JsonValueKind.True : cladding || retaining;
        var wt = WallTypeFor(doc, typeName, thick, mat, exterior, sb);
        var lv = levels[S(r, "storey")];
        double tol = doc.Application.ShortCurveTolerance * 1.05;
        List<Curve> Loop(List<XYZ> pts)
        {
            var cl = new List<Curve>(); var clean = new List<XYZ>();
            foreach (var p in pts) { var q = p * 1000 * MM; if (clean.Count == 0 || clean[^1].DistanceTo(q) > tol) clean.Add(q); }
            if (clean.Count > 2 && clean[0].DistanceTo(clean[^1]) <= tol) clean.RemoveAt(clean.Count - 1);
            for (int i = 0; i < clean.Count; i++) cl.Add(Line.CreateBound(clean[i], clean[(i + 1) % clean.Count]));
            return cl;
        }
        var curves = Loop(outer);
        // a profile wall with inner loops fails on some planes ("Failed to create the wall"): the cut-outs are
        // rectangular wall openings instead, as a person would make them
        var wall = Wall.Create(doc, curves, wt.Id, lv.Id, !cladding);
        wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.Set(0);        // location line: wall centreline
        WallUtils.DisallowWallJoinAtEnd(wall, 0); WallUtils.DisallowWallJoinAtEnd(wall, 1);
        // Revit may attach the top to a level and puts the profile bottom on the level: top unconnected, height = profile
        // height, then the base offset moves the wall to the IFC elevation
        double zWant = outer.Min(p => p.Z) * 1000 * MM, zTop = outer.Max(p => p.Z) * 1000 * MM;
        wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(ElementId.InvalidElementId);
        var pH = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
        if (pH != null && !pH.IsReadOnly) pH.Set(zTop - zWant);
        doc.Regenerate();
        var bb0 = wall.get_BoundingBox(null);
        if (bb0 != null)
        {
            double dz = zWant - bb0.Min.Z;
            var pOff = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
            if (Math.Abs(dz) > 0.5 * MM && pOff != null && !pOff.IsReadOnly) { pOff.Set(pOff.AsDouble() + dz); doc.Regenerate(); }
        }
        foreach (var h in holes)
        {
            double a0 = h.Min(q => alongX ? q.X : q.Y), a1 = h.Max(q => alongX ? q.X : q.Y), z0 = h.Min(q => q.Z), z1 = h.Max(q => q.Z);
            double mid = (pos0 + pos1) / 2;
            var p1 = (alongX ? new XYZ(a0, mid, z0) : new XYZ(mid, a0, z0)) * 1000 * MM;
            var p2 = (alongX ? new XYZ(a1, mid, z1) : new XYZ(mid, a1, z1)) * 1000 * MM;
            try { doc.Create.NewOpening(wall, p1, p2); }
            catch (Exception ex) { sb.AppendLine($"  {S(r, "name")}: opening {a0:F2}..{a1:F2} x {z0:F2}..{z1:F2} failed: {ex.Message.Split('\n')[0]}"); }
        }
        if (circles.Count > 0) sb.AppendLine($"  {S(r, "name")}: {circles.Count} round cut-outs not made (rectangular openings only)");
        if (Debug)
        {
            var bb1 = wall.get_BoundingBox(null);
            sb.AppendLine($"  WALL {S(r, "name")}: type {wt.Name} level {lv.Name} pts {outer.Count} holes {holes.Count} circles {circles.Count} z {outer.Min(p => p.Z):F3}..{outer.Max(p => p.Z):F3} m" +
                $" | base offset {wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() / MM:F0} height {wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() / MM:F0}" +
                $" | bbox z {(bb1 == null ? "null" : $"{bb1.Min.Z / MM / 1000:F3}..{bb1.Max.Z / MM / 1000:F3}")}");
        }
        Tag(wall, r, cladding ? "IfcCovering" : null);
        return wall;
    }

    /// <summary>Slab: rectangle in plan, thickness from the IFC section, slope arrow along the section (prism_yz: slope along Y).</summary>
    static Element BuildFloor(Document doc, JsonElement r, Dictionary<string, Level> levels, StringBuilder sb)
    {
        var sh = r.GetProperty("shapes")[0];
        bool alongX = S(sh, "t") == "prism_yz";          // section in (y, z) extruded along x: the slope runs along y
        var pts = sh.GetProperty("pts").EnumerateArray().Select(q => (a: q[0].GetDouble(), z: q[1].GetDouble())).ToList();
        var ext = Arr(sh.GetProperty(alongX ? "x" : "y"));
        double a0 = pts.Min(p => p.a), a1 = pts.Max(p => p.a);
        double zTop0 = pts.Where(p => Math.Abs(p.a - a0) < 1e-6).Max(p => p.z), zTop1 = pts.Where(p => Math.Abs(p.a - a1) < 1e-6).Max(p => p.z);
        double zBot0 = pts.Where(p => Math.Abs(p.a - a0) < 1e-6).Min(p => p.z);
        double thick = (zTop0 - zBot0) * 1000;
        var ft = FloorTypeFor(doc, S(r, "type") ?? RebuildConfig.Current.TypeName("floor", S(r, "material"), thick), thick, S(r, "material"), sb);
        var lv = levels[S(r, "storey")];
        XYZ P(double a, double b, double z) => (alongX ? new XYZ(b, a, z) : new XYZ(a, b, z)) * 1000 * MM;
        // the sketch at the top elevation of the low end; slope arrow from the low end to the high end
        bool lowAt0 = zTop0 <= zTop1;
        double zLow = Math.Min(zTop0, zTop1), zHigh = Math.Max(zTop0, zTop1);
        var loop = new CurveLoop();
        var c = new[] { P(a0, ext[0], zLow), P(a1, ext[0], zLow), P(a1, ext[1], zLow), P(a0, ext[1], zLow) };
        for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(c[i], c[(i + 1) % 4]));
        double bm = (ext[0] + ext[1]) / 2;
        var tail = P(lowAt0 ? a0 : a1, bm, zLow); var head = P(lowAt0 ? a1 : a0, bm, zLow);
        double slope = (zHigh - zLow) / (a1 - a0);
        Floor fl;
        if (slope > 1e-9) fl = Floor.Create(doc, new List<CurveLoop> { loop }, ft.Id, lv.Id, true, Line.CreateBound(tail, head), slope);
        else fl = Floor.Create(doc, new List<CurveLoop> { loop }, ft.Id, lv.Id);
        fl.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(zLow * 1000 * MM - lv.Elevation);
        Tag(fl, r);
        return fl;
    }

    /// <summary>Roof sheet: extrusion roof whose profile is the IFC top line of the sheet, extruded across the building.</summary>
    static Element BuildRoofSheet(Document doc, JsonElement r, Dictionary<string, Level> levels, StringBuilder sb)
    {
        var sh = r.GetProperty("shapes")[0];
        bool alongX = S(sh, "t") == "prism_xz";          // profile in (x, z), extruded along y
        var pts = sh.GetProperty("pts").EnumerateArray().Select(q => (a: q[0].GetDouble(), z: q[1].GetDouble())).ToList();
        var ext = Arr(sh.GetProperty(alongX ? "y" : "x"));
        // top line: for each distinct a the highest z, in order of a
        var top = pts.GroupBy(p => Math.Round(p.a, 4)).Select(g => (a: g.Key, z: g.Max(p => p.z))).OrderBy(p => p.a).ToList();
        var bot = pts.GroupBy(p => Math.Round(p.a, 4)).Select(g => (a: g.Key, z: g.Min(p => p.z))).OrderBy(p => p.a).ToList();
        double thick = top.Zip(bot, (t, b) => t.z - b.z).Average() * 1000;
        var rt = RoofTypeFor(doc, S(r, "type") ?? RebuildConfig.Current.TypeName("roof", S(r, "material"), thick, "F1"), Math.Max(thick, 1), S(r, "material"), sb);
        var lv = levels[S(r, "storey")];
        double mid = ext[0];
        XYZ P(double a, double z) => (alongX ? new XYZ(a, mid, z) : new XYZ(mid, a, z)) * 1000 * MM;
        var prof = new CurveArray();
        for (int i = 0; i + 1 < top.Count; i++) prof.Append(Line.CreateBound(P(top[i].a, top[i].z), P(top[i + 1].a, top[i + 1].z)));
        var plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v => !v.IsTemplate && v.GenLevel?.Id == lv.Id);
        var o = P(top[0].a, top[0].z);
        // the roof is extruded along cutVector x lineDirection (= +Y for a line along +X): a line along -Y gives +X
        var rp = doc.Create.NewReferencePlane(o, o + (alongX ? XYZ.BasisX : -XYZ.BasisY) * 10, XYZ.BasisZ * 10, plan);
        var roof = doc.Create.NewExtrusionRoof(prof, rp, lv, rt, 0, (ext[1] - ext[0]) * 1000 * MM);
        if (Debug) { var rb = roof.get_BoundingBox(null); sb.AppendLine($"  ROOF {S(r, "name")}: bbox ({rb.Min.X / MM / 1000:F2},{rb.Min.Y / MM / 1000:F2},{rb.Min.Z / MM / 1000:F3})-({rb.Max.X / MM / 1000:F2},{rb.Max.Y / MM / 1000:F2},{rb.Max.Z / MM / 1000:F3})"); }
        Tag(roof, r, "IfcCovering");
        return roof;
    }

    static Element BuildPipe(Document doc, JsonElement r, Dictionary<string, Level> levels, StringBuilder sb, PipeType pipeType, ElementId systemType)
    {
        var sh = r.GetProperty("shapes")[0];
        var pts = sh.GetProperty("pts").EnumerateArray().Select(q => new XYZ(q[0].GetDouble(), q[1].GetDouble(), q[2].GetDouble()) * 1000 * MM).ToList();
        var lv = levels[S(r, "storey")];
        var pipe = Pipe.Create(doc, systemType, pipeType.Id, lv.Id, pts[0], pts[1]);
        double od = sh.GetProperty("r").GetDouble() * 2000;   // mm outer diameter
        // nominal diameter: given on the record, or the standard size whose outside diameter is nearest
        double dn = r.TryGetProperty("dn", out var dnv) && dnv.ValueKind == JsonValueKind.Number ? dnv.GetDouble() : PipeSizes.OrderBy(p => Math.Abs(p.od - od)).First().dn;
        pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(dn * MM);
        if (Debug)
        {
            doc.Regenerate();
            var lc = pipe.Location as LocationCurve;
            sb.AppendLine($"  PIPE {S(r, "name")}: OD {od:F1} -> DN {dn} | Revit OD {pipe.get_Parameter(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER)?.AsDouble() / MM:F1} | length {lc.Curve.Length / MM:F0} mm");
        }
        Tag(pipe, r);
        return pipe;
    }

    static Element BuildTray(Document doc, JsonElement r, Dictionary<string, Level> levels, StringBuilder sb, CableTrayType trayType)
    {
        var sh = r.GetProperty("shapes")[0];
        var x = Arr(sh.GetProperty("x")); var y = Arr(sh.GetProperty("y")); var z = Arr(sh.GetProperty("z"));
        bool alongX = x[1] - x[0] >= y[1] - y[0];
        double zc = (z[0] + z[1]) / 2;
        var p0 = (alongX ? new XYZ(x[0], (y[0] + y[1]) / 2, zc) : new XYZ((x[0] + x[1]) / 2, y[0], zc)) * 1000 * MM;
        var p1 = (alongX ? new XYZ(x[1], (y[0] + y[1]) / 2, zc) : new XYZ((x[0] + x[1]) / 2, y[1], zc)) * 1000 * MM;
        var lv = levels[S(r, "storey")];
        var tray = CableTray.Create(doc, trayType.Id, p0, p1, lv.Id);
        tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM)?.Set((alongX ? y[1] - y[0] : x[1] - x[0]) * 1000 * MM);
        tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM)?.Set((z[1] - z[0]) * 1000 * MM);
        Tag(tray, r);
        return tray;
    }

    static Element BuildRoom(Document doc, JsonElement r, Dictionary<string, Level> levels, StringBuilder sb)
    {
        var sh = r.GetProperty("shapes")[0];
        var pts = sh.GetProperty("pts").EnumerateArray().Select(q => new XYZ(q[0].GetDouble(), q[1].GetDouble(), 0) * 1000 * MM).ToList();
        // a space may hang on the building in the IFC (no storey): the level just below the space bottom
        double z0 = sh.GetProperty("z")[0].GetDouble() * 1000 * MM;
        var lv = S(r, "storey") != null && levels.ContainsKey(S(r, "storey")) ? levels[S(r, "storey")]
            : levels.Values.Where(l => l.Elevation <= z0 + 1.5).OrderByDescending(l => l.Elevation).FirstOrDefault() ?? levels.Values.OrderBy(l => l.Elevation).First();
        var plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v => !v.IsTemplate && v.GenLevel?.Id == lv.Id && v.ViewType == ViewType.FloorPlan);
        var sp = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, lv.Elevation)));
        var ca = new CurveArray();
        for (int i = 0; i < pts.Count; i++) ca.Append(Line.CreateBound(new XYZ(pts[i].X, pts[i].Y, lv.Elevation), new XYZ(pts[(i + 1) % pts.Count].X, pts[(i + 1) % pts.Count].Y, lv.Elevation)));
        doc.Create.NewRoomBoundaryLines(sp, ca, plan);
        doc.Regenerate();
        // a point inside: the vertex centroid (good for convex and L-shaped spaces)
        double cx = pts.Average(p => p.X), cy = pts.Average(p => p.Y);
        var room = doc.Create.NewRoom(lv, new UV(cx, cy));
        room.Name = S(r, "long_name") ?? S(r, "name");
        room.Number = S(r, "name");
        try { Tag(room, r); } catch (Exception ex) { sb.AppendLine($"  room {S(r, "name")}: IfcGUID not set ({ex.Message.Split('\n')[0]})"); }
        return room;
    }

    public static string Run(Document doc, string planPath, string what, string mode, string reportPath)
    {
        var sb = new StringBuilder();
        void Flush() { if (!string.IsNullOrEmpty(reportPath)) try { File.WriteAllText(reportPath, sb.ToString()); } catch { } }
        using var js = JsonDocument.Parse(File.ReadAllText(planPath));
        var root = js.RootElement;
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToDictionary(l => l.Name, l => l);
        var have = new FilteredElementCollector(doc).WhereElementIsNotElementType().Select(e => e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString())
            .Where(m => !string.IsNullOrEmpty(m)).ToHashSet();
        var geo = Build.IfcGeometry(planPath);
        if (string.IsNullOrWhiteSpace(what)) what = "all";
        HashSet<string> names = null;
        if (what.StartsWith("names=")) { names = what.Substring(6).Split(',').ToHashSet(); what = "all"; }
        bool Want(JsonElement r)
        {
            if (names != null) return names.Contains(S(r, "name")) && !have.Contains(S(r, "guid"));
            string c = S(r, "cls"), pt = S(r, "ptype");
            string k = c == "IfcWall" ? "walls" : c == "IfcCovering" && pt == "CLADDING" ? "cladding" : c == "IfcCovering" ? "roofs" : c == "IfcSlab" ? "floors"
                : c == "IfcPipeSegment" ? "pipes" : c == "IfcCableCarrierSegment" ? "trays" : c == "IfcSpace" ? "rooms" : c == "IfcStair" ? "stairs" : "other";
            return (what == "all" ? k != "stairs" && k != "other" : what.Split(',').Contains(k)) && !have.Contains(S(r, "guid"));
        }
        var todo = root.GetProperty("system").EnumerateArray().Where(Want).OrderBy(r => r.TryGetProperty("phase", out var ph) ? ph.GetInt32() : 0).ToList();
        Debug = todo.Count <= 12;
        sb.AppendLine($"system elements to build: {todo.Count} ({what})");
        using var tg = new TransactionGroup(doc, "IfcRebuild: system " + what);
        tg.Start();
        PipeType pipeType = null; ElementId sysType = null; CableTrayType trayType = null;
        if (todo.Any(r => S(r, "cls") == "IfcPipeSegment"))
        {
            pipeType = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().OrderBy(t => t.Name == "Default" ? 0 : 1).First();
            var pst = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().ToList();
            var want = RebuildConfig.Current.PipeSystemType;
            sysType = ((string.IsNullOrEmpty(want) ? null : pst.FirstOrDefault(t => t.Name.Contains(want))) ?? pst.First()).Id;
            sb.AppendLine($"  pipe type {pipeType.Name}, system {doc.GetElement(sysType).Name}");
        }
        if (todo.Any(r => S(r, "cls") == "IfcCableCarrierSegment"))
        {
            trayType = new FilteredElementCollector(doc).OfClass(typeof(CableTrayType)).Cast<CableTrayType>().OrderBy(t => t.Name.Contains("Ladder") ? 1 : 0).First();
            sb.AppendLine($"  cable tray type {trayType.Name}");
        }
        var made = new List<(JsonElement r, ElementId id)>();
        int failed = 0, done = 0;
        foreach (var chunk in todo.Chunk(40))
        {
            Progress.Report($"System {done}/{todo.Count}");
            var (st, log) = FailureLog.Run(doc, "IfcRebuild: system", () =>
            {
                foreach (var r in chunk)
                {
                    try
                    {
                        string c = S(r, "cls"), pt = S(r, "ptype");
                        Element e = c == "IfcWall" ? BuildWall(doc, r, levels, sb, false)
                            : c == "IfcCovering" && pt == "CLADDING" ? BuildWall(doc, r, levels, sb, true)
                            : c == "IfcCovering" ? BuildRoofSheet(doc, r, levels, sb)
                            : c == "IfcSlab" ? BuildFloor(doc, r, levels, sb)
                            : c == "IfcPipeSegment" ? BuildPipe(doc, r, levels, sb, pipeType, sysType)
                            : c == "IfcCableCarrierSegment" ? BuildTray(doc, r, levels, sb, trayType)
                            : c == "IfcSpace" ? BuildRoom(doc, r, levels, sb) : null;
                        if (e != null) made.Add((r, e.Id));
                    }
                    catch (Exception ex) { failed++; sb.AppendLine($"  FAILED {S(r, "name")} {S(r, "cls")}: {ex.Message.Split('\n')[0]}"); }
                }
            });
            done += chunk.Length;
            if (st != TransactionStatus.Committed || log.Lines.Count > 0) sb.AppendLine($"  chunk commit={st} {log.Summary().Split('\n')[0]}");
            if (Debug) foreach (var l in log.Lines.Take(12)) sb.AppendLine("    log: " + l);
            Flush();
        }
        sb.AppendLine($"made {made.Count}, failed {failed}");
        int ok = 0;
        foreach (var (r, id) in made)
        {
            var e = doc.GetElement(id); if (e == null || e is Room) continue;
            var bx = Build.SolidBox(e);
            double vol = Build.SolidVolume(e) * Math.Pow(0.3048, 3);
            if (!geo.TryGetValue(S(r, "guid"), out var g)) continue;
            double dmax = bx == null ? 1e9 : Enumerable.Range(0, 6).Max(i => Math.Abs(bx[i] - g.bb[i]));
            double dv = (vol - g.v) / g.v;
            bool pass = dmax <= 10 && Math.Abs(dv) <= 0.01;
            if (pass) ok++;
            else sb.AppendLine($"  MISS {S(r, "name")} {S(r, "cls")}: box {dmax:F0} mm [{(bx == null ? "no solid" : string.Join(",", Enumerable.Range(0, 6).Select(i => (bx[i] - g.bb[i]).ToString("F0", CI))))}] vol {vol:F4}/{g.v:F4} m3 ({dv * 100:F1}%)");
        }
        sb.AppendLine($"check: {ok} / {made.Count(x => doc.GetElement(x.id) is not Room)} match (box 10 mm, volume 1%)" + (geo.Count == 0 ? " (no geometry file)" : ""));
        if (mode == "apply") tg.Assimilate(); else tg.RollBack();
        sb.AppendLine(mode == "apply" ? "APPLIED" : "probe: rolled back");
        Flush();
        return sb.ToString();
    }
}
