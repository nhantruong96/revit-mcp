using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Models.Structure;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Structure
{
    /// <summary>
    /// Creates point, line and area boundary conditions (supports) on analytical elements.
    ///
    /// Revit 2027 added static BoundaryConditions.Create* overloads that place a support on
    /// explicit geometry - a point, a curve, a set of loops. Before that the only entry point
    /// was Document.Create.New*BoundaryConditions, which derives the support geometry from the
    /// host element or a reference to part of it. Both paths are implemented; on the older API
    /// the supplied geometry can only pick which end of the host to support, and the result
    /// says so rather than silently placing the support somewhere else.
    /// </summary>
    public class CreateBoundaryConditionsEventHandler
        : StructuralCreationEventHandlerBase<BoundaryConditionsCreationInfo>
    {
        public override string GetName() => "Create Boundary Conditions";

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
            var notes = new List<string>();

            BoundaryConditions created;

            switch (kind)
            {
                case "point":
                    created = CreatePoint(item, hostId, dof, notes);
                    break;

                case "line":
                    created = CreateLine(item, hostId, dof, notes);
                    break;

                case "area":
                    created = CreateArea(item, hostId, dof, notes);
                    break;

                default:
                    throw new ArgumentException($"Unknown conditionType '{item.ConditionType}'");
            }

            ApplyPreset(created, item, dof, notes);

            return (created, notes.Count == 0 ? null : string.Join("; ", notes));
        }

        private BoundaryConditions CreatePoint(
            BoundaryConditionsItem item, ElementId hostId, DegreesOfFreedom dof, List<string> notes)
        {
#if REVIT2027_OR_GREATER
            if (!BoundaryConditions.IsValidHostId(doc, hostId, BoundaryConditionsType.Point))
                throw new ArgumentException($"Element {item.HostElementId} cannot host a point boundary condition");

            return BoundaryConditions.CreatePointBoundaryConditions(
                doc,
                hostId,
                StructuralAnalysisUtils.ToXYZ(item.Point),
                dof.XTranslation, StructuralAnalysisUtils.PointTranslationSpringToInternal(item.XTranslationSpring),
                dof.YTranslation, StructuralAnalysisUtils.PointTranslationSpringToInternal(item.YTranslationSpring),
                dof.ZTranslation, StructuralAnalysisUtils.PointTranslationSpringToInternal(item.ZTranslationSpring),
                dof.XRotation, StructuralAnalysisUtils.PointRotationSpringToInternal(item.XRotationSpring),
                dof.YRotation, StructuralAnalysisUtils.PointRotationSpringToInternal(item.YRotationSpring),
                dof.ZRotation, StructuralAnalysisUtils.PointRotationSpringToInternal(item.ZRotationSpring));
#else
            // Before 2027 a point support attaches to an end of the host's analytical curve,
            // not to an arbitrary point, so snap to whichever end was asked for.
            Reference reference = EndReferenceNearest(hostId, item.Point, notes);

            return doc.Create.NewPointBoundaryConditions(
                reference,
                dof.XTranslation, StructuralAnalysisUtils.PointTranslationSpringToInternal(item.XTranslationSpring),
                dof.YTranslation, StructuralAnalysisUtils.PointTranslationSpringToInternal(item.YTranslationSpring),
                dof.ZTranslation, StructuralAnalysisUtils.PointTranslationSpringToInternal(item.ZTranslationSpring),
                dof.XRotation, StructuralAnalysisUtils.PointRotationSpringToInternal(item.XRotationSpring),
                dof.YRotation, StructuralAnalysisUtils.PointRotationSpringToInternal(item.YRotationSpring),
                dof.ZRotation, StructuralAnalysisUtils.PointRotationSpringToInternal(item.ZRotationSpring));
#endif
        }

        private BoundaryConditions CreateLine(
            BoundaryConditionsItem item, ElementId hostId, DegreesOfFreedom dof, List<string> notes)
        {
            // A line support only exposes the rotation about the support axis, so the Y and Z
            // rotation fields are deliberately not passed through here.
#if REVIT2027_OR_GREATER
            if (!BoundaryConditions.IsValidHostId(doc, hostId, BoundaryConditionsType.Line))
                throw new ArgumentException($"Element {item.HostElementId} cannot host a line boundary condition");

            return BoundaryConditions.CreateLineBoundaryConditions(
                doc,
                hostId,
                StructuralAnalysisUtils.BuildLine(item.StartPoint, item.EndPoint),
                dof.XTranslation, StructuralAnalysisUtils.LineTranslationSpringToInternal(item.XTranslationSpring),
                dof.YTranslation, StructuralAnalysisUtils.LineTranslationSpringToInternal(item.YTranslationSpring),
                dof.ZTranslation, StructuralAnalysisUtils.LineTranslationSpringToInternal(item.ZTranslationSpring),
                dof.XRotation, StructuralAnalysisUtils.LineRotationSpringToInternal(item.XRotationSpring));
#else
            notes.Add("this Revit version supports the whole host element, so startPoint/endPoint were ignored");

            return doc.Create.NewLineBoundaryConditions(
                doc.GetElement(hostId),
                dof.XTranslation, StructuralAnalysisUtils.LineTranslationSpringToInternal(item.XTranslationSpring),
                dof.YTranslation, StructuralAnalysisUtils.LineTranslationSpringToInternal(item.YTranslationSpring),
                dof.ZTranslation, StructuralAnalysisUtils.LineTranslationSpringToInternal(item.ZTranslationSpring),
                dof.XRotation, StructuralAnalysisUtils.LineRotationSpringToInternal(item.XRotationSpring));
#endif
        }

        private BoundaryConditions CreateArea(
            BoundaryConditionsItem item, ElementId hostId, DegreesOfFreedom dof, List<string> notes)
        {
            // An area support has no rotational degrees of freedom in the API.
#if REVIT2027_OR_GREATER
            if (!BoundaryConditions.IsValidHostId(doc, hostId, BoundaryConditionsType.Area))
                throw new ArgumentException($"Element {item.HostElementId} cannot host an area boundary condition");

            return BoundaryConditions.CreateAreaBoundaryConditions(
                doc,
                hostId,
                new List<CurveLoop> { StructuralAnalysisUtils.BuildCurveLoop(item.Boundary) },
                dof.XTranslation, StructuralAnalysisUtils.AreaTranslationSpringToInternal(item.XTranslationSpring),
                dof.YTranslation, StructuralAnalysisUtils.AreaTranslationSpringToInternal(item.YTranslationSpring),
                dof.ZTranslation, StructuralAnalysisUtils.AreaTranslationSpringToInternal(item.ZTranslationSpring));
#else
            notes.Add("this Revit version supports the whole host element, so boundary was ignored");

            return doc.Create.NewAreaBoundaryConditions(
                doc.GetElement(hostId),
                dof.XTranslation, StructuralAnalysisUtils.AreaTranslationSpringToInternal(item.XTranslationSpring),
                dof.YTranslation, StructuralAnalysisUtils.AreaTranslationSpringToInternal(item.YTranslationSpring),
                dof.ZTranslation, StructuralAnalysisUtils.AreaTranslationSpringToInternal(item.ZTranslationSpring));
#endif
        }

#if !REVIT2027_OR_GREATER
        /// <summary>
        /// Build a reference to the end of the host's analytical curve nearest the requested
        /// point, which is as close as the pre-2027 API lets a point support get.
        /// </summary>
        private Reference EndReferenceNearest(ElementId hostId, JZPoint point, List<string> notes)
        {
            if (doc.GetElement(hostId) is not AnalyticalElement host)
                throw new ArgumentException($"Element {hostId.GetValue()} is not an analytical element");

            if (!host.IsSingleCurve() || host.GetCurve() is not Curve curve)
                throw new ArgumentException(
                    "A point boundary condition needs a curve-based analytical element (member) as its host");

            XYZ target = StructuralAnalysisUtils.ToXYZ(point);
            bool atStart = curve.GetEndPoint(0).DistanceTo(target) <= curve.GetEndPoint(1).DistanceTo(target);

            var selector = new AnalyticalModelSelector(
                atStart ? AnalyticalCurveSelector.StartPoint : AnalyticalCurveSelector.EndPoint);

            if (!host.IsValidSelector(selector))
                throw new ArgumentException("The host does not accept a point support at that end");

            XYZ placed = curve.GetEndPoint(atStart ? 0 : 1);
            if (placed.DistanceTo(target) > StructuralAnalysisUtils.ShortCurveTolerance)
            {
                notes.Add(
                    "this Revit version anchors point supports to a member end, so the support was placed at the " +
                    $"{(atStart ? "start" : "end")} of the host " +
                    $"({StructuralAnalysisUtils.FeetToMm(placed.DistanceTo(target)):F0} mm from the requested point)");
            }

            return host.GetReference(selector);
        }
#endif

        /// <summary>
        /// Stamping the preset back on makes Revit's properties palette show "Fixed" or "Pinned"
        /// instead of "User". It is cosmetic - the degrees of freedom are what the analysis uses -
        /// and the property only exists from Revit 2027.
        /// </summary>
        private void ApplyPreset(
            BoundaryConditions created, BoundaryConditionsItem item, DegreesOfFreedom dof, List<string> notes)
        {
#if REVIT2027_OR_GREATER
            if (!dof.Preset.HasValue)
                return;

            try
            {
                if (created.IsValidPreset(dof.Preset.Value))
                    created.Preset = dof.Preset.Value;
            }
            catch (Exception ex)
            {
                notes.Add($"degrees of freedom applied, but the '{item.Preset}' preset label could not be set: {ex.Message}");
            }
#endif
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

#if REVIT2027_OR_GREATER
            /// <summary>Preset to stamp on the created element, when one applies.</summary>
            public BoundaryConditionsPreset? Preset { get; }
#endif

            private DegreesOfFreedom(
                TranslationRotationValue xTranslation,
                TranslationRotationValue yTranslation,
                TranslationRotationValue zTranslation,
                TranslationRotationValue xRotation,
                TranslationRotationValue yRotation,
                TranslationRotationValue zRotation
#if REVIT2027_OR_GREATER
                , BoundaryConditionsPreset? preset
#endif
                )
            {
                XTranslation = xTranslation;
                YTranslation = yTranslation;
                ZTranslation = zTranslation;
                XRotation = xRotation;
                YRotation = yRotation;
                ZRotation = zRotation;
#if REVIT2027_OR_GREATER
                Preset = preset;
#endif
            }

            public static DegreesOfFreedom From(BoundaryConditionsItem item)
            {
                const TranslationRotationValue fixedDof = TranslationRotationValue.Fixed;
                const TranslationRotationValue freeDof = TranslationRotationValue.Release;

                switch ((item.Preset ?? "Fixed").Trim().ToLowerInvariant())
                {
                    case "fixed":
                        return Build(fixedDof, fixedDof, fixedDof, fixedDof, fixedDof, fixedDof, "Fixed");

                    case "pinned":
                        return Build(fixedDof, fixedDof, fixedDof, freeDof, freeDof, freeDof, "Pinned");

                    case "roller":
                        // Free to slide in plan, held vertically, free to rotate.
                        return Build(freeDof, freeDof, fixedDof, freeDof, freeDof, freeDof, "Roller");

                    case "user":
                        return Build(
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

            private static DegreesOfFreedom Build(
                TranslationRotationValue xTranslation,
                TranslationRotationValue yTranslation,
                TranslationRotationValue zTranslation,
                TranslationRotationValue xRotation,
                TranslationRotationValue yRotation,
                TranslationRotationValue zRotation,
                string presetName)
            {
#if REVIT2027_OR_GREATER
                BoundaryConditionsPreset? preset = presetName == null
                    ? null
                    : StructuralAnalysisUtils.ParseEnum(presetName, BoundaryConditionsPreset.User);

                return new DegreesOfFreedom(
                    xTranslation, yTranslation, zTranslation, xRotation, yRotation, zRotation, preset);
#else
                return new DegreesOfFreedom(
                    xTranslation, yTranslation, zTranslation, xRotation, yRotation, zRotation);
#endif
            }
        }
    }
}
