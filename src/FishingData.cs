using System;
using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.Common;

namespace AnglersArtOfFishing
{
    /// <summary>
    /// One catchable species, loaded from assets/anglersartoffishing/config/species.json.
    /// </summary>
    public class FishSpecies
    {
        public string Code { get; set; }
        public string Name { get; set; }
        /// <summary>Minimum rod strength (1-3) required before this fish can ever bite.</summary>
        public int MinRodStrength { get; set; } = 1;
        /// <summary>Bait type -> relative weight. A bait not listed here can never attract this fish.</summary>
        public Dictionary<string, float> Baits { get; set; } = new Dictionary<string, float>();
        /// <summary>Base spawn weight among eligible fish.</summary>
        public float Weight { get; set; } = 1f;
        public float MinSizeCm { get; set; } = 20f;
        public float MaxSizeCm { get; set; } = 50f;
        /// <summary>0..1</summary>
        public float Power { get; set; } = 0.4f;
        public float StaminaSeconds { get; set; } = 12f;
        /// <summary>0..1</summary>
        public float Aggression { get; set; } = 0.4f;
        /// <summary>Server-side sanity check: a fight can't legitimately end faster than this.</summary>
        public float MinFightSeconds { get; set; } = 6f;
        public string CatchItem { get; set; } = "game:fishraw-freshwater-trout-rainbow-adult";
        public int BaseQuantity { get; set; } = 1;
    }

    public static class SpeciesRegistry
    {
        public static List<FishSpecies> All = new List<FishSpecies>();

        public static void Load(ICoreAPI api)
        {
            try
            {
                var asset = api.Assets.TryGet(new AssetLocation("anglersartoffishing", "config/species.json"));
                if (asset == null)
                {
                    api.Logger.Error("[AnglersArtOfFishing] config/species.json not found");
                    return;
                }
                All = asset.ToObject<List<FishSpecies>>() ?? new List<FishSpecies>();
                api.Logger.Notification("[AnglersArtOfFishing] Loaded {0} fish species", All.Count);
            }
            catch (Exception e)
            {
                api.Logger.Error("[AnglersArtOfFishing] Failed to load species: {0}", e);
            }
        }

        /// <summary>
        /// Picks a species the given rod+bait can actually attract. Returns null if there are none.
        /// </summary>
        public static FishSpecies Roll(Random rand, int rodStrength, string baitType)
        {
            float total = 0f;
            var eligible = new List<(FishSpecies s, float w)>();
            foreach (var s in All)
            {
                if (s.MinRodStrength > rodStrength) continue;
                if (s.Baits == null || !s.Baits.TryGetValue(baitType, out float bw) || bw <= 0f) continue;
                float w = s.Weight * bw;
                eligible.Add((s, w));
                total += w;
            }
            if (eligible.Count == 0) return null;

            float r = (float)rand.NextDouble() * total;
            foreach (var (s, w) in eligible)
            {
                r -= w;
                if (r <= 0f) return s;
            }
            return eligible[eligible.Count - 1].s;
        }

        public static bool AnyForBait(int rodStrength, string baitType)
        {
            foreach (var s in All)
            {
                if (s.MinRodStrength <= rodStrength && s.Baits != null && s.Baits.TryGetValue(baitType, out float w) && w > 0f) return true;
            }
            return false;
        }
    }

    // ------------------------------------------------------------------ networking

    public enum FishingPhase
    {
        Idle = 0,
        Waiting = 1,
        Bite = 2,
        Fight = 3
    }

    /// <summary>Server -> client: describes the player's current fishing state.</summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class FishingStatePacket
    {
        public int Phase;
        /// <summary>Seconds the player has to set the hook (Bite phase).</summary>
        public float BiteWindow;
        public double BobberX, BobberY, BobberZ;
        public long BobberEntityId;

        // Fight parameters (Fight phase)
        public string SpeciesName;
        public float Power;
        public float StaminaSeconds;
        public float Aggression;
        public int RodMargin;
        public float ReelSpeed;
        public float Forgiveness;
        public float StartDistance;

        /// <summary>Lang-key or plain message to display as a toast (any phase).</summary>
        public string Message;
        public float SizeCm;
    }

    /// <summary>Client -> server.</summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class FishingActionPacket
    {
        /// <summary>0 = hook set, 1 = fight finished.</summary>
        public int Action;
        /// <summary>For Action 1: 2 = landed, 1 = line snapped, 0 = escaped.</summary>
        public int Outcome;
        public float FightSeconds;
    }
}
