using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace IfcRebuild;

/// <summary>
/// IFC data onto the rebuilt elements, from the JSON {"props": {"GlobalId": {"class", "attrs": {...}, "psets": {"Pset":
/// {"props": {"Prop": ["IfcType", value]}}}, "qtos": {...}, "classif": [{"system", "id", "name"}]}}}:
/// - every Pset property and every Qto quantity -> shared parameter named "&lt;Pset&gt;.&lt;Prop&gt;" with the IFC names kept
///   character for character (Revit keeps leading, trailing and inner spaces; only the characters Revit forbids are
///   replaced), instance binding on the categories of the elements found, group IFC Parameters;
/// - attributes Name, ObjectType, Tag, Description -> IfcName, IfcObjectType, IfcTag, IfcDescription;
///   PredefinedType -> IFC_EXPORT_PREDEFINEDTYPE; first classification reference -> ClassificationCode "[system]id:name".
/// Definitions go to "&lt;document&gt;_SharedParams.txt" next to the document, rewritten each run; the parameter GUID is derived
/// from its name, so reruns bind the same parameters and parameters of an earlier run that are no longer wanted can be
/// recognised and removed. Elements are found by IfcGUID. mode "probe" (rolled back) | "apply" | "add" (for a JSON that
/// covers only some elements: keeps every other parameter and every binding category). Audit compares every value in
/// the document with the JSON.
/// </summary>
internal static class Params
{
    internal sealed record Def(string RevitName, string Pset, string Prop, string IfcType);

    const string Group = "IFC";
    static readonly string[] AttrParams = { "IfcName", "IfcObjectType", "IfcTag", "IfcDescription", "ClassificationCode" };
    static readonly Dictionary<string, string> AttrOf = new()
    { ["Name"] = "IfcName", ["ObjectType"] = "IfcObjectType", ["Tag"] = "IfcTag", ["Description"] = "IfcDescription" };

    /// <summary>IFC value type -> Revit spec. Measures with units are kept as text (reported).</summary>
    static ForgeTypeId Spec(string t, List<string> notes, string name)
    {
        switch (t)
        {
            case "IfcBoolean" or "IfcLogical": return SpecTypeId.Boolean.YesNo;
            case "IfcInteger" or "IfcCountMeasure": return SpecTypeId.Int.Integer;
            case "IfcReal": return SpecTypeId.Number;
            case "IfcQuantityLength" or "IfcQuantityArea" or "IfcQuantityVolume" or "IfcQuantityWeight" or "IfcQuantityCount": return SpecTypeId.Number;   // SI value as written in the IFC (m, m2, m3, kg)
            case "IfcText" or "IfcLabel" or "IfcIdentifier": return SpecTypeId.String.Text;
            default: notes?.Add($"{t} kept as text: {name}"); return SpecTypeId.String.Text;
        }
    }

    static string Clean(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s) sb.Append("{}[]|;<>?`~:\\".IndexOf(ch) >= 0 ? '_' : ch);
        return sb.ToString();
    }

    static Guid DetGuid(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes("IfcRebuild.param:" + name)));

    static List<Def> Defs(JsonElement props, out Dictionary<(string, string), Def> byIfc)
    {
        byIfc = new();
        var used = new Dictionary<string, Def>();
        foreach (var el in props.EnumerateObject())
            foreach (var ps in Sets(el.Value))
                foreach (var p in ps.Value.GetProperty("props").EnumerateObject())
                {
                    var key = (ps.Name, p.Name);
                    if (byIfc.ContainsKey(key)) continue;
                    var t = p.Value[0].ValueKind == JsonValueKind.String ? p.Value[0].GetString() : "IfcText";
                    var name = Clean(ps.Name) + "." + Clean(p.Name);
                    for (int k = 2; used.ContainsKey(name); k++) name = Clean(ps.Name) + "." + Clean(p.Name) + $" ({k})";
                    byIfc[key] = used[name] = new Def(name, ps.Name, p.Name, t);
                }
        return used.Values.ToList();
    }

    /// <summary>Property sets then quantity sets of one element of the JSON.</summary>
    static IEnumerable<JsonProperty> Sets(JsonElement el) =>
        (el.TryGetProperty("psets", out var p) ? p.EnumerateObject() : Enumerable.Empty<JsonProperty>())
            .Concat(el.TryGetProperty("qtos", out var q) ? q.EnumerateObject() : Enumerable.Empty<JsonProperty>());

    static IEnumerable<JsonProperty> Attrs(JsonElement el) => el.TryGetProperty("attrs", out var a) ? a.EnumerateObject() : Enumerable.Empty<JsonProperty>();
    static JsonElement[] Classif(JsonElement el) => el.TryGetProperty("classif", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().ToArray() : Array.Empty<JsonElement>();

    static string Text(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        JsonValueKind.Null => null,
        _ => v.GetRawText(),
    };

    static string Text(Parameter p) => p == null || !p.HasValue ? null : p.StorageType switch
    {
        StorageType.String => p.AsString(),
        StorageType.Integer => p.AsInteger().ToString(),
        StorageType.Double => p.AsDouble().ToString("R"),
        _ => p.AsValueString(),
    };

    /// <summary>A double parameter against the IFC text: "23.0" and "23" are the same value.</summary>
    static bool SameNumber(Parameter p, string want) =>
        p.StorageType == StorageType.Double && double.TryParse(want, NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
        && Math.Abs(p.AsDouble() - w) <= 1e-9 * Math.Max(1, Math.Abs(w));

    static string ClassCode(JsonElement c)
    {
        string S(string k) => c.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
        return $"[{S("system")}]{S("id")}:{S("name")}";
    }

    /// <summary>Model elements carrying an IfcGUID, one per GUID; template elements (Sun Path, views) excluded.</summary>
    static Dictionary<string, Element> ByMark(Document doc) => new FilteredElementCollector(doc).WhereElementIsNotElementType()
        .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model && e.Category.AllowsBoundParameters)
        .Select(e => (e, m: e.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString()))
        .Where(x => !string.IsNullOrEmpty(x.m) && x.m.Length == 22)
        .GroupBy(x => x.m).ToDictionary(g => g.Key, g => g.First().e);

    public static string Run(Document doc, string jsonPath, string mode)
    {
        using var js = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var props = js.RootElement.GetProperty("props");
        var defs = Defs(props, out var byIfc);
        var notes = new List<string>();
        // "add": a JSON of a few elements only. A subset run in "apply" would remove the parameters of the other elements
        // as stale and rebind the rest to the subset categories; "add" keeps every parameter and binding category.
        bool add = mode == "add";
        if (!add && string.IsNullOrEmpty(doc.PathName)) return "ERROR the document has never been saved: the shared parameter file goes next to it";
        var spPath = add ? Path.Combine(Path.GetTempPath(), "IfcRebuild_add_SharedParams.txt")
                         : Path.Combine(Path.GetDirectoryName(doc.PathName), Path.GetFileNameWithoutExtension(doc.PathName) + "_SharedParams.txt");

        // 1. shared parameter file, rewritten each run
        var app = doc.Application;
        var oldSp = app.SharedParametersFilename;
        var ext = new Dictionary<string, ExternalDefinition>();
        try
        {
            File.WriteAllText(spPath, "");
            app.SharedParametersFilename = spPath;
            var grp = app.OpenSharedParameterFile().Groups.Create(Group);
            ExternalDefinition Make(string name, ForgeTypeId spec) =>
                (ExternalDefinition)grp.Definitions.Create(new ExternalDefinitionCreationOptions(name, spec) { GUID = DetGuid(name) });
            foreach (var a in AttrParams) ext[a] = Make(a, SpecTypeId.String.Text);
            foreach (var d in defs) ext[d.RevitName] = Make(d.RevitName, Spec(d.IfcType, notes, d.RevitName));
        }
        finally { app.SharedParametersFilename = oldSp; }
        // parameters made by an earlier run (GUID derived from their own name) that are no longer wanted
        var wanted = ext.Values.Select(d => d.GUID).ToHashSet();
        var stale = new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).Cast<SharedParameterElement>()
            .Where(s => !add && s.GuidValue == DetGuid(s.Name) && !wanted.Contains(s.GuidValue)).Select(s => s.GuidValue).ToList();

        var marks = ByMark(doc).Where(kv => props.TryGetProperty(kv.Key, out _)).ToDictionary(kv => kv.Key, kv => kv.Value);   // only IFC products: template elements carry Revit IfcGUIDs too
        int set = 0, failed = 0, missingEl = 0, removed = 0, k = 0;
        var fails = new List<string>();
        using var tg = new TransactionGroup(doc, "IfcRebuild: IFC parameters");
        tg.Start();
        // 2. remove stale parameters, bind the current ones
        var (st1, log1) = FailureLog.Run(doc, "IfcRebuild: bind IFC parameters", () =>
        {
            foreach (var spe in new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).Cast<SharedParameterElement>()
                         .Where(s => stale.Contains(s.GuidValue)).ToList())
            {
                doc.ParameterBindings.Remove(spe.GetDefinition());
                doc.Delete(spe.Id);
                removed++;
            }
            var cats = new CategorySet();
            foreach (var c in marks.Values.Select(e => e.Category).GroupBy(c => c.Id).Select(g => g.First())) cats.Insert(c);
            foreach (var d in ext.Values)
            {
                var b = app.Create.NewInstanceBinding(cats);
                var have = add ? SharedParameterElement.Lookup(doc, d.GUID)?.GetDefinition() : null;
                if (have != null && doc.ParameterBindings.get_Item(have) is ElementBinding eb)
                    foreach (Category c in eb.Categories) if (!b.Categories.Contains(c)) b.Categories.Insert(c);
                if (!doc.ParameterBindings.Insert(d, b, GroupTypeId.Ifc)) doc.ParameterBindings.ReInsert(d, b, GroupTypeId.Ifc);
            }
        });
        // 3. values
        var (st2, log2) = FailureLog.Run(doc, "IfcRebuild: write IFC parameters", () =>
        {
            void Put(Parameter p, string what, string v)
            {
                if (v == null) return;
                if (p == null || p.IsReadOnly) { failed++; fails.Add($"{what}: no parameter"); return; }
                bool ok = p.StorageType switch
                {
                    StorageType.Integer => int.TryParse(v, out var i) && p.Set(i),
                    StorageType.Double => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && p.Set(x),
                    _ => p.Set(v),
                };
                if (ok) set++; else { failed++; fails.Add($"{what}: Set failed"); }
            }
            foreach (var el in props.EnumerateObject())
            {
                if (++k % 500 == 0) Progress.Report($"Params {k} elements, {set} values");
                if (!marks.TryGetValue(el.Name, out var e)) { missingEl++; continue; }
                foreach (var a in Attrs(el.Value))
                {
                    if (a.Name == "PredefinedType") Put(e.get_Parameter(BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE), "PredefinedType", Text(a.Value));
                    else if (AttrOf.TryGetValue(a.Name, out var pn)) Put(e.get_Parameter(ext[pn].GUID), pn, Text(a.Value));
                }
                var cl = Classif(el.Value);
                if (cl.Length > 0) Put(e.get_Parameter(ext["ClassificationCode"].GUID), "ClassificationCode", ClassCode(cl[0]));
                if (cl.Length > 1) notes.Add($"{el.Name}: {cl.Length} classification references, one parameter");
                foreach (var ps in Sets(el.Value))
                    foreach (var p in ps.Value.GetProperty("props").EnumerateObject())
                    {
                        var d = byIfc[(ps.Name, p.Name)];
                        Put(e.get_Parameter(ext[d.RevitName].GUID), d.RevitName, Text(p.Value[1]));
                    }
            }
        });
        var audit = Audit(doc, props, marks);
        if (mode == "apply" || add) tg.Assimilate(); else tg.RollBack();
        return $"defs: {defs.Count} Pset/Qto properties + {AttrParams.Length} attribute parameters | {spPath}\n" +
               $"stale removed={removed} | bind commit={st1} write commit={st2} | values set={set} failed={failed} | elements not found={missingEl} | group={(mode == "apply" || add ? "assimilated" : "rolled back")}\n" +
               (fails.Count > 0 ? "fails: " + string.Join("; ", fails.GroupBy(f => f).Select(g => $"{g.Count()}x {g.Key}").Take(10)) + "\n" : "") +
               (notes.Count > 0 ? "notes: " + string.Join("; ", notes.Distinct().Take(10)) + "\n" : "") +
               audit + log1.Summary() + "\n" + log2.Summary();
    }

    public static string Audit(Document doc, string jsonPath)
    {
        using var js = JsonDocument.Parse(File.ReadAllText(jsonPath));
        return Audit(doc, js.RootElement.GetProperty("props"), ByMark(doc));
    }

    /// <summary>Every IFC value against the document: missing parameter, empty, different value; plus values
    /// on IfcRebuild parameters the IFC element does not have (extra). Names and counts only.</summary>
    static string Audit(Document doc, JsonElement props, Dictionary<string, Element> marks)
    {
        var ours = new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).Cast<SharedParameterElement>()
            .Where(s => s.GuidValue == DetGuid(s.Name)).ToDictionary(s => s.GuidValue, s => s.Name);
        var c = new SortedDictionary<string, int>();
        var bad = new Dictionary<string, int>();
        void Hit(string k, string name) { c[k] = c.GetValueOrDefault(k) + 1; if (name != null) bad[k + ": " + name] = bad.GetValueOrDefault(k + ": " + name) + 1; }
        void Cmp(Parameter p, string name, string want)
        {
            c["compared"] = c.GetValueOrDefault("compared") + 1;
            if (p == null) Hit("parameter missing", name);
            else if (Text(p) == null && want != null) Hit("value empty", name);
            else if (Text(p) != want && !SameNumber(p, want)) Hit("value differs", name);
        }
        foreach (var el in props.EnumerateObject())
        {
            if (!marks.TryGetValue(el.Name, out var e)) { Hit("element missing", el.Value.TryGetProperty("class", out var cv) ? cv.GetString() : "?"); continue; }
            var expected = new HashSet<string>();
            foreach (var a in Attrs(el.Value))
            {
                if (a.Name == "PredefinedType") Cmp(e.get_Parameter(BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE), "PredefinedType", Text(a.Value));
                else if (AttrOf.TryGetValue(a.Name, out var pn)) { Cmp(e.LookupParameter(pn), pn, Text(a.Value)); expected.Add(pn); }
            }
            var cl = Classif(el.Value);
            if (cl.Length > 0) { Cmp(e.LookupParameter("ClassificationCode"), "ClassificationCode", ClassCode(cl[0])); expected.Add("ClassificationCode"); }
            foreach (var ps in Sets(el.Value))
                foreach (var p in ps.Value.GetProperty("props").EnumerateObject())
                {
                    var name = Clean(ps.Name) + "." + Clean(p.Name);
                    expected.Add(name);
                    if (p.Value[1].ValueKind != JsonValueKind.Null) Cmp(e.LookupParameter(name), name, Text(p.Value[1]));
                }
            foreach (Parameter p in e.Parameters)
                if (p.IsShared && ours.ContainsKey(p.GUID) && p.HasValue && !expected.Contains(p.Definition.Name)) Hit("extra value", p.Definition.Name);
        }
        return "AUDIT params: " + string.Join(" ", c.Select(kv => $"{kv.Key}={kv.Value}")) + "\n" +
               (bad.Count > 0 ? "  " + string.Join("; ", bad.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Value}x {kv.Key}")) + "\n" : "");
    }
}
