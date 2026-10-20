using System;
using System.Collections.Generic;
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
        }

        public static void Remove()
        {
            harmony?.UnpatchAll(harmony.Id);
            harmony = null;
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

        // ------------------------------------------------------------------ patches

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
        }

        readonly ICoreServerAPI sapi;
        readonly IServerNetworkChannel channel;
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        readonly HashSet<long> frozen = new HashSet<long>();
        readonly Random rand = new Random();

        public VanillaFightServer(ICoreServerAPI api, IServerNetworkChannel channel)
        {
            sapi = api;
            this.channel = channel;
            api.Event.RegisterGameTickListener(Tick, 250);
        }

        public bool IsFighting(IServerPlayer player) => sessions.ContainsKey(player.PlayerUID);
        public bool IsFrozen(long bobberId) => frozen.Contains(bobberId);

        public bool TryStartFight(IServerPlayer player, ItemSlot slot, EntityBobber bobber)
        {
            if (slot.Itemstack?.Collectible is not ItemFishingPole) return false;

            // ---- what's on the hook (loot itself is still rolled by vanilla TryCatchFish on landing)
            string name;
            float power, stamina, aggression;
            if (bobber.caughtFish != null && bobber.caughtFish.Alive)
            {
                bool juvenile = bobber.caughtFish.Code.Path.Contains("juvenile");
                name = bobber.caughtFish.GetName();
                power = juvenile ? 0.3f : 0.6f + 0.2f * (float)rand.NextDouble();
            }
            else if (VanillaPolePatches.IsJunk(bobber))
            {
                name = Lang.Get("anglersartoffishing:vanilla-snag");
                power = 0.12f;
            }
            else
            {
                name = Lang.Get("anglersartoffishing:vanilla-fish");
                power = 0.25f + 0.5f * (float)rand.NextDouble();
            }
            stamina = 6f + 14f * power;
            aggression = 0.15f + 0.6f * power;

            // ---- fixed drag per pole tier
            bool bamboo = slot.Itemstack.Collectible.Variant["material"] == "bamboo";
            int rodMargin = bamboo ? 1 : 0;
            float reelSpeed = bamboo ? 1.1f : 1.0f;
            float forgiveness = bamboo ? 0.6f : 0.5f;

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
                MinSeconds = 2.5f + 4f * power,
                Name = name
            };

            var bp = bobber.Pos;
            channel.SendPacket(new FishingStatePacket
            {
                Phase = (int)FishingPhase.Fight,
                SpeciesName = name,
                Power = power,
                StaminaSeconds = stamina,
                Aggression = aggression,
                RodMargin = rodMargin,
                ReelSpeed = reelSpeed,
                Forgiveness = forgiveness,
                StartDistance = Math.Clamp((float)player.Entity.Pos.XYZ.DistanceTo(bp.XYZ) / CastMaxDist * 0.6f + 0.55f, 0.6f, 1f),
                BobberX = bp.X, BobberY = bp.Y, BobberZ = bp.Z,
                BobberEntityId = bobber.EntityId
            }, player);
            return true;
        }

        /// <summary>Returns true if the packet belonged to a vanilla-pole fight.</summary>
        public bool TryHandleAction(IServerPlayer player, FishingActionPacket packet)
        {
            if (!sessions.TryGetValue(player.PlayerUID, out var s)) return false;
            if (packet.Action != 1) return true;

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
            if (outcome != 2 && bobber != null)
            {
                VanillaPolePatches.ClearCatch(bobber);
            }
            else if (outcome == 2 && bobber != null && VanillaPolePatches.BobberState(bobber) == EnumBobberState.NoEntityFishCatch)
            {
                // Fallback for flat/test worlds or regions where vanilla climate spawn maps lack native fish
                if (!bobber.HasCatchable(bobber.BaitStack, out _))
                {
                    var fallbackFish = sapi.World.GetItem(new AssetLocation("game:fishraw-freshwater-trout-rainbow-adult"));
                    if (fallbackFish != null)
                    {
                        var fallbackStack = new ItemStack(fallbackFish, 1);
                        if (!player.InventoryManager.TryGiveItemstack(fallbackStack, true))
                        {
                            sapi.World.SpawnItemEntity(fallbackStack, player.Entity.Pos.XYZ);
                        }
                    }
                }
            }

            // Vanilla cleanup: TryCatchFish (loot on success, nothing after ClearCatch), bobber death, rope removal
            var slot = player.InventoryManager.ActiveHotbarSlot;
            if (slot?.Itemstack?.Collectible is ItemFishingPole pole && slot.Itemstack.Attributes.GetLong("bobberEntityId", 0) == s.BobberId)
            {
                if (pole.StopFishing(slot.Itemstack, player.Entity)) slot.MarkDirty();
                if (outcome == 1) pole.DamageItem(sapi.World, player.Entity, slot, 2);
                else if (outcome == 2) pole.DamageItem(sapi.World, player.Entity, slot, 1);
            }
            else
            {
                bobber?.Die();
            }

            string msg = outcome switch
            {
                2 => "anglersartoffishing:vanilla-caught",
                1 => "anglersartoffishing:fishing-snapped",
                _ => "anglersartoffishing:fishing-escaped"
            };
            channel.SendPacket(new FishingStatePacket { Phase = (int)FishingPhase.Idle, Message = msg, SpeciesName = s.Name }, player);
        }

        /// <summary>Vanilla StopFishing was triggered from somewhere else mid-fight.</summary>
        public void OnVanillaStop(IServerPlayer player)
        {
            if (!sessions.TryGetValue(player.PlayerUID, out var s)) return;
            sessions.Remove(player.PlayerUID);
            frozen.Remove(s.BobberId);
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
                    if (sapi.World.GetEntityById(s.BobberId) is EntityBobber b) VanillaPolePatches.ClearCatch(b);
                }
            }
        }
    }
}
