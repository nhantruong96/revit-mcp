import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { pointSchema, sendStructuralCommand } from "../utils/StructuralSchemas.js";

export function registerCreateAnalyticalLinkTool(server: McpServer) {
  server.tool(
    "create_analytical_link",
    "Create analytical links joining two analytical hubs. Hubs are the connection points Revit maintains at the ends of analytical elements, so the analytical members or panels must already exist. Address hubs by element id, or give the point each hub sits at and it will be located within a tolerance. Use get_structural_analysis_info with include=['hubs'] to list hub ids. Requires Revit 2023 or later.",
    {
      data: z
        .array(
          z.object({
            startHubId: z
              .number()
              .optional()
              .describe("Element id of the hub at the link start"),
            endHubId: z
              .number()
              .optional()
              .describe("Element id of the hub at the link end"),
            startPoint: pointSchema
              .optional()
              .describe("Location of the start hub in mm, used when no id is given"),
            endPoint: pointSchema
              .optional()
              .describe("Location of the end hub in mm, used when no id is given"),
            searchToleranceMm: z
              .number()
              .positive()
              .default(25)
              .describe("How far from the given point a hub may sit and still match, in mm"),
            linkTypeId: z
              .number()
              .optional()
              .describe("Element id of the AnalyticalLinkType to use"),
            linkTypeName: z
              .string()
              .optional()
              .describe("AnalyticalLinkType name. Defaults to the first one in the model."),
          })
        )
        .min(1)
        .describe("Analytical links to create"),
    },
    async (args) => {
      return sendStructuralCommand("create_analytical_link", { data: args.data });
    }
  );
}
