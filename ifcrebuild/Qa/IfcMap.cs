using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.IFC;
using Autodesk.Revit.DB.Steel;

namespace IfcRebuild;

/// <summary>
/// The map file (one JSON object per line: gid = GlobalId of the IFC source, uid = Revit UniqueId, role, as = Steel Connections
/// id, rg = IfcGUID Revit writes on export) links the rebuilt model to its IFC source when Revit keeps its own IfcGUIDs
/// (config guidPolicy "revit"). Map: elements still carrying IfcGUID = source GlobalId get a line and their IfcGUID cleared;
/// Steel Connections lines (key "as") get the UniqueId of their SteelProxyElement. ExportIfc: IFC export with StoreIFCGUID,
/// then rg of every line read back from the elements.
/// </summary>
internal static class IfcMap
{
    static List<Dictionary<string, string>> Read(string path) =>
        File.Exists(path) ? File.ReadAllLines(path).Where(l => l.Trim().Length > 0)
            .Select(l => JsonSerializer.Deserialize<Dictionary<string, string>>(l)).ToList() : new();

    static void Write(string path, List<Dictionary<string, string>> rows) =>
        File.WriteAllLines(path, rows.Select(r => JsonSerializer.Serialize(r)));

    /// <param name="gidPath">GlobalIds of the IFC source, one per line: only an IfcGUID found there is a source GlobalId (others
    /// were written by Revit on an earlier export and stay).</param>
    public static string Map(Document doc, string mapPath, string gidPath, string mode)
    {
        var rows = Read(mapPath); var sb = new StringBuilder();
        var src = new HashSet<string>(File.ReadAllLines(gidPath).Select(l => l.Trim()).Where(l => l.Length > 0));
        // Steel Connections: Steel Connections id -> Revit UniqueId
        var asToUid = new Dictionary<string, string>();
        foreach (var e in new FilteredElementCollector(doc).OfClass(typeof(SteelProxyElement)).ToElements())
        {
            try { var g = SteelElementProperties.GetFabricationUniqueID(doc, new Reference(e)); if (g != Guid.Empty) asToUid[g.ToString().ToLowerInvariant()] = e.UniqueId; } catch { }
        }
        int asOk = 0, asMiss = 0;
        foreach (var r in rows.Where(r => r.ContainsKey("as") && !r.ContainsKey("uid")))
        {
            if (asToUid.TryGetValue(r["as"].Trim('{', '}').ToLowerInvariant(), out var uid)) { r["uid"] = uid; asOk++; } else asMiss++;
        }
        // elements still carrying IfcGUID = source GlobalId: a new line when the map has none (an earlier line, e.g. framing, only clears it)
        var known = new HashSet<string>(rows.Where(r => r.ContainsKey("uid")).Select(r => r["uid"]));
        var add = new List<(Element e, string gid)>(); var clearOnly = new List<Element>();
        foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements())
        {
            var g = e.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString();
            if (string.IsNullOrEmpty(g) || !src.Contains(g)) continue;
            if (known.Contains(e.UniqueId)) clearOnly.Add(e); else add.Add((e, g));
        }
        sb.AppendLine($"Map {mode}: lines before {rows.Count} | Steel Connections uid found {asOk}, not found {asMiss} | new lines {add.Count} | IfcGUID cleared only {clearOnly.Count}");
        foreach (var g in add.GroupBy(x => x.e.Category?.Name ?? "").OrderByDescending(g => g.Count()).Take(15)) sb.AppendLine($"  {g.Count()} {g.Key}");
        if (mode != "apply") return sb.ToString();
        var (st, _) = FailureLog.Run(doc, "Clear source IfcGUIDs", () =>
        {
            foreach (var (e, g) in add)
            {
                rows.Add(new Dictionary<string, string> { ["gid"] = g, ["uid"] = e.UniqueId, ["role"] = "ELEM" });
                var p = e.get_Parameter(BuiltInParameter.IFC_GUID); if (p != null && !p.IsReadOnly) p.Set("");
            }
            foreach (var e in clearOnly) { var p = e.get_Parameter(BuiltInParameter.IFC_GUID); if (p != null && !p.IsReadOnly) p.Set(""); }
        });
        int left = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements().Count(e => src.Contains(e.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString() ?? ""));
        sb.AppendLine($"still carrying a source GlobalId: {left}");
        Write(mapPath, rows);
        sb.AppendLine("wrote " + mapPath + " | " + st);
        return sb.ToString();
    }

    /// <summary>One TSV line per map line whose element exists: guid (GlobalId of the IFC source), id, cat, fam, typ, tid
    /// (type id), super (host of a nested family instance), rg (exported GlobalId, after ExportIfc).</summary>
    public static string RevitIds(Document doc, string mapPath, string outPath)
    {
        var sb = new StringBuilder("guid\tid\tcat\tfam\ttyp\ttid\tsuper\trg\n"); int n = 0, miss = 0;
        foreach (var r in Read(mapPath))
        {
            var e = r.TryGetValue("uid", out var uid) ? doc.GetElement(uid) : null;
            if (e == null) { miss++; continue; }
            var t = doc.GetElement(e.GetTypeId()) as ElementType;
            var sup = (e as FamilyInstance)?.SuperComponent;
            sb.Append(string.Join("\t", r["gid"], e.Id.Value, e.Category?.Name ?? "", t?.FamilyName ?? "", t?.Name ?? "", t?.Id.Value.ToString() ?? "",
                sup?.Id.Value.ToString() ?? "", r.TryGetValue("rg", out var rg) ? rg : "")).Append('\n'); n++;
        }
        File.WriteAllText(outPath, sb.ToString());
        return $"RevitIds {n} lines, element not found {miss} -> {outPath}";
    }

    /// <summary>Steel Connections elements only (plates, bolts, anchors, welds...), IFC2x3: Revit 2027 exports them only to IFC2x3
    /// (Revit.IFC.Export ExportOptionsCache: IncludeSteelElements = ExportAs2x3 &amp;&amp; option). A 3D view "IfcRebuild Steel" that
    /// shows only those categories filters the export. The GlobalId of each exported element = its compressed Steel Connections id.</summary>
    public static string ExportSteelView(Document doc, string folder, string name)
    {
        var t0 = DateTime.Now; Directory.CreateDirectory(folder);
        var keep = new HashSet<long> { (long)BuiltInCategory.OST_StructConnectionPlates, (long)BuiltInCategory.OST_StructConnectionBolts,
            (long)BuiltInCategory.OST_StructConnectionAnchors, (long)BuiltInCategory.OST_StructConnectionWelds, (long)BuiltInCategory.OST_StructConnectionShearStuds,
            (long)BuiltInCategory.OST_StructConnectionHoles, (long)BuiltInCategory.OST_StructConnections };
        View3D v;
        using (var tr = new Transaction(doc, "Steel export view"))
        {
            tr.Start();
            v = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(x => !x.IsTemplate && x.Name == "IfcRebuild Steel")
                ?? View3D.CreateIsometric(doc, new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.ThreeDimensional).Id);
            v.Name = "IfcRebuild Steel"; v.DetailLevel = ViewDetailLevel.Fine; if (v.ViewTemplateId != ElementId.InvalidElementId) v.ViewTemplateId = ElementId.InvalidElementId;
            foreach (Category c in doc.Settings.Categories)
                if (c.CategoryType == CategoryType.Model && v.CanCategoryBeHidden(c.Id)) v.SetCategoryHidden(c.Id, !keep.Contains(c.Id.Value));
            tr.Commit();
        }
        var opt = new IFCExportOptions { FileVersion = IFCVersion.IFC2x3, ExportBaseQuantities = true, FilterViewId = v.Id };
        opt.AddOption("IncludeSteelElements", "true"); opt.AddOption("VisibleElementsOfCurrentView", "true"); opt.AddOption("ActiveViewId", v.Id.Value.ToString());
        bool ok;
        using (var tr = new Transaction(doc, "Export Steel IFC")) { tr.Start(); ok = doc.Export(folder, name, opt); tr.Commit(); }
        var f = Path.Combine(folder, name.EndsWith(".ifc") ? name : name + ".ifc");
        return $"ExportSteelView {(ok ? "ok" : "FAIL")} {f} {(File.Exists(f) ? new FileInfo(f).Length / 1048576 : 0)} MB | view {v.Id.Value} | {(DateTime.Now - t0).TotalSeconds:F0} s";
    }

    /// <summary>IFC GlobalId (22 characters) of a GUID written as text, bytes in text order (as ifcopenshell.guid.compress).</summary>
    internal static string Compress(string guid)
    {
        const string C = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";
        var h = guid.Trim('{', '}').Replace("-", ""); var b = Enumerable.Range(0, 16).Select(i => Convert.ToInt32(h.Substring(2 * i, 2), 16)).ToArray();
        string B64(long v, int n) { var c = new char[n]; for (int i = n - 1; i >= 0; i--) { c[i] = C[(int)(v % 64)]; v /= 64; } return new string(c); }
        var sb = new StringBuilder(B64(b[0], 2));
        for (int i = 1; i < 16; i += 3) sb.Append(B64(((long)b[i] << 16) + (b[i + 1] << 8) + b[i + 2], 4));
        return sb.ToString();
    }

    /// <summary>IFC export of the whole model (version: an IFCVersion name, default IFC4x3) with Steel Connections elements, then
    /// rg of every map line. A Steel Connections element is exported under the GlobalId of its Steel Connections id, not under
    /// the IfcGUID of its SteelProxyElement: for lines with "as", rg = Compress(as) and rg_proxy = that IfcGUID.</summary>
    public static string ExportIfc(Document doc, string folder, string name, string mapPath, string version)
    {
        var t0 = DateTime.Now; Directory.CreateDirectory(folder);
        var opt = new IFCExportOptions { FileVersion = Enum.Parse<IFCVersion>(string.IsNullOrEmpty(version) ? "IFC4x3" : version), ExportBaseQuantities = true };
        opt.AddOption("StoreIFCGUID", "true");
        opt.AddOption("IncludeSteelElements", "true");                  // Steel Connections plates and bolts (not exported by default)
        opt.AddOption("ExportInternalRevitPropertySets", "true");
        opt.AddOption("ExportIFCCommonPropertySets", "true");
        opt.AddOption("ExportUserDefinedPsets", "false");
        opt.AddOption("ExportPartsAsBuildingElements", "false");
        opt.AddOption("VisibleElementsOfCurrentView", "false");
        bool ok;
        using (var tr = new Transaction(doc, "Export IFC"))               // StoreIFCGUID writes IfcGUID to the elements: needs an open transaction
        {
            tr.Start(); ok = doc.Export(folder, name, opt); tr.Commit();
        }
        int got = 0, miss = 0;
        var rows = Read(mapPath);
        foreach (var r in rows)
        {
            if (!r.TryGetValue("uid", out var uid)) { miss++; continue; }
            var g = doc.GetElement(uid)?.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString();
            if (string.IsNullOrEmpty(g)) { miss++; continue; }
            if (r.TryGetValue("as", out var asId)) { r["rg_proxy"] = g; r["rg"] = Compress(asId); }
            else r["rg"] = g;
            got++;
        }
        if (rows.Count > 0) Write(mapPath, rows);
        var f = Path.Combine(folder, name.EndsWith(".ifc") ? name : name + ".ifc");
        return $"ExportIfc {(ok ? "ok" : "FAIL")} {f} {(File.Exists(f) ? new FileInfo(f).Length / 1048576 : 0)} MB | rg {got}, missing {miss} | {(DateTime.Now - t0).TotalSeconds:F0} s";
    }
}
