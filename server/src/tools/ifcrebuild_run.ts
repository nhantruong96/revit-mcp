import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerIfcRebuildRunTool(server: McpServer) {
  server.tool(
    "ifcrebuild_run",
    "Queue one IfcRebuild method in Revit and return its job id at once; poll it with ifcrebuild_status. IfcRebuild rebuilds a native Revit model from data extracted from an IFC file (levels and grids, loadable and system families, steel framing, Steel Connections plates and bolts, rebar, doors, stairs, joins, IFC parameters, worksets) and checks the result (gate, QA export, snapshot). Builds take minutes to hours, far past the 2-minute limit of a normal request, so they run as background jobs on Revit's thread, one after another. Run the method 'Methods' to list every method with its argument names. Methods that change the model take mode 'probe' (everything rolled back, also the default when mode is left empty) or 'apply'; probe first. Needs the IfcRebuild command set enabled in the Revit MCP plugin settings.",
    {
      method: z
        .string()
        .describe("Entry method name, e.g. 'Ping', 'Methods', 'Setup', 'Build', 'System', 'Frame', 'Steel', 'Rebar', 'Params', 'Gate', 'Save', 'ExportQa', 'Snapshot'."),
      args: z
        .array(z.string())
        .optional()
        .describe("String arguments of the method, in order (the document is passed by Revit). Missing trailing arguments are passed as empty strings, which means the default (e.g. mode probe, no report file)."),
      config: z
        .string()
        .optional()
        .describe("Path of a JSON file with the project conventions (family template folder, type names, rebar hook type, worksets...). Omit for the defaults."),
      dll: z
        .string()
        .optional()
        .describe("Development only: path of a separately built IfcRebuildCommandSet.dll; a fresh copy of it runs instead of the installed one, so changed builder code needs no Revit restart."),
      waitMs: z
        .number()
        .int()
        .min(0)
        .max(100000)
        .optional()
        .describe("Wait up to this long after the job starts for it to finish and return its result directly. Use for short methods (Ping, Gate, Methods); leave 0 for builds."),
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
          return await revitClient.sendCommand("ifcrebuild_run", { ...args, args: args.args || [] });
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `IfcRebuild run failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
