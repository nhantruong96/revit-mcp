import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerSayHelloTool(server: McpServer) {
  server.tool(
    "say_hello",
    "Connection test. Returns the Revit version, the active document and the active view. Shows nothing in Revit - no dialog, nothing to click away.",
    {
      message: z
        .string()
        .optional()
        .describe("Optional text echoed back in the response. Defaults to 'Hello MCP!'"),
    },
    async (args, extra) => {
      const params = args;
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("say_hello", params);
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(response, null, 2),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Say hello failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
