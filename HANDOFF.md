# Angler's Art of Fishing – Handoff Instructions

You are continuing work on a Vintage Story mod (modid `anglersartoffishing`) in this folder.
Read this whole file first, then read the source files listed below before editing anything.

## Goal
A streamlined, immersive fishing minigame inspired by *Fishing Planet*: cast, hook a bite on the "!", then fight the fish by managing line tension with reel timing (<kbd>RMB</kbd>) to tire the fish and crank it to shore. Fish species are gated by **bait type** AND **rod strength**. Priority: clean, satisfying tactile reel mechanics without screen clutter.

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
| `src/FishingData.cs` | `FishSpecies` + `SpeciesRegistry` (loads `config/species.json`, `Roll()` filters by rod strength + bait), ProtoBuf packets `FishingStatePacket` (server→client) and `FishingActionPacket` (client→server). |
| `src/FishingServer.cs` | Sessions per player: cast (water raycast), bait check, bite timer, bite window, starts fight, validates result (min fight time), gives catch, damages rod, consumes bait. |
| `src/FishingClient.cs` | `IRenderer` (Ortho). Runs the sim every frame from input (RMB = reel-only), draws sleek single Horizontal Tension Bar HUD with digital distance counter, manages 3D in-world water splash/wake animations. |
| `src/ItemFishingRod.cs` | Item class `AAFItemFishingRod`; right-click forwards to server/client systems. |
| `src/AnglersArtModSystem.cs` | Registers item class, loads species (server), creates `FishingServer`/`FishingClient`. |
| `assets/anglersartoffishing/` | `itemtypes/fishingrod.json`, `itemtypes/bait.json`, `config/species.json`, `recipes/grid/fishing.json`, `lang/en.json`, textures. |

Flow: right-click water with bait in off-hand → server Waiting → Bite (client shows "!", click within ~1.4s)
→ Fight (client sim with single tension bar) → client sends result → server verifies and rewards.

## Mechanics Details (Fishing Planet Style)
- **Single Horizontal Tension Bar**:
  - Slack Zone ($0.00 - 0.14$, cyan/blue): Too loose; triggers escape if neglected for $> 6.0\text{s}$.
  - Sweet / Fight Zone ($0.25 - 0.85$, vibrant emerald to golden amber): Active winching and fish stamina depletion.
  - Danger Overload Zone ($0.88 - 1.00$, flashing red): Line stress; snaps if held for $> \text{Forgiveness}$ time.
- **Controls**: Reel-only (<kbd>RMB</kbd>). Camera movement is fully free (no mouse locking/tilting). Reeling against a fish dash spikes tension into the red zone; player feathers or releases <kbd>RMB</kbd> during surges to let rod drag absorb the run.
- **Digital Distance**: Displayed cleanly below the tension bar in meters (`Distance: 14.2m`).
- **Water Wake & Splashes**: Dynamically positioned in real-time on the water surface tracing the fish's lateral runs, tail thrashing, and distance to shore.
