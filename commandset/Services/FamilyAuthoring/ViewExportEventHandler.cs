using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.FamilyAuthoring;

/// <summary>
/// Exports a view to an image or to DWG, from the active document or from a background one.
///
/// The point is checking, not publishing. An image is what you look at to see that the thing
/// you built looks like the thing you meant to build; a DWG is what you measure against a
/// reference drawing, because line work can be read back out of it and compared, which a
/// picture does not allow.
///
/// Revit decorates exported file names with the view name, so the files actually written are
/// found by comparing the target folder before and after rather than by assuming the name.
/// </summary>
public class ViewExportEventHandler : FamilyEventHandlerBase
{
    public override string GetName() => "View Export";

    protected override object Run(UIApplication app)
    {
        Document document = FamilyDocumentRegistry.Resolve(Text("documentHandle"), app);

        string format = (Text("format", "image") ?? "image").Trim().ToLowerInvariant();
        string outputFolder = RequiredText("outputFolder");
        string namePrefix = Text("namePrefix", SanitiseFileName(document.Title)) ?? "export";

        if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

        View view = ResolveView(document, Text("viewName"));
        if (view == null)
            throw new InvalidOperationException(
                "No view to export. Give a 'viewName', or open a document that has a usable view.");

        HashSet<string> before = Snapshot(outputFolder);

        switch (format)
        {
            case "image":
            case "png":
                ExportImage(document, view, outputFolder, namePrefix);
                break;

            case "dwg":
                ExportDwg(document, view, outputFolder, namePrefix);
                break;

            default:
                throw new ArgumentException($"Unknown format '{format}'. Use 'image' or 'dwg'.");
        }

        List<string> written = Snapshot(outputFolder).Except(before).OrderBy(p => p).ToList();

        return new
        {
            success = written.Count > 0,
            format,
            viewName = view.Name,
            viewType = view.ViewType.ToString(),
            outputFolder,
            fileCount = written.Count,
            files = written,
            note = written.Count == 0
                ? "Revit reported no error but wrote no file. Check that the view is not empty and that the folder is writable."
                : null,
        };
    }

    private void ExportImage(Document document, View view, string folder, string prefix)
    {
        var options = new ImageExportOptions
        {
            FilePath = Path.Combine(folder, prefix),
            ExportRange = ExportRange.SetOfViews,
            ZoomType = ZoomFitType.FitToPage,
            FitDirection = FitDirectionType.Horizontal,
            HLRandWFViewsFileType = ImageFileType.PNG,
            ShadowViewsFileType = ImageFileType.PNG,
            ImageResolution = ImageResolution.DPI_300,
            PixelSize = Math.Max(256, Parameters?["pixelSize"]?.ToObject<int>() ?? 2000),
        };

        options.SetViewsAndSheets(new List<ElementId> { view.Id });
        document.ExportImage(options);
    }

    private void ExportDwg(Document document, View view, string folder, string prefix)
    {
        var options = new DWGExportOptions
        {
            MergedViews = true,
            // Keeping the sketch geometry as geometry rather than as a proxy is what makes
            // the result measurable afterwards.
            ExportOfSolids = SolidGeometry.ACIS,
        };

        bool exported = document.Export(folder, prefix, new List<ElementId> { view.Id }, options);
        if (!exported)
            throw new InvalidOperationException(
                "Revit's DWG export returned false. The view may be empty, or the name may collide with a locked file.");
    }

    /// <summary>
    /// Picks the view to export. A background document has no active view, so a named view is
    /// looked up and otherwise the first non-template drawable view is taken.
    /// </summary>
    private static View ResolveView(Document document, string viewName)
    {
        var views = new FilteredElementCollector(document)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => !v.IsTemplate && v.CanBePrinted)
            .ToList();

        if (!string.IsNullOrWhiteSpace(viewName))
        {
            return views.FirstOrDefault(v => string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException(
                       $"No view named '{viewName}'. Available: {string.Join(", ", views.Select(v => v.Name).Take(20))}");
        }

        if (document.ActiveView != null && !document.ActiveView.IsTemplate) return document.ActiveView;

        // Prefer something that shows the model rather than a legend or a schedule.
        return views.FirstOrDefault(v => v.ViewType == ViewType.ThreeD)
               ?? views.FirstOrDefault(v => v.ViewType == ViewType.Elevation)
               ?? views.FirstOrDefault(v => v.ViewType == ViewType.FloorPlan)
               ?? views.FirstOrDefault();
    }

    private static HashSet<string> Snapshot(string folder)
    {
        return new HashSet<string>(
            Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string SanitiseFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "export";
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name;
    }
}
