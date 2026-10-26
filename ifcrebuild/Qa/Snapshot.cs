using System.Globalization;
using Autodesk.Revit.DB;

namespace IfcRebuild;

/// <summary>
/// Picture for the self-check after a step: a temporary isometric 3D view, section box around the elements whose
/// IfcGUID is given (+ margin), shaded, fine detail, exported as PNG; the view is rolled back with its group.
/// </summary>
internal static class Snapshot
{
    const double MM = 1 / 304.8;

    /// <param name="guids">IfcGUIDs (= IFC GlobalIds) separated by ','</param>
    /// <param name="view">"x,y,z" of the viewing direction (from the eye towards the model), e.g. "-1,1,-0.7"; empty = that one</param>
    /// <param name="marginMm">margin in mm around the elements (empty = 500), or an explicit box "x0,y0,z0,x1,y1,z1" in mm</param>
    public static string Take(Document doc, string guids, string outPng, string view, string marginMm)
    {
        var want = guids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToHashSet();
        var els = new FilteredElementCollector(doc).WhereElementIsNotElementType()
            .Where(e => want.Contains(e.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString() ?? "")).ToList();
        if (els.Count == 0) return "no element with those IfcGUIDs";
        var mb = (string.IsNullOrWhiteSpace(marginMm) ? "500" : marginMm).Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        double m = mb.Length == 1 ? mb[0] * MM : 0;
        XYZ lo = new(1e18, 1e18, 1e18), hi = new(-1e18, -1e18, -1e18);
        if (mb.Length == 6) { lo = new XYZ(mb[0], mb[1], mb[2]) * MM; hi = new XYZ(mb[3], mb[4], mb[5]) * MM; }
        else
            foreach (var bb in els.Select(e => e.get_BoundingBox(null)).Where(b => b != null))
            {
                lo = new XYZ(Math.Min(lo.X, bb.Min.X), Math.Min(lo.Y, bb.Min.Y), Math.Min(lo.Z, bb.Min.Z));
                hi = new XYZ(Math.Max(hi.X, bb.Max.X), Math.Max(hi.Y, bb.Max.Y), Math.Max(hi.Z, bb.Max.Z));
            }
        var d = (string.IsNullOrWhiteSpace(view) ? "-1,1,-0.7" : view).Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        var fwd = new XYZ(d[0], d[1], d[2]).Normalize();
        var up = fwd.CrossProduct(XYZ.BasisZ).CrossProduct(fwd).Normalize();
        var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
        Directory.CreateDirectory(Path.GetDirectoryName(outPng)!);
        using var tg = new TransactionGroup(doc, "IfcRebuild: snapshot");
        tg.Start();
        View3D v;
        using (var t = new Transaction(doc, "snapshot view"))
        {
            t.Start();
            v = View3D.CreateIsometric(doc, vft.Id);
            v.SetSectionBox(new BoundingBoxXYZ { Min = lo - new XYZ(m, m, m), Max = hi + new XYZ(m, m, m) });
            var c = (lo + hi) / 2;
            v.SetOrientation(new ViewOrientation3D(c - fwd * 100, up, fwd));
            v.DetailLevel = ViewDetailLevel.Fine;
            v.DisplayStyle = DisplayStyle.ShadingWithEdges;
            v.AreAnnotationCategoriesHidden = true;          // levels, grids, tags would set the zoom extents
            foreach (var bic in new[] { BuiltInCategory.OST_Levels, BuiltInCategory.OST_Grids, BuiltInCategory.OST_SectionBox })
                if (Category.GetCategory(doc, bic) is Category cat && v.CanCategoryBeHidden(cat.Id)) v.SetCategoryHidden(cat.Id, true);
            t.Commit();
        }
        var opt = new ImageExportOptions
        {
            ExportRange = ExportRange.SetOfViews,
            FilePath = Path.Combine(Path.GetDirectoryName(outPng)!, Path.GetFileNameWithoutExtension(outPng)),
            HLRandWFViewsFileType = ImageFileType.PNG,
            ShadowViewsFileType = ImageFileType.PNG,
            ImageResolution = ImageResolution.DPI_150,
            ZoomType = ZoomFitType.FitToPage,
            PixelSize = 1600,
        };
        opt.SetViewsAndSheets(new List<ElementId> { v.Id });
        doc.ExportImage(opt);
        tg.RollBack();
        var made = Directory.GetFiles(Path.GetDirectoryName(outPng)!, Path.GetFileNameWithoutExtension(outPng) + "*.png")
            .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        if (made != null && made != outPng) { if (File.Exists(outPng)) File.Delete(outPng); File.Move(made, outPng); }
        return $"elements {els.Count} -> {outPng} ({(File.Exists(outPng) ? new FileInfo(outPng).Length : 0)} bytes)";
    }
}
