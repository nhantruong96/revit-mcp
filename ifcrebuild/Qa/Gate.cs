using Autodesk.Revit.DB;

namespace IfcRebuild;

/// <summary>Hard gate before every save: no DirectShape, no import instance, no in-place family.</summary>
internal static class Gate
{
    public static string Run(Document doc)
    {
        int ds = new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).GetElementCount();
        int imp = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).GetElementCount();
        int inPlace = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
            .Count(fi => fi.Symbol?.Family?.IsInPlace == true);
        var gate = ds == 0 && imp == 0 && inPlace == 0 ? "PASS" : "FAIL";
        return $"GATE {gate}: DirectShape={ds} ImportInstance={imp} InPlace={inPlace}";
    }
}
