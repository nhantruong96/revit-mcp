using System.Text;
using Autodesk.Revit.DB;

namespace IfcRebuild;

/// <summary>Read-only look at family files: category, placement, parameters (type/instance, formula, value of the
/// first type), forms, nested families, reference planes, solid box. paths separated by ';'; report to a file (long).</summary>
internal static class Inspect
{
    public static string Families(Document doc, string paths, string outPath)
    {
        var sb = new StringBuilder();
        foreach (var p in paths.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            sb.AppendLine("##### " + p);
            Document fd = null;
            try
            {
                fd = doc.Application.OpenDocumentFile(p);
                var fm = fd.FamilyManager;
                var own = fd.OwnerFamily;
                sb.AppendLine($"category={own?.FamilyCategory?.Name} placement={own?.FamilyPlacementType} types={fm.Types.Size} " +
                    $"workplane={own?.get_Parameter(BuiltInParameter.FAMILY_WORK_PLANE_BASED)?.AsInteger()} shared={own?.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger()}");
                var ft = fm.Types.Cast<FamilyType>().FirstOrDefault();
                foreach (var fp in fm.GetParameters().OrderBy(x => x.Definition.Name))
                {
                    string val = "";
                    try
                    {
                        if (ft != null && ft.HasValue(fp))
                            val = fp.StorageType switch
                            {
                                StorageType.Double => (ft.AsDouble(fp) ?? 0).ToString("G6") + (fp.Definition.GetDataType() == SpecTypeId.Length ? $" ft ({(ft.AsDouble(fp) ?? 0) * 304.8:F1} mm)" : ""),
                                StorageType.Integer => (ft.AsInteger(fp) ?? 0).ToString(),
                                StorageType.String => ft.AsString(fp),
                                StorageType.ElementId => ft.AsElementId(fp)?.ToString(),
                                _ => ""
                            };
                    }
                    catch { }
                    sb.AppendLine($"  {(fp.IsInstance ? "I" : "T")} {fp.Definition.Name} [{fp.StorageType}/{fp.Definition.GetDataType().TypeId.Split(':').Last()}] = {val} {(fp.IsDeterminedByFormula ? "f=" + fp.Formula : "")} {(fp.IsReporting ? "reporting" : "")}");
                }
                var forms = new FilteredElementCollector(fd).OfClass(typeof(GenericForm)).Cast<GenericForm>().ToList();
                sb.AppendLine("  forms: " + string.Join(", ", forms.GroupBy(f => f.GetType().Name + (f.IsSolid ? "" : "(void)")).Select(g => g.Key + "x" + g.Count())));
                var nested = new FilteredElementCollector(fd).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Select(fi => fi.Symbol.Family.Name).Distinct().ToList();
                if (nested.Count > 0) sb.AppendLine("  nested: " + string.Join(", ", nested));
                var rps = new FilteredElementCollector(fd).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().Select(r => r.Name).Where(n => !string.IsNullOrEmpty(n) && !n.StartsWith("Reference Plane")).ToList();
                sb.AppendLine("  refplanes: " + string.Join(", ", rps));
                // overall box of the solid forms as loaded
                XYZ lo = new(1e9, 1e9, 1e9), hi = new(-1e9, -1e9, -1e9);
                foreach (var f in forms.Where(f => f.IsSolid))
                {
                    var bb = f.get_BoundingBox(null); if (bb == null) continue;
                    lo = new XYZ(Math.Min(lo.X, bb.Min.X), Math.Min(lo.Y, bb.Min.Y), Math.Min(lo.Z, bb.Min.Z));
                    hi = new XYZ(Math.Max(hi.X, bb.Max.X), Math.Max(hi.Y, bb.Max.Y), Math.Max(hi.Z, bb.Max.Z));
                }
                if (lo.X < 1e8) sb.AppendLine($"  solid box mm: ({lo.X * 304.8:F0},{lo.Y * 304.8:F0},{lo.Z * 304.8:F0}) - ({hi.X * 304.8:F0},{hi.Y * 304.8:F0},{hi.Z * 304.8:F0})");
            }
            catch (Exception ex) { sb.AppendLine("  ERROR " + ex.Message); }
            finally { try { fd?.Close(false); } catch { } }
        }
        File.WriteAllText(outPath, sb.ToString());
        return $"ok -> {outPath} ({sb.Length} chars)";
    }
}
