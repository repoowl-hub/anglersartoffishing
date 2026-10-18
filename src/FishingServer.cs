using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace AnglersArtOfFishing
{
    /// <summary>
    /// Authoritative side: validates casts, decides *what* bites (species gated by rod + bait),
    /// hands the fight over to the client for smooth simulation, then verifies and rewards the result.
    /// </summary>
    public class FishingServer
    {
        const float CastMinDist = 2.5f;
        const float CastMaxDist = 16f;
        const float BiteWindowSeconds = 2.2f;
        const long BiteGraceMs = 500;
        const float MaxLineDistance = 26f;
        const long MaxFightMs = 5 * 60 * 1000;

        class Session
        {
            public FishingPhase Phase;
            public Vec3d Bobber;
            public long PhaseEnd;
            public long FightStart;
            public FishSpecies Species;
            public string BaitType;
            public int RodStrength;
            public float SizeFactor;
            public float SizeCm;
        }

        readonly ICoreServerAPI sapi;
        readonly IServerNetworkChannel channel;
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        readonly Random rand = new Random();

        public IServerNetworkChannel Channel => channel;
        public VanillaFightServer VanillaServer { get; set; }

        public FishingServer(ICoreServerAPI api)
        {
            sapi = api;
            channel = api.Network.RegisterChannel(AnglersArtModSystem.ChannelName)
                .RegisterMessageType<FishingStatePacket>()
                .RegisterMessageType<FishingActionPacket>()
                .SetMessageHandler<FishingActionPacket>(OnAction);

            api.Event.RegisterGameTickListener(Tick, 100);
            api.Event.PlayerDisconnect += p => sessions.Remove(p.PlayerUID);
        }

        // ------------------------------------------------------------------ rod use

        public void OnRodUsed(IServerPlayer player, ItemSlot rodSlot)
        {
            if (sessions.TryGetValue(player.PlayerUID, out var existing))
            {
                // Clicking while waiting = reel the line back in. During bite/fight the client drives things.
                if (existing.Phase == FishingPhase.Waiting) End(player, existing, "anglersartoffishing:fishing-retrieved");
                return;
            }

            var rod = rodSlot.Itemstack?.Collectible as ItemFishingRod;
            if (rod == null) return;

            ItemSlot baitSlot = player.Entity.LeftHandItemSlot;
            string baitType = baitSlot?.Itemstack?.Collectible?.Attributes?["baitType"].AsString(null);
            if (string.IsNullOrEmpty(baitType)) { Tell(player, "anglersartoffishing:fishing-nobait"); return; }

            Vec3d bobber = FindWater(player);
            if (bobber == null) { Tell(player, "anglersartoffishing:fishing-nowater"); return; }

            int strength = rod.RodStrength;
            if (!SpeciesRegistry.AnyForBait(strength, baitType)) { Tell(player, "anglersartoffishing:fishing-nothingbites"); return; }

            FishSpecies species = SpeciesRegistry.Roll(rand, strength, baitType);
            if (species == null) { Tell(player, "anglersartoffishing:fishing-nothingbites"); return; }

            float biteSpeed = Math.Max(0.25f, baitSlot.Itemstack.Collectible.Attributes?["biteSpeed"].AsFloat(1f) ?? 1f);
            float wait = (3.5f + (float)rand.NextDouble() * 7.5f) / biteSpeed;

            var session = new Session
            {
                Phase = FishingPhase.Waiting,
                Bobber = bobber,
                PhaseEnd = sapi.World.ElapsedMilliseconds + (long)(wait * 1000),
                Species = species,
                BaitType = baitType,
                RodStrength = strength
            };
            sessions[player.PlayerUID] = session;

            Splash(bobber, 6);
            channel.SendPacket(new FishingStatePacket { Phase = (int)FishingPhase.Waiting, BobberX = bobber.X, BobberY = bobber.Y, BobberZ = bobber.Z }, player);
        }

        Vec3d FindWater(IServerPlayer player)
        {
            var entity = player.Entity;
            Vec3d eye = entity.Pos.XYZ.Add(0, entity.LocalEyePos.Y, 0);
            Vec3f dir = entity.Pos.GetViewVector();
            var accessor = sapi.World.BlockAccessor;
            var pos = new BlockPos(entity.Pos.Dimension);

            for (float d = CastMinDist; d <= CastMaxDist; d += 0.25f)
            {
                double x = eye.X + dir.X * d, y = eye.Y + dir.Y * d, z = eye.Z + dir.Z * d;
                pos.Set((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));

                Block fluid = accessor.GetBlock(pos, BlockLayersAccess.Fluid);
                if (fluid != null && fluid.BlockId != 0 && fluid.Code.Path.StartsWith("water"))
                {
                    // Bobber sits on the surface: find the top of the water column
                    var up = pos.Copy();
                    for (int i = 0; i < 4; i++)
                    {
                        up.Y++;
                        Block above = accessor.GetBlock(up, BlockLayersAccess.Fluid);
                        if (above == null || above.BlockId == 0 || !above.Code.Path.StartsWith("water")) break;
                        pos.Y = up.Y;
                    }
                    return new Vec3d(pos.X + 0.5, pos.Y + 0.9, pos.Z + 0.5);
                }

                Block solid = accessor.GetBlock(pos, BlockLayersAccess.Solid);
                if (solid != null && solid.BlockId != 0 && solid.CollisionBoxes != null && solid.CollisionBoxes.Length > 0) return null;
            }
            return null;
        }

        // ------------------------------------------------------------------ tick

        void Tick(float dt)
        {
            if (sessions.Count == 0) return;
            long now = sapi.World.ElapsedMilliseconds;
            List<string> done = null;

            foreach (var kv in sessions)
            {
                var player = sapi.World.PlayerByUid(kv.Key) as IServerPlayer;
                var s = kv.Value;

                if (player == null || player.ConnectionState != EnumClientState.Playing || player.Entity == null)
                {
                    (done ??= new List<string>()).Add(kv.Key);
                    continue;
                }

                // Must keep holding the rod and stay near the water
                bool holdingRod = player.InventoryManager.ActiveHotbarSlot?.Itemstack?.Collectible is ItemFishingRod;
                if (!holdingRod || player.Entity.Pos.XYZ.DistanceTo(s.Bobber) > MaxLineDistance)
                {
                    (done ??= new List<string>()).Add(kv.Key);
                    // The fish got away when the rod was put away mid-fight / bite
                    if (s.Phase != FishingPhase.Waiting) ConsumeBait(player, s);
                    channel.SendPacket(new FishingStatePacket { Phase = (int)FishingPhase.Idle, Message = holdingRod ? "anglersartoffishing:fishing-toofar" : null }, player);
                    continue;
                }

                switch (s.Phase)
                {
                    case FishingPhase.Waiting:
                        if (now >= s.PhaseEnd)
                        {
                            s.Phase = FishingPhase.Bite;
                            s.PhaseEnd = now + (long)(BiteWindowSeconds * 1000);
                            Splash(s.Bobber, 22);
                            channel.SendPacket(new FishingStatePacket { Phase = (int)FishingPhase.Bite, BiteWindow = BiteWindowSeconds, BobberX = s.Bobber.X, BobberY = s.Bobber.Y, BobberZ = s.Bobber.Z }, player);
                        }
                        break;

                    case FishingPhase.Bite:
                        if (now >= s.PhaseEnd + BiteGraceMs)
                        {
                            ConsumeBait(player, s);
                            (done ??= new List<string>()).Add(kv.Key);
                            channel.SendPacket(new FishingStatePacket { Phase = (int)FishingPhase.Idle, Message = "anglersartoffishing:fishing-missed" }, player);
                        }
                        break;

                    case FishingPhase.Fight:
                        if (now - s.FightStart > MaxFightMs)
                        {
                            (done ??= new List<string>()).Add(kv.Key);
                            channel.SendPacket(new FishingStatePacket { Phase = (int)FishingPhase.Idle, Message = "anglersartoffishing:fishing-escaped" }, player);
                        }
                        break;
                }
            }

            if (done != null) foreach (var uid in done) sessions.Remove(uid);
        }

        // ------------------------------------------------------------------ client actions

        void OnAction(IServerPlayer player, FishingActionPacket packet)
        {
            if (!sessions.TryGetValue(player.PlayerUID, out var s))
            {
                VanillaServer?.TryHandleAction(player, packet);
                return;
            }
            long now = sapi.World.ElapsedMilliseconds;

            if (packet.Action == 0)
            {
                if (s.Phase != FishingPhase.Bite || now > s.PhaseEnd + BiteGraceMs) return;
                StartFight(player, s, now);
            }
            else if (packet.Action == 1 && s.Phase == FishingPhase.Fight)
            {
                float elapsed = (now - s.FightStart) / 1000f;
                switch (packet.Outcome)
                {
                    case 2:
                        // Anti-cheat: nobody lands a fish faster than the species minimum
                        if (elapsed < s.Species.MinFightSeconds * 0.6f)
                        {
                            sapi.Logger.Warning("[AnglersArtOfFishing] {0} landed a fish implausibly fast ({1:0.0}s)", player.PlayerName, elapsed);
                            End(player, s, "anglersartoffishing:fishing-escaped", consumeBait: true);
                        }
                        else Land(player, s);
                        break;
                    case 1:
                        DamageRod(player, 3);
                        End(player, s, "anglersartoffishing:fishing-snapped", consumeBait: true);
                        break;
                    default:
                        End(player, s, "anglersartoffishing:fishing-escaped", consumeBait: true);
                        break;
                }
            }
        }

        void StartFight(IServerPlayer player, Session s, long now)
        {
            var sp = s.Species;
            var rod = player.InventoryManager.ActiveHotbarSlot.Itemstack.Collectible as ItemFishingRod;

            s.Phase = FishingPhase.Fight;
            s.FightStart = now;
            s.SizeFactor = (float)Math.Pow(rand.NextDouble(), 1.7);
            s.SizeCm = sp.MinSizeCm + (sp.MaxSizeCm - sp.MinSizeCm) * s.SizeFactor;

            // Bigger individuals fight a little harder than the species baseline
            float sizeBoost = 0.85f + 0.3f * s.SizeFactor;

            channel.SendPacket(new FishingStatePacket
            {
                Phase = (int)FishingPhase.Fight,
                SpeciesName = sp.Name,
                SizeCm = s.SizeCm,
                Power = Math.Clamp(sp.Power * sizeBoost, 0.05f, 1f),
                StaminaSeconds = sp.StaminaSeconds * (0.9f + 0.3f * s.SizeFactor),
                Aggression = sp.Aggression,
                RodMargin = Math.Max(0, rod.RodStrength - sp.MinRodStrength),
                ReelSpeed = rod.ReelSpeed,
                Forgiveness = rod.Forgiveness,
                StartDistance = Math.Clamp((float)player.Entity.Pos.XYZ.DistanceTo(s.Bobber) / CastMaxDist * 0.6f + 0.55f, 0.6f, 1f),
                BobberX = s.Bobber.X, BobberY = s.Bobber.Y, BobberZ = s.Bobber.Z
            }, player);
        }

        // ------------------------------------------------------------------ results

        void Land(IServerPlayer player, Session s)
        {
            var sp = s.Species;
            Item item = sapi.World.GetItem(new AssetLocation(sp.CatchItem)) ?? sapi.World.GetItem(new AssetLocation("game:fish-raw"));
            if (item == null)
            {
                sapi.Logger.Error("[AnglersArtOfFishing] Catch item '{0}' does not exist", sp.CatchItem);
                End(player, s, null);
                return;
            }

            int qty = Math.Max(1, (int)Math.Round(sp.BaseQuantity * (0.6f + 0.8f * s.SizeFactor)));
            var stack = new ItemStack(item, qty);
            stack.Attributes.SetFloat("fishSizeCm", s.SizeCm);
            stack.Attributes.SetString("fishSpecies", sp.Code);
            // Compatibility with The Angler's Catch
            stack.Attributes.SetFloat("fishSize", s.SizeCm);
            stack.Attributes.SetString("speciesCode", sp.Code);

            if (!player.InventoryManager.TryGiveItemstack(stack, true))
            {
                sapi.World.SpawnItemEntity(stack, player.Entity.Pos.XYZ.Add(0, 0.5, 0));
            }

            DamageRod(player, 1);

            float chance = player.Entity.LeftHandItemSlot?.Itemstack?.Collectible?.Attributes?["consumeChance"].AsFloat(1f) ?? 1f;
            bool consume = rand.NextDouble() <= chance;

            sessions.Remove(player.PlayerUID);
            if (consume) ConsumeBait(player, s);
            channel.SendPacket(new FishingStatePacket
            {
                Phase = (int)FishingPhase.Idle,
                Message = "anglersartoffishing:fishing-caught",
                SpeciesName = sp.Name,
                SizeCm = s.SizeCm
            }, player);
        }

        void End(IServerPlayer player, Session s, string message, bool consumeBait = false)
        {
            sessions.Remove(player.PlayerUID);
            if (consumeBait) ConsumeBait(player, s);
            channel.SendPacket(new FishingStatePacket { Phase = (int)FishingPhase.Idle, Message = message }, player);
        }

        void ConsumeBait(IServerPlayer player, Session s)
        {
            var slot = player.Entity.LeftHandItemSlot;
            if (slot == null || slot.Empty) return;
            if (slot.Itemstack.Collectible.Attributes?["baitType"].AsString(null) != s.BaitType) return;
            slot.TakeOut(1);
            slot.MarkDirty();
        }

        void DamageRod(IServerPlayer player, int amount)
        {
            var slot = player.InventoryManager.ActiveHotbarSlot;
            if (slot?.Itemstack?.Collectible is ItemFishingRod rod) rod.DamageItem(sapi.World, player.Entity, slot, amount);
        }

        void Tell(IServerPlayer player, string key)
        {
            channel.SendPacket(new FishingStatePacket { Phase = (int)FishingPhase.Idle, Message = key }, player);
        }

        void Splash(Vec3d pos, float quantity)
        {
            var props = new SimpleParticleProperties(
                quantity, quantity + 6, ColorUtil.ToRgba(190, 200, 235, 255),
                new Vec3d(pos.X - 0.15, pos.Y, pos.Z - 0.15), new Vec3d(pos.X + 0.15, pos.Y + 0.1, pos.Z + 0.15),
                new Vec3f(-0.6f, 1.2f, -0.6f), new Vec3f(0.6f, 2.2f, 0.6f),
                0.7f, 1f, 0.08f, 0.22f, EnumParticleModel.Quad);
            sapi.World.SpawnParticles(props);
        }
    }
}
