import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerFamilyAddParametersTool(server: McpServer) {
  server.tool(
    "family_add_parameters",
    "Declare family parameters in bulk, in one transaction. Each entry is either a shared parameter looked up by name in a shared parameter file - which is how two families end up sharing an identity for schedules, tags and IFC - or a plain family parameter defined by its spec. Parameters that already exist are skipped, not duplicated, so the call is safe to repeat. Report gives per-parameter outcomes including the GUID each shared parameter got. Formulas are NOT set here: declare everything first, then call family_set_formulas, because a formula can only reference parameters Revit already knows.",
    {
      documentHandle: z
        .string()
        .optional()
        .describe("Handle from family_open_document. Omit to use the active document."),
      sharedParameterFile: z
        .string()
        .optional()
        .describe(
          "Full path of the shared parameter .txt to read definitions from. Required if any entry is shared. The application's own shared parameter file setting is restored afterwards."
        ),
      defaultTypeName: z
        .string()
        .optional()
        .describe("Name for the first family type if the family has none yet. Defaults to 'Standard'."),
      parameters: z
        .array(
          z.object({
            name: z.string().describe("Parameter name. For shared parameters this must match the file exactly."),
            spec: z
              .string()
              .optional()
              .describe(
                "Data type for a plain family parameter: Length, Number, Integer, Text, YesNo, Angle, Material, or a nested name such as Int.Integer. Ignored for shared parameters, which carry their own."
              ),
            group: z
              .string()
              .optional()
              .describe("Parameter group: Geometry, Constraints, Data, IdentityData, Graphics, Materials, Structural, General, Visibility. Defaults to Data."),
            isInstance: z
              .boolean()
              .optional()
              .describe("True for an instance parameter. Nested families need instance parameters for the host to associate to them. Defaults to false."),
            shared: z
              .boolean()
              .optional()
              .describe("Force shared or plain. Defaults to shared when a sharedParameterFile was given."),
            defaultValueMm: z.number().optional().describe("Starting value for a Length, in millimetres."),
            defaultValue: z.number().optional().describe("Starting value for a Number, Integer or YesNo (1/0)."),
            defaultText: z.string().optional().describe("Starting value for a Text parameter."),
          })
        )
        .describe("The parameters to declare."),
    },
    async (args, extra) => {
      const params = args;
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("family_add_parameters", params);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Adding family parameters failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
