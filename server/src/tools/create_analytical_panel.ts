import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { pointSchema, sendStructuralCommand } from "../utils/StructuralSchemas.js";

export function registerCreateAnalyticalPanelTool(server: McpServer) {
  server.tool(
    "create_analytical_panel",
    "Create analytical panels - the analytical floors, walls and foundation slabs of Revit's standalone analytical model. The boundary must be closed and planar; the closing point may be omitted. Coordinates and thickness are in millimetres. Requires Revit 2023 or later.",
    {
      data: z
        .array(
          z.object({
            boundary: z
              .array(pointSchema)
              .min(3)
              .describe("Ordered, planar outline of the panel, in mm"),
            thickness: z
              .number()
              .positive()
              .optional()
              .describe("Panel thickness in mm. Omit to keep Revit's default."),
            structuralRole: z
              .enum([
                "StructuralRoleFloor",
                "StructuralRoleWall",
                "StructuralRolePanel",
                "StructuralRoleCladding",
                "Unset",
              ])
              .optional()
              .describe("Role of the panel"),
            analyzeAs: z
              .enum([
                "SlabOneWay",
                "SlabTwoWay",
                "Mat",
                "SlabOnGrade",
                "Gravity",
                "Lateral",
                "GravityLateral",
                "NotForAnalysis",
              ])
              .optional()
              .describe("How the panel participates in analysis"),
            materialName: z
              .string()
              .optional()
              .describe("Structural material name"),
          })
        )
        .min(1)
        .describe("Analytical panels to create"),
    },
    async (args) => {
      return sendStructuralCommand("create_analytical_panel", { data: args.data });
    }
  );
}
