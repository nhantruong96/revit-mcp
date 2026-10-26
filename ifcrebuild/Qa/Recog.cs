using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace IfcRebuild;

/// <summary>
/// Read-only measurements of the signs that a Revit model was rebuilt by a program, written as JSON: IfcGUID not
/// derivable from the element, creation blocks per category (sorted by ElementId), columns without top level, beams
/// placed by point, insertion points far from the geometry, triangle-mesh families, family / type counts, names with
/// " (n)", warnings, gate.
/// </summary>
internal static class Recog
{
    const double MM = 1 / 304.8;

    public static string Run(Document doc, string outPath)
    {
        var r = new Dictionary<string, object>();
        var model = new FilteredElementCollector(doc).WhereElementIsNotElementType().WhereElementIsViewIndependent()
            .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model && !(e is FamilySymbol)).ToList();
        var fis = model.OfType<FamilyInstance>().ToList();
        // 1. IfcGUID written from outside: differs from the id Revit would derive
        int guidExt = 0, guidOwn = 0, guidNone = 0, guidSub = 0;
        var seen = new Dictionary<string, int>();
        foreach (var e in model)
        {
            var g = e.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString();
            if (string.IsNullOrEmpty(g)) { guidNone++; continue; }
            seen[g] = seen.GetValueOrDefault(g) + 1;
            string own = null;
            try { own = ExportUtils.GetExportId(doc, e.Id).ToString(); } catch { }
            bool isSub = e is FamilyInstance fi && fi.SuperComponent != null;
            if (isSub) guidSub++;
            if (own != null && g == IfcGuidFromExportId(own)) guidOwn++; else guidExt++;
        }
        r["guid_external"] = guidExt; r["guid_revit_own"] = guidOwn; r["guid_none"] = guidNone; r["guid_duplicates"] = seen.Count(kv => kv.Value > 1); r["sub_components"] = guidSub;
        // 2. creation blocks: elements sorted by ElementId; a block = run of consecutive elements of one category
        var byId = model.Where(e => !(e is FamilyInstance fi && fi.SuperComponent != null)).OrderBy(e => e.Id.Value).ToList();
        var blocks = new Dictionary<string, int>(); var counts = new Dictionary<string, int>();
        string last = null;
        foreach (var e in byId)
        {
            var c = e.Category.Name;
            counts[c] = counts.GetValueOrDefault(c) + 1;
            if (c != last) { blocks[c] = blocks.GetValueOrDefault(c) + 1; last = c; }
        }
        r["blocks_per_category"] = counts.Keys.ToDictionary(k => k, k => new { count = counts[k], blocks = blocks[k] });
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).OrderBy(l => l.Id.Value).Select(l => l.Id.Value).ToList();
        var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Select(g => g.Id.Value).ToList();
        r["levels_before_elements"] = levels.Count > 0 && byId.Count > 0 && levels.Max() < byId.Min(e => e.Id.Value);
        var others = byId.Where(e => !(e is Grid) && !(e is Level)).ToList();
        r["grids_before_elements"] = grids.Count > 0 && others.Count > 0 && grids.Max() < others.Min(e => e.Id.Value);
        // 3. columns without a top level; 4. framing placed by point
        var cols = fis.Where(f => f.Category.Id.Value == (long)BuiltInCategory.OST_StructuralColumns).ToList();
        r["columns"] = cols.Count;
        r["columns_without_top_level"] = cols.Count(f => f.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM) == null);
        r["columns_one_level_based"] = cols.Count(f => f.Symbol.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased);
        var framing = fis.Where(f => f.Category.Id.Value == (long)BuiltInCategory.OST_StructuralFraming && f.SuperComponent == null).ToList();
        r["framing"] = framing.Count;
        r["framing_by_point"] = framing.Count(f => f.Location is LocationPoint);
        r["framing_families"] = framing.Select(f => f.Symbol.Family.Name).Distinct().Count();
        // 5. insertion point far from the geometry (point placed instances)
        int far = 0, pointPlaced = 0;
        foreach (var f in fis.Where(f => f.Location is LocationPoint && f.SuperComponent == null))
        {
            pointPlaced++;
            var bb = f.get_BoundingBox(null); if (bb == null) continue;
            var p = ((LocationPoint)f.Location).Point;
            var c = (bb.Min + bb.Max) / 2;
            double half = (bb.Max - bb.Min).GetLength() / 2;
            if (p.DistanceTo(c) > half + 2000 * MM) far++;
        }
        r["point_placed"] = pointPlaced; r["insertion_far_from_geometry"] = far;
        // 6. triangle mesh families: at least 50 faces, more than half planar triangles (instance geometry)
        int meshFams = 0; var famSeen = new HashSet<long>();
        foreach (var f in fis)
        {
            var famId = f.Symbol.Family.Id.Value; if (!famSeen.Add(famId)) continue;
            int faces = 0, tri = 0;
            foreach (var o in f.get_Geometry(new Options()) ?? Enumerable.Empty<GeometryObject>())
                if (o is GeometryInstance gi) foreach (var o2 in gi.GetInstanceGeometry()) if (o2 is Solid s)
                    foreach (Face fc in s.Faces) { faces++; if (fc is PlanarFace pf && pf.EdgeLoops.Size == 1 && pf.EdgeLoops.get_Item(0).Size == 3) tri++; }
            if (faces >= 50 && tri > faces / 2) meshFams++;
        }
        r["families_total"] = new FilteredElementCollector(doc).OfClass(typeof(Family)).GetElementCount();
        r["families_used"] = famSeen.Count;
        r["families_triangle_mesh"] = meshFams;
        r["families_single_instance"] = fis.Where(f => f.SuperComponent == null).GroupBy(f => f.Symbol.Family.Id).Count(g => g.Count() == 1);
        r["family_names_with_(n)"] = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Count(f => System.Text.RegularExpressions.Regex.IsMatch(f.Name, @" \(\d+\)$"));
        r["warnings"] = doc.GetWarnings().Count;
        r["warning_kinds"] = doc.GetWarnings().GroupBy(w => w.GetDescriptionText()).OrderByDescending(g => g.Count()).Take(8).ToDictionary(g => g.Key, g => g.Count());
        r["gate"] = Gate.Run(doc);
        r["rooms"] = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().GetElementCount();
        r["grids"] = grids.Count; r["levels"] = levels.Count;
        r["dimensions"] = new FilteredElementCollector(doc).OfClass(typeof(Dimension)).GetElementCount();
        r["tags"] = new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).GetElementCount();
        r["text_notes"] = new FilteredElementCollector(doc).OfClass(typeof(TextNote)).GetElementCount();
        r["materials_in_use"] = model.Select(e => e.GetMaterialIds(false).Count).Count(n => n > 0);
        r["elements_without_material"] = model.Count(e => e.GetMaterialIds(false).Count == 0 && !(e is Autodesk.Revit.DB.Architecture.Room));
        r["model_elements"] = model.Count;
        r["file_size_mb"] = File.Exists(doc.PathName) ? Math.Round(new FileInfo(doc.PathName).Length / 1048576.0, 1) : 0;
        r["measured_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var json = JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(outPath, json);
        return json;
    }

    /// <summary>Revit's own IfcGUID is the IFC-compressed form of the export GUID.</summary>
    static string IfcGuidFromExportId(string guidStr)
    {
        if (!Guid.TryParse(guidStr, out var g)) return null;
        var b = g.ToByteArray();
        // .NET byte order -> IFC order (big endian for the first three fields)
        var n = new byte[16];
        n[0] = b[3]; n[1] = b[2]; n[2] = b[1]; n[3] = b[0]; n[4] = b[5]; n[5] = b[4]; n[6] = b[7]; n[7] = b[6];
        Array.Copy(b, 8, n, 8, 8);
        const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";
        var sb = new StringBuilder();
        // first 2 chars from 1 byte, then 5 groups of 3 bytes -> 4 chars
        sb.Append(chars[n[0] >> 6 & 3]); sb.Append(chars[n[0] & 63]);
        for (int i = 1; i < 16; i += 3)
        {
            int v = (n[i] << 16) | (n[i + 1] << 8) | n[i + 2];
            sb.Append(chars[(v >> 18) & 63]); sb.Append(chars[(v >> 12) & 63]); sb.Append(chars[(v >> 6) & 63]); sb.Append(chars[v & 63]);
        }
        return sb.ToString();
    }
}
