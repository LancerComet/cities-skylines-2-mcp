# CS2 strategy reference

The rules themselves live in the playbook: **[docs/PLAYBOOK.md](../../../docs/PLAYBOOK.md)** (by topic)
and **[docs/NEW-CITY-CHECKLIST.md](../../../docs/NEW-CITY-CHECKLIST.md)** (ordered steps for a fresh map).
This file keeps the research background behind them and the longer how-tos. Nothing here depends on a
particular save; a city's coordinates and history belong in its memory notes.

Most web guides date from the 2023 launch. The economy was reworked in patch 1.1.5f1 ("Economy 2.0",
June 2024): no government subsidies, and taxes affect demand more strongly. Trust the in-game numbers
(`cs2_demand`, `cs2_budget`) over any guide.

## Development points

- Points come only from milestones. A node needs just its parent node, not other branches.
- Check `cs2_dev_tree` after every milestone. A city once sat on 36 unspent points while the hospital,
  highways, telecom, recycling and solar all looked "milestone-locked".
- Spend on what fixes a current problem. Order that matched community consensus and worked in practice:
  Advanced Road Services → Highways and Large Roads → Roundabouts → Hospital, Crematorium → Recycling,
  Incinerator → Telecom Tower, Post Sorting → Advanced Electricity, Solar → Water Treatment → College,
  University → Welfare, Police HQ → Park Maintenance, Large Parks → Train, Tram, Subway → Intersections.
- Late game: unique buildings (space centre, radio telescope, central bank, airport) add attractiveness
  and XP; never gas power if going for Zero Emission.

## Grade-separated ramps with `cs2_connect_road`

How the elevation parameters work (from `BridgeToolSystem.cs` and `BridgeRoadToolSystem.cs`):
- `e1`/`e2` are metres **above the terrain** at that point, not absolute height. The curve follows the
  ground and is lifted by a linear blend from `e1` to `e2`. Over sloping ground, aim for clearance over
  the road below, not a fixed height.
- They apply only to **free** ends. An end that snaps to an existing node or segment takes that node's
  height. Start a ramp snapped at grade (or to an elevated node) and end each piece free with `e2` set;
  the next piece snaps onto that node and inherits its height.
- Use `snap` 3–5 so the free elevated end isn't pulled onto a nearby ground junction. Never let a
  piece's start or end sit within snap range of the road being crossed.
- Climb about 6–8% (e.g. +6 m per 80–100 m) or validation fails. Reach ~13 m clearance before the
  crossing, cross mid-segment, descend the same way. Keep the span over a highway ≤ 60 m.
- Negative values make tunnels; an underpass needs the same ramp length below ground.
- Left exits rarely fit between close carriageways. A **loop ramp** works instead: exit right, turn
  270° while climbing, cross both carriageways at a right angle ~13 m up, come down into the grid.

## Alternative intersections (FHWA)

DDI, Michigan left, RCUT and continuous-flow designs all remove left turns across oncoming traffic,
cutting signal phases and conflict points (RCUT: 18 vs 32). The cheapest CS2 forms:
- **One-way couplet:** two parallel avenues as `Medium Road Oneway` in opposite directions (same 24 m
  width, no demolition; `cs2_replace_road` with `invert` sets direction). Match tram/bus loops to it.
- **Merge past the signal:** end an inbound ramp ~60 m beyond the signalised junction (snap 2) and set
  the merge node `nolights`.

## Analysis how-tos

- **Draw the network:** dump `cs2_road_graph` for a large radius and render it (e.g. Pillow), coloured
  by flow, to see chokes and one-way directions before redesigning.
- **Wind direction:** there is no wind tool. Read `cs2_gridmap airPollution` around existing industry
  and note which way the plume extends.
- **Groundwater and resources:** `cs2_gridmap groundWater`, `cs2_list_map_tiles` features
  (buildable, groundwater, fertile, ore, oil, water).
- **Chirper** names exact shortfalls ("crime", "packages taking ages", "no internet", "smog", "noise",
  "can't find a job"). The MCP can only read it from a screenshot while the panel is open.

## Happiness

Happiness combines wellbeing and health. Levers: clinics then hospitals, clean air and ground, five
education levels (elementary, high school, college, university), police coverage, working utilities
(water, power, garbage, mail, internet), traffic flow, parks. Passive XP scales with population and
happiness, so quality pays for progression.

## Sources

- Economy 2.0 patch: [GamesRadar](https://www.gamesradar.com/games/city-builder/cities-skylines-2-finally-unleashes-its-huge-economy-20-patch-with-reworked-rent-and-a-fix-for-death-waves-but-itll-also-kill-a-bunch-of-your-citizens), [Paradox dev diary, economy part two](https://www.paradoxinteractive.com/games/cities-skylines-ii/news/dev-diary-economy-part-two)
- Development tree: [Shacknews](https://shacknews.com/article/137520/cities-skylines-2-best-development-upgrades), [Prima Games](https://primagames.com/tips/best-development-tree-unlocks-cities-skylines-2), [VideoGamer](https://www.videogamer.com/guides/cities-skylines-2-development-tree-points)
- Traffic: [Prima Games](https://primagames.com/tips/how-to-manage-traffic-cities-skylines-2), [gameplay.tips](https://gameplay.tips/guides/cities-skylines-ii-traffic-guide.html), [positioniseverything](https://www.positioniseverything.net/how-to-fix-traffic-jams-and-gridlock-in-cities-skylines-original-and-ii/), [Steam discussion](https://steamcommunity.com/app/949230/discussions/0/4030223299336614187)
- Cargo: [Paradox: public cargo transportation](https://www.paradoxinteractive.com/games/cities-skylines-ii/features/public-cargo-transportation), [Paradox: Bridges & Ports dev diary](https://www.paradoxinteractive.com/games/cities-skylines-ii/news/bridges-and-ports-dev-diary-ports), [Steam discussion](https://steamcommunity.com/app/949230/discussions/0/4202489789153796692)
- Pollution and garbage: [GamesRadar pollution guide](https://gamesradar.com/cities-skylines-2-pollution-guide), [VideoGamer garbage guide](https://www.videogamer.com/guides/cities-skylines-2-garbage), [Stealth Optional noise guide](https://stealthoptional.com/guides/cities-skylines-2-noise-pollution/)
- Happiness: [Paradox: citizen simulation](https://www.paradoxinteractive.com/games/cities-skylines-ii/features/citizen-simulation-lifepath), [Prima Games](https://primagames.com/gaming/how-to-increase-citizen-happiness-in-cities-skylines-ii)
