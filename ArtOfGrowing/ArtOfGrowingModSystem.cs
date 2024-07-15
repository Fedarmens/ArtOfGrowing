using ArtOfGrowing.BlockEntites;
using ArtOfGrowing.Blocks;
using ArtOfGrowing.CollectibleBehaviors;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace ArtOfGrowing
{
    public class ArtOfGrowingModSystem : ModSystem
    {
        
        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            api.RegisterBlockClass("BlockHayStorage", typeof(AOGBlockGroundStorage));
            api.RegisterBlockEntityClass("HayStorage", typeof(AOGBlockEntityGroundStorage));
            api.RegisterCollectibleBehaviorClass("HayStorable", typeof(AOGCollectibleBehaviorGroundStorable));
            api.World.Logger.StoryEvent(Lang.Get("artofgrowing:It grows..."));
        }        
    }
}
