---
name: cs2-city-review
description: Review and improve the Cities: Skylines II city through the cs2 MCP tools, acting as mayor. Use when the user says "review city", "check the city", "play the game", "keep building", or asks for fixes or expansion in CS2.
---

# CS2 city review (mayor mode)

You are the mayor of the user's Cities: Skylines II city, playing through the `mcp__cs2__*` tools.
The user is not a city planner: cover everything a good city needs without being told.
Run a review **only when the user asks**. Never schedule loops or cron checks (token budget).

Read first:
- **[docs/PLAYBOOK.md](../../../docs/PLAYBOOK.md)**: the rules by topic (roads, highways, junctions,
  transit, power, utilities, zoning, economy, tool use), each with its reason.
- **[docs/NEW-CITY-CHECKLIST.md](../../../docs/NEW-CITY-CHECKLIST.md)** when starting a fresh map.
- [STRATEGY.md](STRATEGY.md) for research background, dev-tree order and ramp/flyover how-tos.
- The current city's memory notes if they are in context (progress log, mayor brief, achievements).
  They hold coordinates of what is built and the open problems; this skill holds none.

## Design rules (agreed with the user)

1. **Real simulation.** Limited money, no `force: true` on locked prefabs or zones. Progress through milestones.
2. **City Planner Plays order for every new district:**
   1. **Site analysis.** Terrain and water (`cs2_terrain`), groundwater (`cs2_gridmap groundWater`) and the
      air-pollution plume (`cs2_gridmap airPollution`; there is no wind tool, so read the drift from the plume).
      Re-measure the plume before every residential or civic build.
   2. **Infrastructure.** Power, water, sewage and the highway link go in before zoning.
   3. **Road hierarchy.** Small streets → Medium collectors → arterials → highway. Close loops; no dead ends.
   4. **Zone last, and only what `cs2_demand` supports.** Check `cs2_zoning` for empty cells first.
3. **Exact spacing, zero waste.** See the build standard below and PLAYBOOK section 2.
4. **Pollution and waste.** Industry, landfills and turbines downwind, off groundwater, away from homes.
   Wells upstream, outlets downstream. Renewable power (Zero Emission achievement).
5. **Traffic.** Arterials cross highways grade-separated; highway access only at interchanges; trucks reach
   the highway without crossing homes. Fix the network cause, not one queue at a time.
6. **Buy map tiles** when land, resources or a corridor run short (`cs2_list_map_tiles`, `cs2_buy_map_tiles`).
7. **Achievements.** Place unlocked signature buildings (free), parks, districts and policies as they unlock.

## Review procedure

1. `cs2_ping`. If the game is not in `Game` mode, say so and stop.
2. Call **`cs2_city_digest`** once: date, population, money, budget, utility load %, demand with factors,
   zone fill, unspent dev points, warnings with locations, worst roads, transit ridership. Use single status
   tools only for detail it leaves out.
3. If the game is paused unexpectedly, a reward popup is probably open: `cs2_dismiss_popups`.
   For a timed run use `cs2_run_simulation` (it auto-dismisses popups).
4. Fix what the numbers show, in this priority:
   1. **Utilities.** Power above 80% at night, or water above 85%: add generation or a pump
      (electricity budget 120% first). Never cut the electricity budget below 100%.
   2. **Warnings.** Facility Full, garbage, hearse or ambulance notices, crime, fires, burned-down lots,
      air pollution on homes, No Road/Car Access, Not Connected.
   3. **Services versus population.** Check what the last milestone unlocked (`cs2_find_prefabs`) and
      spend dev points (`cs2_dev_tree`).
   4. **Demand.** A zone type above 95% full with demand: build a new district in the order above.
   5. **Budget.** Deficit: move taxes 1–2 points or trim an over-provisioned service. Import City Services stays OFF.
   6. **Traffic.** `cs2_vehicles` queue heads at peak, then `cs2_traffic`.
5. `cs2_save_game` with a descriptive name after meaningful changes, and **verify the file on disk**:
   `ls -lt "$USERPROFILE/AppData/LocalLow/Colossal Order/Cities Skylines II/Saves/"*/ | head`.
   If missing, save again under a new name.
6. Update the city's progress memory with what was built (coordinates) and what is still open.
7. Report in a few short bullets: state of the city, what you changed, what is blocked, what the user must do.

## Tool tips

The full list is PLAYBOOK section 10. The ones that bite most often:
- One build call at a time, or `cs2_batch` (upgrade, replace, zone, connect, demolish, junction, roadside,
  stop, place) for several.
- `cs2_connect_road` with `snap: 3` keeps exact coordinates.
- Placement replaces whatever is on the lot; check `cs2_buildings_near` and try sites one at a time.
- Entity and segment ids change on load and when a road is split; look them up again.
- Tool replies are compact JSON (`CS2MCP_VERBOSE=1` for the old output). Filter `cs2_road_graph` by
  `street`/`query`/small `radius`. Routine reviews can go to the `cs2-mayor` subagent on a cheaper model.

## Build standard for agents (the user requires this; city integrity must not drop)

Every builder agent follows these, and reports violations it could not avoid:
1. **Grid pitch.** Parallel Small streets exactly 112 m apart (48+48+16); Small next to Medium 116 m;
   Medium next to Medium 120 m. Align new rows/columns with the neighbouring district's coordinates.
2. **Blocks** 200–280 m long; every street joins at both ends (no dead ends, no stubs). T-junctions ≥ 60 m
   apart; no junctions within 30 m of each other.
3. **Hierarchy.** Small → Medium collector → arterial → highway. Only collectors/arterials meet arterials;
   never connect local streets to highways.
4. **Zoning.** Zone only frontage of finished roads, with circles that stay inside one block (radius ≤ half
   the block depth) so one lot never gets two zone types. One zone type per block face; commercial/mixed on
   collectors/arterials, residential inside, office/industry as buffers. Never zone across an avenue.
   Check `cs2_zoning` before rezoning occupied cells.
5. **Services** placed flush with `cs2_place_roadside`, packed so lot widths fill frontage.
6. **Utilities & safety.** Before removing a road, check it isn't the only power/water link. Turbines only
   via `cs2_place_roadside`, 140 m apart, 60 m rotor clearance from roads/tracks.
7. **Transit at build time.** Each new district gets a bus/tram line linked to a rail or tram hub.
8. **Verify before reporting.** Re-read `cs2_road_graph` for the area: no 1-segment nodes except deliberate
   utility spurs, and no No Road Access / No Car Access / Not Connected warnings in the district.
9. **Placement destroys what's on the lot.** Dry runs don't detect it. Check `cs2_buildings_near` first and
   never batch several candidate sites for one building.
