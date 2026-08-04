using Autodesk.Revit.DB;
using RevitMCPCommandSet.Models.Common;

#if REVIT2023_OR_GREATER
using Autodesk.Revit.DB.Structure;
using RevitMCPCommandSet.Models.Structure;
#endif

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Shared helpers for the structural analysis command set (analytical model, loads,
    /// boundary conditions). The analytical members/panels API used here is the one
    /// Revit 2023 introduced when the analytical model was split from the physical model,
    /// so everything is compiled out for older configurations.
    /// </summary>
    public static class StructuralAnalysisUtils
    {
        /// <summary>
        /// Message returned by every analysis handler when the add-in runs on a Revit
        /// release that predates the standalone analytical model.
        /// </summary>
        public const string UnsupportedVersionMessage =
            "Structural analysis commands require Revit 2023 or later. Revit 2022 and earlier " +
            "derive the analytical model from physical elements, so it cannot be authored directly.";

        /// <summary>
        /// Millimetres per foot - Revit stores lengths in feet, the MCP tools speak millimetres.
        /// </summary>
        public const double MmPerFoot = 304.8;

        public static double MmToFeet(double millimetres) => millimetres / MmPerFoot;

        public static double FeetToMm(double feet) => feet * MmPerFoot;

        public static XYZ ToXYZ(JZPoint point) =>
            new XYZ(MmToFeet(point.X), MmToFeet(point.Y), MmToFeet(point.Z));

        public static JZPoint ToJZPoint(XYZ point) =>
            new JZPoint(FeetToMm(point.X), FeetToMm(point.Y), FeetToMm(point.Z));

#if REVIT2023_OR_GREATER

        // ---------------------------------------------------------------------
        // Unit conversion. The tools take engineering units (kN, kN*m, kN/m,
        // kN/m2) so callers never have to think about Revit's internal units.
        // ---------------------------------------------------------------------

        /// <summary>Force: kN -> internal.</summary>
        public static double ForceToInternal(double kiloNewtons) =>
            UnitUtils.ConvertToInternalUnits(kiloNewtons, UnitTypeId.Kilonewtons);

        /// <summary>Moment: kN*m -> internal.</summary>
        public static double MomentToInternal(double kiloNewtonMetres) =>
            UnitUtils.ConvertToInternalUnits(kiloNewtonMetres, UnitTypeId.KilonewtonMeters);

        /// <summary>Linear force: kN/m -> internal.</summary>
        public static double LinearForceToInternal(double kiloNewtonsPerMetre) =>
            UnitUtils.ConvertToInternalUnits(kiloNewtonsPerMetre, UnitTypeId.KilonewtonsPerMeter);

        /// <summary>Linear moment: kN*m/m -> internal.</summary>
        public static double LinearMomentToInternal(double kiloNewtonMetresPerMetre) =>
            UnitUtils.ConvertToInternalUnits(kiloNewtonMetresPerMetre, UnitTypeId.KilonewtonMetersPerMeter);

        /// <summary>Area force: kN/m2 -> internal.</summary>
        public static double AreaForceToInternal(double kiloNewtonsPerSquareMetre) =>
            UnitUtils.ConvertToInternalUnits(kiloNewtonsPerSquareMetre, UnitTypeId.KilonewtonsPerSquareMeter);

        /// <summary>Point spring stiffness, translation: kN/m -> internal.</summary>
        public static double PointTranslationSpringToInternal(double value) =>
            UnitUtils.ConvertToInternalUnits(value, UnitTypeId.KilonewtonsPerMeter);

        /// <summary>Point spring stiffness, rotation: kN*m/deg -> internal.</summary>
        public static double PointRotationSpringToInternal(double value) =>
            UnitUtils.ConvertToInternalUnits(value, UnitTypeId.KilonewtonMetersPerDegree);

        /// <summary>Line spring stiffness, translation: kN/m/m -> internal.</summary>
        public static double LineTranslationSpringToInternal(double value) =>
            UnitUtils.ConvertToInternalUnits(value, UnitTypeId.KilonewtonsPerSquareMeter);

        /// <summary>Line spring stiffness, rotation: kN*m/deg/m -> internal.</summary>
        public static double LineRotationSpringToInternal(double value) =>
            UnitUtils.ConvertToInternalUnits(value, UnitTypeId.KilonewtonMetersPerDegreePerMeter);

        /// <summary>Area spring stiffness, translation: kN/m/m2 -> internal.</summary>
        public static double AreaTranslationSpringToInternal(double value) =>
            UnitUtils.ConvertToInternalUnits(value, UnitTypeId.KilonewtonsPerCubicMeter);

        // ---------------------------------------------------------------------
        // Geometry
        // ---------------------------------------------------------------------

        /// <summary>
        /// Build a bound line from two millimetre points, rejecting degenerate input before
        /// Revit does so with a less helpful message.
        /// </summary>
        public static Line BuildLine(JZPoint start, JZPoint end)
        {
            if (start == null || end == null)
                throw new ArgumentException("Both start and end points are required");

            XYZ p0 = ToXYZ(start);
            XYZ p1 = ToXYZ(end);

            if (p0.DistanceTo(p1) < ShortCurveTolerance)
                throw new ArgumentException(
                    $"Start and end points are the same or closer than Revit's short-curve tolerance " +
                    $"({FeetToMm(ShortCurveTolerance):F2} mm)");

            return Line.CreateBound(p0, p1);
        }

        /// <summary>
        /// Revit rejects curves shorter than roughly 1/256 ft; keep the same threshold here so
        /// the failure is reported against the offending item instead of aborting the batch.
        /// </summary>
        public const double ShortCurveTolerance = 1.0 / 256.0;

        /// <summary>
        /// Build a closed curve loop from an ordered list of millimetre points. The loop is
        /// closed automatically when the last point does not repeat the first.
        /// </summary>
        public static CurveLoop BuildCurveLoop(List<JZPoint> boundary)
        {
            if (boundary == null || boundary.Count < 3)
                throw new ArgumentException("A boundary needs at least 3 points");

            var points = boundary.Select(ToXYZ).ToList();

            // Drop an explicit closing point - the loop is closed by the segment we append below.
            if (points.Count > 3 && points[0].DistanceTo(points[points.Count - 1]) < ShortCurveTolerance)
                points.RemoveAt(points.Count - 1);

            if (points.Count < 3)
                throw new ArgumentException("A boundary needs at least 3 distinct points");

            var loop = new CurveLoop();
            for (int i = 0; i < points.Count; i++)
            {
                XYZ from = points[i];
                XYZ to = points[(i + 1) % points.Count];

                if (from.DistanceTo(to) < ShortCurveTolerance)
                    throw new ArgumentException(
                        $"Boundary points {i} and {(i + 1) % points.Count} are coincident");

                loop.Append(Line.CreateBound(from, to));
            }

            return loop;
        }

        // ---------------------------------------------------------------------
        // Element lookup
        // ---------------------------------------------------------------------

        /// <summary>
        /// Resolve a load case by element id or by name. Returns null when neither is supplied
        /// so the caller can fall back to Revit's own default.
        /// </summary>
        public static LoadCase ResolveLoadCase(Document doc, long? loadCaseId, string loadCaseName)
        {
            if (loadCaseId.HasValue && loadCaseId.Value > 0)
            {
                var byId = doc.GetElement(NewElementId(loadCaseId.Value)) as LoadCase;
                if (byId == null)
                    throw new ArgumentException($"Element id {loadCaseId.Value} is not a load case");
                return byId;
            }

            if (string.IsNullOrWhiteSpace(loadCaseName))
                return null;

            var byName = new FilteredElementCollector(doc)
                .OfClass(typeof(LoadCase))
                .Cast<LoadCase>()
                .FirstOrDefault(lc => lc.Name.Equals(loadCaseName, StringComparison.OrdinalIgnoreCase));

            if (byName == null)
                throw new ArgumentException($"No load case named '{loadCaseName}' exists in the model");

            return byName;
        }

        /// <summary>
        /// Find a load symbol (PointLoadType / LineLoadType / AreaLoadType) by name, falling back
        /// to the first one in the model. Every structural template ships at least one.
        /// </summary>
        public static T ResolveLoadType<T>(Document doc, string typeName) where T : Element
        {
            var candidates = new FilteredElementCollector(doc)
                .OfClass(typeof(T))
                .Cast<T>()
                .ToList();

            if (candidates.Count == 0)
                throw new InvalidOperationException(
                    $"The model contains no {typeof(T).Name}. Load the structural analysis template " +
                    "or a family that provides one before creating loads.");

            if (string.IsNullOrWhiteSpace(typeName))
                return candidates[0];

            var match = candidates.FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (match == null)
                throw new ArgumentException(
                    $"No {typeof(T).Name} named '{typeName}'. Available: {string.Join(", ", candidates.Select(c => c.Name))}");

            return match;
        }

        /// <summary>
        /// Resolve a material by name. Returns ElementId.InvalidElementId when no name is given.
        /// </summary>
        public static ElementId ResolveMaterial(Document doc, string materialName)
        {
            if (string.IsNullOrWhiteSpace(materialName))
                return ElementId.InvalidElementId;

            var material = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .FirstOrDefault(m => m.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase));

            if (material == null)
                throw new ArgumentException($"No material named '{materialName}' exists in the model");

            return material.Id;
        }

        /// <summary>
        /// Resolve the host element a load or boundary condition attaches to. Loads accept
        /// InvalidElementId for a free-standing (work-plane based) load, so a missing id is
        /// not an error here.
        /// </summary>
        public static ElementId ResolveHostId(Document doc, long? hostElementId)
        {
            if (!hostElementId.HasValue || hostElementId.Value <= 0)
                return ElementId.InvalidElementId;

            ElementId id = NewElementId(hostElementId.Value);
            if (doc.GetElement(id) == null)
                throw new ArgumentException($"Host element id {hostElementId.Value} does not exist");

            return id;
        }

        /// <summary>
        /// Find the analytical hub closest to a point, within the given tolerance in millimetres.
        /// Hubs are created by Revit at analytical element ends; analytical links join two of them.
        /// </summary>
        public static Hub FindHubNear(Document doc, JZPoint point, double toleranceMm)
        {
            XYZ target = ToXYZ(point);
            double toleranceFeet = MmToFeet(toleranceMm);

            Hub best = null;
            double bestDistance = double.MaxValue;

            foreach (var hub in new FilteredElementCollector(doc).OfClass(typeof(Hub)).Cast<Hub>())
            {
                if (!hub.HasOrigin())
                    continue;

                double distance = hub.GetOrigin().DistanceTo(target);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = hub;
                }
            }

            return bestDistance <= toleranceFeet ? best : null;
        }

        /// <summary>
        /// Apply the settings every load shares - load case, orientation, reaction flag - and
        /// return a note describing anything that could not be applied. Revit rejects some
        /// orientations depending on how the load is hosted, and that is worth reporting
        /// without failing the load itself.
        /// </summary>
        public static string ApplyCommonLoadSettings(Document doc, LoadBase load, LoadItemBase item)
        {
            var notes = new List<string>();

            var loadCase = ResolveLoadCase(doc, item.LoadCaseId, item.LoadCaseName);
            if (loadCase != null)
                load.LoadCaseId = loadCase.Id;

            if (!string.IsNullOrWhiteSpace(item.OrientTo))
            {
                var orientTo = ParseEnum(item.OrientTo, LoadOrientTo.Project);

                if (load.IsOrientToPermitted(orientTo))
                    load.OrientTo = orientTo;
                else
                    notes.Add($"orientTo '{item.OrientTo}' is not permitted for this load; kept {load.OrientTo}");
            }

            if (item.IsReaction)
                load.IsReaction = true;

            return notes.Count == 0 ? null : string.Join("; ", notes);
        }

        /// <summary>
        /// Parse an enum value from its API name, listing the valid names when it does not match.
        /// </summary>
        public static TEnum ParseEnum<TEnum>(string value, TEnum fallback) where TEnum : struct, Enum
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            if (Enum.TryParse(value, true, out TEnum parsed) && Enum.IsDefined(typeof(TEnum), parsed))
                return parsed;

            throw new ArgumentException(
                $"'{value}' is not a valid {typeof(TEnum).Name}. Valid values: {string.Join(", ", Enum.GetNames(typeof(TEnum)))}");
        }

#endif

        /// <summary>
        /// Construct an ElementId from a numeric id across the 2024 signature change.
        /// </summary>
        public static ElementId NewElementId(long value)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(value);
#else
            return new ElementId((int)value);
#endif
        }
    }
}
