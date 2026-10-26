import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerIfcRebuildStatusTool(server: McpServer) {
  server.tool(
    "ifcrebuild_status",
    "Read an IfcRebuild job started with ifcrebuild_run: state (queued, running, done, failed), progress, elapsed time, the warnings Revit raised and IfcRebuild deleted, and when it has ended its result and the path of the result file. Without jobId it lists the running job, the queue length and the recent jobs. It answers while Revit is busy with a job, because it does not need Revit's thread. A result starting with 'ERROR' or 'NOT SAVED' means the method did not do what was asked, whatever the state says.",
    {
      jobId: z
        .string()
        .optional()
        .describe("Job id returned by ifcrebuild_run. Omit to list the recent jobs."),
      waitMs: z
        .number()
        .int()
        .min(0)
        .max(100000)
        .optional()
        .describe("With jobId: wait up to this long for the job to end before answering, instead of polling in a tight loop."),
      maxChars: z
        .number()
        .int()
        .positive()
        .optional()
        .describe("Longest result text to return (default 20000); the full result is always in resultFile."),
    },
    async (args, extra) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("ifcrebuild_status", args);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `IfcRebuild status failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
