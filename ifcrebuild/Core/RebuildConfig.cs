using System.Text.Json;

namespace IfcRebuild;

/// <summary>
/// Project conventions for one job, read from the optional JSON file passed as "config" to ifcrebuild_run (camelCase
/// keys; any key may be left out and keeps its default). Paths, type names and naming formats live here or in the
/// input JSON files, never in the code, so one build serves every project.
/// </summary>
public sealed class RebuildConfig
{
    /// <summary>Config of the job that is running; defaults when the job has no config file.</summary>
    public static RebuildConfig Current => _current ??= Load(null);
    static RebuildConfig _current;

    /// <summary>Loads the config file (empty path: defaults) and makes it current. Called by the job runner, also
    /// through reflection on a separately built copy of this assembly.</summary>
    public static RebuildConfig Use(string path)
    {
        _current = Load(path);
        return _current;
    }

    public static RebuildConfig Load(string path)
    {
        var o = string.IsNullOrWhiteSpace(path) ? new RebuildConfig()
            : JsonSerializer.Deserialize<RebuildConfig>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) ?? new RebuildConfig();
        o.TypeNames ??= new();
        foreach (var kv in DefaultTypeNames) o.TypeNames.TryAdd(kv.Key, kv.Value);
        if (o.Worksets == null || o.Worksets.Count == 0) o.Worksets = DefaultWorksets();
        return o;
    }

    /// <summary>Folder of the .rft family templates; empty = the one Revit is set up with (Application.FamilyTemplatePath).</summary>
    public string FamilyTemplateDir { get; set; } = "";

    /// <summary>Folder where Build saves the shared part families (one bolt, one cleat) it nests; empty = %TEMP%\IfcRebuild\parts.</summary>
    public string PartFamilyDir { get; set; } = "";

    /// <summary>Folder for the job result files; empty = %TEMP%\IfcRebuild\jobs.</summary>
    public string JobsDir { get; set; } = "";

    /// <summary>File next to the plan with the IFC volume and box of each GlobalId: {"gid": {"v": m3, "bb": [x0,y0,z0,x1,y1,z1] in m}}.</summary>
    public string GeometryFile { get; set; } = "ifc_geo.json";

    /// <summary>Family parameter that carries the material of the solids of a generated family.</summary>
    public string MaterialParameter { get; set; } = "Material";

    /// <summary>Material of a door whose plan record names none.</summary>
    public string DoorMaterial { get; set; } = "Steel";

    /// <summary>"source": IfcGUID = IFC GlobalId; "revit": Frame leaves IfcGUID to Revit and only records the pair in the map file.</summary>
    public string GuidPolicy { get; set; } = "source";

    /// <summary>Name of the structural plan Setup makes per level; {level} = level name.</summary>
    public string StructuralPlanName { get; set; } = "{level} - Structural";

    /// <summary>How far grid lines run past the outermost grid when the plan gives no extent (mm).</summary>
    public double GridMarginMm { get; set; } = 8000;

    /// <summary>Piping system type for pipes: the first whose name contains this text; empty = the first one.</summary>
    public string PipeSystemType { get; set; } = "";

    /// <summary>Rebar hook type used for 135 degree hooks.</summary>
    public string RebarHookType { get; set; } = "Stirrup/Tie Seismic - 135 deg";

    /// <summary>Name of a generated rebar bar type; {d} = diameter in mm, {grade} = steel grade.</summary>
    public string RebarBarTypeName { get; set; } = "{d} {grade}";

    /// <summary>Standard and stirrup bend diameter of a generated bar type, as a multiple of the bar diameter.</summary>
    public double RebarBendDiameterFactor { get; set; } = 4;

    /// <summary>Source types the stair builder duplicates (they must exist in the project template).</summary>
    public string StairRunTypeSource { get; set; } = "M_Monolithic Run";
    public string StairLandingTypeSource { get; set; } = "300mm Thickness";
    public string StairTypeSource { get; set; } = "M_Monolithic Stair";

    /// <summary>Names of the types the builders create, by key; {material} and {thickness} (mm) are filled in.
    /// A "type" field on the input record wins over these.</summary>
    public Dictionary<string, string> TypeNames { get; set; } = new();

    static readonly Dictionary<string, string> DefaultTypeNames = new()
    {
        ["wall"] = "Wall {material} {thickness}mm",
        ["retainingWall"] = "Retaining wall {material} {thickness}mm",
        ["cladding"] = "Cladding {material} {thickness}mm",
        ["floor"] = "Floor {material} {thickness}mm",
        ["roof"] = "Roof {material} {thickness}mm",
        ["stairRun"] = "Run {material} {thickness}mm",
        ["stairLanding"] = "Landing {material} {thickness}mm",
        ["stair"] = "Stair {material}",
        ["ramp"] = "Ramp {material} {thickness}mm",
    };

    public string TypeName(string key, string material, double thicknessMm, string format = "F0") =>
        (TypeNames.TryGetValue(key, out var f) ? f : key + " {material} {thickness}mm")
            .Replace("{material}", material ?? "")
            .Replace("{thickness}", thicknessMm.ToString(format, System.Globalization.CultureInfo.InvariantCulture))
            .Trim();

    /// <summary>Collab: worksets and the categories (BuiltInCategory names, e.g. "OST_Walls") assigned to them.</summary>
    public List<WorksetRule> Worksets { get; set; }

    /// <summary>Collab: workset of concrete framing and columns, and of steel framing, columns and Steel Connections elements.</summary>
    public string ConcreteWorkset { get; set; } = "Structure - Concrete";
    public string SteelWorkset { get; set; } = "Structure - Steel";

    /// <summary>Collab: the two worksets Revit makes when worksharing is enabled.</summary>
    public string LevelsWorkset { get; set; } = "Shared Levels and Grids";
    public string DefaultWorkset { get; set; } = "Workset1";

    public sealed class WorksetRule
    {
        public string Name { get; set; }
        public List<string> Categories { get; set; } = new();
    }

    static List<WorksetRule> DefaultWorksets() => new()
    {
        new() { Name = "Structure - Rebar", Categories = { "OST_Rebar" } },
        new() { Name = "Structure - Steel", Categories = { "OST_StructConnections", "OST_StructConnectionPlates", "OST_StructConnectionBolts",
                                                           "OST_StructConnectionAnchors", "OST_StructConnectionWelds", "OST_Assemblies" } },
        new() { Name = "Structure - Concrete", Categories = { "OST_StructuralFoundation", "OST_Floors", "OST_Stairs", "OST_Ramps" } },
        new() { Name = "Architecture", Categories = { "OST_Walls", "OST_Doors", "OST_Roofs", "OST_Rooms", "OST_GenericModel", "OST_SpecialityEquipment" } },
        new() { Name = "Electrical", Categories = { "OST_LightingFixtures", "OST_ElectricalFixtures", "OST_ElectricalEquipment", "OST_CableTray",
                                                    "OST_SecurityDevices", "OST_CommunicationDevices" } },
        new() { Name = "Fire Protection", Categories = { "OST_Sprinklers", "OST_FireAlarmDevices", "OST_MechanicalEquipment" } },
        new() { Name = "Plumbing", Categories = { "OST_PipeCurves", "OST_PipeFitting", "OST_PlumbingFixtures" } },
    };

    internal string TemplateDir(Autodesk.Revit.ApplicationServices.Application app) =>
        string.IsNullOrWhiteSpace(FamilyTemplateDir) ? app.FamilyTemplatePath : FamilyTemplateDir;

    internal string Template(Autodesk.Revit.ApplicationServices.Application app, string file)
    {
        var path = Path.Combine(TemplateDir(app), file);
        if (!File.Exists(path)) throw new FileNotFoundException($"family template {file} not found in {TemplateDir(app)} (set familyTemplateDir in the config)", path);
        return path;
    }

    internal string PartDir() => string.IsNullOrWhiteSpace(PartFamilyDir) ? Path.Combine(Path.GetTempPath(), "IfcRebuild", "parts") : PartFamilyDir;

    internal string JobDir() => string.IsNullOrWhiteSpace(JobsDir) ? Path.Combine(Path.GetTempPath(), "IfcRebuild", "jobs") : JobsDir;
}
