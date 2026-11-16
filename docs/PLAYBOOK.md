# Cities: Skylines II playbook

City-agnostic rules learned building a ~195k-population city through this repo's MCP bridge.
Each rule carries its reason. Coordinates and entity ids of a specific city belong in that city's
memory notes, not here. For a fresh map, follow [NEW-CITY-CHECKLIST.md](NEW-CITY-CHECKLIST.md).
Research background and sources: [STRATEGY.md](../.claude/skills/cs2-city-review/STRATEGY.md).

Trust in-game numbers (`cs2_demand`, `cs2_budget`, `cs2_traffic`) over any guide: the economy was
reworked in patch 1.1.5f1 (Economy 2.0, June 2024) and many web guides predate it.

## 1. New-city bootstrap (first ~50k people)

1. **Site analysis before any road.** `cs2_terrain` (water, slopes), `cs2_gridmap groundWater`,
   `cs2_list_map_tiles` (features). Find the outside highway, high-voltage line and water edges.
   There is no wind tool: once industry runs, read the drift from `cs2_gridmap airPollution`.
2. **Pick zones by site.** Homes upwind and away from the shore outlet; industry downwind on land
   without groundwater, with a direct truck route to the highway. Reserve a utility strip
   (turbines, landfill, pumps) away from homes.
3. **Infrastructure before zoning:** highway link, power, a groundwater pump upstream, a sewage
   outlet downstream of every well.
4. **Road skeleton first, grid second.** Lay the main avenue/collector loop from the highway
   exit, then the 112 m local grid inside it (section 2). Plan a second exit from day one.
5. **Zone only to demand** (`cs2_demand`), starting with low-density homes, local shops and basic
   industry. Check `cs2_zoning` for empty cells first: empty zoned lots suppress demand.
6. **Services ahead of growth:** clinic, elementary school, police, fire, landfill, cemetery as each
   unlocks. Never switch on Import City Services (section 9).
7. **Spend development points every milestone** (`cs2_dev_tree`). Many "locked" buildings are
   dev-tree nodes, not milestones.
8. **Transit as soon as depots unlock;** every new district gets a line at build time.
9. **Save under a new name after each milestone and before every big change; verify on disk.**

## 2. Grid, spacing and hierarchy

- Zone cells are 8 m; lots are at most 6 cells (48 m) deep. Small Road 16 m, Medium Road 24 m.
- Parallel Small streets **112 m** apart (48+48+16); Small next to Medium **116 m**; Medium next to
  Medium **120 m**. Align new rows with the neighbouring district's coordinates.
- Blocks 200–280 m long; T-junctions ≥ 60 m apart, no junctions within 30 m of each other; every
  street joins at both ends (no dead ends, no stubs).
- Hierarchy: Small → Medium collector → arterial → highway. Only collectors and arterials meet
  arterials. Keep collectors Medium; an over-wide side road (Large) gets equal priority with the
  arterial it meets and queues the main flow.
- Every district needs **two entrances** to different points of the network, so one junction
  failing does not cut it off.

## 3. Highways and arterials

- **Arterials cross highways grade-separated about every 500 m** (overpass or tunnel), so cross-town
  trips stay off the highway. A highway that splits the main boulevard into right-in/right-out
  stubs pushes every cross-town trip onto the highway: the root cause of city-wide jams.
- **Highway access only at real interchanges, about 1 km apart.** No at-grade Ts on a highway.
- **Never connect local streets to a highway.** End them as cul-de-sacs ~20 m short and route them
  through a collector.
- **Lane balance at merges:** lanes out = lanes in. 2+2 → 4 works; 3+2 → 4 queued badly. Widen the
  carriageway after a merge, and give busy ramps 2 lanes (a 1-lane flyover carrying ~400 vehicles
  was the real limit behind a chain of queues).
- **Fixing queue heads one at a time just moves the jam.** Find the network cause (missing
  crossings, a lane drop, two lit junctions close together) before widening.
- Two signalised junctions < 40 m apart jam; merge them into one. Removing lights at tight Ts where
  flows cross caused accidents: keep them there.
- End a busy on-ramp ~60 m past a signalised junction and set that merge node `nolights`.
- A one-way couplet (two parallel `Medium Road Oneway` in opposite directions) makes every junction
  2-phase without demolition.
- Dead-end stubs off ramps attract U-turners that block the ramp: remove them.
- Freight: trucks reach the highway without crossing homes (dedicated arterial, cargo rail, harbor).
  Consider the Heavy Traffic Ban policy in residential districts.
- Measure over 8–12 in-game hours: `cs2_traffic` flow is a period average. Use `cs2_vehicles`
  queue heads at peak to find the real choke.

## 4. Junction and bridge techniques

- **Roundabouts:** `cs2_place_roundabout` on a junction id from `cs2_road_graph`; pick a size
  (`cs2_find_prefabs` netobject `round`) that fits between neighbouring junctions and buildings.
  Placing one on a junction that has one replaces it.
- **To remove a roundabout, demolish all its legs** (that removes the node) and redraw the roads.
- **Intersection stamps** (Intersections dev node, `cs2_find_prefabs` category `stamp`) occupy their
  full footprint box (240–1600 m); they only fit open land. Try rotations; failed placements cost
  nothing. **Never demolish a stamp piecewise:** leftover ghost pieces block later builds, even
  after reload. Demolish via its owner.
- **Widening clears what it overlaps:** `cs2_replace_road` to a wider prefab can clip lots,
  depots, hospitals or subway yards. Check `cs2_buildings_near` along the road first.
- **Bus stops block road widening** and `cs2_demolish` refuses them. Keep stops off arterials you may
  widen; put them on collectors, mid-block, not on corners.
- **Flyovers with `cs2_connect_road`:**
  - `e1`/`e2` are metres **above terrain**, applied only to free (unsnapped) ends; a snapped end
    takes the node's height. Build in pieces: each ends free at its `e2`, the next snaps onto it.
  - `snap: 3–5` so a free elevated end isn't pulled onto a ground junction.
  - Climb ≤ 6–8% (about +6 m per 80–100 m); reach ≥ 10–13 m clearance before the crossing.
  - Cross mid-segment: a curve through an existing node joins it at grade.
  - **Keep each span over a highway ≤ 60 m**, or the game drops a pillar between carriageways and
    the build fails.
  - Negative `e` builds tunnels; go ~12–20 m deep under roads.
  - Check `cs2_list_roads` (prefab `High-voltage Line`) for pylons first: a pylon on the alignment
    fails flyovers (OverlapExisting / SteepSlope); a tunnel can pass under it.
- Lots touching a junction corner fail validation; keep buildings clear of corners.

## 5. Transit

- **Depot before stops.** Bus stops, tram stops and subway/train stations report "locked" until a
  Bus Depot, Tram Depot, Subway Yard or train depot exists, even with the node bought.
- Hierarchy: subway/train trunks, trams on medium corridors, buses as feeders into stations.
- **Tram on a road is a road upgrade** (`cs2_upgrade_road` upgrades=tram); drawing tram track over
  a road builds nothing.
- **Subway track:**
  - Curves need ~**150 m+ radius**; tighter S-bends build fine but trains can't path
    (PathfindFailed). Put the curve's control point on the intersection of the tangents.
  - No kinks: chained straights meeting at 40–60° look connected but don't work.
  - 15–20 m deep under roads; ≥ 12 m vertical separation where lines cross.
  - Wind-turbine rotors block even underground track.
  - Connect the yard's **storage tracks** to the line, or the line gets 0 vehicles.
  - Unpowered stations stop service: keep power margin.
- **Train count scales with line length.** A short line caps at ~3 trains whatever the policy; to
  add capacity, extend the line (a 6.7 km line ran 4 trains, 9.7 km ran 6).
- **Lines can't be edited:** after track or stop changes, delete and recreate the line so the depot
  re-dispatches, then set the Route Vehicle Count policy (`cs2_set_line_policy`;
  `cs2_set_line_vehicles` only picks the model). Confirm vehicles > 0.
- Delete lines with ~0 riders or that duplicate a rail line; merge loops that stay inside one
  district into lines reaching a hub.

## 6. Power

- **Night is the binding constraint:** wind and solar output drop; measure load at night/evening,
  not midday. Keep ≥ 20–25% margin and add generation at > 80% night load.
- **Electricity service budget 120% adds ~15% output** cheaply; try it before building. Never cut
  it below 100% (caused an outage).
- **Wind turbines only via `cs2_place_roadside`**: placed by x/z they face away from the road and
  stay "Powerline Not Connected". Space them ~140 m; rotors (60 m radius) stay inside owned tiles,
  off roads and tracks. Build a dedicated small-road turbine field, not beside homes.
- **Big plants (solar, geothermal, incinerator, batteries) need a high-voltage hookup.** Put a
  TransformerStation **right beside** each plant, one short HV hop; roads carry low voltage onward.
  Long HV lines through town are impossible: pylons can't stand on roads or built lots.
- Solar needs a long clear frontage (288×552 lot). **Geothermal needs a geothermal hotspot**
  (map resource), not just groundwater; it fails everywhere else.
- Zero-emission goal: no gas/coal.

## 7. Water, sewage, garbage and utilities

- **Roads carry power and small pipes. Before removing a road, check what it carries.** Removing
  highway crossings once cut a wind farm off (820 buildings dark). Lay Low-voltage Ground Cable and
  Combined Large Pipe across gaps first.
- Past ~10k people, add trunk pipes (Large Water / Large Sewage / Combined Large) from pumps and
  outlets into each district; road pipes are small.
- Pumps on clean groundwater, upstream; outlets downstream; never pump near landfills (they poison
  the aquifer). Treat sewage before it reaches the shore.
- **Orphaned pipes and cables** left after a rebuild raise Pipeline/Powerline Not Connected
  warnings: remove them.
- Garbage: spread landfill/recycling/incinerator capacity across the city; all-in-one-corner left
  the far side short. Replace landfills with recycling and incineration as the city grows.
- Rebuild a "Facility Full" landfill or cemetery in place (demolish + place) to reset it.

## 8. Pollution and zoning

- Industry, landfills and incinerators downwind and off groundwater; a shop/office buffer strip
  between industry and homes. Re-measure the plume before each residential or civic build; it
  grows with industry.
- **Rezoning occupied cells condemns them;** the game clears and regrows. Works for moving industry
  or densifying, but do it block by block with a rollback save and keep housing demand covered.
- **Zone circles must stay inside one block** (radius ≤ half block depth from the street). A circle
  reaching across an avenue rezones and condemns the far side.
- One zone type per block face; commercial/mixed on collectors and arterials, residential inside,
  office/industry as buffers. Densify near subway/tram stops in 2-row stages.
- New medium-density lots can take 8+ in-game hours to start building.
- Noise: keep homes off highways, industry and turbine fields; pedestrian/small streets and parks.

## 9. Economy and progression

- **Import City Services (Paid Outside Services) is a trap:** at 13k people it cost ~$2.2M/month
  and flipped the budget to a deficit. Keep it OFF; build your own services.
- Move taxes 1–2 points at a time and watch the `Taxes` factor in `cs2_demand`.
- Loans are fine for a planned big build when income covers repayment.
- Upgrading or raising a service budget is often cheaper than a new building (garbage 140% fixed
  coverage gaps).
- **Milestones are XP-based** (`cs2_milestones`). Passive XP from population + happiness dominates
  at scale; milestones 11–20 unlock no buildings, only money, dev points, permits, loan limit.
- Dev-point order that worked: road services → highways/large roads → roundabouts → hospital,
  crematorium → recycling, incinerator → telecom, post sorting → advanced electricity, solar →
  water treatment → college/university → transit (train, tram, subway) → intersections.
- Buy tiles ahead of need (`cs2_list_map_tiles`, `cs2_tiles_info` for cost); keep some permits.
- Happiness levers: clinics/hospital, clean air, education, police, working utilities, traffic,
  parks. Read Chirper (screenshot while the panel is open) for exact complaints.

## 10. Operations (tool use)

- **Verify every save on disk.** `cs2_save_game` is async and can fail silently; an unverified save
  once lost a subway line. Check the Saves folder
  (`$USERPROFILE/AppData/LocalLow/Colossal Order/Cities Skylines II/Saves/`) and re-save under a new
  name if missing. Save a rollback before big road work.
- **Entity ids renumber on load.** Never reuse ids from an earlier session; look them up again.
  Segment ids also change when a road is split.
- **One build call at a time**, or `cs2_batch` for several (parallel calls fail with "another build
  operation is in progress").
- `cs2_connect_road` with `snap: 3` keeps exact coordinates; the default 8 jumps to old junctions.
- **Placement replaces whatever is on the lot,** and dry runs don't detect it. Check
  `cs2_buildings_near` first; try candidate sites one at a time, stop at the first success.
- Place services with `cs2_prefab_info` (size) + `cs2_place_roadside`, flush and packed.
- On a road heading +z, `side: right` is +x; heading +x, `side: right` is −z.
- `cs2_gridmap` / `cs2_terrain` return large files: parse with `python -I -c` and print only your
  area (256×256 row-major [z][x], worldMin −7168, cellSize 56).
- Start reviews with one `cs2_city_digest`; use filtered `cs2_road_graph` calls (small radius,
  `street`/`query`).
- Reward popups pause the game: `cs2_run_simulation` auto-dismisses them, or `cs2_dismiss_popups`.
- A road that looks like a dead-end stub may be a large building's only access; check before removing.
- Real simulation: never `force: true`.
