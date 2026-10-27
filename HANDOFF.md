# Angler's Art of Fishing – Handoff Instructions

You are continuing work on a Vintage Story mod (modid `anglersartoffishing`) in this folder.
Read this whole file first, then read the source files listed below before editing anything.

## Goal
A streamlined, immersive fishing minigame inspired by *Fishing Planet*, built on top of the **vanilla fishing pole** (wood and bamboo). Cast as normal, hook a bite when the bobber plunges, then fight the fish by managing line tension with reel timing (<kbd>RMB</kbd>) to tire it and crank it to shore. Vanilla decides which fish bites; this mod owns the fight. Priority: clean, satisfying tactile reel mechanics without screen clutter.

## Rules for working
- Look up API signatures in `../VintageStoryAPIDocs/` and the skill at
  `../.agents/skills/vintage-story-modding/SKILL.md`. Do not guess signatures.
  When grepping the docs, always use `Includes` for a specific .md file; never grep the whole docs
  folder without a narrow pattern (output is huge).
- Server mutates the world/inventory; client only does input, HUD, particles.
- Do NOT touch the sibling `anglerscatch` mod. Preserve existing comments.
- Build after every change:
  `dotnet build -c Release -p:VINTAGE_STORY="$env:APPDATA\VSLGameVersions\1.22.7"`
  (Must report 0 errors).
- Keep responses concise.

## Current state
| File | Purpose |
|---|---|
| `src/FightSimulation.cs` | Pure C# fight sim (no VS API). Fishing Planet style tension model: reel winching vs fish pull load with fixed rod tier drag dampening. Stamina, distance, and 3D lateral trajectory. |
| `src/FishingData.cs` | `FishSpecies` + `SpeciesRegistry` (loads `config/species.json`, server only), `FishingPhase` (Idle / Fight), ProtoBuf packets `FishingStatePacket` (server→client) and `FishingActionPacket` (client→server, fight outcome). |
| `src/FishingClient.cs` | Dual renderer: `BobberMotionRenderer` (`EnumRenderStage.Before` order 0.95) runs the fight sim and positions the bobber (Y tension dive, thrash, yaw, pitch, roll) and rope line end in 3D, and handles physical bite dipping; `FishingClient` (`EnumRenderStage.Ortho`) draws sleek single Horizontal Tension Bar HUD with digital distance counter. Diegetic bite cue: bobber plunges underwater with splash sound/ripples (no exclamation mark HUD clutter). |
| `src/VanillaPoleBridge.cs` | Harmony bridge (`VanillaPolePatches` + `VanillaFightServer`) hooking the vanilla `ItemFishingPole` (wood and bamboo). `VanillaFightServer` owns the network channel and the per-player fight sessions. Seamlessly takes over when a fish bites, freezes bobber bite expiration during fight, validates the result (min fight time), routes loot through vanilla `TryCatchFish` on landing (rewarding authentic vanilla whole fish `game:fishraw-...`), and clears catch on line snap/escape. |
| `src/AnglersArtModSystem.cs` | Loads species (server), initializes `VanillaFightServer`/`FishingClient`, applies and unpatches Harmony hooks into vanilla poles. |
| `assets/anglersartoffishing/` | `itemtypes/bait.json` (spinner lure only, with `isFishBait` & `consumeChance`), `config/species.json` (fight stats per vanilla fish `fishraw-...`), `recipes/grid/fishing.json` (spinner lure recipe), `lang/en.json`, `textures/item/bait-spinner.png` (regenerate with `generate_icons.py`). |

The mod has no rods, sessions or casting of its own: an earlier standalone rod (`ItemFishingRod`, `FishingServer`) was removed in favour of the vanilla pole. `species.json` still carries `Baits` and `Weight` per fish, but they are **not applied** yet because vanilla picks the fish; they are kept for a future bait-gating patch on vanilla's fish selection.

Flow (Vanilla Pole Hook): right-click casts vanilla bobber + rope → vanilla waits for bite → on bite, vanilla splash plays and bobber physically plunges under water → right-click hooks fish → custom fight takes over (bobber & rope dance across the water in 3D following foaming fish) → on landing, vanilla `TryCatchFish` rewards the catch naturally (authentic whole fish); on snap/escape, catch is cleared and pole damaged. Standard fishing relies on vanilla baits (earthworms, dough, meat), with the craftable spinner lure as a reusable metal lure option. Pole tier only affects the fight: bamboo gets slightly more drag dampening (`RodMargin` 1), reel speed (1.1×) and snap forgiveness (0.6s vs 0.5s) than wood.

## Mechanics Details (Fishing Planet Style)
- **Single Horizontal Tension Bar**:
  - Slack Zone ($0.00 - 0.14$, cyan/blue): Too loose; triggers escape if neglected for $> 6.0\text{s}$.
  - Sweet / Fight Zone ($0.25 - 0.85$, vibrant emerald to golden amber): Active winching and fish stamina depletion.
  - Danger Overload Zone ($0.88 - 1.00$, flashing red): Line stress; snaps if held for $> \text{Forgiveness}$ time.
- **Controls**: Reel-only (<kbd>RMB</kbd>). Camera movement is fully free (no mouse locking/tilting). Reeling against a fish dash spikes tension into the red zone; player feathers or releases <kbd>RMB</kbd> during surges to let rod drag absorb the run.
- **Diegetic Bite Cue**: No on-screen exclamation mark or text popups. When a fish bites, the bobber physically plunges into the water (~0.3m) with erratic jerks, tilts downwards, and emits splash particles with vanilla splash audio.
- **Digital Distance**: Displayed cleanly below the tension bar in meters (`Distance: 14.2m`).
- **Water Wake & Splashes**: Dynamically positioned in real-time on the water surface tracing the fish's lateral runs, tail thrashing, and distance to shore.
- **3D Line & Bobber Tracking**: During a fight on the vanilla pole, `BobberMotionRenderer` at `EnumRenderStage.Before` (RenderOrder 0.95, executing after `EntityBehaviorInterpolatePosition`) updates `bobber.Pos`, `bobber.Pos.Yaw/Pitch/Roll`, `EntityBehaviorInterpolatePosition` snapshots (`pL`, `pN`, queue), and `ClothSystem.LastPoint.Pos`. The 3D bobber and physical fishing rope follow the running fish and foaming wake across the water with realistic diving and thrashing.

