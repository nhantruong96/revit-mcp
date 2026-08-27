# revit-mcp

**Connect AI assistants to Autodesk Revit via the Model Context Protocol.**

revit-mcp enables AI clients like Claude, Cline, and other MCP-compatible tools to read, create, modify, and delete elements in Revit projects. It consists of three components: a TypeScript MCP server that exposes tools to AI, a C# Revit add-in that bridges commands into Revit, and a command set that implements the actual Revit API operations.

> [!NOTE]
> This is a fork of the original [revit-mcp](https://github.com/mcp-servers-for-revit/revit-mcp) project with additional tools and functionality improvements.

## Architecture

```mermaid
flowchart LR
    Client["MCP Client<br/>(Claude, Cline, etc.)"]
    Server["MCP Server<br/><code>server/</code>"]
    Plugin["Revit Plugin<br/><code>plugin/</code>"]
    CommandSet["Command Set<br/><code>commandset/</code>"]
    Revit["Revit API"]

    Client <-->|stdio| Server
    Server <-->|WebSocket| Plugin
    Plugin -->|loads| CommandSet
    CommandSet -->|executes| Revit
```

The **MCP Server** (TypeScript) translates tool calls from AI clients into WebSocket messages. The **Revit Plugin** (C#) runs inside Revit, listens for those messages, and dispatches them to the **Command Set** (C#), which executes the actual Revit API operations and returns results back up the chain.

## Requirements

- **Node.js 18+** (for the MCP server)
- **Autodesk Revit 2023 - 2027** (any supported version)

## Quick Start (Using a Release)

1. Download the ZIP for your Revit version from the [Releases](https://github.com/nhantruong96/revit-mcp/releases) page (e.g., `revit-mcp-v1.0.0-Revit2025.zip`)

2. Extract the ZIP and copy the contents to your Revit addins folder:
   ```
   %AppData%\Autodesk\Revit\Addins\<your Revit version>\
   ```
   After copying you should have:
   ```
   Addins/2025/
   ├── revit-mcp.addin
   └── revit_mcp_plugin/
       ├── RevitMCPPlugin.dll
       ├── ...
       └── Commands/
           └── RevitMCPCommandSet/
               ├── command.json
               └── 2025/
                   ├── RevitMCPCommandSet.dll
                   └── ...
   ```

3. Configure the MCP server in your AI client (see [MCP Server Setup](#mcp-server-setup))

4. Start Revit — if prompted about an unknown add-in, click **Always Load**

5. In Revit, click the **Settings** button on the revit-mcp ribbon tab, enable the commands you want to use, and click **Save**

6. Click the **Revit MCP Switch** button on the same ribbon panel to start the server — the button label flips to `MCP Server ON`. The server does not start automatically; without this step the AI client cannot reach Revit.

> **Note:** Enabled commands are loaded when the server starts. If you change which commands are enabled while the server is already running, click **Revit MCP Switch** twice (off, then on) to reload them — otherwise calls fail with `Method '<name>' not found`.

## MCP Server Setup

The MCP server is published as an npm package and can be run directly with `npx`.

> **Known issue with `npx`:** the currently published version (`1.0.0`) depends on `better-sqlite3` v11, which ships no prebuilt binary for Node 22+. On those versions npm falls back to compiling from source with `node-gyp`, which fails unless Visual Studio with the "Desktop development with C++" workload is installed — the server then exits before it can start. The fix (bumping to `better-sqlite3` v12) is already in this repository but has not been released to npm yet. Until it is, either pin Node 20 or use [Running from source](#running-the-server-from-source) below.

**Claude Code**

Run this in a **terminal** (not inside Claude Code):

```bash
claude mcp add revit-mcp -- cmd /c npx -y mcp-server-for-revit
```

**Claude Desktop**

Claude Desktop → Settings → Developer → Edit Config → `claude_desktop_config.json`:

```json
{
    "mcpServers": {
        "revit-mcp": {
            "command": "cmd",
            "args": ["/c", "npx", "-y", "mcp-server-for-revit"]
        }
    }
}
```

Restart Claude Desktop. When you see the hammer icon, the MCP server is connected.

![Claude Desktop connection](./assets/claude.png)

### Running the server from source

This avoids npm entirely and always matches the code in this repository. Requires **Node.js 20+**.

```bash
cd server
npm ci
npm run build
```

Then point your client at the built entry point instead of `npx`:

```json
{
    "mcpServers": {
        "revit-mcp": {
            "command": "C:\\Program Files\\nodejs\\node.exe",
            "args": ["C:\\path\\to\\revit-mcp\\server\\build\\index.js"]
        }
    }
}
```

Re-run `npm run build` after changing anything under `server/src/`.

## Revit Plugin Setup

If using a release ZIP, the plugin is already included. For manual installation:

1. Build the plugin from `plugin/` (see [Development](#development))
2. Copy `revit-mcp.addin` to `%AppData%\Autodesk\Revit\Addins\<version>\`
3. Copy the `revit_mcp_plugin/` folder to the same addins directory

## Command Set Setup

If using a release ZIP, the command set is pre-installed inside the plugin. For manual installation:

1. Build the command set from `commandset/` (see [Development](#development))
2. Inside the plugin's installation directory, create `Commands/RevitMCPCommandSet/<year>/`
3. Copy the built DLLs into that folder
4. Copy `command.json` (from repo root) into `Commands/RevitMCPCommandSet/`

## Supported Tools

| Tool | Description |
| ---- | ----------- |
| `get_current_view_info` | Get current active view info |
| `get_current_view_elements` | Get elements from the current active view |
| `get_available_family_types` | Get available family types in current project |
| `get_selected_elements` | Get currently selected elements |
| `get_material_quantities` | Calculate material quantities and takeoffs |
| `ai_element_filter` | Intelligent element querying tool for AI assistants |
| `analyze_model_statistics` | Analyze model complexity with element counts |
| `create_point_based_element` | Create point-based elements (door, window, furniture) |
| `create_line_based_element` | Create line-based elements (wall, beam, pipe) |
| `create_surface_based_element` | Create surface-based elements (floor, ceiling, roof) |
| `create_grid` | Create a grid system with smart spacing generation |
| `create_level` | Create levels at specified elevations |
| `create_room` | Create and place rooms at specified locations |
| `create_dimensions` | Create dimension annotations in the current view |
| `create_structural_framing_system` | Create a structural beam framing system |
| `delete_element` | Delete elements by ID |
| `operate_element` | Operate on elements (select, setColor, hide, etc.) |
| `color_elements` | Color elements based on a parameter value |
| `tag_all_walls` | Tag all walls in the current view |
| `tag_all_rooms` | Tag all rooms in the current view |
| `export_room_data` | Export all room data from the project |
| `store_project_data` | Store project metadata in local database |
| `store_room_data` | Store room metadata in local database |
| `query_stored_data` | Query stored project and room data |
| `delete_stored_project` | Delete a stored project and its rooms from the local database (requires `confirm: true`) |
| `send_code_to_revit` | Send C# code to Revit to execute |
| `say_hello` | Connection test — returns Revit version, active document and view; shows nothing in Revit |

### Structural analysis

These tools drive the standalone analytical model Revit introduced in 2023, which is authored independently of the physical model. This is the reason Revit 2023 is the project's minimum supported version.

Lengths are in millimetres, forces in kN, moments in kN·m, line loads in kN/m and area loads in kN/m². Start with `get_structural_analysis_info` — the other tools take element ids (load cases, host elements, hubs, load symbols) and that is where they come from.

| Tool | Description |
| ---- | ----------- |
| `get_structural_analysis_info` | Read the analytical model: analytical elements, hubs, supports, load cases, combinations and load symbols |
| `create_analytical_member` | Create analytical members (analytical beams, columns and braces) |
| `create_analytical_panel` | Create analytical panels (analytical floors, walls and foundation slabs) |
| `create_analytical_link` | Create analytical links between analytical hubs |
| `create_boundary_conditions` | Create point, line and area boundary conditions (supports) |
| `create_load_case` | Create load cases, adding their load natures when missing |
| `create_point_load` | Create point loads (concentrated forces and moments) |
| `create_line_load` | Create line loads (uniform or trapezoidal) |
| `create_area_load` | Create area loads (pressures over a closed outline) |
| `create_load_combination` | Create load combinations from factored cases or nested combinations |

Every creation tool takes a `data` array and reports per-item results, so one bad item is rolled back and reported against its index without losing the rest of the batch.

**Geometry precision varies by Revit version.** The API for placing supports and hosted loads on explicit geometry arrived gradually, so on older releases some geometry is derived from the host instead. The tools still work — they just say what they did in the item's `message`:

| | Revit 2023 | Revit 2024–2026 | Revit 2027 |
| --- | --- | --- | --- |
| Hosted point load | snapped to nearest host end | exact point | exact point |
| Hosted line / area load | spans whole host | exact geometry | exact geometry |
| Point support | snapped to nearest host end | snapped to nearest host end | exact point |
| Line / area support | spans whole host | spans whole host | exact geometry |

Free-standing (unhosted) loads keep their exact geometry on every supported version.

Newly added commands default to disabled — after updating the command set, enable them in the Revit plugin's **Command Set settings** page.

### Family authoring

These tools work on family (`.rfa`) documents rather than on the project. A family opened
with `family_open_document` is a *background* document: it never becomes the active one, so
authoring does not disturb whatever the user has on screen. Every other tool in the group
takes the `documentHandle` that returns, and handles live only for the current Revit session.

Lengths cross the boundary in millimetres, and volumes come back in cubic millimetres, so
results compare directly against a hand calculation without converting anything.

| Tool | Description |
| ---- | ----------- |
| `family_open_document` | Open, create, save or close a family document in the background |
| `family_add_parameters` | Declare parameters in bulk, shared (from a shared parameter file) or plain |
| `family_set_formulas` | Assign or clear formulas on parameters that already exist |
| `family_read_parameters` | Read the parameter table back: instance flag, shared GUID, formula, value |
| `family_flex` | Push values in, regenerate, report read-back values and warnings added |
| `family_measure_geometry` | Volume, surface area and bounding box, per element and in total |
| `view_export` | Export a view to PNG or DWG, from a background document or the active one |

Two ordering rules the tools cannot enforce for you:

- **Declare every parameter before setting any formula.** A formula can only reference names
  Revit already knows, so `family_add_parameters` has to finish before `family_set_formulas`
  starts. Splitting them into two tools is what makes that hard to get wrong.
- **Never put a formula on a parameter a type catalog feeds.** A formula makes the parameter
  read-only, and Revit then ignores that catalog column without reporting anything.

`family_read_parameters` exists because writes are worth checking: a parameter that came out
as a type parameter when it had to be an instance one, or a shared parameter whose GUID does
not match the file every other family used, both look like success until something downstream
breaks. Same reasoning for `family_measure_geometry` — parameters reading back correctly does
not prove the sketch locked to the plane you meant.

## Dialogs and warnings

Nothing a command does should need someone at the Revit window. Two layers take care of that:

- **Inside a transaction** — `send_code_to_revit` and the room tools route Revit's failure messages through `McpFailurePreprocessor` (`commandset/Utils/McpFailureHandling.cs`): warnings are deleted, errors Revit offers a resolution for are resolved, anything else rolls the transaction back. The messages come back on the response as `revitMessages`, and a rolled-back transaction is reported as a failure rather than a silent success.
- **Everywhere else** — while a command is in flight, the plugin's `DialogGuard` (`plugin/Core/DialogGuard.cs`) answers every modal dialog Revit raises: OK, or Yes for a Yes/No box, falling back to Cancel when the dialog rejects those. This covers `TaskDialog.Show` calls inside handlers and dialogs raised outside transactions, such as *Duplicate Types* during `CopyElements`. Each dismissed dialog is written to the plugin log and listed under `dismissedDialogs` on the response (object results only).

The guard is only active while a request is running. With the server on but idle, dialogs you open yourself are not touched.

> **Caveat:** the guard cannot tell a progress dialog from a prompt. Dismissing Revit's rendering progress dialog cancels the render, so start renders from the Revit UI rather than through an MCP command.

## Testing

The test project uses [Nice3point.TUnit.Revit](https://github.com/Nice3point/RevitUnit) to run integration tests against a live Revit instance. No separate addin installation is required — the framework injects into the running Revit process automatically.

### Prerequisites

- **.NET 10 SDK** — required by Nice3point.Revit.Sdk 6.1.0. Install via `winget install Microsoft.DotNet.SDK.10`
- **Autodesk Revit 2027** (or 2026 / 2025) — must be installed and licensed on your machine

### Running Tests

1. Open Revit 2027 (or 2026 / 2025) and wait for it to fully load
2. Run the tests from the command line:

```bash
# For Revit 2027
dotnet test -c Debug.R27 -r win-x64 tests/commandset

# For Revit 2026
dotnet test -c Debug.R26 -r win-x64 tests/commandset

# For Revit 2025
dotnet test -c Debug.R25 -r win-x64 tests/commandset
```

> **Note:** The `-r win-x64` flag is required on ARM64 machines because the Revit API assemblies are x64-only.

Alternatively, you can use `dotnet run`:

```bash
cd tests/commandset
dotnet run -c Debug.R27
```

### IDE Support

- **JetBrains Rider** — enable "Testing Platform support" in Settings > Build, Execution, Deployment > Unit Testing > Testing Platform
- **Visual Studio** — tests should be discoverable through the standard Test Explorer

### Test Structure

| Directory | Purpose |
|-----------|---------|
| `tests/commandset/AssemblyInfo.cs` | Global `[assembly: TestExecutor<RevitThreadExecutor>]` registration |
| `tests/commandset/Architecture/` | Tests for level and room creation commands |
| `tests/commandset/DataExtraction/` | Tests for model statistics, room data export, and material quantities |
| `tests/commandset/ColorSplashTests.cs` | Tests for color override functionality |
| `tests/commandset/TagRoomsTests.cs` | Tests for room tagging functionality |

### Writing New Tests

Test classes inherit from `RevitApiTest` and use TUnit's async assertion API:

```csharp
public class MyTests : RevitApiTest
{
    private static Document _doc;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task MyTest_Condition_ExpectedResult()
    {
        var elements = new FilteredElementCollector(_doc)
            .WhereElementIsNotElementType()
            .ToElements();

        await Assert.That(elements.Count).IsGreaterThan(0);
    }
}
```

## Development

### MCP Server

```bash
cd server
npm install
npm run build
```

The server compiles TypeScript to `server/build/`. During development you can run it directly with `npx tsx server/src/index.ts`.

### Revit Plugin + Command Set

Open `revit-mcp.sln` in Visual Studio. The solution contains both the plugin and command set projects. Build configurations target Revit 2023-2027:

- **Revit 2023-2024**: .NET Framework 4.8 (`Release R23`, `Release R24`)
- **Revit 2025-2026**: .NET 8 (`Release R25`, `Release R26`)
- **Revit 2027**: .NET 10 (`Release R27`)

Building the solution automatically assembles the complete deployable layout in `plugin/bin/AddIn <year> <config>/` - the command set is copied into the plugin's `Commands/` folder as part of the build.

## Project Structure

```
revit-mcp/
├── revit-mcp.sln    # Combined solution (plugin + commandset + tests)
├── command.json     # Command set manifest
├── server/          # MCP server (TypeScript) - tools exposed to AI clients
├── plugin/          # Revit add-in (C#) - WebSocket bridge inside Revit
├── commandset/      # Command implementations (C#) - Revit API operations
├── tests/           # Integration tests (C#) - TUnit tests against live Revit
├── assets/          # Images for documentation
├── .github/         # CI/CD workflows, contributing guide, code of conduct
├── LICENSE
└── README.md
```

## Releasing

A single `v*` tag drives the entire release. The [release workflow](.github/workflows/release.yml) automatically:

- Builds the Revit plugin + command set for Revit 2023-2027
- Creates a GitHub release with `revit-mcp-vX.Y.Z-Revit<year>.zip` assets
- Publishes the MCP server to npm as [`mcp-server-for-revit`](https://www.npmjs.com/package/mcp-server-for-revit)

To create a release:

1. Run the bump script (updates `server/package.json`, `server/package-lock.json`, and `plugin/Properties/AssemblyInfo.cs`, then commits and tags):
   ```powershell
   ./scripts/release.ps1 -Version X.Y.Z
   ```

2. Push to trigger the workflow:
   ```bash
   git push origin main --tags
   ```

> [!NOTE]
> npm publish uses [trusted publishing](https://docs.npmjs.com/trusted-publishers/) via OIDC — no npm token is required. Provenance attestation is generated automatically.

## Acknowledgements

This project is a fork of the work by the [mcp-servers-for-revit](https://github.com/mcp-servers-for-revit) team. The original repositories:

- [revit-mcp](https://github.com/mcp-servers-for-revit/revit-mcp) - MCP server
- [revit-mcp-plugin](https://github.com/mcp-servers-for-revit/revit-mcp-plugin) - Revit plugin
- [revit-mcp-commandset](https://github.com/mcp-servers-for-revit/revit-mcp-commandset) - Command set

Thank you to the original authors for creating the foundation that this project builds upon.

## License

[MIT](LICENSE)
