import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerFamilyFlexTool(server: McpServer) {
  server.tool(
    "family_flex",
    "Flex a family: push a set of values in, regenerate, and report what came back. This is how a parametric family is tested. One call answers all three questions worth asking - did the values take, did the formulas that depend on them produce the right results, and did the geometry survive without constraint warnings. Warnings are counted before and after, so a flex that ADDS one is distinguishable from a flex that merely inherited it; warnings left in a family multiply by every instance placed in a project. Run it at both extremes of every parameter range, not just at comfortable values.",
    {
      documentHandle: z
        .string()
        .optional()
        .describe("Handle from family_open_document. Omit to use the active document."),
      typeName: z
        .string()
        .optional()
        .describe("Switch to this family type before applying values. Omit to use the current type."),
      values: z
        .array(
          z.object({
            name: z.string().describe("Parameter to set. Must not be one driven by a formula."),
            valueMm: z.number().optional().describe("Value for a Length, in millimetres."),
            value: z.number().optional().describe("Value for a Number, Integer or YesNo (1/0)."),
            text: z.string().optional().describe("Value for a Text parameter."),
          })
        )
        .optional()
        .describe("Values to apply. Omit to regenerate and re-read without changing anything."),
      readBack: z
        .array(z.string())
        .optional()
        .describe(
          "Parameters to report after the regenerate. Omit to report every formula-driven parameter, which is usually what you want to check."
        ),
    },
    async (args, extra) => {
      const params = args;
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("family_flex", params);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Flexing the family failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
