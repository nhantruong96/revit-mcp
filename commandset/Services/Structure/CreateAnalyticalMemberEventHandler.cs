using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Models.Structure;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Structure
{
    /// <summary>
    /// Creates analytical members - the analytical beams, columns and braces of the
    /// standalone analytical model.
    /// </summary>
    public class CreateAnalyticalMemberEventHandler
        : StructuralCreationEventHandlerBase<AnalyticalMemberCreationInfo>
    {
        public override string GetName() => "Create Analytical Member";

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

            return CreateBatch("Create Analytical Members", "AnalyticalMember", Parameters.Data, CreateMember);
        }

        private (Element, string) CreateMember(AnalyticalMemberItem item)
        {
            Line curve = StructuralAnalysisUtils.BuildLine(item.StartPoint, item.EndPoint);

            if (!AnalyticalMember.IsValidCurve(curve))
                throw new ArgumentException(
                    "Revit rejected the curve for an analytical member; check that the two points are distinct and not degenerate");

            var member = AnalyticalMember.Create(doc, curve);
            var notes = new List<string>();

            if (!string.IsNullOrWhiteSpace(item.StructuralRole))
            {
                var role = StructuralAnalysisUtils.ParseEnum(item.StructuralRole, AnalyticalStructuralRole.Unset);

                if (member.IsValidStructuralRole(role))
                    member.StructuralRole = role;
                else
                    notes.Add($"structuralRole '{item.StructuralRole}' is not valid for this member; left as {member.StructuralRole}");
            }

            if (!string.IsNullOrWhiteSpace(item.AnalyzeAs))
            {
                var analyzeAs = StructuralAnalysisUtils.ParseEnum(item.AnalyzeAs, AnalyzeAs.Gravity);

                if (member.IsValidAnalyzeAs(analyzeAs))
                    member.AnalyzeAs = analyzeAs;
                else
                    notes.Add($"analyzeAs '{item.AnalyzeAs}' is not valid for this member; left as {member.AnalyzeAs}");
            }

            ElementId sectionTypeId = ResolveSectionType(item);
            if (sectionTypeId != ElementId.InvalidElementId)
            {
                if (member.IsValidSectionTypeId(sectionTypeId))
                    member.SectionTypeId = sectionTypeId;
                else
                    notes.Add("the requested section type is not valid for an analytical member; kept the default section");
            }

            ElementId materialId = StructuralAnalysisUtils.ResolveMaterial(doc, item.MaterialName);
            if (materialId != ElementId.InvalidElementId)
                member.MaterialId = materialId;

            if (item.CrossSectionRotation != 0)
                member.CrossSectionRotation = item.CrossSectionRotation * Math.PI / 180.0;

            return (member, notes.Count == 0 ? null : string.Join("; ", notes));
        }

        /// <summary>
        /// Resolve the family symbol that gives the member its cross section, by id or by name.
        /// </summary>
        private ElementId ResolveSectionType(AnalyticalMemberItem item)
        {
            if (item.SectionTypeId.HasValue && item.SectionTypeId.Value > 0)
                return StructuralAnalysisUtils.NewElementId(item.SectionTypeId.Value);

            if (string.IsNullOrWhiteSpace(item.SectionTypeName))
                return ElementId.InvalidElementId;

            var symbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s => s.Name.Equals(item.SectionTypeName, StringComparison.OrdinalIgnoreCase));

            if (symbol == null)
                throw new ArgumentException($"No family type named '{item.SectionTypeName}' exists in the model");

            return symbol.Id;
        }
    }
}
