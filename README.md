# CS2MCP — An MCP Server for Cities: Skylines II

**English** | [简体中文](README.CHS.md)

Give Claude (or any MCP client) **full control** over a running Cities: Skylines II game: read city data, drive the camera and take screenshots, build roads and buildings, paint zones, manage finances and policies, and control simulation time.

## Architecture

```
Claude Code / Claude Desktop (any MCP client)
      │  MCP (stdio)
      ▼
cs2-mcp  (mcp-server/, Node.js process)
      │  HTTP, localhost-only 127.0.0.1:8642
      ▼
CS2MCP bridge mod  (CS2MCP.Bridge/, inside the game process)
      │  all ECS reads/writes run on the simulation main thread
      ▼
Cities: Skylines II
```

- **In-game mod** (C#): runs a minimal HTTP server on a `TcpListener` bound to 127.0.0.1 inside the game process. Requests are queued by listener threads and executed on the simulation main thread by an ECS system registered at `SystemUpdatePhase.UIUpdate` (works while paused). Construction goes through a custom `ToolBaseSystem` using the game's native definition/validation/apply pipeline; demolition goes through the bulldozer pipeline — no entity mutations that bypass game validation.
- **MCP server** (TypeScript): translates MCP tool calls into HTTP requests against the bridge mod, over stdio transport.

## Requirements

- Windows + Cities: Skylines II (Steam)
- .NET SDK 8.0+ (to build the mod)
- Node.js 18+ (to run the MCP server)

## Build & Install

```powershell
# 1. Build the in-game mod (auto-deploys to the game's local Mods folder)
dotnet build CS2MCP.Bridge\CS2MCP.Bridge.csproj

# 2. Build the MCP server
cd mcp-server
npm install
npm run build
```

The mod deploys to `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\CS2MCP\` and loads automatically on game start — no Paradox Mods publishing required. To disable it temporarily, rename the folder to `.CS2MCP`.

### Game path configuration

Building the mod references assemblies from the game folder. Resolution order (first match wins):

1. Command line: `dotnet build -p:GamePath="X:\...\Cities Skylines II"`
2. **`CS2_PATH`** environment variable
3. `CSII_INSTALLATIONPATH` environment variable (set by the official modding toolchain)
4. Auto-probing of common Steam library locations (`C:\Program Files (x86)\Steam`, `Steam` / `SteamLibrary` on common drive letters)

If the game cannot be found the build fails with a clear error. Example:

```powershell
setx CS2_PATH "D:\Steam\steamapps\common\Cities Skylines II"
```

### Runtime environment variables (.env supported)

The MCP server loads `mcp-server/.env` via [dotenv](https://github.com/motdotla/dotenv) (see `.env.example`):

| Variable | Scope | Default | Description |
|---|---|---|---|
| `CS2_BRIDGE_URL` | MCP server | `http://127.0.0.1:8642` | Address of the bridge mod |
| `CS2MCP_PORT` | game process | `8642` | Bridge listen port (change both together) |
| `CS2_PATH` | build time | auto-probed | Game install folder |

## Using with Claude

**Claude Code**: [.mcp.json](.mcp.json) in the repository root registers the `cs2` server — just open Claude Code in the project directory (it asks for trust on first use). To register from another directory:

```powershell
claude mcp add cs2 -- node <repo-path>\mcp-server\dist\index.js
```

**Claude Desktop** (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "cs2": {
      "command": "node",
      "args": ["<repo-path>\\mcp-server\\dist\\index.js"]
    }
  }
}
```

Start the game, load a save, then ask Claude: "How are my city's finances?", "Zone a residential area by the river", "Build a road connecting the industrial area to the highway".

## Tool Reference (62 tools)

**State & view**

| Tool | Description |
|---|---|
| `cs2_ping` | Bridge liveness, mod version, game mode (responds even while loading) |
| `cs2_game_state` | Mode, city name, pause/speed, in-game date & time |
| `cs2_city_overview` | Population, happiness, health, money, XP, date |
| `cs2_screenshot` | Current view as PNG (end-of-frame capture, default 1280px wide) |
| `cs2_get_camera` / `cs2_set_camera` | Camera read/write (pivot/angles/zoom) — combined with screenshots, the AI's own eyes |

**City data**

| Tool | Description |
|---|---|
| `cs2_demand` | RCI demand + demand factors (internal 0-255 scale; refreshes only while simulating) |
| `cs2_budget` | Budget breakdown: 14 income / 15 expense sources |
| `cs2_city_services` | Electricity, water & sewage, garbage status |
| `cs2_labor` | Employment, unemployment, jobs by education level, age structure |
| `cs2_statistics` | Time series for 60+ statistics (32 samples per in-game day) |
| `cs2_terrain` | Whole-map heightmap + water depth grid |
| `cs2_gridmap` | Native cell-map layers: land value, ground/air/noise pollution, ground water |
| `cs2_zoning` | Zoning summary (occupied/empty per zone type) |
| `cs2_notifications` | All in-world warning icons (no power/water, abandoned...) with target entities |
| `cs2_traffic` | City-wide traffic flow, worst roads (flow %, volume, four daily periods) and traffic bottlenecks, with the game's own numbers |
| `cs2_vehicles` | Census of driving vehicles by type and destination, what stopped vehicles wait for, the queue heads holding traffic up and any gridlock loops |
| `cs2_road_graph` | Road segments of an area with their junctions, direction, flow and traffic lights (optionally every network, incl. station tracks) |
| `cs2_inspect` | Single-entity detail (residents/employees/status flags) |

**Construction**

| Tool | Description |
|---|---|
| `cs2_find_prefabs` | Search building/road/network/tree prefabs by name |
| `cs2_place_building` | Place buildings/trees (terrain-sampled height, rotation, native validation) |
| `cs2_build_road` | Any network segment: straight / curved via `cx,cz` / elevated via `e1,e2` (bridges, ramps) |
| `cs2_upgrade_road` | Road upgrades: grass/trees/wide sidewalk/sound barrier/parking/lighting/median |
| `cs2_set_junction_control` | Set a junction to traffic lights, no lights, an all-way stop or the game default (the intersection tools' node upgrades) |
| `cs2_replace_road` | Change the road type of existing segments in place (the road tool's Replace mode): widen a highway, make a street one-way |
| `cs2_connect_road` | Build a road or track whose ends snap to existing junctions or split existing segments (like the road tool's snapping), optionally raised or lowered |
| `cs2_prefab_info` | Footprint, size, placement rules and cost of a building, or width and layers of a network |
| `cs2_place_shoreline` | Place a harbor, pump or other shoreline building on the water's edge, snapped like the game's placement tool |
| `cs2_place_roadside` | Place a station, depot or service building flush against a road segment, facing it |
| `cs2_buildings_near` | Buildings around a point with kind, footprint and facing, to find free spots or what to clear |
| `cs2_zone_area` / `cs2_list_zones` | Paint zoning (`None` to clear) and list zone types |
| `cs2_demolish` | Demolish buildings/segments/trees/districts (bulldozer pipeline) |
| `cs2_list_buildings` / `cs2_list_roads` / `cs2_list_objects` | Entity listings (ids, coordinates) |

**Finance & policy**

| Tool | Description |
|---|---|
| `cs2_get_taxes` / `cs2_set_tax` | Tax rates for the four zone classes (clamped to game limits) |
| `cs2_policies` / `cs2_set_policy` | City policies (with localized names) |
| `cs2_service_budgets` / `cs2_set_service_budget` | Per-service budget sliders 50-150% |
| `cs2_get_fees` / `cs2_set_fee` | Service fees (electricity/water/healthcare/education...) |
| `cs2_get_loan` / `cs2_set_loan` | Borrow / repay loans |
| `cs2_list_districts` / `cs2_create_district` | List districts / draw a district polygon |
| `cs2_district_policies` / `cs2_set_district_policy` | District policies |
| `cs2_tiles_info` | Owned map tiles / upkeep info |
| `cs2_list_map_tiles` / `cs2_buy_map_tiles` | List tiles (ownership, natural features, permits left) / buy tiles through the game's own purchase logic (price, permits and funds checked by the game) |

**Public transport**

| Tool | Description |
|---|---|
| `cs2_list_transit_stops` | Stops with type, name, position, owning station, attached road and the lines serving them; `airplane` lists airport gates (passenger or cargo) and the map's air outside connections |
| `cs2_place_transit_stop` | Place a roadside bus/tram stop via the object tool pipeline (attached, snapped and validated by the game) |
| `cs2_create_transit_line` | Line through ordered stops (ids, station buildings or points snapped to stops) via the route tool pipeline; waits for the game's pathfinding and refuses unreachable segments |
| `cs2_list_transit_lines` | Lines with the transportation panel's numbers (stops, vehicles, passengers on board, usage, length, schedule) and optional per-stop waiting passengers |
| `cs2_delete_transit_line` | Delete a line exactly like the game's own delete button |
| `cs2_line_policies` / `cs2_set_line_policy` | A line's policies (ticket price, vehicle count...) and setting them like the line panel does |
| `cs2_line_vehicles` / `cs2_set_line_vehicles` | Vehicle models a line can use, with capacity, and choosing the model new vehicles use, like the line panel's vehicle selection |

**Time & meta**

| Tool | Description |
|---|---|
| `cs2_set_simulation` | Pause / set speed (0-8) |
| `cs2_run_simulation` | Timed run: simulate N in-game hours, then auto-pause |
| `cs2_save_game` | Trigger a save (recommended before large AI operations) |

## Troubleshooting

- **`cs2_ping` unreachable**: the mod is not loaded. Check `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\CS2MCP.log` (should contain `bridge listening on ...`); if the file is missing, check `Player.log` in the same folder.
- **409 no city loaded**: still in the main menu — load a save first.
- **Lock states look wrong**: unpause briefly once after loading (lock-related endpoints return a `stalenessWarning` until the simulation has ticked).
- **Port conflict**: set `CS2MCP_PORT` for the game process and update `CS2_BRIDGE_URL` in `.env` to match.

## Known Limitations / Roadmap

- Terraforming is not implemented yet
- Transit: stops can be placed for bus/tram (roadside); train/metro stops are station buildings (place with `cs2_place_building`) that must be connected by tracks. Lines only get vehicles when a depot of the same type exists and the simulation runs
- Ramps can only connect at segment endpoints (nodes); mid-segment smooth merges are not supported yet
- Screenshots capture the game's current rendering: with a road-tool panel open the game renders roads in white outline mode

## Disclaimer & Credits

- This is an unofficial community mod, not affiliated with Colossal Order or Paradox Interactive.
- `CS2MCP.Bridge/CreateDefinitions.cs` is ported from [LineTool-CS2](https://github.com/algernon-A/LineTool-CS2) (Apache-2.0, © algernon) and contains portions derived from decompiled game code, subject to the Paradox Interactive User Agreement.
- Thanks to the CS2 modding community — the public code of LineTool, InfoLoom, Traffic, unity-mcp and others made this project possible.

## License

[Apache-2.0](LICENSE)
