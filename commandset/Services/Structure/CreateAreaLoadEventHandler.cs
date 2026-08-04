using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Models.Structure;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Structure
{
    /// <summary>
    /// Creates area loads (pressures over a closed outline).
    /// </summary>
    public class CreateAreaLoadEventHandler
        : StructuralCreationEventHandlerBase<AreaLoadCreationInfo>
    {
        public override string GetName() => "Create Area Load";

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

#if REVIT2024_OR_GREATER
            // AreCurveLoopsValid only exists from Revit 2024; before that a bad outline is
            // caught by Create itself and reported per item like any other failure.
            if (!AreaLoad.AreCurveLoopsValid(loops))
                throw new ArgumentException(
                    "Revit rejected the boundary; an area load needs a closed, planar, non-self-intersecting outline");
#endif

            XYZ force = new XYZ(
                StructuralAnalysisUtils.AreaForceToInternal(item.Force.X),
                StructuralAnalysisUtils.AreaForceToInternal(item.Force.Y),
                StructuralAnalysisUtils.AreaForceToInternal(item.Force.Z));

            var notes = new List<string>();
            AreaLoad load = Place(hostId, loops, force, symbol, notes);

            string common = StructuralAnalysisUtils.ApplyCommonLoadSettings(doc, load, item);
            if (!string.IsNullOrEmpty(common))
                notes.Add(common);

            if (item.IsProjected)
                load.IsProjected = true;

            return (load, notes.Count == 0 ? null : string.Join("; ", notes));
        }

        /// <summary>
        /// Revit 2024 introduced the overload that takes a host and explicit loops together.
        /// On 2023 a hosted load covers the whole host, while a free-standing one still keeps
        /// the outline it was given.
        /// </summary>
        private AreaLoad Place(
            ElementId hostId, List<CurveLoop> loops, XYZ force, AreaLoadType symbol, List<string> notes)
        {
#if REVIT2024_OR_GREATER
            return AreaLoad.Create(doc, hostId, loops, force, symbol);
#else
            if (hostId == ElementId.InvalidElementId)
                return AreaLoad.Create(doc, loops, force, symbol);

            notes.Add("this Revit version spreads hosted area loads over the whole host, so boundary was ignored");
            return AreaLoad.Create(doc, hostId, force, symbol);
#endif
        }
    }
}
