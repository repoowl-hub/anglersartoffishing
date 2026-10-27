using System;
using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Util;

namespace AnglersArtOfFishing
{
    /// <summary>
    /// One catchable species, loaded from assets/anglersartoffishing/config/species.json.
    /// </summary>
    public class FishSpecies
    {
        public string Code { get; set; }
        public string Name { get; set; }
        /// <summary>0..1</summary>
        public float Power { get; set; } = 0.4f;
        public float StaminaSeconds { get; set; } = 12f;
        /// <summary>0..1</summary>
        public float Aggression { get; set; } = 0.4f;
        /// <summary><see cref="FightStyle"/> name (Darter, Jumper, Runner, Ambusher, Bulldog, Headshaker). Empty = picked from Power.</summary>
        public string Style { get; set; }
        /// <summary>Server-side sanity check: a fight can't legitimately end faster than this.</summary>
        public float MinFightSeconds { get; set; } = 6f;
        /// <summary>Vanilla raw fish item, used only to match the hooked fish entity to this entry.</summary>
        public string CatchItem { get; set; }
    }

    public static class SpeciesRegistry
    {
        public static List<FishSpecies> All = new List<FishSpecies>();

        static readonly Dictionary<string, FishSpecies> resolved = new Dictionary<string, FishSpecies>();

        /// <summary>
        /// Fight data for a fish creature type. Any field set in the creature's shared
        /// <c>attributes.fishData.fight</c> block (power, stamina, aggression, minFightSeconds, style) wins; the rest
        /// comes from the best matching species.json entry, or, for fish nobody has described, from the fish's size.
        /// </summary>
        public static FishSpecies ResolveFor(EntityProperties props)
        {
            string key = props?.Code?.ToString() ?? "";
            if (resolved.TryGetValue(key, out var cached)) return cached;

            var baseline = FindByCode(props?.Code) ?? FromSize(props);
            var fight = props?.Attributes?["fishData"]?["fight"];
            var result = fight == null || !fight.Exists ? baseline : new FishSpecies
            {
                Code = baseline.Code,
                Name = baseline.Name,
                CatchItem = baseline.CatchItem,
                Power = fight["power"].AsFloat(baseline.Power),
                StaminaSeconds = fight["stamina"].AsFloat(baseline.StaminaSeconds),
                Aggression = fight["aggression"].AsFloat(baseline.Aggression),
                MinFightSeconds = fight["minFightSeconds"].AsFloat(baseline.MinFightSeconds),
                Style = fight["style"].AsString(baseline.Style)
            };
            resolved[key] = result;
            return result;
        }

        /// <summary>
        /// species.json entry for a creature code: a Code with a domain ("bluemarlin:marlin-blue-*") is matched as a
        /// wildcard against the full code, a plain Code as a substring of the path. The longest match wins.
        /// </summary>
        public static FishSpecies FindByCode(AssetLocation code)
        {
            if (code == null) return null;
            FishSpecies best = null;
            foreach (var s in All)
            {
                if (string.IsNullOrEmpty(s.Code)) continue;
                bool hit = s.Code.Contains(':') ? WildcardUtil.Match(new AssetLocation(s.Code), code) : code.Path.Contains(s.Code);
                if (hit && (best == null || s.Code.Length > best.Code.Length)) best = s;
            }
            return best;
        }

        /// <summary>Fallback for undescribed fish: scale the fight with the hitbox (vanilla: 0.5 panfish .. 0.8 arapaima).</summary>
        static FishSpecies FromSize(EntityProperties props)
        {
            float width = props?.CollisionBoxSize?.X ?? 0.6f;
            float power = Math.Clamp(0.2f + (width - 0.5f) * 1.8f, 0.1f, 0.85f);
            return new FishSpecies
            {
                Code = props?.Code?.Path,
                Power = power,
                StaminaSeconds = 5f + 13f * power,
                Aggression = 0.2f + 0.5f * power,
                MinFightSeconds = 2.5f + 8f * power,
                Style = StyleForPower(power).ToString()
            };
        }

        /// <summary>The species' configured style, or a size-based guess when none (or an unknown one) is set.</summary>
        public static FightStyle ResolveStyle(FishSpecies s, float power)
        {
            if (s?.Style != null && Enum.TryParse(s.Style, true, out FightStyle style)) return style;
            return StyleForPower(power);
        }

        public static FightStyle StyleForPower(float power) =>
            power < 0.30f ? FightStyle.Darter : power < 0.55f ? FightStyle.Headshaker : FightStyle.Runner;

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
                resolved.Clear();
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
        /// <summary>Entity code of the hooked fish (for drawing its model). Null for junk.</summary>
        public string FishEntityCode;
        /// <summary>Real fish kept in the world during the fight (Catch &amp; Release); the client doesn't draw it. 0 if none.</summary>
        public long HiddenFishEntityId;

        // Fight parameters (Fight phase)
        public string SpeciesName;
        public float Power;
        public float StaminaSeconds;
        public float Aggression;
        public int Style;
        public int RodMargin;
        public float ReelSpeed;
        public float Forgiveness;
        public float StartDistance;
        public float SlackEscapeSeconds;

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
