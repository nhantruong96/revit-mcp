import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerViewExportTool(server: McpServer) {
  server.tool(
    "view_export",
    "Export a view to a PNG image or to DWG, from the active document or from a background one opened with family_open_document. Use an image when you need to look at what was built - measuring parameters proves the model exists, not that the drawing shows it. Use DWG when you need to measure against a reference drawing, because line work can be read back out of a DWG and compared, which a picture does not allow. Revit decorates exported file names with the view name, so the tool reports the paths actually written rather than the name you asked for. Note: exporting can raise a progress dialog, and a dialog guard cannot tell one from a prompt.",
    {
      documentHandle: z
        .string()
        .optional()
        .describe("Handle from family_open_document. Omit to use the active document."),
      format: z
        .enum(["image", "dwg"])
        .default("image")
        .describe("'image' for a PNG to look at, 'dwg' for line work to measure."),
      outputFolder: z
        .string()
        .describe("Folder to write into. Created if it does not exist. Keep exports out of the project folder."),
      namePrefix: z
        .string()
        .optional()
        .describe("File name prefix. Defaults to the document title."),
      viewName: z
        .string()
        .optional()
        .describe(
          "View to export. A background document has no active view, so name one; otherwise the first drawable non-template view is used, preferring 3D, then elevation, then plan."
        ),
      pixelSize: z
        .number()
        .optional()
        .describe("Image width in pixels. Defaults to 2000, minimum 256."),
    },
    async (args, extra) => {
      const params = args;
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("view_export", params);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `View export failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
