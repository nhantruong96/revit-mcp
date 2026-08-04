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
    /// Creates point loads (concentrated forces and moments).
    /// </summary>
    public class CreatePointLoadEventHandler
        : StructuralCreationEventHandlerBase<PointLoadCreationInfo>
    {
        public override string GetName() => "Create Point Load";

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

            var load = PointLoad.Create(doc, hostId, point, force, moment, symbol);
            string note = StructuralAnalysisUtils.ApplyCommonLoadSettings(doc, load, item);

            return (load, note);
        }

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
#endif
    }
}
