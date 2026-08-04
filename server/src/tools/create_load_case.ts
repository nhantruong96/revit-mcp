import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendStructuralCommand } from "../utils/StructuralSchemas.js";

export function registerCreateLoadCaseTool(server: McpServer) {
  server.tool(
    "create_load_case",
    "Create structural load cases. The load nature a case belongs to is created automatically when the model does not have it yet, defaulting to the category name. Create the cases before the loads that reference them.",
    {
      data: z
        .array(
          z.object({
            name: z
              .string()
              .min(1)
              .describe("Load case name, e.g. 'DL1' or 'Live - Office'"),
            category: z
              .enum([
                "Dead",
                "Live",
                "Wind",
                "Snow",
                "RoofLive",
                "Accidental",
                "Temperature",
                "Seismic",
              ])
              .default("Dead")
              .describe("Load case category"),
            natureName: z
              .string()
              .optional()
              .describe(
                "Load nature name. Defaults to the category name; created if it does not exist."
              ),
            number: z
              .number()
              .int()
              .optional()
              .describe(
                "Case number. Left to Revit when omitted; a number already in use is reported and skipped."
              ),
          })
        )
        .min(1)
        .describe("Load cases to create"),
    },
    async (args) => {
      return sendStructuralCommand("create_load_case", { data: args.data });
    }
  );
}
