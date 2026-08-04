import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import {
  deleteProjectDetailed,
  getAllProjects,
  getProjectsByName
} from "../database/service.js";

export function registerDeleteStoredProjectTool(server: McpServer) {
  server.tool(
    "delete_stored_project",
    "Permanently delete a stored project, and every room stored against it, from the local database. This only touches the MCP server's own SQLite file - it never modifies the Revit model. The deletion cannot be undone, so it requires confirm: true. Identify the project by project_id or project_name; use query_stored_data with 'all_projects' first if you need to look one up.",
    {
      project_id: z
        .number()
        .optional()
        .describe("ID of the project to delete. Takes precedence over project_name."),
      project_name: z
        .string()
        .optional()
        .describe(
          "Exact name of the project to delete. Ignored when project_id is given, and refused when several projects share the name."
        ),
      confirm: z
        .boolean()
        .describe(
          "Must be true. This is an irreversible delete that also removes the project's rooms, so it will not run without explicit confirmation."
        )
    },
    async (args: any) => {
      const reply = (payload: any) => ({
        content: [{ type: "text" as const, text: JSON.stringify(payload, null, 2) }]
      });

      try {
        if (args.confirm !== true) {
          return reply({
            success: false,
            error:
              "Deletion not confirmed. Re-send with confirm: true once you are sure - this removes the project and all of its stored rooms, and cannot be undone."
          });
        }

        if (args.project_id === undefined && !args.project_name) {
          return reply({
            success: false,
            error: "Provide either project_id or project_name.",
            available_projects: getAllProjects().map((p: any) => ({
              id: p.id,
              project_name: p.project_name
            }))
          });
        }

        let projectId: number | undefined = args.project_id;

        if (projectId === undefined) {
          const matches = getProjectsByName(args.project_name);

          if (matches.length === 0) {
            return reply({
              success: false,
              error: `No project named '${args.project_name}' was found.`,
              available_projects: getAllProjects().map((p: any) => ({
                id: p.id,
                project_name: p.project_name
              }))
            });
          }

          // Refuse rather than guess: picking the first of several would delete
          // the wrong project's rooms with no way back.
          if (matches.length > 1) {
            return reply({
              success: false,
              error: `${matches.length} projects are named '${args.project_name}'. Re-run with project_id to say which one.`,
              matches
            });
          }

          projectId = matches[0].id;
        }

        const result = deleteProjectDetailed(projectId);

        if (!result) {
          return reply({
            success: false,
            error: `No project with ID ${projectId} was found.`,
            available_projects: getAllProjects().map((p: any) => ({
              id: p.id,
              project_name: p.project_name
            }))
          });
        }

        return reply({
          success: result.deleted,
          message: result.deleted
            ? `Deleted project '${result.project_name}' (ID ${result.project_id}) and ${result.rooms_deleted} stored room(s).`
            : `Project '${result.project_name}' (ID ${result.project_id}) was not deleted.`,
          project_id: result.project_id,
          project_name: result.project_name,
          rooms_deleted: result.rooms_deleted
        });
      } catch (error: any) {
        return reply({
          success: false,
          error: `Failed to delete project: ${error.message}`
        });
      }
    }
  );
}
