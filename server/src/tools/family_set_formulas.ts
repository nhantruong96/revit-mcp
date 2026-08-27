import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerFamilySetFormulasTool(server: McpServer) {
  server.tool(
    "family_set_formulas",
    "Assign or clear formulas on family parameters that already exist, in one transaction. Call this after family_add_parameters has declared every name the formulas reference. Revit formulas are case sensitive, and a parameter carrying a formula becomes read-only - which is why a parameter fed by a type catalog column must never be given one, or Revit will silently ignore that column. Pass an empty formula to clear one. Failures are reported per parameter with the name attached, which Revit's own message does not tell you.",
    {
      documentHandle: z
        .string()
        .optional()
        .describe("Handle from family_open_document. Omit to use the active document."),
      formulas: z
        .array(
          z.object({
            name: z.string().describe("Name of an already-declared parameter."),
            formula: z
              .string()
              .optional()
              .describe(
                "The formula, e.g. \"(Do - Di) / 2\" or \"and(Show_Rebar, N2 > 1)\". Case sensitive. Omit or leave empty to clear an existing formula."
              ),
          })
        )
        .describe("The formulas to apply, in any order."),
    },
    async (args, extra) => {
      const params = args;
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("family_set_formulas", params);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Setting family formulas failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
