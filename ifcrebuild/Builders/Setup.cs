using System.Globalization;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace IfcRebuild;

/// <summary>
/// The frame of the model from plan.json, every part optional:
/// {"levels": [{"name", "elev": m, "guid"}],
///  "grids": {"x": [{"name", "v": mm}], "y": [{"name", "v": mm}], "extent": [x0, y0, x1, y1] mm},
///  "materials": {"name": {"rgb": [r, g, b] 0..1, "transparency": 0..1, "class": "Concrete" | "Metal" | ...}},
///  "project": {"project_guid", "site_guid", "site_name", "building_guid", "building_name",
///              "map_conversion": {"E", "N", "H" (m), "xa", "xo" (x axis abscissa, ordinate)}}}.
/// Levels: the template levels are renamed and moved in elevation order, extra ones created, the rest deleted; one floor
/// plan and one structural plan (config structuralPlanName) per level. Grids are replaced; without "extent" they run
/// config gridMarginMm past the outermost grids. Shared coordinates from IfcMapConversion. mode "probe" | "apply".
/// </summary>
internal static class Setup
{
    const double MM = 1 / 304.8;
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static string Run(Document doc, string planPath, string mode)
    {
        var sb = new StringBuilder();
        using var js = JsonDocument.Parse(File.ReadAllText(planPath));
        var root = js.RootElement;
        var opt = RebuildConfig.Current;
        using var tg = new TransactionGroup(doc, "IfcRebuild: setup");
        tg.Start();
        if (root.TryGetProperty("levels", out var levelsEl))
        {
            var (st, log) = FailureLog.Run(doc, "IfcRebuild: levels", () =>
            {
                var want = levelsEl.EnumerateArray().Select(l => (name: l.GetProperty("name").GetString(), z: l.GetProperty("elev").GetDouble() * 1000 * MM, guid: S(l, "guid")))
                    .OrderBy(l => l.z).ToList();
                var have = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().ToList();
                var planType = vft.First(t => t.ViewFamily == ViewFamily.FloorPlan);
                var structType = vft.FirstOrDefault(t => t.ViewFamily == ViewFamily.StructuralPlan);
                var used = new List<Level>();
                for (int i = 0; i < want.Count; i++)
                {
                    Level lv;
                    if (i < have.Count) { lv = have[i]; lv.Elevation = want[i].z; }
                    else lv = Level.Create(doc, want[i].z);
                    lv.Name = want[i].name;
                    if (!string.IsNullOrEmpty(want[i].guid)) lv.get_Parameter(BuiltInParameter.IFC_GUID)?.Set(want[i].guid);
                    used.Add(lv);
                    sb.AppendLine($"level {lv.Name} = {want[i].z / MM:F0} mm ({(i < have.Count ? "renamed" : "created")})");
                }
                doc.Regenerate();
                // one floor plan and one structural plan per level
                var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v => !v.IsTemplate && v.GenLevel != null).ToList();
                foreach (var lv in used)
                {
                    var fp = plans.FirstOrDefault(v => v.GenLevel.Id == lv.Id && v.ViewType == ViewType.FloorPlan);
                    if (fp == null) { fp = ViewPlan.Create(doc, planType.Id, lv.Id); sb.AppendLine($"  floor plan {lv.Name} created"); }
                    try { fp.Name = lv.Name; } catch { }
                    if (structType != null && !plans.Any(v => v.GenLevel.Id == lv.Id && v.ViewType == ViewType.EngineeringPlan))
                    {
                        var sp = ViewPlan.Create(doc, structType.Id, lv.Id);
                        try { sp.Name = opt.StructuralPlanName.Replace("{level}", lv.Name); } catch { }
                    }
                }
                // template levels not in the IFC: deleted with their views
                foreach (var lv in have.Skip(want.Count).ToList()) { sb.AppendLine($"level {lv.Name} deleted"); doc.Delete(lv.Id); }
            });
            sb.AppendLine($"levels commit={st} {log.Summary().Split('\n')[0]}");
        }
        if (root.TryGetProperty("project", out var pr))
        {
            var (st, log) = FailureLog.Run(doc, "IfcRebuild: project", () =>
            {
                var pi = doc.ProjectInformation;
                void Put(BuiltInParameter bip, string key) { var v = S(pr, key); if (v != null) pi.get_Parameter(bip)?.Set(v); }
                Put(BuiltInParameter.IFC_PROJECT_GUID, "project_guid");
                Put(BuiltInParameter.IFC_SITE_GUID, "site_guid");
                Put(BuiltInParameter.IFC_BUILDING_GUID, "building_guid");
                if (S(pr, "building_name") is string bn) { pi.Name = bn; pi.BuildingName = bn; }
                if (S(pr, "site_name") is string sn) pi.Address = sn;
            });
            sb.AppendLine($"project commit={st} {log.Summary().Split('\n')[0]}");
        }
        if (root.TryGetProperty("grids", out var gr))
        {
            var (st, log) = FailureLog.Run(doc, "IfcRebuild: grids", () =>
            {
                foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).ToElementIds().ToList()) doc.Delete(g);
                var gx = gr.TryGetProperty("x", out var xa) ? xa.EnumerateArray().ToList() : new List<JsonElement>();
                var gy = gr.TryGetProperty("y", out var ya) ? ya.EnumerateArray().ToList() : new List<JsonElement>();
                double x0, y0, x1, y1;
                if (gr.TryGetProperty("extent", out var ex)) { x0 = ex[0].GetDouble(); y0 = ex[1].GetDouble(); x1 = ex[2].GetDouble(); y1 = ex[3].GetDouble(); }
                else
                {
                    var xs = gx.Select(g => g.GetProperty("v").GetDouble()).DefaultIfEmpty(0).ToList(); var ys = gy.Select(g => g.GetProperty("v").GetDouble()).DefaultIfEmpty(0).ToList();
                    double m = opt.GridMarginMm;
                    x0 = xs.Min() - m; x1 = xs.Max() + m; y0 = ys.Min() - m; y1 = ys.Max() + m;
                }
                int n = 0;
                foreach (var g in gx)
                {
                    double x = g.GetProperty("v").GetDouble() * MM;
                    Grid.Create(doc, Line.CreateBound(new XYZ(x, y0 * MM, 0), new XYZ(x, y1 * MM, 0))).Name = g.GetProperty("name").GetString(); n++;
                }
                foreach (var g in gy)
                {
                    double y = g.GetProperty("v").GetDouble() * MM;
                    Grid.Create(doc, Line.CreateBound(new XYZ(x0 * MM, y, 0), new XYZ(x1 * MM, y, 0))).Name = g.GetProperty("name").GetString(); n++;
                }
                sb.AppendLine($"grids {n}");
            });
            sb.AppendLine($"grids commit={st} {log.Summary().Split('\n')[0]}");
        }
        if (root.TryGetProperty("materials", out var ms))
        {
            var (st, log) = FailureLog.Run(doc, "IfcRebuild: materials", () =>
            {
                var have = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.First());
                int n = 0;
                foreach (var m in ms.EnumerateObject())
                {
                    if (have.ContainsKey(m.Name)) continue;
                    var mat = (Material)doc.GetElement(Material.Create(doc, m.Name));
                    if (m.Value.TryGetProperty("rgb", out var rgbEl))
                    {
                        var rgb = rgbEl.EnumerateArray().Select(c => (byte)Math.Round(c.GetDouble() * 255)).ToArray();
                        mat.Color = new Color(rgb[0], rgb[1], rgb[2]);
                    }
                    if (m.Value.TryGetProperty("transparency", out var tr)) mat.Transparency = (int)Math.Round(tr.GetDouble() * 100);
                    mat.UseRenderAppearanceForShading = false;
                    mat.MaterialClass = S(m.Value, "class") ?? "Generic";
                    n++;
                }
                sb.AppendLine($"materials created {n}");
            });
            sb.AppendLine($"materials commit={st} {log.Summary().Split('\n')[0]}");
        }
        if (root.TryGetProperty("project", out var pr2) && pr2.TryGetProperty("map_conversion", out var mc))
        {
            var (st, log) = FailureLog.Run(doc, "IfcRebuild: shared coordinates", () =>
            {
                double e = mc.GetProperty("E").GetDouble() * 1000 * MM, nn = mc.GetProperty("N").GetDouble() * 1000 * MM, h = mc.GetProperty("H").GetDouble() * 1000 * MM;
                double ang = Math.Atan2(mc.GetProperty("xo").GetDouble(), mc.GetProperty("xa").GetDouble());
                var loc = doc.ActiveProjectLocation;
                loc.SetProjectPosition(XYZ.Zero, new ProjectPosition(e, nn, h, ang));
                var pp = loc.GetProjectPosition(XYZ.Zero);
                sb.AppendLine($"survey: E {pp.EastWest / MM / 1000:F3} N {pp.NorthSouth / MM / 1000:F3} H {pp.Elevation / MM / 1000:F3} m, angle {pp.Angle * 180 / Math.PI:F4} deg");
            });
            sb.AppendLine($"coordinates commit={st} {log.Summary().Split('\n')[0]}");
        }
        if (mode == "apply") tg.Assimilate(); else tg.RollBack();
        sb.AppendLine(mode == "apply" ? "APPLIED" : "probe: rolled back");
        return sb.ToString();
    }
}
