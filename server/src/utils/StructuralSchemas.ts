import { z } from "zod";
import { withRevitConnection } from "./ConnectionManager.js";

/**
 * A 3D point in millimetres, matching the coordinate convention the rest of the
 * Revit tools use.
 */
export const pointSchema = z.object({
  x: z.number().describe("X coordinate in mm"),
  y: z.number().describe("Y coordinate in mm"),
  z: z.number().describe("Z coordinate in mm"),
});

/**
 * A force or moment vector. The unit depends on the load - see each tool's description.
 * Downward gravity load is negative Z.
 */
export const vectorSchema = z.object({
  x: z.number().default(0).describe("X component"),
  y: z.number().default(0).describe("Y component"),
  z: z.number().default(0).describe("Z component"),
});

/**
 * The fields every load shares: which case it belongs to, which symbol it uses, and
 * how its vectors are oriented.
 */
export const loadCommonFields = {
  loadCaseId: z
    .number()
    .optional()
    .describe("Element id of the load case. Takes precedence over loadCaseName."),
  loadCaseName: z
    .string()
    .optional()
    .describe(
      "Load case name, resolved against the model. Omit to let Revit use its default case."
    ),
  loadTypeName: z
    .string()
    .optional()
    .describe("Load symbol name. Defaults to the first symbol of the matching kind in the model."),
  hostElementId: z
    .number()
    .optional()
    .describe(
      "Analytical element the load is hosted on. Omit for a free-standing load placed on the work plane."
    ),
  orientTo: z
    .enum(["Project", "WorkPlane", "HostLocalCoordinateSystem"])
    .optional()
    .describe("Coordinate system the load vectors are expressed in"),
  isReaction: z
    .boolean()
    .default(false)
    .describe("Mark the load as a reaction rather than an applied load"),
};

/**
 * Send a structural analysis command to Revit and format the reply. Every analysis
 * tool returns the same envelope, so the handling is identical.
 */
export async function sendStructuralCommand(
  commandName: string,
  params: unknown
): Promise<{ content: Array<{ type: "text"; text: string }> }> {
  try {
    const response = await withRevitConnection(async (revitClient) => {
      return await revitClient.sendCommand(commandName, params);
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
          text: `${commandName} failed: ${
            error instanceof Error ? error.message : String(error)
          }`,
        },
      ],
    };
  }
}
