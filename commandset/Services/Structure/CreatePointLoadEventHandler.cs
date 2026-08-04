using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Models.Structure;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Structure
{
    /// <summary>
    /// Creates point loads (concentrated forces and moments).
    /// </summary>
    public class CreatePointLoadEventHandler
        : StructuralCreationEventHandlerBase<PointLoadCreationInfo>
    {
        public override string GetName() => "Create Point Load";

        protected override AIResult<List<StructuralCreationResult>> Run()
        {
            if (!Parameters.Validate(out string validationError))
            {
                return new AIResult<List<StructuralCreationResult>>
                {
                    Success = false,
                    Message = $"Validation failed: {validationError}",
                    Response = null
                };
            }

            return CreateBatch("Create Point Loads", "PointLoad", Parameters.Data, CreatePointLoad);
        }

        private (Element, string) CreatePointLoad(PointLoadItem item)
        {
            var symbol = StructuralAnalysisUtils.ResolveLoadType<PointLoadType>(doc, item.LoadTypeName);
            ElementId hostId = StructuralAnalysisUtils.ResolveHostId(doc, item.HostElementId);

            if (hostId != ElementId.InvalidElementId && !PointLoad.IsValidHostId(doc, hostId))
                throw new ArgumentException($"Element {item.HostElementId} cannot host a point load");

            XYZ point = StructuralAnalysisUtils.ToXYZ(item.Point);
            XYZ force = ToForce(item.Force);
            XYZ moment = ToMoment(item.Moment);
            var notes = new List<string>();

            PointLoad load = Place(item, hostId, point, force, moment, symbol, notes);

            string common = StructuralAnalysisUtils.ApplyCommonLoadSettings(doc, load, item);
            if (!string.IsNullOrEmpty(common))
                notes.Add(common);

            return (load, notes.Count == 0 ? null : string.Join("; ", notes));
        }

        /// <summary>
        /// Revit 2024 introduced the overload that takes a host and an explicit point together.
        /// On 2023 a hosted load can only be anchored to an end of the host, and a free-standing
        /// one needs a sketch plane, so both cases are handled separately there.
        /// </summary>
        private PointLoad Place(
            PointLoadItem item, ElementId hostId, XYZ point, XYZ force, XYZ moment, PointLoadType symbol,
            List<string> notes)
        {
#if REVIT2024_OR_GREATER
            return PointLoad.Create(doc, hostId, point, force, moment, symbol);
#else
            if (hostId == ElementId.InvalidElementId)
                return PointLoad.Create(
                    doc, point, force, moment, symbol,
                    StructuralAnalysisUtils.CreateSketchPlaneThrough(doc, point));

            var selector = NearestEndSelector(hostId, point, notes);
            return PointLoad.Create(doc, hostId, selector, force, moment, symbol);
#endif
        }

#if !REVIT2024_OR_GREATER
        /// <summary>
        /// Pick the end of the host nearest the requested point, and say how far the load moved.
        /// </summary>
        private AnalyticalElementSelector NearestEndSelector(ElementId hostId, XYZ point, List<string> notes)
        {
            if (doc.GetElement(hostId) is not AnalyticalElement host || !host.IsSingleCurve()
                || host.GetCurve() is not Curve curve)
            {
                notes.Add("this Revit version cannot place a hosted point load at an arbitrary point; used the whole host");
                return AnalyticalElementSelector.Whole;
            }

            bool atStart = curve.GetEndPoint(0).DistanceTo(point) <= curve.GetEndPoint(1).DistanceTo(point);
            XYZ placed = curve.GetEndPoint(atStart ? 0 : 1);

            if (placed.DistanceTo(point) > StructuralAnalysisUtils.ShortCurveTolerance)
            {
                notes.Add(
                    "this Revit version anchors hosted point loads to a member end, so the load was placed at the " +
                    $"{(atStart ? "start" : "end")} of the host " +
                    $"({StructuralAnalysisUtils.FeetToMm(placed.DistanceTo(point)):F0} mm from the requested point)");
            }

            return atStart ? AnalyticalElementSelector.StartOrBase : AnalyticalElementSelector.EndOrTop;
        }
#endif

        /// <summary>kN -> internal force vector.</summary>
        private static XYZ ToForce(LoadVector vector)
        {
            if (vector == null) return XYZ.Zero;

            return new XYZ(
                StructuralAnalysisUtils.ForceToInternal(vector.X),
                StructuralAnalysisUtils.ForceToInternal(vector.Y),
                StructuralAnalysisUtils.ForceToInternal(vector.Z));
        }

        /// <summary>kN*m -> internal moment vector.</summary>
        private static XYZ ToMoment(LoadVector vector)
        {
            if (vector == null) return XYZ.Zero;

            return new XYZ(
                StructuralAnalysisUtils.MomentToInternal(vector.X),
                StructuralAnalysisUtils.MomentToInternal(vector.Y),
                StructuralAnalysisUtils.MomentToInternal(vector.Z));
        }
    }
}
