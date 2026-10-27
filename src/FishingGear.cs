using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace AnglersArtOfFishing
{
    /// <summary>
    /// Lures and pole upgrades.
    ///
    /// Lures: each lure item has its own <c>baitTag</c> and a <c>lureStyles</c> list. Vanilla only lets a fish bite
    /// a bait whose tag is in the fish entity's <c>baitTags</c>, so on load we append each lure's tag to every fish
    /// whose species (species.json) fights in one of those styles.
    ///
    /// Upgrades: line and reel items carry a <c>poleUpgrade</c> attribute. Crafting one onto a vanilla pole stores
    /// its item code on the pole (<see cref="LineAttr"/> / <see cref="ReelAttr"/>); the fight reads the bonuses back.
    /// </summary>
    public static class FishingGear
    {
        public const string LineAttr = "aafLine";
        public const string ReelAttr = "aafReel";

        // ------------------------------------------------------------------ lures

        /// <summary>Server side, after all assets are loaded: teach fish to bite the lures that suit their style.</summary>
        public static void ApplyLureTags(ICoreAPI api)
        {
            // style -> lure tags
            var tagsByStyle = new Dictionary<FightStyle, List<string>>();
            foreach (var item in api.World.Items)
            {
                var attrs = item?.Attributes;
                if (item?.Code == null || attrs == null || !attrs["lureStyles"].Exists) continue;
                string tag = attrs["baitTag"].AsString(null);
                if (tag == null) continue;

                foreach (var styleName in attrs["lureStyles"].AsArray<string>(Array.Empty<string>()))
                {
                    if (!Enum.TryParse(styleName, true, out FightStyle style)) continue;
                    if (!tagsByStyle.TryGetValue(style, out var list)) tagsByStyle[style] = list = new List<string>();
                    if (!list.Contains(tag)) list.Add(tag);
                }
            }
            if (tagsByStyle.Count == 0) return;

            int patched = 0;
            foreach (var type in api.World.EntityTypes)
            {
                // Any fish creature, vanilla or modded (vanilla codes start with "fish-", modded ones need not)
                string path = type?.Code?.Path;
                if (path == null || !(type.Class == "EntityFish" || path.StartsWith("fish-")) || type.Attributes?.Token is not JObject attrObj) continue;

                var species = SpeciesRegistry.ResolveFor(type);
                if (!tagsByStyle.TryGetValue(SpeciesRegistry.ResolveStyle(species, species.Power), out var tags)) continue;

                if (attrObj["baitTags"] is not JArray baitTags) attrObj["baitTags"] = baitTags = new JArray();
                foreach (var tag in tags)
                {
                    bool present = false;
                    foreach (var t in baitTags) if ((string)t == tag) { present = true; break; }
                    if (!present) baitTags.Add(tag);
                }
                patched++;
            }
            api.Logger.Notification("[AnglersArtOfFishing] Added lure bait tags to {0} fish entity types", patched);
        }

        // ------------------------------------------------------------------ pole upgrades

        /// <summary>Postfix on ItemFishingPole.OnCreatedByCrafting: remember a crafted-on line or reel.</summary>
        public static void Postfix_PoleCrafted(ItemSlot[] allInputSlots, ItemSlot outputSlot)
        {
            if (outputSlot?.Itemstack == null || allInputSlots == null) return;
            foreach (var slot in allInputSlots)
            {
                var up = slot?.Itemstack?.Collectible?.Attributes?["poleUpgrade"];
                if (up == null || !up.Exists) continue;
                string attr = up["slot"].AsString() switch
                {
                    "line" => LineAttr,
                    "reel" => ReelAttr,
                    _ => null
                };
                if (attr != null) outputSlot.Itemstack.Attributes.SetString(attr, slot.Itemstack.Collectible.Code.ToString());
            }
        }

        /// <summary>Postfix on ItemFishingPole.GetHeldItemInfo: list the fitted line and reel.</summary>
        public static void Postfix_PoleInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world)
        {
            var stack = inSlot?.Itemstack;
            if (stack == null) return;
            AppendPart(stack, LineAttr, "anglersartoffishing:pole-line", dsc, world);
            AppendPart(stack, ReelAttr, "anglersartoffishing:pole-reel", dsc, world);
        }

        static void AppendPart(ItemStack pole, string attr, string langKey, StringBuilder dsc, IWorldAccessor world)
        {
            var part = GetPart(pole, attr, world);
            if (part != null) dsc.AppendLine(Lang.Get(langKey, part.GetHeldItemName(new ItemStack(part))));
        }

        static Item GetPart(ItemStack pole, string attr, IWorldAccessor world)
        {
            string code = pole?.Attributes?.GetString(attr);
            return code == null ? null : world.GetItem(new AssetLocation(code));
        }

        /// <summary>Extra seconds of line overload the fitted line tolerates.</summary>
        public static float SnapBonus(ItemStack pole, IWorldAccessor world) =>
            GetPart(pole, LineAttr, world)?.Attributes?["poleUpgrade"]["snapBonus"].AsFloat(0f) ?? 0f;

        /// <summary>Fractional reel speed bonus of the fitted reel (0.1 = +10%).</summary>
        public static float ReelSpeedBonus(ItemStack pole, IWorldAccessor world) =>
            GetPart(pole, ReelAttr, world)?.Attributes?["poleUpgrade"]["reelSpeedBonus"].AsFloat(0f) ?? 0f;
    }
}
