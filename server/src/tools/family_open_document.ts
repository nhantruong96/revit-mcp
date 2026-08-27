import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerFamilyOpenDocumentTool(server: McpServer) {
  server.tool(
    "family_open_document",
    "Open, create, save or close a family (.rfa) document in the background. Returns a documentHandle that every other family_* tool takes, so authoring never has to disturb whatever the user has open. 'open' loads an existing .rfa and leaves the active document alone; 'create' builds one from a template .rft; 'save'/'saveAs' write it out; 'close' releases it; 'list' shows the handles this Revit session is holding. Handles do not survive a Revit restart.",
    {
      action: z
        .enum(["open", "create", "save", "saveAs", "close", "list"])
        .default("open")
        .describe("What to do. Defaults to 'open'."),
      filePath: z
        .string()
        .optional()
        .describe("For 'open': full path of the .rfa to load."),
      templatePath: z
        .string()
        .optional()
        .describe(
          "For 'create': full path of the family template .rft, e.g. C:\\ProgramData\\Autodesk\\RVT 2027\\Family Templates\\English\\Metric Generic Model.rft"
        ),
      savePath: z
        .string()
        .optional()
        .describe("For 'create' and 'saveAs': full path of the .rfa to write."),
      documentHandle: z
        .string()
        .optional()
        .describe("Handle returned by a previous open/create. Required for save, saveAs and close."),
      overwrite: z
        .boolean()
        .optional()
        .describe("Allow saveAs to replace an existing file. Defaults to true."),
      save: z
        .boolean()
        .optional()
        .describe("For 'close': save before closing. Defaults to false."),
      defaultTypeName: z
        .string()
        .optional()
        .describe("Name for the first family type when one has to be created. Defaults to 'Standard'."),
    },
    async (args, extra) => {
      const params = args;
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("family_open_document", params);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Family document command failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
