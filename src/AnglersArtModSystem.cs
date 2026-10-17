using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace AnglersArtOfFishing
{
    public class AnglersArtModSystem : ModSystem
    {
        public const string ChannelName = "anglersartoffishing";

        public FishingServer Server { get; private set; }
        public FishingClient Client { get; private set; }

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            api.RegisterItemClass("AAFItemFishingRod", typeof(ItemFishingRod));
        }

        public override void AssetsLoaded(ICoreAPI api)
        {
            base.AssetsLoaded(api);
            if (api.Side == EnumAppSide.Server) SpeciesRegistry.Load(api);
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);
            Server = new FishingServer(api);
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            base.StartClientSide(api);
            Client = new FishingClient(api);
        }

        public override void Dispose()
        {
            Client?.Dispose();
            base.Dispose();
        }
    }
}
