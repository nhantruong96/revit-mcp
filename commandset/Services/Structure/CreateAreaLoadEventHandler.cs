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
    /// Creates area loads (pressures over a closed outline).
    /// </summary>
    public class CreateAreaLoadEventHandler
        : StructuralCreationEventHandlerBase<AreaLoadCreationInfo>
    {
        public override string GetName() => "Create Area Load";

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

            return CreateBatch("Create Area Loads", "AreaLoad", Parameters.Data, CreateAreaLoad);
        }

        private (Element, string) CreateAreaLoad(AreaLoadItem item)
        {
            var symbol = StructuralAnalysisUtils.ResolveLoadType<AreaLoadType>(doc, item.LoadTypeName);
            ElementId hostId = StructuralAnalysisUtils.ResolveHostId(doc, item.HostElementId);

            if (hostId != ElementId.InvalidElementId && !AreaLoad.IsValidHostId(doc, hostId))
                throw new ArgumentException($"Element {item.HostElementId} cannot host an area load");

            var loops = new List<CurveLoop> { StructuralAnalysisUtils.BuildCurveLoop(item.Boundary) };

            if (!AreaLoad.AreCurveLoopsValid(loops))
                throw new ArgumentException(
                    "Revit rejected the boundary; an area load needs a closed, planar, non-self-intersecting outline");

            XYZ force = new XYZ(
                StructuralAnalysisUtils.AreaForceToInternal(item.Force.X),
                StructuralAnalysisUtils.AreaForceToInternal(item.Force.Y),
                StructuralAnalysisUtils.AreaForceToInternal(item.Force.Z));

            var load = AreaLoad.Create(doc, hostId, loops, force, symbol);

            string note = StructuralAnalysisUtils.ApplyCommonLoadSettings(doc, load, item);

            if (item.IsProjected)
                load.IsProjected = true;

            return (load, note);
        }
#endif
    }
}
