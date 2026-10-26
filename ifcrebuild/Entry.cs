using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace IfcRebuild;

/// <summary>
/// The methods ifcrebuild_run calls by name. The first parameter is the active document (or the UIApplication for the
/// methods that open or create documents); every other parameter is a string taken in order from the job's "args", and a
/// missing trailing argument arrives as "". Each method returns a text report; the long ones also write it to
/// reportPath, because a job can outlive the client that started it. Methods that change the model take mode "probe"
/// (everything rolled back, also the default) or "apply". Lengths in the input files are mm unless a builder says m.
/// </summary>
public static class Entry
{
    public static string Ping(Document doc, string arg) =>
        $"IfcRebuild {typeof(Entry).Assembly.GetName().Version} | doc={doc.Title} | arg={arg}";

    /// <summary>Every method of this class with its argument names (the document argument left out).</summary>
    public static string Methods(UIApplication app) => string.Join("\n",
        typeof(Entry).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.DeclaringType == typeof(Entry))
            .OrderBy(m => m.Name).Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Skip(1).Select(p => p.Name))})"));

    // ---------------------------------------------------------------- documents
    /// <summary>New working document from a template (empty = Revit's default), saved once with maxBackups (default 20); never overwrites.</summary>
    public static string NewDocument(Document doc, string path, string maxBackups, string template) => DocTools.NewDocument(doc, path, maxBackups, template);
    public static string Open(Document doc, string path) => DocTools.Open(doc, path);
    /// <summary>Empty project from a template (empty = Revit's default), saved to path (overwritten) and made active; needs no open document.</summary>
    public static string NewEmpty(UIApplication app, string path, string template) => DocTools.NewEmpty(app, path, template);
    public static string OpenActive(UIApplication app, string path) => "active " + app.OpenAndActivateDocument(path).Document.PathName;
    /// <summary>Saves only when the gate passes.</summary>
    public static string Save(Document doc)
    {
        var g = IfcRebuild.Gate.Run(doc);
        return g.StartsWith("GATE PASS") ? g + "\n" + DocTools.Save(doc) : "NOT SAVED " + g;
    }
    /// <summary>No DirectShape, no import instance, no in-place family.</summary>
    public static string Gate(Document doc) => IfcRebuild.Gate.Run(doc);

    // ---------------------------------------------------------------- builders
    /// <summary>Levels, plans, grids, materials, project GUIDs and shared coordinates from plan.json.</summary>
    public static string Setup(Document doc, string planPath, string mode) => IfcRebuild.Setup.Run(doc, planPath, mode);
    /// <summary>Loadable families and their instances from plan.json; filter "phase=a-b;cls=..;kind=..;names=..;prefix=.."; max 0 = all.</summary>
    public static string Build(Document doc, string planPath, string filter, string mode, string max, string reportPath) =>
        IfcRebuild.Build.Run(doc, planPath, filter, mode, Batch.Int(max, 0), reportPath);
    /// <summary>Deletes the plan elements matching the filter (found by Mark) and the families they leave unused.</summary>
    public static string Delete(Document doc, string planPath, string filter, string mode) => IfcRebuild.Build.Delete(doc, planPath, filter, mode);
    /// <summary>System families from plan.json "system": what = walls | cladding | floors | roofs | pipes | trays | rooms | all | names=a,b.</summary>
    public static string System(Document doc, string planPath, string what, string mode, string reportPath) => SystemFamilies.Run(doc, planPath, what, mode, reportPath);
    /// <summary>Doors hosted in their walls from plan.json "doors"; filter "names=a,b" | "".</summary>
    public static string Doors(Document doc, string planPath, string filter, string mode, string reportPath) => IfcRebuild.Doors.Run(doc, planPath, filter, mode, reportPath);
    /// <summary>Join Geometry from the yields_to lists of the plan; filter "phase=a-b" | "names=.." | "".</summary>
    public static string Joins(Document doc, string planPath, string filter, string mode, string reportPath) => IfcRebuild.Joins.Run(doc, planPath, filter, mode, reportPath);
    /// <summary>Stairs or ramps as native system families; what = stairs | ramps; filter "name,name" | "".</summary>
    public static string Circulation(Document doc, string jsonPath, string what, string mode, string filter) => IfcRebuild.Circulation.Run(doc, jsonPath, what, mode, filter);
    /// <summary>Steel framing with library families, matched by section signature; filter "names=..|fam=..|max=n|range=a-b"; mode probe | apply | refit.</summary>
    public static string Frame(Document doc, string jsonPath, string filter, string mode, string mapPath, string reportPath) => IfcRebuild.Frame.Run(doc, jsonPath, filter, mode, mapPath, reportPath);
    /// <summary>Steel Connections plates, bolt and anchor patterns per node; filter "nodes=a,b|max=n|range=a-b".</summary>
    public static string Steel(Document doc, string jsonPath, string filter, string mode, string mapPath, string reportPath) => IfcRebuild.Steel.Build(doc, jsonPath, filter, mode, mapPath, reportPath);
    /// <summary>One plate and one bolt group by name, rolled back; reports what Steel Connections made.</summary>
    public static string SteelProbe(Document doc, string jsonPath, string plate, string bolt) => IfcRebuild.Steel.Probe(doc, jsonPath, plate, bolt);
    /// <summary>One Steel Connections plate at the origin (smoke test, best on an empty project).</summary>
    public static string SteelSmoke(Document doc) => IfcRebuild.Steel.Smoke(doc);
    /// <summary>Read-only count of Steel Connections objects by type and of SteelProxyElement.</summary>
    public static string SteelCount(Document doc) => IfcRebuild.Steel.Count(doc);
    /// <summary>Deletes the Revit elements of the source GlobalIds in gidsPath (one per line), found through the map "uid", so their nodes can be rebuilt.</summary>
    public static string SteelDeleteByMap(Document doc, string mapPath, string gidsPath, string mode) => IfcRebuild.Steel.DeleteByMap(doc, mapPath, gidsPath, mode);
    /// <summary>Read-only CSV gid,as,n,d,grip,add,length,cx,cy,cz (mm) of the bolt patterns in the map lines role BOLTS.</summary>
    public static string SteelBoltReport(Document doc, string mapPath, string outPath) => IfcRebuild.Steel.BoltReport(doc, mapPath, outPath);
    /// <summary>Bolt grip from a CSV "as,grip_mm,len|add[,cx,cy,cz]" for bolts through framing Steel Connections cannot measure.</summary>
    public static string SteelSetGrip(Document doc, string csvPath, string mode) => IfcRebuild.Steel.SetGrip(doc, csvPath, mode);
    /// <summary>New outline for plates already built, keeping their Steel Connections id: JSON {gid: {n, pts}}.</summary>
    public static string SteelSetContour(Document doc, string mapPath, string jsonPath, string mode) => IfcRebuild.Steel.SetContour(doc, mapPath, jsonPath, mode);
    /// <summary>Plates already built (map lines PLATE): thickness on the +n side (Portioning 1).</summary>
    public static string SteelFixPlates(Document doc, string mapPath, string mode) => IfcRebuild.Steel.FixPlates(doc, mapPath, mode);
    /// <summary>Bolt patterns already built (map lines BOLTS): count and spacing from the Steel JSON; filter "max=n|range=a-b|names=gid,..".</summary>
    public static string SteelFixBolts(Document doc, string jsonPath, string mapPath, string filter, string mode) => IfcRebuild.Steel.FixBolts(doc, jsonPath, mapPath, filter, mode);
    /// <summary>Anchor patterns already built (map lines ANCHORS): diameter and straight length from the Steel JSON; filter "max=n|range=a-b".</summary>
    public static string SteelFixAnchors(Document doc, string jsonPath, string mapPath, string filter, string mode) => IfcRebuild.Steel.FixAnchors(doc, jsonPath, mapPath, filter, mode);
    /// <summary>Read-only: direction, length and standard of the first n anchor patterns (default 10).</summary>
    public static string SteelAnchorInfo(Document doc, string n) => IfcRebuild.Steel.AnchorInfo(doc, n);
    /// <summary>Rolled back: the anchor lengths "l1,l2,.." (mm) Steel Connections keeps for diameter d (mm).</summary>
    public static string SteelAnchorTry(Document doc, string d, string lengths) => IfcRebuild.Steel.AnchorTry(doc, d, lengths);
    /// <summary>Three plates with Portioning 0, 0.5, 1 at the origin (smoke test, best on an empty project).</summary>
    public static string SteelSmokePortioning(Document doc) => IfcRebuild.Steel.SmokePortioning(doc);
    /// <summary>Native Rebar per bar set of the rebar JSON; filter "hosts=a,b|max=n|range=a-b".</summary>
    public static string Rebar(Document doc, string jsonPath, string filter, string mode, string mapPath, string reportPath) => IfcRebuild.Rebars.Run(doc, jsonPath, filter, mode, mapPath, reportPath);
    /// <summary>One Revit assembly per node of the JSON.</summary>
    public static string Assemblies(Document doc, string jsonPath, string mode, string reportPath) => IfcRebuild.Assemblies.Run(doc, jsonPath, mode, reportPath);
    /// <summary>IFC attributes, Psets, Qtos and classification as shared parameters; mode probe | apply | add.</summary>
    public static string Params(Document doc, string jsonPath, string mode) => IfcRebuild.Params.Run(doc, jsonPath, mode);
    public static string AuditParams(Document doc, string jsonPath) => IfcRebuild.Params.Audit(doc, jsonPath);
    /// <summary>Worksets per discipline (rules in the config) in one file, saved as central; mode probe (counts) | apply.</summary>
    public static string Collab(Document doc, string mode) => IfcRebuild.Collab.Run(doc, mode);

    // ---------------------------------------------------------------- GUIDs and QA
    /// <summary>pairs "PROJECT=gid;SITE=gid;BUILDING=gid;level name=gid;...".</summary>
    public static string SetSpatialGuids(Document doc, string pairs, string mode) => GuidTools.SetSpatial(doc, pairs, mode);
    /// <summary>Empties every Mark that equals the element's IfcGUID (the working key of the build).</summary>
    public static string ClearMarks(Document doc, string mode) => GuidTools.ClearMarks(doc, mode);
    /// <summary>Map file lines for elements still carrying IfcGUID = source GlobalId (gidPath: one per line; IfcGUID then cleared)
    /// and the SteelProxyElement UniqueId of Steel Connections lines.</summary>
    public static string GuidMap(Document doc, string mapPath, string gidPath, string mode) => IfcMap.Map(doc, mapPath, gidPath, mode);
    /// <summary>IFC export (version default IFC4x3) with Steel Connections elements, storing IfcGUIDs; then rg of every map line.</summary>
    public static string ExportIfc(Document doc, string folder, string name, string mapPath, string version) => IfcMap.ExportIfc(doc, folder, name, mapPath, version);
    /// <summary>Same as ExportIfc (kept for callers that name the version explicitly).</summary>
    public static string ExportIfcVersion(Document doc, string folder, string name, string mapPath, string version) => IfcMap.ExportIfc(doc, folder, name, mapPath, version);
    /// <summary>Steel Connections elements only, IFC2x3 (the only schema Revit 2027 exports them to), through a 3D view showing only those categories.</summary>
    public static string ExportSteelView(Document doc, string folder, string name) => IfcMap.ExportSteelView(doc, folder, name);
    /// <summary>TSV guid, id, cat, fam, typ, tid, super, rg of every map line whose element exists (read-only).</summary>
    public static string RevitIds(Document doc, string mapPath, string outPath) => IfcMap.RevitIds(doc, mapPath, outPath);

    // ---------------------------------------------------------------- families
    /// <summary>Read-only: families loaded and used, library families among them (libDir of .rfa, optional), families per category.</summary>
    public static string FamilyStats(Document doc, string libDir) => Families.Stats(doc, libDir);
    /// <summary>Unused families and family types only, as Purge Unused.</summary>
    public static string PurgeUnusedFamilies(Document doc, string mode) => Families.PurgeUnused(doc, mode);
    /// <summary>Classification code (tsv "UniqueId&lt;TAB&gt;code", cut to codeLength, default 5) as name prefix of the families the build made; library families (libDir) keep their names.</summary>
    public static string RenameFamiliesUniformat(Document doc, string tsvPath, string libDir, string mode, string codeLength) => Families.RenameByCode(doc, tsvPath, libDir, mode, codeLength);
    /// <summary>Read-only: bounding box (mm) of an element (ElementId or UniqueId) at Coarse, Medium and Fine detail.</summary>
    public static string GeoDetail(Document doc, string uid) => Families.GeoDetail(doc, uid);
    /// <summary>Experiment: an L-shaped bolt as sweeps and cylinders in a generic model family; volumes.</summary>
    public static string TestSweep(Document doc) => Families.TestSweep(doc);
    /// <summary>Experiment: Member Left / Right of the Beams and Braces template and a family of length L (mm) placed on a line of length L; rolled back.</summary>
    public static string TestTemplate(Document doc, string mmLength) => Families.TestTemplate(doc, mmLength);
    /// <summary>One line per element with an IfcGUID: guid|class|category|volume m3|box mm|mark (read-only).</summary>
    public static string ExportQa(Document doc, string outPath) => IfcRebuild.ExportQa.Run(doc, outPath);
    /// <summary>Signs that a model was rebuilt by a program, measured read-only, as JSON.</summary>
    public static string Recog(Document doc, string outPath) => IfcRebuild.Recog.Run(doc, outPath);
    /// <summary>Category, placement, parameters and forms of family files (paths separated by ';'), to a file.</summary>
    public static string InspectFamilies(Document doc, string paths, string outPath) => Inspect.Families(doc, paths, outPath);
    /// <param name="view">viewing direction "x,y,z"; margin: mm, or an explicit box "x0,y0,z0,x1,y1,z1" in mm</param>
    public static string Snapshot(Document doc, string guids, string outPng, string view, string margin) =>
        IfcRebuild.Snapshot.Take(doc, guids, outPng, view, margin);
}
