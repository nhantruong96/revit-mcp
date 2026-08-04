import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { pointSchema, sendStructuralCommand } from "../utils/StructuralSchemas.js";

export function registerCreateAnalyticalMemberTool(server: McpServer) {
  server.tool(
    "create_analytical_member",
    "Create analytical members - the analytical beams, columns and braces of Revit's standalone analytical model. These are analysis elements in their own right, not the analytical representation of a physical beam. Coordinates are in millimetres.",
    {
      data: z
        .array(
          z.object({
            startPoint: pointSchema.describe("Start of the member, in mm"),
            endPoint: pointSchema.describe("End of the member, in mm"),
            structuralRole: z
              .enum([
                "StructuralRoleBeam",
                "StructuralRoleColumn",
                "StructuralRoleMember",
                "StructuralRoleGirder",
                "Unset",
              ])
              .optional()
              .describe("Role of the member. StructuralRoleMember is a brace."),
            analyzeAs: z
              .enum([
                "Gravity",
                "Lateral",
                "GravityLateral",
                "Hanger",
                "NotForAnalysis",
              ])
              .optional()
              .describe("How the member participates in analysis"),
            sectionTypeId: z
              .number()
              .optional()
              .describe("Element id of the family symbol supplying the cross section"),
            sectionTypeName: z
              .string()
              .optional()
              .describe("Family type name supplying the cross section, used when no id is given"),
            materialName: z
              .string()
              .optional()
              .describe("Structural material name, e.g. 'Concrete, Cast-in-Place gray'"),
            crossSectionRotation: z
              .number()
              .default(0)
              .describe("Cross-section rotation in degrees"),
          })
        )
        .min(1)
        .describe("Analytical members to create"),
    },
    async (args) => {
      return sendStructuralCommand("create_analytical_member", { data: args.data });
    }
  );
}
