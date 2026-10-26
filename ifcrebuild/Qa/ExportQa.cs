using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;

namespace IfcRebuild;

/// <summary>
/// One line per element instance that carries an IfcGUID: guid|class|category|volume m3|box mm (xmin,ymin,zmin,
/// xmax,ymax,zmax in internal coordinates)|mark, for comparing the rebuilt model with the IFC outside Revit. Read-only.
/// Elements whose IfcGUID Revit generates itself (curtain panels, nested parts) are written too; a nested shared part is
/// listed as "Part" with the IfcGUID of its parent in the last column.
/// </summary>
internal static class ExportQa
{
    public static string Run(Document doc, string outPath)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        var els = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements();
        int n = 0, subs = 0;
        foreach (var e in els)
        {
            var guid = e.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString();
            if (string.IsNullOrEmpty(guid) || e.Category == null) continue;
            if (e is FamilyInstance fi && fi.SuperComponent != null) { subs++; sb.AppendLine(string.Join("|", guid, "Part", e.Category.Name, "0", "", fi.SuperComponent.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString())); continue; }
            var bx = Build.SolidBox(e);
            double vol = Build.SolidVolume(e) * Math.Pow(0.3048, 3);
            sb.AppendLine(string.Join("|", guid, e.GetType().Name, e.Category.Name, vol.ToString("F6", ci),
                bx == null ? "" : string.Join(",", bx.Select(x => x.ToString("F1", ci))), e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? ""));
            if (++n % 2000 == 0) Progress.Report($"ExportQa {n} elements");
        }
        File.WriteAllText(outPath, sb.ToString());
        return $"ok {n} elements (+{subs} nested parts) -> {outPath}";
    }
}
