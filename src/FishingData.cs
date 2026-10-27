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
        /// <summary>Bait type -> relative weight. Not applied yet: vanilla still picks which fish bites.</summary>
        public Dictionary<string, float> Baits { get; set; } = new Dictionary<string, float>();
        /// <summary>Base spawn weight. Not applied yet: vanilla still picks which fish bites.</summary>
        public float Weight { get; set; } = 1f;
        /// <summary>0..1</summary>
        public float Power { get; set; } = 0.4f;
        public float StaminaSeconds { get; set; } = 12f;
        /// <summary>0..1</summary>
        public float Aggression { get; set; } = 0.4f;
        /// <summary>Server-side sanity check: a fight can't legitimately end faster than this.</summary>
        public float MinFightSeconds { get; set; } = 6f;
        public string CatchItem { get; set; } = "game:fishraw-freshwater-trout-rainbow-adult";
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
    }

    // ------------------------------------------------------------------ networking

    public enum FishingPhase
    {
        Idle = 0,
        Fight = 1
    }

    /// <summary>Server -> client: describes the player's current fishing state.</summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class FishingStatePacket
    {
        public int Phase;
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
    }

    /// <summary>Client -> server.</summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class FishingActionPacket
    {
        /// <summary>Sent once the fight finishes: 2 = landed, 1 = line snapped, 0 = escaped.</summary>
        public int Outcome;
        public float FightSeconds;
    }
}
