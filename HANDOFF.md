# Angler's Art of Fishing – Handoff Instructions

You are continuing work on a Vintage Story mod (modid `anglersartoffishing`) in this folder.
Read this whole file first, then read the source files listed below before editing anything.

## Goal
A fishing minigame modelled on Monster Hunter Wilds: cast, hook a bite, then fight the fish by
managing line tension to exhaust it, then land it. Fish species are gated by **bait type** AND
**rod strength**. Priority: the fight must feel smooth and mechanically satisfying.

## Rules for working
- Look up API signatures in `../VintageStoryAPIDocs/` and the skill at
  `../.agents/skills/vintage-story-modding/SKILL.md`. Do not guess signatures.
  When grepping the docs, always use `Includes` for a specific .md file; never grep the whole docs
  folder without a narrow pattern (output is huge).
- Server mutates the world/inventory; client only does input, HUD, particles.
- Do NOT touch the sibling `anglerscatch` mod. Preserve existing comments.
- Build after every change:
  `dotnet build -c Release -p:VINTAGE_STORY="$env:APPDATA\VSLGameVersions\1.22.7"`
  (1.22.6 from the csproj default is NOT installed; 1.22.7 is.) Must report 0 errors.
- Keep responses concise.

## Current state (builds clean, NOT yet tested in-game)
| File | Purpose |
|---|---|
| `src/FightSimulation.cs` | Pure C# fight sim (no VS API). Tension, stamina, distance, fish behaviours (Swim/Windup/Dash/Recover/Tired), snap/slack/escape outcomes. All feel constants at the top. |
| `src/FishingData.cs` | `FishSpecies` + `SpeciesRegistry` (loads `config/species.json`, `Roll()` filters by rod strength + bait), ProtoBuf packets `FishingStatePacket` (server→client) and `FishingActionPacket` (client→server). |
| `src/FishingServer.cs` | Sessions per player: cast (water raycast), bait check, bite timer, bite window, starts fight, validates result (min fight time), gives catch, damages rod, consumes bait. |
| `src/FishingClient.cs` | `IRenderer` (Ortho). Runs the sim every frame from input (RMB = reel, Left/Right/A/D = steer), draws HUD with 1x1 white texture + cached text textures, reports outcome to server. |
| `src/ItemFishingRod.cs` | Item class `AAFItemFishingRod`; right-click forwards to server/client systems. |
| `src/AnglersArtModSystem.cs` | Registers item class, loads species (server), creates `FishingServer`/`FishingClient`. |
| `assets/anglersartoffishing/` | `itemtypes/fishingrod.json` (crude/sturdy/master, attrs `rodStrength`,`reelSpeed`,`forgiveness`), `itemtypes/bait.json` (worm/grain/fishscrap/spinner, attrs `baitType`,`biteSpeed`,`consumeChance`), `config/species.json`, `recipes/grid/fishing.json`, `lang/en.json`, generated icons in `textures/item/`. |
| `generate_icons.py` | Regenerates placeholder 16x16 icons (stdlib only). |

Flow: right-click water with bait in off-hand → server Waiting → Bite (client shows "!", click within ~1.4s)
→ Fight (client sim) → client sends result → server verifies and rewards.

## Known uncertainties / first things to verify
1. Vanilla codes used in recipes/catches are guesses: `game:fish-raw`, `game:flaxtwine`, `game:rot`,
   `game:ingot-copper`, `game:ingot-iron`, `game:stick`, `game:grain-*`. Check
   `%APPDATA%\VintagestoryData\Logs\server-main.log` / `client-main.log` for missing-item warnings and fix.
2. Items use flat sprite rendering (`"texture": {"base": ...}`, no shape). Confirm they load and show icons.
3. HUD text alpha fade is not implemented (toast just disappears); Rect alpha works.
4. A/D steering also strafes the player. Consider arrow-keys-only or locking movement during a fight.
5. Balance has never been playtested. Constants in `FightSimulation.cs` are theoretical.

## Suggested next tasks (in order)
1. Package and test: `powershell -ExecutionPolicy Bypass -File ../.agents/skills/vintage-story-modding/scripts/package_mod.ps1 -ModPath "anglersartoffishing"`
   (may need the 1.22.7 path adjustment; check the script). Launch the game, fix log errors.
2. Add a headless balance test harness (console or xunit referencing only `FightSimulation.cs`) with a
   bot that releases during Dash and reels otherwise; verify low-power fish are landable ~100% and
   max-power fish with a crude rod are hard but not impossible (the species gate already prevents
   that pairing, so test margin 0 vs margin 2).
3. Polish feel: camera/FOV punch on Dash start (hook `BehaviourChanged`), sounds (find valid vanilla
   sound paths first), screen-edge red vignette in danger, smoother toast fade.
4. Bobber entity or particle ripple that follows the bobber position and reacts to fight state;
   rod hold/cast animation.
5. Extra gating ideas: water type (fresh/salt), depth, time of day, size bias by bait/rod.
6. Add a `.gitignore` (`bin/`, `obj/`) for this folder if missing.

## Design rules for the fight (do not break these without good reason)
- Releasing the reel halves the pull reaching the rod but lets the fish gain distance (risk/reward).
- Stamina only drains in the sweet zone (and faster while counter-steering a dash).
- Forgiveness timer (not instant snap) before the line breaks; slack timer before the fish escapes.
- Fish always telegraphs (Windup) before a Dash.
- Server decides species/size; client simulates; server sanity-checks the result.
