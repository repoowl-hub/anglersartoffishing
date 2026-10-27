using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AnglersArtOfFishing
{
    /// <summary>
    /// Hooks the custom fight into the vanilla <see cref="ItemFishingPole"/>.
    ///
    /// Vanilla flow: RMB casts the bobber (rope + EntityBobber). When something bites, the bobber's private
    /// <c>catchAccum</c> becomes &gt; 0. The next RMB calls <c>OnHeldInteractStart -&gt; StopFishing -&gt; TryCatchFish</c>
    /// which hands over the catch instantly.
    ///
    /// Insert point: a Prefix on <c>ItemFishingPole.OnHeldInteractStart</c>. If a bite is active we skip
    /// <c>StopFishing</c>, freeze the bobber and start our fight instead. Casting, bobber physics, bite rolls,
    /// bait handling and loot all stay vanilla. When the fight is landed we simply let vanilla
    /// <c>StopFishing</c> run (so <c>TryCatchFish</c> gives the loot and other mods patching it keep working);
    /// on failure we clear the pending catch first.
    /// </summary>
    public static class VanillaPolePatches
    {
        public const string BiteAttr = "aafBite";

        public static VanillaFightServer Server;
        public static FishingClient Client;

        /// <summary>Client side: a real fish kept in the world during the fight (Catch &amp; Release), not drawn until it ends.</summary>
        public static long HiddenFishEntityId;

        static Harmony harmony;

        static readonly AccessTools.FieldRef<EntityBobber, float> CatchAccum =
            AccessTools.FieldRefAccess<EntityBobber, float>("catchAccum");
        public static readonly AccessTools.FieldRef<EntityBobber, EnumBobberState> BobberState =
            AccessTools.FieldRefAccess<EntityBobber, EnumBobberState>("bobberState");

        public static void Apply(string harmonyId)
        {
            // Single player runs client + server in one process: only patch once
            if (harmony != null) return;
            harmony = new Harmony(harmonyId);

            harmony.Patch(AccessTools.Method(typeof(ItemFishingPole), nameof(ItemFishingPole.OnHeldInteractStart)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(VanillaPolePatches), nameof(Prefix_OnHeldInteractStart))));
            harmony.Patch(AccessTools.Method(typeof(ItemFishingPole), nameof(ItemFishingPole.StopFishing)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(VanillaPolePatches), nameof(Prefix_StopFishing))));
            harmony.Patch(AccessTools.Method(typeof(EntityBobber), "onServertick"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(VanillaPolePatches), nameof(Prefix_BobberServerTick))));
            harmony.Patch(AccessTools.Method(typeof(EntityBobber), nameof(EntityBobber.OnGameTick)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(VanillaPolePatches), nameof(Postfix_BobberGameTick))));
            var skipHidden = new HarmonyMethod(AccessTools.Method(typeof(VanillaPolePatches), nameof(Prefix_SkipHiddenFish)));
            harmony.Patch(AccessTools.Method(typeof(EntityShapeRenderer), nameof(EntityShapeRenderer.DoRender3DOpaque)), prefix: skipHidden);
            harmony.Patch(AccessTools.Method(typeof(EntityShapeRenderer), nameof(EntityShapeRenderer.DoRender3DOpaqueBatched)), prefix: skipHidden);
            harmony.Patch(AccessTools.Method(typeof(EntityBobber), "getRandomFishEntityProperties"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(VanillaPolePatches), nameof(Prefix_GetRandomFish))));
            harmony.Patch(AccessTools.Method(typeof(ItemFishingPole), nameof(ItemFishingPole.OnCreatedByCrafting)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(FishingGear), nameof(FishingGear.Postfix_PoleCrafted))));
            harmony.Patch(AccessTools.Method(typeof(ItemFishingPole), nameof(ItemFishingPole.GetHeldItemInfo)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(FishingGear), nameof(FishingGear.Postfix_PoleInfo))));
        }

        public static void Remove()
        {
            harmony?.UnpatchAll(harmony.Id);
            harmony = null;
            pinnedFish.Clear();
            HiddenFishEntityId = 0;
            Server = null;
            Client = null;
        }

        // ------------------------------------------------------------------ bobber helpers

        public static EntityBobber GetBobber(IWorldAccessor world, ItemStack stack)
        {
            long id = stack?.Attributes.GetLong("bobberEntityId", 0) ?? 0;
            return id == 0 ? null : world.GetEntityById(id) as EntityBobber;
        }

        /// <summary>True while vanilla would hand over a catch on the next click.</summary>
        public static bool IsBiting(EntityBobber b)
        {
            if (CatchAccum(b) <= 0f) return false;
            if (b.caughtFish != null && b.caughtFish.Alive) return true;
            var st = BobberState(b);
            return st == EnumBobberState.NoEntityFishCatch || st == EnumBobberState.JunkCatch;
        }

        public static bool IsJunk(EntityBobber b) =>
            (b.caughtFish == null || !b.caughtFish.Alive) && BobberState(b) == EnumBobberState.JunkCatch;

        /// <summary>Makes the following vanilla TryCatchFish() a no-op (fish escaped / line snapped).</summary>
        public static void ClearCatch(EntityBobber b)
        {
            b.caughtFish = null;
            BobberState(b) = EnumBobberState.NoCatch;
            CatchAccum(b) = 0f;
            b.BaitStack = null; // the fish ran off with the bait
        }

        // ------------------------------------------------------------------ pinned fish

        /// <summary>
        /// Fish rolled when the hook was set, per bobber. Vanilla only picks a fish inside <c>TryCatchFish</c>
        /// and hands it over in the same call, so the fight can't sit between the two. Instead the pick is made
        /// up front with vanilla's own picker, and while pinned that picker keeps returning it - so the landed
        /// fish is the one that was fought.
        /// </summary>
        static readonly Dictionary<long, (EntityProperties props, bool juvenile)> pinnedFish = new Dictionary<long, (EntityProperties, bool)>();

        public static void PinFish(long bobberId, EntityProperties props, bool juvenile) => pinnedFish[bobberId] = (props, juvenile);
        public static void UnpinFish(long bobberId) => pinnedFish.Remove(bobberId);

        // ------------------------------------------------------------------ patches

        public static bool Prefix_SkipHiddenFish(EntityShapeRenderer __instance) =>
            HiddenFishEntityId == 0 || __instance.entity?.EntityId != HiddenFishEntityId;

        public static bool Prefix_GetRandomFish(EntityBobber __instance, ref float abundanceValue, ref EntityProperties __result)
        {
            if (!pinnedFish.TryGetValue(__instance.EntityId, out var pin)) return true;
            __result = pin.props;
            // TryCatchFish makes the catch a juvenile when Rand.NextDouble() <= abundance: force the age that was fought
            abundanceValue = pin.juvenile ? 2f : -1f;
            return false;
        }

        public static bool Prefix_OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, ref EnumHandHandling handling)
        {
            if (byEntity?.World == null || slot?.Itemstack == null) return true;
            var bobber = GetBobber(byEntity.World, slot.Itemstack);
            if (bobber == null) return true;

            if (byEntity.World.Side == EnumAppSide.Server)
            {
                if (Server == null || byEntity is not EntityPlayer ep || ep.Player is not IServerPlayer sp) return true;

                // Clicks during the fight are reel input (read directly on the client) - never reel in / recast
                if (Server.IsFighting(sp)) { handling = EnumHandHandling.PreventDefault; return false; }

                if (IsBiting(bobber) && Server.TryStartFight(sp, slot, bobber))
                {
                    handling = EnumHandHandling.PreventDefault;
                    return false;
                }
                return true;
            }

            // Client: don't predict StopFishing (it would unregister the rope locally) while hooked / fighting
            if ((Client != null && Client.InFight) || bobber.WatchedAttributes.GetBool(BiteAttr))
            {
                handling = EnumHandHandling.PreventDefault;
                return false;
            }
            return true;
        }

        /// <summary>Any other vanilla stop (drop, disconnect, slot removal) during a fight = the fish escapes.</summary>
        public static void Prefix_StopFishing(EntityAgent byEntity)
        {
            if (byEntity?.World?.Side != EnumAppSide.Server || Server == null) return;
            if (byEntity is EntityPlayer ep && ep.Player is IServerPlayer sp) Server.OnVanillaStop(sp);
        }

        public static bool Prefix_BobberServerTick(EntityBobber __instance)
        {
            // Freeze vanilla bite logic while our fight runs, otherwise it would reset the catch
            return Server == null || !Server.IsFrozen(__instance.EntityId);
        }

        public static void Postfix_BobberGameTick(EntityBobber __instance)
        {
            if (__instance.World?.Side != EnumAppSide.Server) return;
            bool bite = (Server != null && Server.IsFrozen(__instance.EntityId)) || IsBiting(__instance);
            if (__instance.WatchedAttributes.GetBool(BiteAttr) != bite) __instance.WatchedAttributes.SetBool(BiteAttr, bite);
        }
    }

    /// <summary>Server side of fights started from the vanilla fishing pole.</summary>
    public class VanillaFightServer
    {
        const long MaxFightMs = 5 * 60 * 1000;
        const float CastMaxDist = 16f;

        class Session
        {
            public long BobberId;
            public long Start;
            public float MinSeconds;
            public string Name;
            /// <summary>Real fish kept (hidden) in the world for Catch &amp; Release; 0 if none.</summary>
            public long HiddenFishId;
        }

        readonly ICoreServerAPI sapi;
        readonly IServerNetworkChannel channel;
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        readonly HashSet<long> frozen = new HashSet<long>();
        readonly Random rand = new Random();

        public VanillaFightServer(ICoreServerAPI api)
        {
            sapi = api;
            channel = api.Network.RegisterChannel(AnglersArtModSystem.ChannelName)
                .RegisterMessageType<FishingStatePacket>()
                .RegisterMessageType<FishingActionPacket>()
                .SetMessageHandler<FishingActionPacket>((player, packet) => TryHandleAction(player, packet));
            api.Event.RegisterGameTickListener(Tick, 250);
        }

        public bool IsFighting(IServerPlayer player) => sessions.ContainsKey(player.PlayerUID);
        public bool IsFrozen(long bobberId) => frozen.Contains(bobberId);

        static readonly MethodInfo GetRandomFishMethod =
            AccessTools.Method(typeof(EntityBobber), "getRandomFishEntityProperties", new[] { typeof(ItemStack), typeof(float).MakeByRefType(), typeof(bool) });

        public bool TryStartFight(IServerPlayer player, ItemSlot slot, EntityBobber bobber)
        {
            if (slot.Itemstack?.Collectible is not ItemFishingPole) return false;

            // ---- what's on the hook: resolve exact species from SpeciesRegistry (loaded from species.json)
            string name;
            float power, stamina, aggression;
            float minSeconds;
            FightStyle style;
            string fishEntityCode = null;
            long hiddenFishId = 0;

            if (VanillaPolePatches.IsJunk(bobber))
            {
                name = Lang.Get("anglersartoffishing:vanilla-snag");
                power = 0.12f;
                stamina = 5.0f;
                aggression = 0.10f;
                minSeconds = 2.5f;
                style = FightStyle.Bulldog; // dead weight dragging along the bottom
            }
            else
            {
                EntityProperties entityProps = null;
                bool juvenile = false;

                if (bobber.caughtFish != null && bobber.caughtFish.Alive)
                {
                    var fish = bobber.caughtFish;
                    entityProps = fish.Properties;
                    juvenile = fish.Code.Path.Contains("juvenile");

                    // A real fish took the bait. It would keep swimming about next to the fight model, so:
                    if (sapi.ModLoader.IsModEnabled("crfish"))
                    {
                        // Catch & Release lands real fish alive through TryCatchFish, so keep the entity: the client stops
                        // drawing it and the tick keeps it parked at the bobber until the fight is resolved
                        hiddenFishId = fish.EntityId;
                    }
                    else
                    {
                        // otherwise take it out of the world and land the same species through the pinned no-entity catch
                        VanillaPolePatches.PinFish(bobber.EntityId, entityProps, juvenile);
                        bobber.caughtFish = null;
                        VanillaPolePatches.BobberState(bobber) = EnumBobberState.NoEntityFishCatch;
                        sapi.World.DespawnEntity(fish, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                    }
                }
                else
                {
                    // No real fish in the water: make vanilla's catch roll now (fish + age, same rules as TryCatchFish)
                    // and pin it so the landing hands over exactly this fish
                    try
                    {
                        object[] args = new object[] { bobber.BaitStack, 0f, false };
                        entityProps = GetRandomFishMethod?.Invoke(bobber, args) as EntityProperties;
                        if (entityProps != null)
                        {
                            // Only a juvenile if the species has a juvenile catch; otherwise vanilla hands over the adult
                            juvenile = sapi.World.Rand.NextDouble() <= (float)args[1] && HasJuvenileCatch(entityProps);
                            VanillaPolePatches.PinFish(bobber.EntityId, entityProps, juvenile);
                        }
                    }
                    catch (Exception e)
                    {
                        sapi.Logger.Warning("[AnglersArtOfFishing] Could not roll the hooked fish: {0}", e.Message);
                    }

                    // Nothing catchable here after all: let vanilla resolve the bite on its own (it hands over nothing)
                    if (entityProps == null) return false;
                }

                // Model to draw: the rolled species, in the juvenile variant if that's what will be landed
                var modelCode = entityProps?.Code;
                if (modelCode != null && juvenile && modelCode.Path.EndsWith("-adult"))
                {
                    var juvCode = modelCode.CopyWithPath(modelCode.Path.Substring(0, modelCode.Path.Length - 6) + "-juvenile");
                    if (sapi.World.GetEntityType(juvCode) != null) modelCode = juvCode;
                }
                fishEntityCode = modelCode?.ToString();

                // Fight data: the fish's own fishData block, else species.json, else its size (any modded fish works)
                var data = SpeciesRegistry.ResolveFor(entityProps);
                name = (data.Name ?? Lang.Get("anglersartoffishing:vanilla-fish")) + (juvenile ? " (Juvenile)" : "");
                power = juvenile ? data.Power * 0.65f : data.Power;
                stamina = juvenile ? data.StaminaSeconds * 0.65f : data.StaminaSeconds;
                aggression = data.Aggression;
                minSeconds = juvenile ? data.MinFightSeconds * 0.65f : data.MinFightSeconds;
                style = SpeciesRegistry.ResolveStyle(data, data.Power);
            }

            // ---- fixed drag per pole tier
            bool bamboo = slot.Itemstack.Collectible.Variant["material"] == "bamboo";
            int rodMargin = bamboo ? 1 : 0;
            float reelSpeed = bamboo ? 1.1f : 1.0f;
            float forgiveness = bamboo ? 0.6f : 0.5f;

            // ---- crafted-on line and reel
            forgiveness += FishingGear.SnapBonus(slot.Itemstack, sapi.World);
            reelSpeed *= 1f + FishingGear.ReelSpeedBonus(slot.Itemstack, sapi.World);

            // ---- stale bait sets the hook lightly: the fish throws it sooner on a slack line (6s fresh .. 3.5s rotten)
            float slackEscape = 6.0f - 2.5f * BaitSpoilage(bobber.BaitStack);

            // ---- freeze the vanilla bobber & make the rope unbreakable while fighting
            frozen.Add(bobber.EntityId);
            bobber.Pos.Motion.Set(0, 0, 0);
            var cm = sapi.ModLoader.GetModSystem<ClothManager>();
            var rope = cm?.GetClothSystem(slot.Itemstack.Attributes.GetInt("clothId"));
            if (rope != null) rope.CanRip = false;

            sessions[player.PlayerUID] = new Session
            {
                BobberId = bobber.EntityId,
                Start = sapi.World.ElapsedMilliseconds,
                MinSeconds = minSeconds,
                Name = name,
                HiddenFishId = hiddenFishId
            };

            var bp = bobber.Pos;
            channel.SendPacket(new FishingStatePacket
            {
                Phase = (int)FishingPhase.Fight,
                SpeciesName = name,
                Power = power,
                StaminaSeconds = stamina,
                Aggression = aggression,
                Style = (int)style,
                RodMargin = rodMargin,
                ReelSpeed = reelSpeed,
                Forgiveness = forgiveness,
                SlackEscapeSeconds = slackEscape,
                StartDistance = Math.Clamp((float)player.Entity.Pos.XYZ.DistanceTo(bp.XYZ) / CastMaxDist * 0.6f + 0.55f, 0.6f, 1f),
                BobberX = bp.X, BobberY = bp.Y, BobberZ = bp.Z,
                BobberEntityId = bobber.EntityId,
                FishEntityCode = fishEntityCode,
                HiddenFishEntityId = hiddenFishId
            }, player);
            return true;
        }

        /// <summary>Mirrors TryCatchFish: the catch is the first drop with its "age" variant swapped, if that item exists.</summary>
        bool HasJuvenileCatch(EntityProperties props)
        {
            var catchItem = props?.Drops?.Length > 0 ? props.Drops[0]?.ResolvedItemstack?.Collectible : null;
            if (catchItem?.Variant?["age"] == null) return false;
            return sapi.World.GetItem(catchItem.CodeWithVariant("age", "juvenile")) != null;
        }

        /// <summary>0 = fresh (or doesn't perish), 1 = fully spoiled.</summary>
        float BaitSpoilage(ItemStack bait)
        {
            if (bait?.Collectible == null) return 0f;
            // Work on a copy: reading the state also advances the stack's perish bookkeeping
            var state = bait.Collectible.UpdateAndGetTransitionState(sapi.World, new DummySlot(bait.Clone()), EnumTransitionType.Perish);
            return Math.Clamp(state?.TransitionLevel ?? 0f, 0f, 1f);
        }

        /// <summary>Returns true if the packet belonged to a vanilla-pole fight.</summary>
        public bool TryHandleAction(IServerPlayer player, FishingActionPacket packet)
        {
            if (!sessions.TryGetValue(player.PlayerUID, out var s)) return false;

            float elapsed = (sapi.World.ElapsedMilliseconds - s.Start) / 1000f;
            int outcome = packet.Outcome;
            if (outcome == 2 && elapsed < s.MinSeconds * 0.6f)
            {
                sapi.Logger.Warning("[AnglersArtOfFishing] {0} landed a fish implausibly fast ({1:0.0}s)", player.PlayerName, elapsed);
                outcome = 0;
            }
            Resolve(player, s, outcome);
            return true;
        }

        void Resolve(IServerPlayer player, Session s, int outcome)
        {
            sessions.Remove(player.PlayerUID);
            frozen.Remove(s.BobberId);

            var bobber = sapi.World.GetEntityById(s.BobberId) as EntityBobber;

            // Reusable lures (consumeChance < 1) survive an escape and usually a landing; a snapped line takes them
            ItemStack savedBait = null;
            if (outcome != 1 && bobber?.BaitStack != null)
            {
                float consumeChance = bobber.BaitStack.Collectible.Attributes?["consumeChance"].AsFloat(1f) ?? 1f;
                bool keep = consumeChance < 1f && (outcome == 0 || rand.NextDouble() > consumeChance);
                if (keep) savedBait = bobber.BaitStack.Clone();
            }

            if (outcome != 2 && bobber != null)
            {
                VanillaPolePatches.ClearCatch(bobber);
            }

            // Vanilla cleanup: TryCatchFish (loot on success, nothing after ClearCatch), bobber death, rope removal
            var slot = player.InventoryManager.ActiveHotbarSlot;
            if (slot?.Itemstack?.Collectible is ItemFishingPole pole && slot.Itemstack.Attributes.GetLong("bobberEntityId", 0) == s.BobberId)
            {
                if (pole.StopFishing(slot.Itemstack, player.Entity)) slot.MarkDirty();
                if (savedBait != null)
                {
                    slot.Itemstack.Attributes.SetItemstack("fishingBait", savedBait);
                    slot.MarkDirty();
                }
                if (outcome == 1) pole.DamageItem(sapi.World, player.Entity, slot, 2);
                else if (outcome == 2) pole.DamageItem(sapi.World, player.Entity, slot, 1);
            }
            else
            {
                bobber?.Die();
            }
            VanillaPolePatches.UnpinFish(s.BobberId);

            string msg = outcome switch
            {
                2 => "anglersartoffishing:vanilla-caught",
                1 => "anglersartoffishing:fishing-snapped",
                _ => "anglersartoffishing:fishing-escaped"
            };
            channel.SendPacket(new FishingStatePacket { Phase = (int)FishingPhase.Idle, Message = msg }, player);
        }

        /// <summary>Vanilla StopFishing was triggered from somewhere else mid-fight.</summary>
        public void OnVanillaStop(IServerPlayer player)
        {
            if (!sessions.TryGetValue(player.PlayerUID, out var s)) return;
            sessions.Remove(player.PlayerUID);
            frozen.Remove(s.BobberId);
            VanillaPolePatches.UnpinFish(s.BobberId);
            if (sapi.World.GetEntityById(s.BobberId) is EntityBobber bobber) VanillaPolePatches.ClearCatch(bobber);
            channel.SendPacket(new FishingStatePacket { Phase = (int)FishingPhase.Idle, Message = "anglersartoffishing:fishing-escaped" }, player);
        }

        void Tick(float dt)
        {
            if (sessions.Count == 0) return;
            long now = sapi.World.ElapsedMilliseconds;
            List<string> dead = null;

            foreach (var kv in sessions)
            {
                var player = sapi.World.PlayerByUid(kv.Key) as IServerPlayer;
                var s = kv.Value;
                var bobber = sapi.World.GetEntityById(s.BobberId) as EntityBobber;

                bool playerGone = player == null || player.ConnectionState != EnumClientState.Playing || player.Entity == null;
                var slot = playerGone ? null : player.InventoryManager.ActiveHotbarSlot;
                bool stillHooked = !playerGone && bobber != null && bobber.Alive
                    && slot?.Itemstack?.Collectible is ItemFishingPole
                    && slot.Itemstack.Attributes.GetLong("bobberEntityId", 0) == s.BobberId;

                if (!stillHooked || now - s.Start > MaxFightMs) (dead ??= new List<string>()).Add(kv.Key);
                else if (s.HiddenFishId != 0 && sapi.World.GetEntityById(s.HiddenFishId) is Entity hidden && hidden.Alive)
                {
                    // Keep the hidden fish right by the bobber so it can't wander off or despawn before it's landed
                    hidden.Pos.SetPos(bobber.Pos.X, bobber.Pos.Y - 0.5, bobber.Pos.Z);
                    hidden.Pos.Motion.Set(0, 0, 0);
                }
            }

            if (dead == null) return;
            foreach (var uid in dead)
            {
                var s = sessions[uid];
                var player = sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (player?.Entity != null && player.ConnectionState == EnumClientState.Playing)
                {
                    Resolve(player, s, 0);
                }
                else
                {
                    sessions.Remove(uid);
                    frozen.Remove(s.BobberId);
                    VanillaPolePatches.UnpinFish(s.BobberId);
                    if (sapi.World.GetEntityById(s.BobberId) is EntityBobber b) VanillaPolePatches.ClearCatch(b);
                }
            }
        }
    }
}
