import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerFamilyReadParametersTool(server: McpServer) {
  server.tool(
    "family_read_parameters",
    "Read a family's whole parameter table back: name, storage type, spec, group, instance flag, shared flag and GUID, formula, read-only state and the value the current type holds. Use it to check what a write actually did rather than trusting that it did it - a parameter that came out as type rather than instance, or a shared parameter whose GUID does not match the file everyone else used, both look like success until something downstream breaks. Lengths are reported in millimetres as well as raw internal units. Also returns the type names and counts.",
    {
      documentHandle: z
        .string()
        .optional()
        .describe("Handle from family_open_document. Omit to use the active document."),
      includeValues: z
        .boolean()
        .optional()
        .describe("Include the value each parameter holds in the current type. Defaults to true."),
      nameContains: z
        .string()
        .optional()
        .describe("Only report parameters whose name contains this text, case insensitive."),
    },
    async (args, extra) => {
      const params = args;
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("family_read_parameters", params);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Reading family parameters failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
