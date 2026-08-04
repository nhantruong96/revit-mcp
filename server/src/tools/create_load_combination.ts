import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendStructuralCommand } from "../utils/StructuralSchemas.js";

export function registerCreateLoadCombinationTool(server: McpServer) {
  server.tool(
    "create_load_combination",
    "Create load combinations from factored load cases or nested combinations, e.g. '1.2DL + 1.6LL'. Components are matched by element id or by name against both load cases and existing combinations. Load usages named here are created when missing. Requires Revit 2023 or later.",
    {
      data: z
        .array(
          z.object({
            name: z
              .string()
              .min(1)
              .describe("Combination name, e.g. '1.2DL + 1.6LL'"),
            combinationType: z
              .enum(["Combination", "Envelope"])
              .default("Combination")
              .describe("Factored sum, or envelope of its components"),
            state: z
              .enum([
                "Ultimate",
                "Serviceability",
                "ServiceabilityQuasiPermanent",
                "ServiceabilityFrequent",
                "ServiceabilityRare",
                "Accidental",
                "Special",
              ])
              .default("Ultimate")
              .describe("Limit state the combination represents"),
            components: z
              .array(
                z.object({
                  loadCaseId: z
                    .number()
                    .optional()
                    .describe("Element id of the load case or nested combination"),
                  name: z
                    .string()
                    .optional()
                    .describe(
                      "Name of the load case or nested combination, used when no id is given"
                    ),
                  factor: z
                    .number()
                    .default(1)
                    .describe("Multiplier applied to the case, e.g. 1.2"),
                })
              )
              .min(1)
              .describe("Factored terms of the combination"),
            usageNames: z
              .array(z.string())
              .optional()
              .describe("Load usage names to attach. Usages that do not exist are created."),
          })
        )
        .min(1)
        .describe("Load combinations to create"),
    },
    async (args) => {
      return sendStructuralCommand("create_load_combination", { data: args.data });
    }
  );
}
