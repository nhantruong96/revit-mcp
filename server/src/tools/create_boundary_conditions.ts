import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { pointSchema, sendStructuralCommand } from "../utils/StructuralSchemas.js";

const dofSchema = z
  .enum(["Fixed", "Release", "Spring"])
  .default("Fixed")
  .describe("Degree of freedom state, only read when preset is 'User'");

export function registerCreateBoundaryConditionsTool(server: McpServer) {
  server.tool(
    "create_boundary_conditions",
    "Create boundary conditions (supports) on analytical elements. A support must be hosted, so pass the analytical element id it attaches to - get_structural_analysis_info lists them. Use preset Fixed, Pinned or Roller for the common cases, or User with the six degree-of-freedom fields. Geometry is in millimetres. Spring stiffness units depend on the support: kN/m and kN*m/deg for point, kN/m/m and kN*m/deg/m for line, kN/m/m2 for area. A line support only exposes the rotation about its own axis, and an area support has no rotational freedoms. Requires Revit 2023 or later.",
    {
      data: z
        .array(
          z.object({
            conditionType: z
              .enum(["point", "line", "area"])
              .default("point")
              .describe("Shape of the support"),
            hostElementId: z
              .number()
              .describe("Analytical element the support attaches to"),
            point: pointSchema
              .optional()
              .describe("Location of a point support, in mm"),
            startPoint: pointSchema
              .optional()
              .describe("Start of a line support, in mm"),
            endPoint: pointSchema
              .optional()
              .describe("End of a line support, in mm"),
            boundary: z
              .array(pointSchema)
              .optional()
              .describe("Outline of an area support, in mm"),
            preset: z
              .enum(["Fixed", "Pinned", "Roller", "User"])
              .default("Fixed")
              .describe(
                "Shorthand for the six degrees of freedom. Anything other than 'User' ignores the individual fields."
              ),
            xTranslation: dofSchema,
            yTranslation: dofSchema,
            zTranslation: dofSchema,
            xRotation: dofSchema,
            yRotation: dofSchema,
            zRotation: dofSchema,
            xTranslationSpring: z
              .number()
              .default(0)
              .describe("Translational spring stiffness about X"),
            yTranslationSpring: z
              .number()
              .default(0)
              .describe("Translational spring stiffness about Y"),
            zTranslationSpring: z
              .number()
              .default(0)
              .describe("Translational spring stiffness about Z"),
            xRotationSpring: z
              .number()
              .default(0)
              .describe("Rotational spring stiffness about X"),
            yRotationSpring: z
              .number()
              .default(0)
              .describe("Rotational spring stiffness about Y (point supports only)"),
            zRotationSpring: z
              .number()
              .default(0)
              .describe("Rotational spring stiffness about Z (point supports only)"),
          })
        )
        .min(1)
        .describe("Boundary conditions to create"),
    },
    async (args) => {
      return sendStructuralCommand("create_boundary_conditions", { data: args.data });
    }
  );
}
