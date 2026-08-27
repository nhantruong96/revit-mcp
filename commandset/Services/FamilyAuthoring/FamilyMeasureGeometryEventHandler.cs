using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.FamilyAuthoring;

/// <summary>
/// Measures the solid geometry in a family document: volume, surface area and bounding box,
/// per element and in total.
///
/// This is what turns "the extrusion looks right" into something checkable. A family whose
/// parameters all read back correctly can still be built on a sketch that locked to the
/// wrong plane, and the cheapest way to catch it is to compare the volume Revit reports
/// against the volume the shape is supposed to have.
///
/// Volumes come back in cubic millimetres and lengths in millimetres, so the comparison can
/// be done directly against a hand calculation without converting anything.
/// </summary>
public class FamilyMeasureGeometryEventHandler : FamilyEventHandlerBase
{
    public override string GetName() => "Family Measure Geometry";

    protected override object Run(UIApplication app)
    {
        Document document = FamilyDocumentRegistry.Resolve(Text("documentHandle"), app);

        string detailText = Text("detailLevel", "Fine");
        ViewDetailLevel detail = Enum.TryParse(detailText, true, out ViewDetailLevel parsed)
            ? parsed
            : ViewDetailLevel.Fine;

        var options = new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = false,
            DetailLevel = detail,
        };

        string categoryFilter = Text("categoryContains");
        bool summaryOnly = Flag("summaryOnly", false);
        int maxElements = Parameters?["maxElements"]?.ToObject<int>() ?? 50;
        if (maxElements < 1) maxElements = 1;

        var elements = new List<object>();
        double totalVolume = 0;
        double totalArea = 0;
        int solidCount = 0;
        int measuredCount = 0;
        int categoryFilterSkipped = 0;
        int elementsWithoutCategory = 0;

        var collector = new FilteredElementCollector(document)
            .WhereElementIsNotElementType();

        foreach (Element element in collector)
        {
            string categoryName = element.Category?.Name;
            if (categoryName == null) elementsWithoutCategory++;

            if (!string.IsNullOrWhiteSpace(categoryFilter)
                && (categoryName == null
                    || categoryName.IndexOf(categoryFilter, StringComparison.OrdinalIgnoreCase) < 0))
            {
                categoryFilterSkipped++;
                continue;
            }

            GeometryElement geometry;
            try
            {
                geometry = element.get_Geometry(options);
            }
            catch
            {
                // Plenty of things in a family document have no geometry to speak of -
                // reference planes, dimensions, the family manager's own bookkeeping.
                continue;
            }

            if (geometry == null) continue;

            double volume = 0;
            double area = 0;
            int solids = 0;
            Accumulate(geometry, ref volume, ref area, ref solids);

            if (solids == 0) continue;

            totalVolume += volume;
            totalArea += area;
            solidCount += solids;
            measuredCount++;

            // A long array turns the per-element list into thousands of lines of noise, so
            // the caller can ask for the totals alone or cap how many rows come back.
            if (summaryOnly || elements.Count >= maxElements) continue;

            elements.Add(new
            {
                elementId = element.Id.ToString(),
                name = element.Name,
                category = categoryName,
                subcategory = ReadSubcategory(element),
                isVisible = ReadVisibility(element),
                shownAtDetailLevel = ReadDetailLevelVisibility(element),
                solidCount = solids,
                volumeMm3 = FamilyTypeIdResolver.InternalVolumeToMm3(volume),
                surfaceAreaMm2 = area * 304.8 * 304.8,
                boundingBoxMm = Describe(element.get_BoundingBox(null)),
            });
        }

        // A category filter that matched nothing deserves an explanation rather than a bare
        // zero: inside a family document the forms carry no category at all, so filtering by
        // one silently discards everything and looks exactly like an empty family.
        string filterNote = null;
        if (!string.IsNullOrWhiteSpace(categoryFilter) && measuredCount == 0 && categoryFilterSkipped > 0)
        {
            filterNote = elementsWithoutCategory > 0
                ? $"categoryContains='{categoryFilter}' discarded every element, and {elementsWithoutCategory} of them have no category at all - which is normal inside a family document. Drop the filter."
                : $"categoryContains='{categoryFilter}' matched no element.";
        }

        return new
        {
            success = true,
            title = document.Title,
            detailLevel = detail.ToString(),
            elementCount = measuredCount,
            solidCount,
            totalVolumeMm3 = FamilyTypeIdResolver.InternalVolumeToMm3(totalVolume),
            totalSurfaceAreaMm2 = totalArea * 304.8 * 304.8,
            listedCount = elements.Count,
            truncated = !summaryOnly && measuredCount > elements.Count,
            truncatedNote = (!summaryOnly && measuredCount > elements.Count)
                ? $"Listing capped at maxElements={maxElements}; the totals above still cover all {measuredCount} elements."
                : null,
            filterNote,
            elements = summaryOnly ? null : elements,
        };
    }

    /// <summary>
    /// The family's own coarse/medium/fine switches for this form.
    ///
    /// Worth reporting separately from the volume: geometry read back at a given detail level
    /// does NOT respect these switches, so a form flagged coarse-only still measures at fine.
    /// Without these three flags there is no way to tell a coarse stand-in from a solid that
    /// is about to double-count material in a schedule.
    /// </summary>
    private static object ReadDetailLevelVisibility(Element element)
    {
        try
        {
            if (element is not GenericForm form) return null;

            FamilyElementVisibility visibility = form.GetVisibility();
            if (visibility == null) return null;

            return new
            {
                coarse = visibility.IsShownInCoarse,
                medium = visibility.IsShownInMedium,
                fine = visibility.IsShownInFine,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Walks instances as well as plain solids, since nested content arrives wrapped.</summary>
    private static void Accumulate(GeometryElement geometry, ref double volume, ref double area, ref int solids)
    {
        foreach (GeometryObject item in geometry)
        {
            switch (item)
            {
                case Solid solid when solid.Volume > 1e-9:
                    volume += solid.Volume;
                    area += solid.SurfaceArea;
                    solids++;
                    break;

                case GeometryInstance instance:
                    GeometryElement nested = instance.GetInstanceGeometry();
                    if (nested != null) Accumulate(nested, ref volume, ref area, ref solids);
                    break;
            }
        }
    }

    private static object Describe(BoundingBoxXYZ box)
    {
        if (box == null) return null;

        return new
        {
            minMm = new
            {
                x = FamilyTypeIdResolver.InternalToMm(box.Min.X),
                y = FamilyTypeIdResolver.InternalToMm(box.Min.Y),
                z = FamilyTypeIdResolver.InternalToMm(box.Min.Z),
            },
            maxMm = new
            {
                x = FamilyTypeIdResolver.InternalToMm(box.Max.X),
                y = FamilyTypeIdResolver.InternalToMm(box.Max.Y),
                z = FamilyTypeIdResolver.InternalToMm(box.Max.Z),
            },
            sizeMm = new
            {
                x = FamilyTypeIdResolver.InternalToMm(box.Max.X - box.Min.X),
                y = FamilyTypeIdResolver.InternalToMm(box.Max.Y - box.Min.Y),
                z = FamilyTypeIdResolver.InternalToMm(box.Max.Z - box.Min.Z),
            },
        };
    }

    private static string ReadSubcategory(Element element)
    {
        try
        {
            if (element is GenericForm form) return form.Subcategory?.Name;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool? ReadVisibility(Element element)
    {
        try
        {
            Parameter visible = element.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
            return visible?.AsInteger() == 1;
        }
        catch
        {
            return null;
        }
    }
}
