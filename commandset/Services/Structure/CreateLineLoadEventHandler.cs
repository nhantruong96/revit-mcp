using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Models.Structure;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Structure
{
    /// <summary>
    /// Creates line loads (distributed forces and moments along a line).
    /// </summary>
    public class CreateLineLoadEventHandler
        : StructuralCreationEventHandlerBase<LineLoadCreationInfo>
    {
        public override string GetName() => "Create Line Load";

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

            return CreateBatch("Create Line Loads", "LineLoad", Parameters.Data, CreateLineLoad);
        }

        private (Element, string) CreateLineLoad(LineLoadItem item)
        {
            var symbol = StructuralAnalysisUtils.ResolveLoadType<LineLoadType>(doc, item.LoadTypeName);
            ElementId hostId = StructuralAnalysisUtils.ResolveHostId(doc, item.HostElementId);

            if (hostId != ElementId.InvalidElementId && !LineLoad.IsValidHostId(doc, hostId))
                throw new ArgumentException($"Element {item.HostElementId} cannot host a line load");

            Line curve = StructuralAnalysisUtils.BuildLine(item.StartPoint, item.EndPoint);
            var notes = new List<string>();

            LineLoad load = Place(hostId, curve, ToLinearForce(item.Force), ToLinearMoment(item.Moment), symbol, notes);

            string common = StructuralAnalysisUtils.ApplyCommonLoadSettings(doc, load, item);
            if (!string.IsNullOrEmpty(common))
                notes.Add(common);

            if (item.IsProjected)
                load.IsProjected = true;

            // A second vector turns the load trapezoidal. Revit only accepts it on a
            // non-uniform load, so report rather than fail when it is refused.
            if (item.ForceEnd != null || item.MomentEnd != null)
            {
                try
                {
                    if (item.ForceEnd != null)
                        load.ForceVector2 = ToLinearForce(item.ForceEnd);

                    if (item.MomentEnd != null)
                        load.MomentVector2 = ToLinearMoment(item.MomentEnd);
                }
                catch (Exception ex)
                {
                    notes.Add($"the load was created as uniform - Revit rejected the end vector: {ex.Message}");
                }
            }

            return (load, notes.Count == 0 ? null : string.Join("; ", notes));
        }

        /// <summary>
        /// Revit 2024 introduced the overload that takes a host and an explicit curve together.
        /// On 2023 a hosted load spans the whole host, and a free-standing one needs a sketch
        /// plane containing the line.
        /// </summary>
        private LineLoad Place(
            ElementId hostId, Line curve, XYZ force, XYZ moment, LineLoadType symbol, List<string> notes)
        {
#if REVIT2024_OR_GREATER
            return LineLoad.Create(doc, hostId, curve, force, moment, symbol);
#else
            if (hostId == ElementId.InvalidElementId)
                return LineLoad.Create(
                    doc, curve.GetEndPoint(0), curve.GetEndPoint(1), force, moment, symbol,
                    StructuralAnalysisUtils.CreateSketchPlaneContaining(doc, curve.GetEndPoint(0), curve.GetEndPoint(1)));

            notes.Add("this Revit version spans hosted line loads across the whole host, so startPoint/endPoint were ignored");
            return LineLoad.Create(doc, hostId, force, moment, symbol);
#endif
        }

        /// <summary>kN/m -> internal linear force vector.</summary>
        private static XYZ ToLinearForce(LoadVector vector)
        {
            if (vector == null) return XYZ.Zero;

            return new XYZ(
                StructuralAnalysisUtils.LinearForceToInternal(vector.X),
                StructuralAnalysisUtils.LinearForceToInternal(vector.Y),
                StructuralAnalysisUtils.LinearForceToInternal(vector.Z));
        }

        /// <summary>kN*m/m -> internal linear moment vector.</summary>
        private static XYZ ToLinearMoment(LoadVector vector)
        {
            if (vector == null) return XYZ.Zero;

            return new XYZ(
                StructuralAnalysisUtils.LinearMomentToInternal(vector.X),
                StructuralAnalysisUtils.LinearMomentToInternal(vector.Y),
                StructuralAnalysisUtils.LinearMomentToInternal(vector.Z));
        }
    }
}
