using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCPCommandSet.Utils;

/// <summary>
/// Keeps the family documents a session opened in the background so later commands can
/// address them by handle.
///
/// Family authoring cannot go through <c>ActiveUIDocument</c> the way the rest of the
/// command set does. A family opened with <see cref="Application.OpenDocumentFile(string)"/>
/// is a separate document that never becomes the active one, and forcing it to the front
/// would disturb whatever the user has on screen. So the document lives here, keyed by a
/// handle the tool passes back on every subsequent call.
///
/// Handles are per Revit session. Nothing here survives a restart, which is deliberate:
/// a stale handle should fail loudly rather than resolve to a document that has since
/// been closed and reopened from disk.
/// </summary>
public static class FamilyDocumentRegistry
{
    private static readonly object _gate = new object();
    private static readonly Dictionary<string, Document> _documents = new Dictionary<string, Document>();
    private static int _counter;

    /// <summary>Registers a document and returns its handle.</summary>
    public static string Register(Document document)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));

        lock (_gate)
        {
            _counter++;
            string handle = $"fam{_counter}";
            _documents[handle] = document;
            return handle;
        }
    }

    /// <summary>
    /// Resolves a handle. An empty handle falls back to the active document, which is what
    /// you want when the family is already open in the Revit UI and the user is watching it.
    /// </summary>
    public static Document Resolve(string handle, UIApplication app)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            Document active = app?.ActiveUIDocument?.Document;
            if (active == null)
                throw new InvalidOperationException(
                    "No documentHandle was given and Revit has no active document to fall back on.");
            return active;
        }

        Document document;
        lock (_gate)
        {
            if (!_documents.TryGetValue(handle, out document))
                throw new InvalidOperationException(
                    $"Unknown documentHandle '{handle}'. Handles only live for one Revit session; open the family again to get a new one.");
        }

        // A document closed behind our back throws on every member, so check the cheapest one.
        if (!document.IsValidObject)
        {
            lock (_gate) _documents.Remove(handle);
            throw new InvalidOperationException(
                $"The document behind handle '{handle}' has been closed. Open the family again to get a new handle.");
        }

        return document;
    }

    /// <summary>Drops a handle without touching the document itself.</summary>
    public static bool Forget(string handle)
    {
        lock (_gate)
        {
            return handle != null && _documents.Remove(handle);
        }
    }

    /// <summary>Everything currently registered, for the list action.</summary>
    public static IReadOnlyList<object> List()
    {
        lock (_gate)
        {
            var stale = new List<string>();
            var items = new List<object>();

            foreach (KeyValuePair<string, Document> entry in _documents)
            {
                if (!entry.Value.IsValidObject)
                {
                    stale.Add(entry.Key);
                    continue;
                }

                items.Add(new
                {
                    documentHandle = entry.Key,
                    title = entry.Value.Title,
                    pathName = entry.Value.PathName,
                    isFamilyDocument = entry.Value.IsFamilyDocument,
                });
            }

            foreach (string key in stale) _documents.Remove(key);
            return items;
        }
    }
}
