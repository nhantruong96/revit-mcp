import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerFamilyMeasureGeometryTool(server: McpServer) {
  server.tool(
    "family_measure_geometry",
    "Measure the solid geometry in a family document: volume, surface area and bounding box, per element and in total, in cubic millimetres and millimetres so results compare directly against a hand calculation. This is what turns 'the extrusion looks right' into something checkable - a family whose parameters all read back correctly can still be built on a sketch locked to the wrong plane, and comparing the reported volume against the volume the shape should have is the cheapest way to catch it. Each element also reports shownAtDetailLevel, the family's own coarse/medium/fine switches, which is how you catch a coarse stand-in that is also showing at fine detail and about to double-count material. Note: cutting a solid with a void moves the geometry onto a new 'Joined Solid Geometry' element - the original then measures zero, and the total is what to trust.",
    {
      documentHandle: z
        .string()
        .optional()
        .describe("Handle from family_open_document. Omit to use the active document."),
      detailLevel: z
        .enum(["Coarse", "Medium", "Fine", "Undefined"])
        .optional()
        .describe("Detail level to evaluate geometry at. Defaults to Fine. Note that geometry read at a detail level does NOT respect the family's own visibility switches - read shownAtDetailLevel for those."),
      summaryOnly: z
        .boolean()
        .optional()
        .describe("Return the totals without listing every element. Use for a long array, where the per-element list is thousands of lines of noise."),
      maxElements: z
        .number()
        .optional()
        .describe("Cap how many elements are listed. Defaults to 50. Totals always cover everything regardless of the cap."),
      categoryContains: z
        .string()
        .optional()
        .describe("Only measure elements whose category name contains this text. Rarely useful in a family document: forms there usually have no category at all, so this filter discards everything. The result says so when that happens."),
    },
    async (args, extra) => {
      const params = args;
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("family_measure_geometry", params);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Measuring family geometry failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
