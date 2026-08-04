import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import {
  loadCommonFields,
  pointSchema,
  sendStructuralCommand,
  vectorSchema,
} from "../utils/StructuralSchemas.js";

export function registerCreatePointLoadTool(server: McpServer) {
  server.tool(
    "create_point_load",
    "Create point loads - concentrated forces and moments. The point is in millimetres, force in kN and moment in kN*m; a downward gravity load is a negative Z force. At least one of force or moment must be non-zero. Requires Revit 2023 or later.",
    {
      data: z
        .array(
          z.object({
            point: pointSchema.describe("Where the load is applied, in mm"),
            force: vectorSchema
              .optional()
              .describe("Force vector in kN. Downward gravity load is negative Z."),
            moment: vectorSchema.optional().describe("Moment vector in kN*m"),
            ...loadCommonFields,
          })
        )
        .min(1)
        .describe("Point loads to create"),
    },
    async (args) => {
      return sendStructuralCommand("create_point_load", { data: args.data });
    }
  );
}
