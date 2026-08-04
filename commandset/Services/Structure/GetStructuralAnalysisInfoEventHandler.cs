using Newtonsoft.Json;
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
    /// Reads the analysis side of the model: analytical elements, hubs, supports, load
    /// cases and combinations, and the load symbols available. The other analysis commands
    /// take element ids, and this is where a caller gets them.
    /// </summary>
    public class GetStructuralAnalysisInfoEventHandler
        : StructuralAnalysisEventHandlerBase<StructuralAnalysisQueryInfo, StructuralAnalysisInfo>
    {
        public override string GetName() => "Get Structural Analysis Info";

#if REVIT2023_OR_GREATER
        protected override AIResult<StructuralAnalysisInfo> Run()
        {
            int limit = Parameters.Limit > 0 ? Parameters.Limit : 200;
            var info = new StructuralAnalysisInfo();

            if (Parameters.Wants("analyticalMembers"))
                info.AnalyticalMembers = Collect<AnalyticalMember>(limit, DescribeMember);

            if (Parameters.Wants("analyticalPanels"))
                info.AnalyticalPanels = Collect<AnalyticalPanel>(limit, DescribePanel);

            if (Parameters.Wants("analyticalLinks"))
                info.AnalyticalLinks = Collect<AnalyticalLink>(limit, DescribeLink);

            if (Parameters.Wants("hubs"))
                info.Hubs = Collect<Hub>(limit, DescribeHub);

            if (Parameters.Wants("boundaryConditions"))
                info.BoundaryConditions = Collect<BoundaryConditions>(limit, DescribeBoundaryCondition);

            if (Parameters.Wants("loadNatures"))
                info.LoadNatures = Collect<LoadNature>(limit, n => new AnalysisEntry { ElementId = n.Id.GetValue(), Name = n.Name });

            if (Parameters.Wants("loadCases"))
                info.LoadCases = Collect<LoadCase>(limit, DescribeLoadCase);

            if (Parameters.Wants("loadCombinations"))
                info.LoadCombinations = Collect<LoadCombination>(limit, DescribeCombination);

            if (Parameters.Wants("loadUsages"))
                info.LoadUsages = Collect<LoadUsage>(limit, u => new AnalysisEntry { ElementId = u.Id.GetValue(), Name = u.Name });

            if (Parameters.Wants("loadTypes"))
            {
                info.PointLoadTypes = Collect<PointLoadType>(limit, DescribeType);
                info.LineLoadTypes = Collect<LineLoadType>(limit, DescribeType);
                info.AreaLoadTypes = Collect<AreaLoadType>(limit, DescribeType);
                info.AnalyticalLinkTypes = Collect<AnalyticalLinkType>(limit, DescribeType);
            }

            if (Parameters.Wants("loads"))
            {
                var loads = new List<AnalysisEntry>();
                loads.AddRange(Collect<PointLoad>(limit, l => DescribeLoad(l, "PointLoad")));
                loads.AddRange(Collect<LineLoad>(limit, l => DescribeLoad(l, "LineLoad")));
                loads.AddRange(Collect<AreaLoad>(limit, l => DescribeLoad(l, "AreaLoad")));
                info.Loads = loads;
            }

            return new AIResult<StructuralAnalysisInfo>
            {
                Success = true,
                Message = $"Read the analytical model of '{doc.Title}'",
                Response = info
            };
        }

        /// <summary>
        /// Collect up to <paramref name="limit" /> elements of a class and project them
        /// into a report entry.
        /// </summary>
        private List<AnalysisEntry> Collect<T>(int limit, Func<T, AnalysisEntry> describe) where T : Element
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(T))
                .Cast<T>()
                .Take(limit)
                .Select(describe)
                .ToList();
        }

        private AnalysisEntry DescribeMember(AnalyticalMember member)
        {
            var entry = new AnalysisEntry
            {
                ElementId = member.Id.GetValue(),
                Kind = "AnalyticalMember",
                StructuralRole = member.StructuralRole.ToString(),
                AnalyzeAs = member.AnalyzeAs.ToString(),
                Material = NameOf(member.MaterialId),
                SectionType = NameOf(member.SectionTypeId)
            };

            if (member.GetCurve() is Curve curve && curve.IsBound)
            {
                entry.Start = StructuralAnalysisUtils.ToJZPoint(curve.GetEndPoint(0));
                entry.End = StructuralAnalysisUtils.ToJZPoint(curve.GetEndPoint(1));
            }

            return entry;
        }

        private AnalysisEntry DescribePanel(AnalyticalPanel panel)
        {
            return new AnalysisEntry
            {
                ElementId = panel.Id.GetValue(),
                Kind = "AnalyticalPanel",
                StructuralRole = panel.StructuralRole.ToString(),
                AnalyzeAs = panel.AnalyzeAs.ToString(),
                Material = NameOf(panel.MaterialId),
                ThicknessMm = StructuralAnalysisUtils.FeetToMm(panel.Thickness)
            };
        }

        private AnalysisEntry DescribeLink(AnalyticalLink link)
        {
            return new AnalysisEntry
            {
                ElementId = link.Id.GetValue(),
                Kind = "AnalyticalLink",
                Name = NameOf(link.GetTypeId()),
                Start = link.Start == null ? null : StructuralAnalysisUtils.ToJZPoint(link.Start),
                End = link.End == null ? null : StructuralAnalysisUtils.ToJZPoint(link.End),
                StartHubId = link.StartHubId.GetValue(),
                EndHubId = link.EndHubId.GetValue()
            };
        }

        private AnalysisEntry DescribeHub(Hub hub)
        {
            return new AnalysisEntry
            {
                ElementId = hub.Id.GetValue(),
                Kind = "Hub",
                Origin = hub.HasOrigin() ? StructuralAnalysisUtils.ToJZPoint(hub.GetOrigin()) : null
            };
        }

        private AnalysisEntry DescribeBoundaryCondition(BoundaryConditions conditions)
        {
            return new AnalysisEntry
            {
                ElementId = conditions.Id.GetValue(),
                Kind = "BoundaryConditions",
                ConditionType = conditions.GetBoundaryConditionsType().ToString(),
                Preset = conditions.Preset.ToString(),
                HostElementId = conditions.HostElementId.GetValue()
            };
        }

        private AnalysisEntry DescribeLoadCase(LoadCase loadCase)
        {
            return new AnalysisEntry
            {
                ElementId = loadCase.Id.GetValue(),
                Kind = "LoadCase",
                Name = loadCase.Name,
                Number = loadCase.Number,
                Nature = NameOf(loadCase.NatureId),
                Category = Autodesk.Revit.DB.Category.GetCategory(doc, loadCase.SubcategoryId)?.Name
            };
        }

        private AnalysisEntry DescribeCombination(LoadCombination combination)
        {
            return new AnalysisEntry
            {
                ElementId = combination.Id.GetValue(),
                Kind = "LoadCombination",
                Name = combination.Name,
                CombinationType = combination.Type.ToString(),
                State = combination.State.ToString(),
                Components = combination.GetComponents()
                    .Select(c => new AnalysisComponentEntry
                    {
                        ElementId = c.LoadCaseOrCombinationId.GetValue(),
                        Name = NameOf(c.LoadCaseOrCombinationId),
                        Factor = c.Factor
                    })
                    .ToList()
            };
        }

        private AnalysisEntry DescribeType(Element type)
        {
            return new AnalysisEntry
            {
                ElementId = type.Id.GetValue(),
                Kind = type.GetType().Name,
                Name = type.Name
            };
        }

        private AnalysisEntry DescribeLoad(LoadBase load, string kind)
        {
            return new AnalysisEntry
            {
                ElementId = load.Id.GetValue(),
                Kind = kind,
                LoadCase = load.LoadCaseName,
                Nature = load.LoadNatureName,
                HostElementId = load.HostElementId == null ? null : load.HostElementId.GetValue()
            };
        }

        /// <summary>
        /// Name of the element an id points at, or null when the id is unset or dangling.
        /// </summary>
        private string NameOf(ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId)
                return null;

            try
            {
                return doc.GetElement(id)?.Name;
            }
            catch
            {
                return null;
            }
        }
#endif
    }

    /// <summary>
    /// Inventory of the analytical model. Sections the caller did not ask for stay null
    /// and are dropped from the JSON.
    /// </summary>
    public class StructuralAnalysisInfo
    {
        [JsonProperty("analyticalMembers", NullValueHandling = NullValueHandling.Ignore)]
        public List<AnalysisEntry> AnalyticalMembers { get; set; }

        [JsonProperty("analyticalPanels", NullValueHandling = NullValueHandling.Ignore)]
        public List<AnalysisEntry> AnalyticalPanels { get; set; }

        [JsonProperty("analyticalLinks", NullValueHandling = NullValueHandling.Ignore)]
        public List<AnalysisEntry> AnalyticalLinks { get; set; }

        [JsonProperty("hubs", NullValueHandling = NullValueHandling.Ignore)]
        public List<AnalysisEntry> Hubs { get; set; }

        [JsonProperty("boundaryConditions", NullValueHandling = NullValueHandling.Ignore)]
        public List<AnalysisEntry> BoundaryConditions { get; set; }

        [JsonProperty("loadNatures", NullValueHandling = NullValueHandling.Ignore)]
        public List<AnalysisEntry> LoadNatures { get; set; }

        [JsonProperty("loadCases", NullValueHandling = NullValueHandling.Ignore)]
        public List<AnalysisEntry> LoadCases { get; set; }

        [JsonProperty("loadCombinations", NullValueHandling = NullValueHandling.Ignore)]
        public List<AnalysisEntry> LoadCombinations { get; set; }

        [JsonProperty("loadUsages", NullValueHandling = NullValueHandling.Ignore)]
        public List<AnalysisEntry> LoadUsages { get; set; }

        [JsonProperty("pointLoadTypes", NullValueHandling = NullValueHandling.Ignore)]
        public List<AnalysisEntry> PointLoadTypes { get; set; }

        [JsonProperty("lineLoadTypes", NullValueHandling = NullValueHandling.Ignore)]
        public List<AnalysisEntry> LineLoadTypes { get; set; }

        [JsonProperty("areaLoadTypes", NullValueHandling = NullValueHandling.Ignore)]
        public List<AnalysisEntry> AreaLoadTypes { get; set; }

        [JsonProperty("analyticalLinkTypes", NullValueHandling = NullValueHandling.Ignore)]
        public List<AnalysisEntry> AnalyticalLinkTypes { get; set; }

        [JsonProperty("loads", NullValueHandling = NullValueHandling.Ignore)]
        public List<AnalysisEntry> Loads { get; set; }
    }

    /// <summary>
    /// One row of the inventory. Fields that do not apply to the element kind stay null
    /// and are dropped from the JSON.
    /// </summary>
    public class AnalysisEntry
    {
        [JsonProperty("elementId")]
        public long ElementId { get; set; }

        [JsonProperty("kind", NullValueHandling = NullValueHandling.Ignore)]
        public string Kind { get; set; }

        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string Name { get; set; }

        [JsonProperty("structuralRole", NullValueHandling = NullValueHandling.Ignore)]
        public string StructuralRole { get; set; }

        [JsonProperty("analyzeAs", NullValueHandling = NullValueHandling.Ignore)]
        public string AnalyzeAs { get; set; }

        [JsonProperty("material", NullValueHandling = NullValueHandling.Ignore)]
        public string Material { get; set; }

        [JsonProperty("sectionType", NullValueHandling = NullValueHandling.Ignore)]
        public string SectionType { get; set; }

        /// <summary>Millimetres.</summary>
        [JsonProperty("thicknessMm", NullValueHandling = NullValueHandling.Ignore)]
        public double? ThicknessMm { get; set; }

        [JsonProperty("start", NullValueHandling = NullValueHandling.Ignore)]
        public JZPoint Start { get; set; }

        [JsonProperty("end", NullValueHandling = NullValueHandling.Ignore)]
        public JZPoint End { get; set; }

        [JsonProperty("origin", NullValueHandling = NullValueHandling.Ignore)]
        public JZPoint Origin { get; set; }

        [JsonProperty("startHubId", NullValueHandling = NullValueHandling.Ignore)]
        public long? StartHubId { get; set; }

        [JsonProperty("endHubId", NullValueHandling = NullValueHandling.Ignore)]
        public long? EndHubId { get; set; }

        [JsonProperty("hostElementId", NullValueHandling = NullValueHandling.Ignore)]
        public long? HostElementId { get; set; }

        [JsonProperty("conditionType", NullValueHandling = NullValueHandling.Ignore)]
        public string ConditionType { get; set; }

        [JsonProperty("preset", NullValueHandling = NullValueHandling.Ignore)]
        public string Preset { get; set; }

        [JsonProperty("number", NullValueHandling = NullValueHandling.Ignore)]
        public int? Number { get; set; }

        [JsonProperty("nature", NullValueHandling = NullValueHandling.Ignore)]
        public string Nature { get; set; }

        [JsonProperty("category", NullValueHandling = NullValueHandling.Ignore)]
        public string Category { get; set; }

        [JsonProperty("loadCase", NullValueHandling = NullValueHandling.Ignore)]
        public string LoadCase { get; set; }

        [JsonProperty("combinationType", NullValueHandling = NullValueHandling.Ignore)]
        public string CombinationType { get; set; }

        [JsonProperty("state", NullValueHandling = NullValueHandling.Ignore)]
        public string State { get; set; }

        [JsonProperty("components", NullValueHandling = NullValueHandling.Ignore)]
        public List<AnalysisComponentEntry> Components { get; set; }
    }

    /// <summary>
    /// One factored term of a load combination, as reported by the inventory.
    /// </summary>
    public class AnalysisComponentEntry
    {
        [JsonProperty("elementId")]
        public long ElementId { get; set; }

        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string Name { get; set; }

        [JsonProperty("factor")]
        public double Factor { get; set; }
    }
}
