using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Models.Structure;

#if REVIT2023_OR_GREATER
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitMCPCommandSet.Utils;
#endif

namespace RevitMCPCommandSet.Services.Structure
{
    /// <summary>
    /// Creates line loads (distributed forces and moments along a line).
    /// </summary>
    public class CreateLineLoadEventHandler
        : StructuralCreationEventHandlerBase<LineLoadCreationInfo>
    {
        public override string GetName() => "Create Line Load";

#if REVIT2023_OR_GREATER
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

            var load = LineLoad.Create(
                doc,
                hostId,
                curve,
                ToLinearForce(item.Force),
                ToLinearMoment(item.Moment),
                symbol);

            var notes = new List<string>();

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
#endif
    }
}
