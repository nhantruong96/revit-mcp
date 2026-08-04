import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import {
  loadCommonFields,
  pointSchema,
  sendStructuralCommand,
  vectorSchema,
} from "../utils/StructuralSchemas.js";

export function registerCreateAreaLoadTool(server: McpServer) {
  server.tool(
    "create_area_load",
    "Create area loads - pressures over a closed outline. The boundary is in millimetres and must be closed, planar and non-self-intersecting; the closing point may be omitted. Force is in kN/m2; a downward gravity load is a negative Z force.",
    {
      data: z
        .array(
          z.object({
            boundary: z
              .array(pointSchema)
              .min(3)
              .describe("Ordered, planar outline of the loaded area, in mm"),
            force: vectorSchema.describe(
              "Force per unit area in kN/m2. Downward gravity load is negative Z."
            ),
            isProjected: z
              .boolean()
              .default(false)
              .describe("Apply the load to the projected area instead of the true area"),
            ...loadCommonFields,
          })
        )
        .min(1)
        .describe("Area loads to create"),
    },
    async (args) => {
      return sendStructuralCommand("create_area_load", { data: args.data });
    }
  );
}
