# New city checklist

Ordered steps for a fresh map through the cs2 MCP tools. The reasons behind each rule are in
[PLAYBOOK.md](PLAYBOOK.md). Start a new memory note for the city (progress log with coordinates) as soon
as the first road is down; keep coordinates there, not in the repo.

## 0. Before the first road
- [ ] `cs2_ping`: game in `Game` mode. `cs2_city_digest` for starting money and unlocks.
- [ ] `cs2_terrain` and `cs2_gridmap groundWater` (parse the saved JSON with `python -I -c`); note water,
      slopes, flat land, groundwater pockets.
- [ ] `cs2_list_map_tiles`: owned tiles, features (buildable, groundwater, fertile, ore, oil, water),
      neighbours worth buying later.
- [ ] `cs2_list_roads` near the edge: where the outside highway and high-voltage line enter.
- [ ] Sketch the zones: homes upwind and near clean water; industry downwind, off groundwater, with a short
      truck route to the highway; a utility strip (turbines, landfill) between them. Guess the wind from
      map prevailing direction for now; confirm from the plume once industry runs.

## 1. Infrastructure (before zoning)
- [ ] Highway exit → main avenue (Medium) into the city. Plan a second exit location now.
- [ ] Utility strip road (Small) away from homes: wind turbines via `cs2_place_roadside`, 140 m apart,
      rotors inside owned tiles.
- [ ] Groundwater pump upstream (`cs2_prefab_info` for size, `cs2_place_roadside`); sewage outlet downstream.
- [ ] Check `cs2_notifications`: no Not Connected / no power / no water.

## 2. Road skeleton and grid
- [ ] Collector loop off the avenue; local Small streets at 112 m pitch (116 m beside a Medium).
- [ ] Blocks 200–280 m, every street joined at both ends, junctions ≥ 60 m apart.
- [ ] Industry gets its own collector to the highway; no truck route through homes.
- [ ] `cs2_batch` for many ops; `cs2_connect_road` with `snap: 3`.
- [ ] `cs2_road_graph` for the area: no 1-segment nodes.

## 3. Zone to demand
- [ ] `cs2_demand`; zone low-density residential inside blocks, commercial on the avenue, industry in
      its district. Circles inside one block (radius ≤ half block depth).
- [ ] `cs2_run_simulation` (e.g. 24–48 h at speed 8); re-check demand and `cs2_zoning` empty cells
      before adding more.

## 4. Services as they unlock
- [ ] Each milestone: `cs2_milestones`, `cs2_dev_tree` → spend points; `cs2_find_prefabs` for new unlocks.
- [ ] Clinic, elementary school, police, fire, landfill (downwind), cemetery, parks; placed flush with
      `cs2_place_roadside` after `cs2_buildings_near`.
- [ ] Confirm Import City Services is OFF (`cs2_policies` / budget) once services exist.
- [ ] Save under a new name after each milestone; verify the file on disk.

## 5. Growth to ~50k
- [ ] Measure the pollution plume (`cs2_gridmap airPollution`); move industry downwind early if it hits
      homes (rezoning occupied cells condemns them; stage it).
- [ ] Second highway interchange and a second entrance for each district.
- [ ] Depots first, then bus lines feeding a hub; every new district gets a line at build time.
- [ ] Trunk pipes past ~10k people; power checked at night with ≥ 20% margin.
- [ ] Buy tiles before land runs out (`cs2_tiles_info` for cost).

## 6. Scaling past 50k
- [ ] Arterials cross highways grade-separated ~every 500 m; highway access only at interchanges ~1 km apart.
- [ ] Subway/train trunks (depot/yard first, curves ≥ 150 m radius, long lines for more trains); trams on
      medium corridors.
- [ ] Big power plants (solar, geothermal on a hotspot) with a transformer right beside each; electricity
      budget 120%.
- [ ] Spread garbage capacity (recycling, incinerator) across the city.
- [ ] Densify near rail stops in 2-row stages; parks within ~400 m of dense housing.

## Every session
- [ ] One `cs2_city_digest`; fix utilities → warnings → services → demand → budget → traffic.
- [ ] Ids from earlier sessions are stale (renumbered on load); look them up again.
- [ ] Save, verify on disk, update the city memory note.
