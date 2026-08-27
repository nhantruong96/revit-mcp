import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const transactionModeSchema = z
  .enum(["auto", "none"])
  .default("auto")
  .describe(
    "How the snippet should interact with Revit transactions. Use 'auto' to wrap the snippet in a transaction, or 'none' when the called code manages its own transactions."
  );

export function registerSendCodeToRevitTool(server: McpServer) {
  server.tool(
    "send_code_to_revit",
    "Send C# code to Revit for execution. Your code is inserted verbatim into the body of `public static object Execute(Document document, object[] parameters)`, so write statements only - no method or class declaration - and end with a `return`. The lowercase variable `document` is the document being worked on; `Document` on its own is the type, not the instance. System, System.Linq, System.Collections.Generic, Autodesk.Revit.DB and Autodesk.Revit.UI are already imported. Pass documentHandle to run against a background family document instead of the active one.",
    {
      code: z
        .string()
        .describe(
          "C# statements to run inside Execute(Document document, object[] parameters). Use the lowercase `document` variable for the active document and `parameters` for the arguments. Example: `return new FilteredElementCollector(document).OfClass(typeof(Wall)).GetElementCount();`"
        ),
      parameters: z
        .array(z.string())
        .optional()
        .describe(
          "Optional execution parameters that will be passed to your code"
        ),
      transactionMode: transactionModeSchema,
      documentHandle: z
        .string()
        .optional()
        .describe(
          "Handle from family_open_document. Runs the snippet against that background document instead of the active one, so a family being authored in the background stays the target. Omit to use the active document."
        ),
    },
    async (args, extra) => {
      const params = {
        code: args.code,
        parameters: args.parameters || [],
        transactionMode: args.transactionMode,
        documentHandle: args.documentHandle,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("send_code_to_revit", params);
        });

        return {
          content: [
            {
              type: "text",
              text: `Code execution successful!\nResult: ${JSON.stringify(
                response,
                null,
                2
              )}`,
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Code execution failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
