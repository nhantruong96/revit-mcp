using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Models.Structure;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Structure
{
    /// <summary>
    /// Creates analytical links between two analytical hubs. Hubs are the connection points
    /// Revit maintains at the ends of analytical elements, so a link can only be made where
    /// analytical geometry already exists.
    /// </summary>
    public class CreateAnalyticalLinkEventHandler
        : StructuralCreationEventHandlerBase<AnalyticalLinkCreationInfo>
    {
        public override string GetName() => "Create Analytical Link";

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

            return CreateBatch("Create Analytical Links", "AnalyticalLink", Parameters.Data, CreateLink);
        }

        private (Element, string) CreateLink(AnalyticalLinkItem item)
        {
            ElementId startHubId = ResolveHub(item.StartHubId, item.StartPoint, item.SearchToleranceMm, "start");
            ElementId endHubId = ResolveHub(item.EndHubId, item.EndPoint, item.SearchToleranceMm, "end");

            if (startHubId == endHubId)
                throw new ArgumentException("The start and end hub are the same element");

            ElementId linkTypeId = ResolveLinkType(item);

            var link = AnalyticalLink.Create(doc, linkTypeId, startHubId, endHubId);

            return (link, null);
        }

        /// <summary>
        /// Take the hub id as given, or find the hub sitting at the supplied point.
        /// </summary>
        private ElementId ResolveHub(long? hubId, JZPoint point, double toleranceMm, string end)
        {
            if (hubId.HasValue && hubId.Value > 0)
            {
                ElementId id = StructuralAnalysisUtils.NewElementId(hubId.Value);

                if (!AnalyticalLink.IsValidHub(doc, id))
                    throw new ArgumentException($"Element id {hubId.Value} is not a valid analytical hub for the {end} of the link");

                return id;
            }

            double tolerance = toleranceMm > 0 ? toleranceMm : 25.0;
            var hub = StructuralAnalysisUtils.FindHubNear(doc, point, tolerance);

            if (hub == null)
                throw new ArgumentException(
                    $"No analytical hub found within {tolerance:F0} mm of the {end} point. " +
                    "Links connect existing analytical elements - create the analytical members first, " +
                    "or pass an explicit hub id from get_structural_analysis_info.");

            if (!AnalyticalLink.IsValidHub(doc, hub.Id))
                throw new ArgumentException($"The hub found at the {end} point cannot be linked");

            return hub.Id;
        }

        /// <summary>
        /// Resolve the analytical link type by id or name, defaulting to the first one loaded.
        /// </summary>
        private ElementId ResolveLinkType(AnalyticalLinkItem item)
        {
            if (item.LinkTypeId.HasValue && item.LinkTypeId.Value > 0)
            {
                ElementId id = StructuralAnalysisUtils.NewElementId(item.LinkTypeId.Value);

                if (doc.GetElement(id) is not AnalyticalLinkType)
                    throw new ArgumentException($"Element id {item.LinkTypeId.Value} is not an AnalyticalLinkType");

                return id;
            }

            return StructuralAnalysisUtils.ResolveLoadType<AnalyticalLinkType>(doc, item.LinkTypeName).Id;
        }
    }
}
