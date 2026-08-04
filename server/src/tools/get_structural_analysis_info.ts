import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendStructuralCommand } from "../utils/StructuralSchemas.js";

export function registerGetStructuralAnalysisInfoTool(server: McpServer) {
  server.tool(
    "get_structural_analysis_info",
    "Read the analytical side of the Revit model: analytical members and panels, analytical links and hubs, boundary conditions, load natures, load cases, load combinations, load usages and the available load symbols. Use this first - the other structural analysis tools take element ids (load cases, host elements, hubs, load types) and this is where they come from. Lengths are reported in millimetres.",
    {
      include: z
        .array(
          z.enum([
            "analyticalMembers",
            "analyticalPanels",
            "analyticalLinks",
            "hubs",
            "boundaryConditions",
            "loadNatures",
            "loadCases",
            "loadCombinations",
            "loadUsages",
            "loadTypes",
            "loads",
          ])
        )
        .optional()
        .describe(
          "Sections to return. Omit for everything except 'hubs', which is large and only needed when creating analytical links."
        ),
      limit: z
        .number()
        .int()
        .positive()
        .default(200)
        .describe("Maximum number of entries per section"),
    },
    async (args) => {
      return sendStructuralCommand("get_structural_analysis_info", {
        include: args.include,
        limit: args.limit,
      });
    }
  );
}
