using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace AnglersArtOfFishing
{
    /// <summary>
    /// Fishing rod. Right click to cast / reel in, click again when a fish bites to set the hook.
    /// All game logic lives in <see cref="FishingServer"/> / <see cref="FishingClient"/>.
    /// </summary>
    public class ItemFishingRod : Item
    {
        public int RodStrength => Attributes?["rodStrength"].AsInt(1) ?? 1;
        public float ReelSpeed => Attributes?["reelSpeed"].AsFloat(1f) ?? 1f;
        public float Forgiveness => Attributes?["forgiveness"].AsFloat(0.45f) ?? 0.45f;

        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            // Never let a click with the rod fall through to block / entity interaction
            handling = EnumHandHandling.PreventDefault;
            if (!firstEvent) return;

            var sys = api.ModLoader.GetModSystem<AnglersArtModSystem>();
            if (sys == null) return;

            if (api.Side == EnumAppSide.Server)
            {
                if (byEntity is EntityPlayer ep && ep.Player is IServerPlayer sp) sys.Server?.OnRodUsed(sp, slot);
            }
            else
            {
                sys.Client?.OnRodUsed();
            }
        }

        public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel)
        {
            // Keep the interaction "alive" so the hold animation / use state stays consistent while reeling.
            return true;
        }

        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
        {
            base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
            dsc.AppendLine(Lang.Get("anglersartoffishing:rod-strength", RodStrength));
            dsc.AppendLine(Lang.Get("anglersartoffishing:rod-help"));
        }
    }
}
