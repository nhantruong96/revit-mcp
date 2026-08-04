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
    /// Creates point, line and area boundary conditions (supports) on analytical elements.
    /// </summary>
    public class CreateBoundaryConditionsEventHandler
        : StructuralCreationEventHandlerBase<BoundaryConditionsCreationInfo>
    {
        public override string GetName() => "Create Boundary Conditions";

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

            return CreateBatch("Create Boundary Conditions", "BoundaryConditions", Parameters.Data, CreateCondition);
        }

        private (Element, string) CreateCondition(BoundaryConditionsItem item)
        {
            ElementId hostId = StructuralAnalysisUtils.ResolveHostId(doc, item.HostElementId);

            if (hostId == ElementId.InvalidElementId)
                throw new ArgumentException("hostElementId is required for a boundary condition");

            var dof = DegreesOfFreedom.From(item);
            string kind = (item.ConditionType ?? "point").ToLowerInvariant();

            BoundaryConditions created;

            switch (kind)
            {
                case "point":
                    if (!BoundaryConditions.IsValidHostId(doc, hostId, BoundaryConditionsType.Point))
                        throw new ArgumentException($"Element {item.HostElementId} cannot host a point boundary condition");

                    created = BoundaryConditions.CreatePointBoundaryConditions(
                        doc,
                        hostId,
                        StructuralAnalysisUtils.ToXYZ(item.Point),
                        dof.XTranslation, StructuralAnalysisUtils.PointTranslationSpringToInternal(item.XTranslationSpring),
                        dof.YTranslation, StructuralAnalysisUtils.PointTranslationSpringToInternal(item.YTranslationSpring),
                        dof.ZTranslation, StructuralAnalysisUtils.PointTranslationSpringToInternal(item.ZTranslationSpring),
                        dof.XRotation, StructuralAnalysisUtils.PointRotationSpringToInternal(item.XRotationSpring),
                        dof.YRotation, StructuralAnalysisUtils.PointRotationSpringToInternal(item.YRotationSpring),
                        dof.ZRotation, StructuralAnalysisUtils.PointRotationSpringToInternal(item.ZRotationSpring));
                    break;

                case "line":
                    if (!BoundaryConditions.IsValidHostId(doc, hostId, BoundaryConditionsType.Line))
                        throw new ArgumentException($"Element {item.HostElementId} cannot host a line boundary condition");

                    // A line support only exposes the rotation about the support axis, so the
                    // Y and Z rotation fields are deliberately not passed through here.
                    created = BoundaryConditions.CreateLineBoundaryConditions(
                        doc,
                        hostId,
                        StructuralAnalysisUtils.BuildLine(item.StartPoint, item.EndPoint),
                        dof.XTranslation, StructuralAnalysisUtils.LineTranslationSpringToInternal(item.XTranslationSpring),
                        dof.YTranslation, StructuralAnalysisUtils.LineTranslationSpringToInternal(item.YTranslationSpring),
                        dof.ZTranslation, StructuralAnalysisUtils.LineTranslationSpringToInternal(item.ZTranslationSpring),
                        dof.XRotation, StructuralAnalysisUtils.LineRotationSpringToInternal(item.XRotationSpring));
                    break;

                case "area":
                    if (!BoundaryConditions.IsValidHostId(doc, hostId, BoundaryConditionsType.Area))
                        throw new ArgumentException($"Element {item.HostElementId} cannot host an area boundary condition");

                    // An area support has no rotational degrees of freedom in the API.
                    created = BoundaryConditions.CreateAreaBoundaryConditions(
                        doc,
                        hostId,
                        new List<CurveLoop> { StructuralAnalysisUtils.BuildCurveLoop(item.Boundary) },
                        dof.XTranslation, StructuralAnalysisUtils.AreaTranslationSpringToInternal(item.XTranslationSpring),
                        dof.YTranslation, StructuralAnalysisUtils.AreaTranslationSpringToInternal(item.YTranslationSpring),
                        dof.ZTranslation, StructuralAnalysisUtils.AreaTranslationSpringToInternal(item.ZTranslationSpring));
                    break;

                default:
                    throw new ArgumentException($"Unknown conditionType '{item.ConditionType}'");
            }

            string note = null;

            // Stamping the preset back on makes Revit's properties palette show "Fixed" or
            // "Pinned" instead of "User", but it is cosmetic - the degrees of freedom above
            // are what the analysis uses.
            if (dof.Preset.HasValue)
            {
                try
                {
                    if (created.IsValidPreset(dof.Preset.Value))
                        created.Preset = dof.Preset.Value;
                }
                catch (Exception ex)
                {
                    note = $"degrees of freedom applied, but the '{item.Preset}' preset label could not be set: {ex.Message}";
                }
            }

            return (created, note);
        }

        /// <summary>
        /// The six degrees of freedom a support fixes, releases or springs, resolved from
        /// either a preset name or the individual fields.
        /// </summary>
        private readonly struct DegreesOfFreedom
        {
            public TranslationRotationValue XTranslation { get; }
            public TranslationRotationValue YTranslation { get; }
            public TranslationRotationValue ZTranslation { get; }
            public TranslationRotationValue XRotation { get; }
            public TranslationRotationValue YRotation { get; }
            public TranslationRotationValue ZRotation { get; }

            /// <summary>Preset to stamp on the created element, when one applies.</summary>
            public BoundaryConditionsPreset? Preset { get; }

            private DegreesOfFreedom(
                TranslationRotationValue xTranslation,
                TranslationRotationValue yTranslation,
                TranslationRotationValue zTranslation,
                TranslationRotationValue xRotation,
                TranslationRotationValue yRotation,
                TranslationRotationValue zRotation,
                BoundaryConditionsPreset? preset)
            {
                XTranslation = xTranslation;
                YTranslation = yTranslation;
                ZTranslation = zTranslation;
                XRotation = xRotation;
                YRotation = yRotation;
                ZRotation = zRotation;
                Preset = preset;
            }

            public static DegreesOfFreedom From(BoundaryConditionsItem item)
            {
                const TranslationRotationValue fixedDof = TranslationRotationValue.Fixed;
                const TranslationRotationValue freeDof = TranslationRotationValue.Release;

                switch ((item.Preset ?? "Fixed").Trim().ToLowerInvariant())
                {
                    case "fixed":
                        return new DegreesOfFreedom(
                            fixedDof, fixedDof, fixedDof,
                            fixedDof, fixedDof, fixedDof,
                            BoundaryConditionsPreset.Fixed);

                    case "pinned":
                        return new DegreesOfFreedom(
                            fixedDof, fixedDof, fixedDof,
                            freeDof, freeDof, freeDof,
                            BoundaryConditionsPreset.Pinned);

                    case "roller":
                        // Free to slide in plan, held vertically, free to rotate.
                        return new DegreesOfFreedom(
                            freeDof, freeDof, fixedDof,
                            freeDof, freeDof, freeDof,
                            BoundaryConditionsPreset.Roller);

                    case "user":
                        return new DegreesOfFreedom(
                            StructuralAnalysisUtils.ParseEnum(item.XTranslation, fixedDof),
                            StructuralAnalysisUtils.ParseEnum(item.YTranslation, fixedDof),
                            StructuralAnalysisUtils.ParseEnum(item.ZTranslation, fixedDof),
                            StructuralAnalysisUtils.ParseEnum(item.XRotation, fixedDof),
                            StructuralAnalysisUtils.ParseEnum(item.YRotation, fixedDof),
                            StructuralAnalysisUtils.ParseEnum(item.ZRotation, fixedDof),
                            null);

                    default:
                        throw new ArgumentException(
                            $"preset '{item.Preset}' is not recognised. Use Fixed, Pinned, Roller or User.");
                }
            }
        }
#endif
    }
}
