using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.FamilyAuthoring;

/// <summary>
/// Opens, creates, saves and closes family documents in the background.
///
/// Both ways in are offered because they behave differently.
/// <c>OpenDocumentFile</c> loads an existing .rfa as a background document and leaves the
/// active document alone. <c>NewFamilyDocument</c> builds one from a template; it is the
/// obvious route but is known to be fussy about the context it runs in, so the result
/// reports which route produced the document and whether the active document survived it.
/// </summary>
public class FamilyDocumentEventHandler : FamilyEventHandlerBase
{
    public override string GetName() => "Family Document";

    protected override object Run(UIApplication app)
    {
        string action = (Text("action", "open") ?? "open").Trim().ToLowerInvariant();

        // Recorded before and after so the caller can prove the user's document was not
        // disturbed, rather than taking it on trust.
        string activeBefore = app.ActiveUIDocument?.Document?.Title;

        object payload = action switch
        {
            "open" => Open(app),
            "create" => Create(app),
            "save" => Save(app),
            "saveas" => SaveAs(app),
            "close" => Close(app),
            "list" => new { success = true, documents = FamilyDocumentRegistry.List() },
            _ => throw new ArgumentException(
                $"Unknown action '{action}'. Use open, create, save, saveAs, close or list."),
        };

        string activeAfter = app.ActiveUIDocument?.Document?.Title;

        return new
        {
            result = payload,
            activeDocumentBefore = activeBefore,
            activeDocumentAfter = activeAfter,
            activeDocumentUnchanged = string.Equals(activeBefore, activeAfter, StringComparison.Ordinal),
        };
    }

    private object Open(UIApplication app)
    {
        string filePath = RequiredText("filePath");
        if (!File.Exists(filePath))
            throw new System.IO.FileNotFoundException($"No file at '{filePath}'.");

        Document document = app.Application.OpenDocumentFile(filePath);
        if (document == null)
            throw new InvalidOperationException($"Revit returned no document for '{filePath}'.");

        return Describe(FamilyDocumentRegistry.Register(document), document, "OpenDocumentFile");
    }

    private object Create(UIApplication app)
    {
        string templatePath = RequiredText("templatePath");
        if (!File.Exists(templatePath))
            throw new System.IO.FileNotFoundException($"No family template at '{templatePath}'.");

        // Validate the destination BEFORE creating anything. A new family document that
        // cannot be saved is left with nowhere to go: it is not in the registry, not on
        // disk, and not closeable by the caller - and Revit has been seen to die on it.
        string savePath = Text("savePath");
        if (!string.IsNullOrWhiteSpace(savePath))
        {
            string directory = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
        }

        Document document = app.Application.NewFamilyDocument(templatePath);
        if (document == null)
            throw new InvalidOperationException(
                $"NewFamilyDocument returned null for '{templatePath}'. Fall back to creating the .rfa by hand and opening it with action \"open\".");

        // A brand new family document has no path, so save it straight away when asked;
        // several later operations are easier to reason about once the file exists on disk.
        if (!string.IsNullOrWhiteSpace(savePath))
        {
            try
            {
                document.SaveAs(savePath, new SaveAsOptions { OverwriteExistingFile = Flag("overwrite", true) });
            }
            catch (Exception ex)
            {
                // Close the orphan before reporting, so a failed save costs the caller a
                // message rather than a document nobody holds a handle to.
                try { document.Close(false); } catch { }

                throw new InvalidOperationException(
                    $"The family was created but could not be saved to '{savePath}': {ex.Message}. "
                    + "The unsaved document has been closed.", ex);
            }
        }

        return Describe(FamilyDocumentRegistry.Register(document), document, "NewFamilyDocument");
    }

    private object Save(UIApplication app)
    {
        Document document = FamilyDocumentRegistry.Resolve(Text("documentHandle"), app);

        if (string.IsNullOrWhiteSpace(document.PathName))
            throw new InvalidOperationException(
                "This document has never been saved, so there is no path to save it to. Use action \"saveAs\" with a savePath.");

        document.Save();
        return new { success = true, savedTo = document.PathName };
    }

    private object SaveAs(UIApplication app)
    {
        Document document = FamilyDocumentRegistry.Resolve(Text("documentHandle"), app);
        string savePath = RequiredText("savePath");

        string directory = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        document.SaveAs(savePath, new SaveAsOptions { OverwriteExistingFile = Flag("overwrite", true) });
        return new { success = true, savedTo = savePath };
    }

    private object Close(UIApplication app)
    {
        string handle = RequiredText("documentHandle");
        Document document = FamilyDocumentRegistry.Resolve(handle, app);

        bool saveFirst = Flag("save", false);
        string path = document.PathName;

        document.Close(saveFirst);
        FamilyDocumentRegistry.Forget(handle);

        return new { success = true, closed = handle, saved = saveFirst, pathName = path };
    }

    private static object Describe(string handle, Document document, string route)
    {
        return new
        {
            success = true,
            documentHandle = handle,
            title = document.Title,
            pathName = document.PathName,
            isFamilyDocument = document.IsFamilyDocument,
            familyCategory = document.IsFamilyDocument ? document.OwnerFamily?.FamilyCategory?.Name : null,
            route,
        };
    }
}
