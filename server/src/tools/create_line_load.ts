import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import {
  loadCommonFields,
  pointSchema,
  sendStructuralCommand,
  vectorSchema,
} from "../utils/StructuralSchemas.js";

export function registerCreateLineLoadTool(server: McpServer) {
  server.tool(
    "create_line_load",
    "Create line loads - forces and moments distributed along a line. Points are in millimetres, force in kN/m and moment in kN*m/m; a downward gravity load is a negative Z force. Supplying forceEnd or momentEnd makes the load trapezoidal instead of uniform. At least one of force or moment must be non-zero. Requires Revit 2023 or later.",
    {
      data: z
        .array(
          z.object({
            startPoint: pointSchema.describe("Start of the loaded line, in mm"),
            endPoint: pointSchema.describe("End of the loaded line, in mm"),
            force: vectorSchema
              .optional()
              .describe("Force per unit length at the start, in kN/m. Downward is negative Z."),
            forceEnd: vectorSchema
              .optional()
              .describe("Force per unit length at the end, in kN/m. Omit for a uniform load."),
            moment: vectorSchema
              .optional()
              .describe("Moment per unit length at the start, in kN*m/m"),
            momentEnd: vectorSchema
              .optional()
              .describe("Moment per unit length at the end, in kN*m/m. Omit for a uniform load."),
            isProjected: z
              .boolean()
              .default(false)
              .describe(
                "Apply the load to the projected length instead of the true length"
              ),
            ...loadCommonFields,
          })
        )
        .min(1)
        .describe("Line loads to create"),
    },
    async (args) => {
      return sendStructuralCommand("create_line_load", { data: args.data });
    }
  );
}
