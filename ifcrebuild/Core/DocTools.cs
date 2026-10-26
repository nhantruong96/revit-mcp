using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace IfcRebuild;

/// <summary>One working document per IFC: created once, saved after every step. Revit keeps the backups itself
/// (name.0001.rvt, .0002.rvt ... next to the file, up to MaximumBackups); to go back a step, open a backup.</summary>
public static class DocTools
{
    public static string NewDocument(Document doc, string path, string maxBackups, string template)
    {
        if (File.Exists(path)) return "EXISTS " + path + ": use Open, the working document is never recreated";
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var app = doc.Application;
        var nd = app.NewProjectDocument(string.IsNullOrEmpty(template) ? app.DefaultProjectTemplate : template);
        nd.SaveAs(path, new SaveAsOptions { MaximumBackups = Batch.Int(maxBackups, 20) });
        nd.Close(false);
        return "created " + new UIApplication(app).OpenAndActivateDocument(path).Document.PathName;
    }

    public static string NewEmpty(UIApplication app, string path, string template)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var nd = app.Application.NewProjectDocument(string.IsNullOrEmpty(template) ? app.Application.DefaultProjectTemplate : template);
        nd.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
        nd.Close(false);
        return "active " + app.OpenAndActivateDocument(path).Document.PathName;
    }

    public static string Open(Document doc, string path) =>
        doc.PathName.Equals(path, StringComparison.OrdinalIgnoreCase) ? "already active " + path
            : "active " + new UIApplication(doc.Application).OpenAndActivateDocument(path).Document.PathName;

    /// <summary>Normal save. When Revit was started with the file on its command line this fails inside an API call
    /// ("Unable to close all open transaction phases"): then save with PostCommand(Save) or on the next Idling event.</summary>
    public static string Save(Document doc)
    {
        if (string.IsNullOrEmpty(doc.PathName)) return "ERROR document has never been saved";
        doc.Save(new SaveOptions { Compact = false });
        var dir = Path.GetDirectoryName(doc.PathName);
        var stem = Path.GetFileNameWithoutExtension(doc.PathName);
        var backups = Directory.GetFiles(dir, stem + ".0*.rvt").OrderBy(f => f).ToList();
        return $"saved {doc.PathName} | backups ({backups.Count}): {string.Join(", ", backups.Select(Path.GetFileName))}";
    }
}
