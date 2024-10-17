using ArtOfGrowing.BlockEntites;
using ArtOfGrowing.Blocks;
using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ArtOfGrowing.CollectibleBehaviors
{
    public enum AOGEnumGroundStorageLayout
    {
        /// <summary>
        /// Single item in the center
        /// </summary>
        SingleCenter,
        /// <summary>
        /// One item left, one item right
        /// </summary>
        Halves,
        /// <summary>
        /// Needs a wall behind
        /// </summary>
        WallHalves,
        /// <summary>
        /// One item in each 4 corners
        /// </summary>
        Quadrants,
        /// <summary>
        /// A generic stack of items
        /// </summary>
        Stacking
    }
    public enum AOGEnumDropType
    {
        Items,
        Block,
        None
    }


    public class AOGGroundStorageProperties
    {
        public AOGEnumGroundStorageLayout Layout = AOGEnumGroundStorageLayout.SingleCenter;
        public int WallOffY = 1;
        public AssetLocation PlaceRemoveSound = new AssetLocation("sounds/player/build");
        public bool RandomizeSoundPitch;
        public AssetLocation StackingModel;

        [Obsolete("Use ModelItemsToStackSizeRatio instead, which is now a float instead of int?")]
        public int? TessQuantityElements { set { ModelItemsToStackSizeRatio = value ?? 0; } get { return (int)ModelItemsToStackSizeRatio; } }

        public float ModelItemsToStackSizeRatio = 1;
        public Dictionary<string, AssetLocation> StackingTextures;
        public int MaxStackingHeight = 1;
        public int StackingCapacity = 1;
        public int TransferQuantity = 1;
        public int BulkTransferQuantity = 4;
        public bool SprintKey;
        public bool UpSolid = false;

        public Cuboidf CollisionBox;
        public Cuboidf SelectionBox;
        public float CbScaleYByLayer = 0;

        public int MaxFireable = 9999;

        public int DropUse = 1;
        public int DropBulk = 4;
        public AOGEnumDropType CanDrop = AOGEnumDropType.None;
        public AssetLocation DropBlock;
        public AssetLocation DropItem;
        public AssetLocation DropItem2;
        public int DropCount = 1;
        public int DropCount2 = 1;
        public bool CanWater = false;

        public AOGGroundStorageProperties Clone()
        {
            return new AOGGroundStorageProperties()
            {
                Layout = Layout,
                WallOffY = WallOffY,
                PlaceRemoveSound = PlaceRemoveSound,
                RandomizeSoundPitch = RandomizeSoundPitch,
                StackingCapacity = StackingCapacity,
                StackingModel = StackingModel,
                StackingTextures = StackingTextures,
                MaxStackingHeight = MaxStackingHeight,
                TransferQuantity = TransferQuantity,
                BulkTransferQuantity = BulkTransferQuantity,
                CollisionBox = CollisionBox,
                SelectionBox = SelectionBox,
                CbScaleYByLayer = CbScaleYByLayer,
                MaxFireable = MaxFireable,
                SprintKey = SprintKey,
                UpSolid = UpSolid,
                CanDrop = CanDrop,
                DropUse = DropUse,
                DropBulk = DropBulk,
                DropBlock = DropBlock,
                DropItem = DropItem,
                DropItem2 = DropItem2,
                DropCount = DropCount,
                DropCount2 = DropCount2,
                CanWater = CanWater,
            };
        }
    }


    public class AOGCollectibleBehaviorGroundStorable : CollectibleBehavior
    {
        public AOGGroundStorageProperties StorageProps
        {
            get;
            protected set;
        }

        public AOGCollectibleBehaviorGroundStorable(CollectibleObject collObj) : base(collObj)
        {
        }

        public override void Initialize(JsonObject properties)
        {
            base.Initialize(properties);

            StorageProps = properties.AsObject<AOGGroundStorageProperties>(null, collObj.Code.Domain);
        }


        public override void OnHeldInteractStart(ItemSlot itemslot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling)
        {
            Interact(itemslot, byEntity, blockSel, entitySel, firstEvent, ref handHandling, ref handling);
        }


        public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot, ref EnumHandling handling)
        {
            handling = EnumHandling.PassThrough;
            return new WorldInteraction[]
            {
                new WorldInteraction
                {
                    HotKeyCodes = StorageProps.SprintKey ? new string[] {"ctrl", "shift" } : new string[] {"shift"},
                    ActionLangCode = "heldhelp-place",
                    MouseButton = EnumMouseButton.Right
                }
            };
        }


        public static void Interact(ItemSlot itemslot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling)
        {
            IWorldAccessor world = byEntity?.World;

            if (blockSel == null || world == null || !byEntity.Controls.ShiftKey) return;


            IPlayer byPlayer = null;
            if (byEntity is EntityPlayer) byPlayer = world.PlayerByUid(((EntityPlayer)byEntity).PlayerUID);
            if (byPlayer == null) return;

            if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak))
            {
                itemslot.MarkDirty();
                world.BlockAccessor.MarkBlockDirty(blockSel.Position.UpCopy());
                return;
            }

            AOGBlockGroundStorage blockgs = world.GetBlock(new AssetLocation("haystorage")) as AOGBlockGroundStorage;
            if (blockgs == null) return;

            BlockEntity be = world.BlockAccessor.GetBlockEntity(blockSel.Position);
            BlockEntity beAbove = world.BlockAccessor.GetBlockEntity(blockSel.Position.UpCopy());
            if (be is AOGBlockEntityGroundStorage || beAbove is AOGBlockEntityGroundStorage)
            {
                if (((be as AOGBlockEntityGroundStorage) ?? (beAbove as AOGBlockEntityGroundStorage)).OnPlayerInteractStart(byPlayer, blockSel))
                {
                    handHandling = EnumHandHandling.PreventDefault;
                }
                return;
            }

            // Must be aiming at the up face
            if (blockSel.Face != BlockFacing.UP) return;
            Block onBlock = world.BlockAccessor.GetBlock(blockSel.Position);

            // Must have a support below
            if (!onBlock.CanAttachBlockAt(world.BlockAccessor, blockgs, blockSel.Position, BlockFacing.UP))
            {
                return;
            }

            // Must have empty space above
            BlockPos pos = blockSel.Position.AddCopy(blockSel.Face);
            if (world.BlockAccessor.GetBlock(pos).Replaceable < 6000) return;


            if (blockgs.CreateStorage(byEntity.World, blockSel, byPlayer))
            {
                handHandling = EnumHandHandling.PreventDefault;
            }
        }



    }
}
