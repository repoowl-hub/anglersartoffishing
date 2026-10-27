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
| `src/FightSimulation.cs` | Pure C# fight sim (no VS API). Fishing Planet style tension model: reel winching vs fish pull load with fixed rod tier drag dampening. Stamina, distance, 3D lateral trajectory, and per-species `FightStyle` move sets (see Fight Styles). |
| `src/FishingData.cs` | `FishSpecies` + `SpeciesRegistry` (loads `config/species.json`, server only), `FishingPhase` (Idle / Fight), ProtoBuf packets `FishingStatePacket` (server→client) and `FishingActionPacket` (client→server, fight outcome). |
| `src/FishingClient.cs` | Dual renderer: `BobberMotionRenderer` (`EnumRenderStage.Before` order 0.95) runs the fight sim and positions the bobber (Y tension dive, thrash, yaw, pitch, roll) and rope line end in 3D, and handles physical bite dipping; `FishingClient` (`EnumRenderStage.Ortho`) draws sleek single Horizontal Tension Bar HUD with digital distance counter. Diegetic bite cue: bobber plunges underwater with splash sound/ripples (no exclamation mark HUD clutter). |
| `src/VanillaPoleBridge.cs` | Harmony bridge (`VanillaPolePatches` + `VanillaFightServer`) hooking the vanilla `ItemFishingPole` (wood and bamboo). `VanillaFightServer` owns the network channel and the per-player fight sessions. Seamlessly takes over when a fish bites, freezes bobber bite expiration during fight, validates the result (min fight time), routes loot through vanilla `TryCatchFish` on landing (rewarding authentic vanilla whole fish `game:fishraw-...`), and clears catch on line snap/escape. |
| `src/FishingGear.cs` | Lures (adds each lure's bait tag to fish of its styles on server load) and pole upgrades (records crafted-on line/reel, tooltip, fight bonuses). |
| `src/FishModelRenderer.cs` | Client renderer that draws the hooked species' vanilla model (shape + entity-atlas texture) during the fight. |
| `src/AnglersArtModSystem.cs` | Loads species (server), applies lure tags (server `AssetsFinalize`), initializes `VanillaFightServer`/`FishingClient`, applies and unpatches Harmony hooks into vanilla poles. |
| `assets/anglersartoffishing/` | `itemtypes/bait.json` (4 lures), `itemtypes/fishingline.json` + `fishingreel.json` (pole upgrades), `config/species.json` (fight stats + style per vanilla fish), `recipes/grid/fishing.json` (lure/line/reel crafting, attach-to-pole recipes), `patches/fishinggear.json` (vanilla bait recipes keep pole attributes), `lang/en.json`, `shapes/item/*.json` + `textures/item/*.png` (3D item models, see Item Models). |

## Item Models
`generate_models.py` (pure stdlib) writes the 3D shapes for the four lures (`lure-{type}`), the reel (`fishingreel`) and the line spool (`fishingline`) into `shapes/item/`, plus their textures: `lurepaint.png` (4×4 grid of solid paint colours the lures' faces sample) and `line-braided/waxed.png`. Everything else reuses vanilla textures (iron sheet hooks, copper/tin bronze sheet blades, feather, oak). The reel's `metal` and the spool's `line` textures are overridden per variant in the itemtype. Cylinders are true octagonal prisms built from four slabs (`rod_x`/`rod_z`).

The shapes are standard VS shape JSON, so they open in Blockbench with the VS plugin. **If you hand-edit a model in Blockbench, stop regenerating that one** (re-running the script overwrites all six). Transforms (gui/fp/tp/ground) in the itemtypes are first guesses and untested in-game.

The mod has no rods, sessions or casting of its own: an earlier standalone rod (`ItemFishingRod`, `FishingServer`) was removed in favour of the vanilla pole.

### Bait
- **Which fish bites is vanilla's job.** Each bait has a `baitTag` (`bait-worm`, `bait-meat`, `bait-stinkmeat`, `bait-stinkdough`, `bait-fishtiny`, `bait-fishsmall`) and each fish entity lists accepted tags in `baitTagsByType` (`game:entities/animal/fish/*-adult.json`; `nobait` = bare hook). `species.json` holds fight stats only, no bait data.
- **Lures** (`consumeChance: 0.05`): kept on an escape, kept 95% of the time on a landing, lost when the line snaps (`VanillaFightServer.Resolve`). Lost if the fight ends because the player switched away from the pole. Each has its own tag and only attracts fish of matching fight styles:

  | Lure | Tag | Styles | Recipe (grid) |
  |---|---|---|---|
  | Spinner | `bait-lure-spinner` | Runner | copper ingot + flax twine → 3 |
  | Popper | `bait-lure-popper` | Jumper | feather + stick + copper bit → 2 |
  | Lead jig | `bait-lure-jig` | Bulldog | lead bit + feather + copper bit → 2 |
  | Spoon | `bait-lure-spoon` | Ambusher | 2 tin bronze bits + copper bit → 2 |

  Data-driven: a lure item's `lureStyles` attribute names the styles; `FishingGear.ApplyLureTags` (server `AssetsFinalize`) appends its `baitTag` to the resolved `baitTags` of every fish entity (adults and juveniles) whose species.json `Style` matches. Darters and head-shakers stay on natural bait. Lures go on the pole like vanilla bait: shapeless pole + lure grid recipe (vanilla `ItemFishingPole.OnCreatedByCrafting` accepts any `isFishBait` item).
- **Bait freshness**: `VanillaFightServer.BaitSpoilage` reads the bait's Perish `TransitionLevel`. Stale bait sets the hook lightly: slack-line escape time drops from 6s (fresh) to 3.5s (fully spoiled), sent as `SlackEscapeSeconds` in the fight packet.

Flow (Vanilla Pole Hook): right-click casts vanilla bobber + rope → vanilla waits for bite → on bite, vanilla splash plays and bobber physically plunges under water → right-click hooks fish → custom fight takes over (bobber & rope dance across the water in 3D following foaming fish) → on landing, vanilla `TryCatchFish` rewards the catch naturally (authentic whole fish); on snap/escape, catch is cleared and pole damaged. Standard fishing relies on vanilla baits (earthworms, dough, meat), with the craftable spinner lure as a reusable metal lure option. Pole tier only affects the fight: bamboo gets slightly more drag dampening (`RodMargin` 1), reel speed (1.1×) and snap forgiveness (0.6s vs 0.5s) than wood.

### Pole upgrades
Crafted onto a vanilla pole with a shapeless pole + part recipe (`copyAttributesFrom: P`, so durability, bait and the other part are kept). A Harmony postfix on `ItemFishingPole.OnCreatedByCrafting` stores the part's item code in `aafLine` / `aafReel`; a postfix on `GetHeldItemInfo` lists them. A new part replaces the old one (the old one is consumed). Bonuses are read from the part item's `poleUpgrade` attribute in `TryStartFight`:

| Part | Recipe | Effect |
|---|---|---|
| Braided line | 3 flax twine | +0.15s snap tolerance |
| Waxed line | braided line + beeswax | +0.30s snap tolerance |
| Copper reel | copper ingot + stick | +10% reel speed |
| Iron reel | iron ingot + stick | +20% reel speed |

`patches/fishinggear.json` adds `copyAttributesFrom: "P"` to vanilla's three pole + bait recipes so re-baiting doesn't wipe the upgrades.

### Fish model
The fight packet carries `FishEntityCode` (juvenile variant when a juvenile was rolled). `FishModelRenderer` tessellates that entity type's `Client.LoadedShape` with its first texture from `EntityTextureAtlas` and draws it in the opaque pass at the fish's position. Depth below the surface follows the behaviour (deep on dives, at the top when tired or within 0.15 of the bank), it leaps out on jumps (the bobber then stays on the water), and rolls onto its side when tired. If the model can't be built, the bobber stands in for the fish as before. The model hangs from its **mouth**, which sits on the hook directly below the bobber: fish shapes face -X (vanilla convention, the marlin too via its root Y-90 bone), and `FindMouth` walks the tessellated quads back from the nose to the first slice reaching 35% of the body height, skipping thin bills and whiskers. Pitch is positive = nose up (`RotateZ(-Pitch)` because the nose is -X).

### Player stance
The client blocks its own pole clicks during a fight (so it doesn't take the rope down early), which means the client never runs vanilla `StopFishing` and its `fishingpole-idle` animation would stick after landing. `FishingClient` stops that animation itself when the fight-end Idle packet arrives.

### Angler's Catch compatibility
No changes needed. Landed fish are handed over by vanilla `TryCatchFish` → `EntityPlayer.TryGiveItemStack`, which Angler's Catch hooks to assign `fishSize`/`speciesCode`/catcher (or `EntityItem.Initialize` if the inventory is full). Neither mod patches anything the other relies on.

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


## Which fish is on the hook
Vanilla has two kinds of bite, and the fight must use the same fish the player lands:
- **Real fish in the water** (`bobber.caughtFish` alive): at the hook set its species and age are pinned, the bobber is switched to `NoEntityFishCatch` and the real fish is despawned (`EnumDespawnReason.Removed`), so it doesn't swim around next to the fight model. Landing then goes through the pinned path below. (Any Angler's Catch size already stored on that fish entity is not carried over; Angler's Catch rolls one on landing.) **With Catch & Release (`crfish`) loaded** the real fish is kept instead, because C&R lands real fish alive through `TryCatchFish`: its id goes to the client in `FishingStatePacket.HiddenFishEntityId`, a prefix on vanilla `EntityShapeRenderer.DoRender3DOpaque`/`DoRender3DOpaqueBatched` skips drawing it, and the server tick parks it at the bobber (so it can't wander off or despawn). It reappears when the fight ends; if it escaped, `ClearCatch` has released it.
- **No-entity catch** (`bobberState == NoEntityFishCatch`, the common case): vanilla only picks a fish inside `TryCatchFish` — `getRandomFishEntityProperties` (uniform pick among fish that fit the climate, water and bait tag), then juvenile if `Rand.NextDouble() <= abundance`, then gives the item, all in one call. Since a fight can't sit inside that call, `TryStartFight` makes that same roll at the hook set and `VanillaPolePatches.PinFish`es it. A Harmony prefix on `getRandomFishEntityProperties` returns the pinned fish (and forces the age through `abundanceValue`) until the fight resolves, so landing gives exactly the fish that was fought. `HasCatchable` also calls the picker, so the pin isn't one-shot: it's removed in `Resolve`, `OnVanillaStop` and the tick cleanup.
- **Nothing catchable**: vanilla only bites when the location's abundance is above 0, so worlds without native fish get no bites at all. If the roll at hook set still comes back empty (or the reflected picker is missing after a game update), `TryStartFight` returns false and vanilla resolves the bite itself (no fight, nothing caught). There is no substitute fish.

### Fight data for any fish (`SpeciesRegistry.ResolveFor`)
Works for vanilla and modded fish alike, cached per creature code. Each field is taken from the first source that has it:
1. **The creature's shared `fishData` block** — the cross-mod format (any fish mod can ship it, or add it to someone else's fish with a `dependsOn` patch). Angler's Art reads `fight`; `sizeCm`/`weightKg` are there for Angler's Catch to adopt later.
   ```json
   "attributes": { "fishData": {
     "sizeCm": [200, 450], "weightKg": [100, 900],
     "fight": { "power": 0.95, "stamina": 16.5, "aggression": 0.85, "minFightSeconds": 12, "style": "Jumper" } } }
   ```
2. **species.json** — a `Code` with a domain (`"bluemarlin:marlin-blue-*"`) is a wildcard on the full code; a plain `Code` is a substring of the path. Longest match wins. Vanilla fish live here.
3. **Size fallback** for undescribed fish: power from the hitbox width (vanilla 0.5 panfish → 0.8 arapaima/sturgeon; capped at 0.85), style from power.

A fish counts for lures if its class is `EntityFish` (or its code starts with `fish-`), so modded fish get lure tags from their resolved style. A rolled juvenile only counts when the species' catch item has a juvenile variant (mirrors `TryCatchFish`, which otherwise hands over the adult).

First external test: the **bluemarlin** mod (sibling folder) ships `fishData` on its creature — Jumper, power 0.95 (above every vanilla fish), ~13–15s fights in the harness.

## Fight Styles
Each species in `species.json` has a `Style` modelled on how the real fish fights (missing/unknown → picked from `Power`: <0.30 Darter, <0.55 Headshaker, else Runner; junk snags fight as Bulldog). `FightSimulation.Profiles` holds each style's move weights, opening move, last surge and timing multipliers.

| Style | Species | Signature |
|---|---|---|
| Darter | alewife, chub, crappie, perch, tilapia, bream, herring, pacific perch | Quick short darts and tugs, short telegraphs, fast pace |
| Jumper | rainbow trout, bass, arapaima, mahi-mahi | Leaps clear of the water |
| Runner | carp, salmon, mackerel, skipjack tuna | Long line-peeling runs |
| Ambusher | pike, pickerel, barracuda, piranha | Explosive opening dash, harder dashes, long lulls |
| Bulldog | catfish, sheatfish, grouper, amberjack, wreckfish, coelacanth, sturgeon | Deep dives that hold the bottom |
| Headshaker | brown trout, walleye, snapper, gurnard, haddock, hake, pollock, wolffish | Head shakes and short dives |

Moves (all except Shake are telegraphed by a Windup whose in-world cue depends on `NextAction`):
- **Dash**: short heavy surge; release. **Run**: ~2–4s (longer for bigger fish) sustained pull (0.74) that takes line even while reeling; feather. Steady pressure drains stamina ×1.15.
- **Jump**: launch (pull 0.75) → airborne (pull 0; slack counts ×5 towards a thrown hook, reeling takes up line fast) → re-entry slam (0.95). Keep reeling in the air, ease off on the splash. Costs the fish 4% stamina. Visual: bobber arcs out of the water, big splash + `game:sounds/environment/*splash*` on exit and re-entry; the fish rises during the windup.
- **Shake**: 4.5 Hz pull oscillation; reel gains half speed. Visual: fast roll and tight wiggle.
- **Dive**: steady 0.82 pull, reeling gains line at about half speed, steady pressure drains stamina ×1.3. Visual: bobber dragged ~0.45 under, no surface wake, bubbles.

Size (`Power`) scales run/dive length, windup and calm spells (bigger = slower but longer), jump height and splash size. Fish with `Power` ≥ 0.35 make one last surge (style's `Surge` move) when they first come within 0.22 distance of the bank; the surge is 60% of a normal move's length. A fish that runs out of stamina mid-Run or mid-Dive gives it up and tires.

Balance check: a scratch harness ran 300 fights per species with scripted players. A careful player (even with ~0.25s reaction lag) lands everything; holding the reel nonstop snaps the line on every ambusher and on most fish from ~0.48 Power up (smaller runners and darters survive it). Fight length runs ~6–9s (darters) to ~13–16s (biggest bulldogs, runners, jumpers). To keep big fights in that range, `StaminaSeconds` above 10 was compressed to `10 + (old − 10) × 0.45` (e.g. 22 → 15.4).
