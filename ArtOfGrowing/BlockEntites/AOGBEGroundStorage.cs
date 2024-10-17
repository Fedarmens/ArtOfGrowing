using ArtOfGrowing.Blocks;
using ArtOfGrowing.CollectibleBehaviors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using System.Collections;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Diagnostics.Metrics;

namespace ArtOfGrowing.BlockEntites
{

    public class AOGBlockEntityGroundStorage : BlockEntityDisplay, IBlockEntityContainer, ITexPositionSource, IRotatable
    {
        Block block;
        RoomRegistry roomReg;
        public override void Initialize(ICoreAPI api)
        {
            capi = api as ICoreClientAPI;
            block = api.World.BlockAccessor.GetBlock(Pos);
            base.Initialize(api);
            RegisterGameTickListener(OnTick, 200);

            roomReg = Api.ModLoader.GetModSystem<RoomRegistry>();

            Inventory.OnAcquireTransitionSpeed = Inventory_OnAcquireTransitionSpeed;

            DetermineStorageProperties(null);

            if (capi != null)
            {
                updateMeshes();
                //initMealRandomizer();
            }
        }
        public object inventoryLock = new object(); // Because OnTesselation runs in another thread

        protected InventoryGeneric inventory;

        public AOGGroundStorageProperties StorageProps { get; protected set; }
        public bool forceStorageProps = false;
        protected AOGEnumGroundStorageLayout? overrideLayout;

        public int TransferQuantity => StorageProps?.TransferQuantity ?? 1;
        public int BulkTransferQuantity => StorageProps.Layout == AOGEnumGroundStorageLayout.Stacking ? StorageProps.BulkTransferQuantity : 1;

        protected virtual int invSlotCount => 4;
        protected Cuboidf[] colBoxes;
        protected Cuboidf[] selBoxes;

        ItemSlot isUsingSlot;
        /// <summary>
        /// Needed to suppress client-side "set this block to air on empty inventory" functionality for newly placed blocks
        /// otherwise set block to air can be triggered e.g. by the second tick of a player's right-click block interaction client-side, before the first server packet arrived to set the inventory to non-empty (due to lag of any kind)
        /// </summary>
        public bool clientsideFirstPlacement = false;

        public override int DisplayedItems {
            get
            {
                if (StorageProps == null) return 0;
                switch (StorageProps.Layout)
                {
                    case AOGEnumGroundStorageLayout.SingleCenter: return 1;
                    case AOGEnumGroundStorageLayout.Halves: return 2;
                    case AOGEnumGroundStorageLayout.WallHalves: return 2;
                    case AOGEnumGroundStorageLayout.Quadrants: return 4;
                    case AOGEnumGroundStorageLayout.Stacking: return 1;
                }

                return 0;
            }
        }
        public int TotalStackSize
        {
            get
            {
                int sum = 0;
                foreach (var slot in inventory) sum += slot.StackSize;
                return sum;
            }
        }

        public int Capacity
        {
            get { 
                switch (StorageProps.Layout)
                {
                    case AOGEnumGroundStorageLayout.SingleCenter: return 1;
                    case AOGEnumGroundStorageLayout.Halves: return 2;
                    case AOGEnumGroundStorageLayout.WallHalves: return 2;
                    case AOGEnumGroundStorageLayout.Quadrants: return 4;
                    case AOGEnumGroundStorageLayout.Stacking: return StorageProps.StackingCapacity;
                    default: return 1;
                }
            }
        }
        protected override float Inventory_OnAcquireTransitionSpeed(EnumTransitionType transType, ItemStack stack, float baseMul)
        {
            float positionAwarePerishRate = Api != null && transType == EnumTransitionType.Perish ? GetPerishRate() : 1; 
            BlockPos sealevelpos = Pos.Copy();
            sealevelpos.Y = Api.World.SeaLevel;

            float temperature = temperatureCached;
            if (temperature < -999f)
            {
                temperature = Api.World.BlockAccessor.GetClimateAt(sealevelpos, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, Api.World.Calendar.TotalDays).Temperature;
                if (Api.Side == EnumAppSide.Server) temperatureCached = temperature;   // Cache the temperature for the remainder of this tick
            }

            if (room == null)
            {
                room = roomReg.GetRoomForPosition(Pos);
            }

            float soilTempWeight = 0f;
            float skyLightProportion = (float)room.SkylightCount / Math.Max(1, room.SkylightCount + room.NonSkylightCount);   // avoid any risk of divide by zero

            if (room.IsSmallRoom)
            {
                soilTempWeight = 1f;
                // If there's too much skylight, it's less cellar-like
                soilTempWeight -= 0.4f * skyLightProportion;
                // If non-cooling blocks exceed cooling blocks, it's less cellar-like
                soilTempWeight -= 0.5f * GameMath.Clamp((float)room.NonCoolingWallCount / Math.Max(1, room.CoolingWallCount), 0f, 1f);
            }

            int lightlevel = Api.World.BlockAccessor.GetLightLevel(Pos, EnumLightLevelType.OnlySunLight);

            // light level above 12 makes it additionally warmer, especially when part of a cellar or a greenhouse
            float lightImportance = 0.1f;
            // light in small fully enclosed rooms has a big impact
            if (room.IsSmallRoom) lightImportance += 0.3f * soilTempWeight + 1.75f * skyLightProportion;
            // light in large most enclosed rooms (e.g. houses, greenhouses) has medium impact
            else if (room.ExitCount <= 0.1f * (room.CoolingWallCount + room.NonCoolingWallCount)) lightImportance += 1.25f * skyLightProportion;
            // light outside rooms (e.g. chests on world surface) has low impact but still warms them above base air temperature
            else lightImportance += 0.5f * skyLightProportion;
            lightImportance = GameMath.Clamp(lightImportance, 0f, 1.5f);    

            float airTemp = temperature + GameMath.Clamp(lightlevel - 11, 0, 10) * lightImportance;

            // Lets say deep soil temperature is a constant 5°C
            float cellarTemp = 5;

            // How good of a cellar it is depends on how much rock or soil was used on he cellars walls
            float hereTemp = GameMath.Lerp(airTemp, cellarTemp, soilTempWeight);

            // For fairness lets say if its colder outside, use that temp instead
            hereTemp = Math.Min(hereTemp, airTemp);

            bool water = false;
            bool canWater = StorageProps.CanWater;
            Api.World.BlockAccessor.SearchFluidBlocks(
                new BlockPos(Pos.X, Pos.Y, Pos.Z),
                new BlockPos(Pos.X, Pos.Y, Pos.Z),
                (block, pos) =>
                {
                    if (block.LiquidCode == "water") water = true;
                    return true;
                }
            );


            // Some neat curve to turn the temperature into a spoilage rate
            // http://fooplot.com/#W3sidHlwZSI6MCwiZXEiOiJtYXgoMC4xLG1pbigyLjUsM14oeC8xOS0xLjIpKS0wLjEpIiwiY29sb3IiOiIjMDAwMDAwIn0seyJ0eXBlIjoxMDAwLCJ3aW5kb3ciOlsiLTIwIiwiNDAiLCIwIiwiMyJdLCJncmlkIjpbIjIuNSIsIjAuMjUiXX1d
            // max(0.1, min(2.5, 3^(x/15 - 1.2))-0.1)
            if (transType == EnumTransitionType.Dry && !water) positionAwarePerishRate = Math.Max(0.1f, Math.Min(2.4f, (float)Math.Pow(3, hereTemp / 19 - 1.2) - 0.1f)) * 4;
            if (transType == EnumTransitionType.Melt && water && canWater) positionAwarePerishRate = 4;

            return baseMul * positionAwarePerishRate;
        }
        public override float GetPerishRate()
        {

            float rate = 1;
            bool water = false;
            bool canWater = StorageProps.CanWater;
            Api.World.BlockAccessor.SearchFluidBlocks(
                new BlockPos(Pos.X, Pos.Y, Pos.Z),
                new BlockPos(Pos.X, Pos.Y, Pos.Z),
                (block, pos) =>
                {
                    if (block.LiquidCode == "water") water = true;
                    return true;
                }
            );
            if (water && !canWater) rate = 4;

            return rate;
        }

        public override InventoryBase Inventory
        {
            get { return inventory; }
        }

        public override string InventoryClassName
        {
            get { return "haystorage"; }
        }

        public override string AttributeTransformCode => "groundStorageTransform";

        public float MeshAngle { get; set; }
        public BlockFacing AttachFace { get; set; }

        public override TextureAtlasPosition this[string textureCode]
        {
            get
            {
                // Prio 1: Get from list of explicility defined textures
                if (StorageProps.Layout == AOGEnumGroundStorageLayout.Stacking && StorageProps.StackingTextures != null)
                {
                    if (StorageProps.StackingTextures.TryGetValue(textureCode, out var texturePath))
                    {
                        return getOrCreateTexPos(texturePath);
                    }
                }

                // Prio 2: Try other texture sources
                return base[textureCode];
            }
        }

        public bool CanAttachBlockAt(BlockFacing blockFace, Cuboidi attachmentArea)
        {
            if (StorageProps == null) return false;
            return blockFace == BlockFacing.UP && StorageProps.Layout == AOGEnumGroundStorageLayout.Stacking && inventory[0].StackSize == Capacity && StorageProps.UpSolid;
        }

        public AOGBlockEntityGroundStorage() : base()
        {
            inventory = new InventoryGeneric(invSlotCount, null, null, (int slotId, InventoryGeneric inv) => new ItemSlot(inv));
            foreach (var slot in inventory)
            {
                slot.StorageType |= EnumItemStorageFlags.Backpack;
            }

            inventory.OnGetAutoPushIntoSlot = GetAutoPushIntoSlot;
            inventory.OnGetAutoPullFromSlot = GetAutoPullFromSlot;

            colBoxes = new Cuboidf[] { new Cuboidf(0, 0, 0, 1, 0.25f, 1) };
            selBoxes = new Cuboidf[] { new Cuboidf(0, 0, 0, 1, 0.25f, 1) };
        }

        private ItemSlot GetAutoPullFromSlot(BlockFacing atBlockFace)
        {
            return null;
        }

        private ItemSlot GetAutoPushIntoSlot(BlockFacing atBlockFace, ItemSlot fromSlot)
        {
            return null;
        }

        public void ForceStorageProps(AOGGroundStorageProperties storageProps)
        {
            StorageProps = storageProps;
            forceStorageProps = true;
        }

        public Cuboidf[] GetSelectionBoxes()
        {
            return selBoxes;
        }

        public Cuboidf[] GetCollisionBoxes()
        {
            return colBoxes;
        }

        public virtual bool OnPlayerInteractStart(IPlayer player, BlockSelection bs)
        {
            ItemSlot hotbarSlot = player.InventoryManager.ActiveHotbarSlot;

            if (!hotbarSlot.Empty && !hotbarSlot.Itemstack.Collectible.HasBehavior<AOGCollectibleBehaviorGroundStorable>()) return false;

            if (!BlockBehaviorReinforcable.AllowRightClickPickup(Api.World, Pos, player)) return false;

            DetermineStorageProperties(hotbarSlot);

            bool ok = false;

            if (StorageProps != null)
            {
                if (!hotbarSlot.Empty && StorageProps.SprintKey && !player.Entity.Controls.CtrlKey) return false;

                // fix RAD rotation being CCW - since n=0, e=-PiHalf, s=Pi, w=PiHalf so we swap east and west by inverting sign
                // changed since > 1.18.1 since east west on WE rotation was broken, to allow upgrading/downgrading without issues we invert the sign for all* usages instead of saving new value 
                var hitPos = rotatedOffset(bs.HitPosition.ToVec3f(), -MeshAngle);

                if (StorageProps.Layout == AOGEnumGroundStorageLayout.Quadrants && inventory.Empty)
                {
                    double dx = Math.Abs(hitPos.X - 0.5);
                    double dz = Math.Abs(hitPos.Z - 0.5);
                    if (dx < 2 / 16f && dz < 2 / 16f)
                    {
                        overrideLayout = AOGEnumGroundStorageLayout.SingleCenter;
                        DetermineStorageProperties(hotbarSlot);
                    }
                }

                switch (StorageProps.Layout)
                {
                    case AOGEnumGroundStorageLayout.SingleCenter:
                        ok = putOrGetItemSingle(inventory[0], player, bs);
                        break;


                    case AOGEnumGroundStorageLayout.WallHalves:
                    case AOGEnumGroundStorageLayout.Halves:
                        if (hitPos.X < 0.5)
                        {
                            ok = putOrGetItemSingle(inventory[0], player, bs);
                        }
                        else
                        {
                            ok = putOrGetItemSingle(inventory[1], player, bs);
                        }
                        break;

                    case AOGEnumGroundStorageLayout.Quadrants:
                        int pos = ((hitPos.X > 0.5) ? 2 : 0) + ((hitPos.Z > 0.5) ? 1 : 0);
                        ok = putOrGetItemSingle(inventory[pos], player, bs);
                        break;

                    case AOGEnumGroundStorageLayout.Stacking:
                        ok = putOrGetItemStacking(player, bs);
                        break;
                }
            }

            if (ok)
            {
                MarkDirty();    // Don't re-draw on client yet, that will be handled in FromTreeAttributes after we receive an updating packet from the server  (updating meshes here would have the wrong inventory contents, and also create a potential race condition)
            }

            if (inventory.Empty && !clientsideFirstPlacement)
            {
                Api.World.BlockAccessor.SetBlock(0, Pos);
                Api.World.BlockAccessor.TriggerNeighbourBlockUpdate(Pos);
            }

            return ok;
        }



        public bool OnPlayerInteractStep(float secondsUsed, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (isUsingSlot?.Itemstack?.Collectible is IContainedInteractable collIci)
            {
                return collIci.OnContainedInteractStep(secondsUsed, this, isUsingSlot, byPlayer, blockSel);
            }

            isUsingSlot = null;
            return false;
        }


        public void OnPlayerInteractStop(float secondsUsed, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (isUsingSlot?.Itemstack.Collectible is IContainedInteractable collIci)
            {
                collIci.OnContainedInteractStop(secondsUsed, this, isUsingSlot, byPlayer, blockSel);
            }

            isUsingSlot = null;
        }






        public ItemSlot GetSlotAt(BlockSelection bs)
        {
            if (StorageProps == null) return null;

            switch (StorageProps.Layout)
            {
                case AOGEnumGroundStorageLayout.SingleCenter:
                    return inventory[0];

                case AOGEnumGroundStorageLayout.Halves:
                case AOGEnumGroundStorageLayout.WallHalves:
                    if (bs.HitPosition.X < 0.5)
                    {
                        return inventory[0];
                    }
                    else
                    {
                        return inventory[1];
                    }

                case AOGEnumGroundStorageLayout.Quadrants:
                    var hitPos = rotatedOffset(bs.HitPosition.ToVec3f(),-MeshAngle);
                    int pos = ((hitPos.X > 0.5) ? 2 : 0) + ((hitPos.Z > 0.5) ? 1 : 0);
                    return inventory[pos];

                case AOGEnumGroundStorageLayout.Stacking:
                    return inventory[0];
            }

            return null;
        }



        public bool OnTryCreateKiln()
        {
            ItemStack stack = inventory.FirstNonEmptySlot.Itemstack;
            if (stack == null) return false;

            if (stack.StackSize > StorageProps.MaxFireable)
            {
                capi?.TriggerIngameError(this, "overfull", Lang.Get("Can only fire up to {0} at once.", StorageProps.MaxFireable));
                return false;
            }
            
            if (stack.Collectible.CombustibleProps == null || stack.Collectible.CombustibleProps.SmeltingType != EnumSmeltType.Fire)
            {
                capi?.TriggerIngameError(this, "notfireable", Lang.Get("This is not a fireable block or item", StorageProps.MaxFireable));
                return false;
            }


            return true;
        }

        public virtual void DetermineStorageProperties(ItemSlot sourceSlot)
        {
            ItemStack sourceStack = inventory.FirstNonEmptySlot?.Itemstack ?? sourceSlot?.Itemstack;

            if (!forceStorageProps)
            {
                if (StorageProps == null)
                {
                    if (sourceStack == null) return;

                    StorageProps = sourceStack.Collectible?.GetBehavior<AOGCollectibleBehaviorGroundStorable>()?.StorageProps;
                }
            }

            if (StorageProps == null) return;  // Seems necessary to avoid crash with certain items placed in game version 1.15-pre.1?

            if (StorageProps.CollisionBox != null)
            {
                colBoxes[0] = selBoxes[0] = StorageProps.CollisionBox.Clone();
            } else
            {
                if (sourceStack?.Block != null)
                {
                    colBoxes[0] = selBoxes[0] = sourceStack.Block.CollisionBoxes[0].Clone();
                }
            }

            if (StorageProps.SelectionBox != null)
            {
                selBoxes[0] = StorageProps.SelectionBox.Clone();
            }

            if (StorageProps.CbScaleYByLayer != 0)
            {
                colBoxes[0] = colBoxes[0].Clone();
                colBoxes[0].Y2 *= ((int)Math.Ceiling(StorageProps.CbScaleYByLayer * inventory[0].StackSize) * 8) / 8;

                selBoxes[0] = colBoxes[0];
            }

            if (overrideLayout != null)
            {
                StorageProps = StorageProps.Clone();
                StorageProps.Layout = (AOGEnumGroundStorageLayout)overrideLayout;
            }
        }



        protected bool putOrGetItemStacking(IPlayer byPlayer, BlockSelection bs)
        {
            if (Api.Side == EnumAppSide.Client)
            {
                (byPlayer as IClientPlayer)?.TriggerFpAnimation(EnumHandInteract.HeldItemInteract);
                return true;
            }

            BlockPos abovePos = Pos.UpCopy();
            BlockEntity be = Api.World.BlockAccessor.GetBlockEntity(abovePos);
            if (be is AOGBlockEntityGroundStorage beg)
            {
                return beg.OnPlayerInteractStart(byPlayer, bs);
            }

            bool sneaking = byPlayer.Entity.Controls.ShiftKey;
            bool sitting = byPlayer.Entity.Controls.FloorSitting;

            ItemSlot hotbarSlot = byPlayer.InventoryManager.ActiveHotbarSlot;


            if (sneaking && hotbarSlot.Empty) return false;
            /*if (sneaking && TotalStackSize >= Capacity)
            {
                Block pileblock = Api.World.BlockAccessor.GetBlock(Pos);
                Block aboveblock = Api.World.BlockAccessor.GetBlock(abovePos);

                if (aboveblock.IsReplacableBy(pileblock))
                {
                    AOGBlockGroundStorage bgs = pileblock as AOGBlockGroundStorage;
                    var bsc = bs.Clone();
                    bsc.Position.Up();
                    bsc.Face = null;
                    return bgs.CreateStorage(Api.World, bsc, byPlayer);
                }

                return false;
            }*/


            bool equalStack = inventory[0].Empty || hotbarSlot.Itemstack != null && hotbarSlot.Itemstack.Equals(Api.World, inventory[0].Itemstack, GlobalConstants.IgnoredStackAttributes);

            if (sneaking && !equalStack)
            {
                return false;
            }

            lock (inventoryLock)
            {
                if (sneaking)
                {
                    return TryPutItem(byPlayer);
                }
                if (sitting)
                {
                    return TryInteract(byPlayer);
                }
                else
                {
                    return TryTakeItem(byPlayer);
                }
            }
        }


        public virtual bool TryPutItem(IPlayer player)
        {
            if (TotalStackSize >= Capacity) return false;

            ItemSlot hotbarSlot = player.InventoryManager.ActiveHotbarSlot;

            if (hotbarSlot.Itemstack == null) return false;

            ItemSlot invSlot = inventory[0];

            if (invSlot.Empty)
            {
                if (hotbarSlot.TryPutInto(Api.World, invSlot, TransferQuantity) > 0)
                {
                    Api.World.PlaySoundAt(StorageProps.PlaceRemoveSound.WithPathPrefixOnce("sounds/"), Pos.X, Pos.Y, Pos.Z, null, 0.88f + (float)Api.World.Rand.NextDouble() * 0.24f, 16);
                }
                return true;
            }

            if (invSlot.Itemstack.Equals(Api.World, hotbarSlot.Itemstack, GlobalConstants.IgnoredStackAttributes))
            {
                bool putBulk = player.Entity.Controls.CtrlKey;

                int q = GameMath.Min(hotbarSlot.StackSize, putBulk ? BulkTransferQuantity : TransferQuantity, Capacity - TotalStackSize);

                // add to the pile and average item temperatures
                int oldSize = invSlot.Itemstack.StackSize;
                invSlot.Itemstack.StackSize += q;
                if (oldSize + q > 0)
                {
                    float tempPile = invSlot.Itemstack.Collectible.GetTemperature(Api.World, invSlot.Itemstack);
                    float tempAdded = hotbarSlot.Itemstack.Collectible.GetTemperature(Api.World, hotbarSlot.Itemstack);
                    invSlot.Itemstack.Collectible.SetTemperature(Api.World, invSlot.Itemstack, (tempPile * oldSize + tempAdded * q) / (oldSize + q), false);
                }

                if (player.WorldData.CurrentGameMode != EnumGameMode.Creative)
                {
                    hotbarSlot.TakeOut(q);
                    hotbarSlot.OnItemSlotModified(null);
                }

                Api.World.PlaySoundAt(StorageProps.PlaceRemoveSound.WithPathPrefixOnce("sounds/"), Pos.X, Pos.Y, Pos.Z, null, 0.88f + (float)Api.World.Rand.NextDouble() * 0.24f, 16);

                MarkDirty();

                Cuboidf[] collBoxes = Api.World.BlockAccessor.GetBlock(Pos).GetCollisionBoxes(Api.World.BlockAccessor, Pos);
                if (collBoxes != null && collBoxes.Length > 0 && CollisionTester.AabbIntersect(collBoxes[0], Pos.X, Pos.Y, Pos.Z, player.Entity.SelectionBox, player.Entity.SidedPos.XYZ))
                {
                    player.Entity.SidedPos.Y += collBoxes[0].Y2 - (player.Entity.SidedPos.Y - (int)player.Entity.SidedPos.Y);
                }          

                return true;
            }

            return false;
        }
        public bool TryInteract(IPlayer player)
        {
            bool takeBulk = player.Entity.Controls.CtrlKey;
            int dropUse = StorageProps.DropUse;
            int dropCount = StorageProps.DropCount;
            int dropCount2 = StorageProps.DropCount2;
            if (takeBulk)
            {
                dropUse = StorageProps.DropUse * StorageProps.DropBulk;
                dropCount = StorageProps.DropCount * StorageProps.DropBulk;
                dropCount2 = StorageProps.DropCount2 * StorageProps.DropBulk;
            }
            ItemSlot hotbarSlot = player.InventoryManager.ActiveHotbarSlot;

            if (hotbarSlot.Itemstack != null) return false;

            switch (StorageProps.CanDrop)
            {
                case AOGEnumDropType.Items:
                    if (inventory[0].Itemstack.StackSize >= dropUse)
                    {
                        var dropItem = StorageProps.DropItem.Clone();
                        var dropItem2 = StorageProps.DropItem2.Clone();
                        ItemStack dropI = new(Api.World.GetItem(dropItem))
                        {
                            StackSize = dropCount
                        };
                        if (player.InventoryManager.TryGiveItemstack(dropI))
                        {
                            inventory[0].Itemstack.StackSize = inventory[0].Itemstack.StackSize - dropUse;
                            player.Entity.World.SpawnItemEntity(dropI, player.Entity.Pos.XYZ.AddCopy(0, 0.5, 0));
                        }
                        ItemStack dropI2 = new(Api.World.GetItem(dropItem2))
                        {
                            StackSize = dropCount2
                        };
                        if (player.InventoryManager.TryGiveItemstack(dropI2))
                        {
                            player.Entity.World.SpawnItemEntity(dropI2, player.Entity.Pos.XYZ.AddCopy(0, 0.5, 0));
                        }
                    }
                    break;
                case AOGEnumDropType.Block:
                    if (inventory[0].Itemstack.StackSize >= dropUse)
                    {
                        var dropBlock = StorageProps.DropBlock.Clone();
                        ItemStack dropB = new(Api.World.GetBlock(dropBlock))
                        {
                            StackSize = dropCount
                        };
                        if (player.InventoryManager.TryGiveItemstack(dropB))
                        {
                            inventory[0].Itemstack.StackSize = inventory[0].Itemstack.StackSize - dropUse;
                            player.Entity.World.SpawnItemEntity(dropB, player.Entity.Pos.XYZ.AddCopy(0, 0.5, 0));
                        }
                    }
                    break;
            }     
            

            if (TotalStackSize == 0)
            {
                Api.World.BlockAccessor.SetBlock(0, Pos);
            }

            Api.World.PlaySoundAt(StorageProps.PlaceRemoveSound, Pos.X, Pos.Y, Pos.Z, null, 0.88f + (float)Api.World.Rand.NextDouble() * 0.24f, 16);

            MarkDirty();

            (player as IClientPlayer)?.TriggerFpAnimation(EnumHandInteract.HeldItemInteract);

            return true;
        }

        public bool TryTakeItem(IPlayer player)
        {
            bool takeBulk = player.Entity.Controls.CtrlKey;
            int q = GameMath.Min(takeBulk ? BulkTransferQuantity : TransferQuantity, TotalStackSize);

            if (inventory[0]?.Itemstack != null)
            {
                ItemStack stack = inventory[0].TakeOut(q);
                player.InventoryManager.TryGiveItemstack(stack);

                if (stack.StackSize > 0)
                {
                    Api.World.SpawnItemEntity(stack, Pos.ToVec3d().Add(0.5, 0.5, 0.5));
                }
            }

            if (TotalStackSize == 0)
            {
                Api.World.BlockAccessor.SetBlock(0, Pos);
            }

            Api.World.PlaySoundAt(StorageProps.PlaceRemoveSound, Pos.X, Pos.Y, Pos.Z, null, 0.88f + (float)Api.World.Rand.NextDouble() * 0.24f, 16);

            MarkDirty();

            (player as IClientPlayer)?.TriggerFpAnimation(EnumHandInteract.HeldItemInteract);

            return true;
        }



        public bool putOrGetItemSingle(ItemSlot ourSlot, IPlayer player, BlockSelection bs)
        {
            isUsingSlot = null;
            if (!ourSlot.Empty && ourSlot.Itemstack.Collectible is IContainedInteractable collIci)
            {
                if (collIci.OnContainedInteractStart(this, ourSlot, player, bs))
                {
                    AOGBlockGroundStorage.IsUsingContainedBlock = true;
                    isUsingSlot = ourSlot;
                    return true;
                }
            }

            ItemSlot hotbarSlot = player.InventoryManager.ActiveHotbarSlot;
            if (!hotbarSlot.Empty && !inventory.Empty)
            {
                bool layoutEqual = StorageProps.Layout == hotbarSlot.Itemstack.Collectible.GetBehavior<AOGCollectibleBehaviorGroundStorable>()?.StorageProps.Layout;
                if (!layoutEqual) return false;
            }


            lock (inventoryLock)
            {
                if (ourSlot.Empty)
                {
                    if (hotbarSlot.Empty) return false;

                    if (player.WorldData.CurrentGameMode == EnumGameMode.Creative)
                    {
                        ItemStack stack = hotbarSlot.Itemstack.Clone();
                        stack.StackSize = 1;
                        if (new DummySlot(stack).TryPutInto(Api.World, ourSlot, TransferQuantity) > 0) {
                            Api.World.PlaySoundAt(StorageProps.PlaceRemoveSound, Pos.X, Pos.Y, Pos.Z, player, 0.88f + (float)Api.World.Rand.NextDouble() * 0.24f, 16);
                        }
                    } else {
                        if (hotbarSlot.TryPutInto(Api.World, ourSlot, TransferQuantity) > 0)
                        {
                            Api.World.PlaySoundAt(StorageProps.PlaceRemoveSound, Pos.X, Pos.Y, Pos.Z, player, 0.88f + (float)Api.World.Rand.NextDouble() * 0.24f, 16);
                        }
                    }
                }
                else
                {
                    if (!player.InventoryManager.TryGiveItemstack(ourSlot.Itemstack, true))
                    {
                        Api.World.SpawnItemEntity(ourSlot.Itemstack, new Vec3d(Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5));
                    }

                    Api.World.PlaySoundAt(StorageProps.PlaceRemoveSound, Pos.X, Pos.Y, Pos.Z, player, 0.88f + (float)Api.World.Rand.NextDouble() * 0.24f, 16);

                    ourSlot.Itemstack = null;
                    ourSlot.MarkDirty();
                }
            }

            return true;
        }
        public virtual string GetBlockName()
        {
            var props = StorageProps;
            if (props == null || inventory.Empty) return Lang.Get("artofgrowing:Empty Hay");
            return Lang.Get("artofgrowing:Hay Storage");
        }
        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            float ripenRate = GameMath.Clamp(((1 - GetPerishRate()) - 0.5f) * 3, 0, 1);
            if (inventory.Empty) return;

            string[] contentSummary = getContentSummary();
            bool water = false;
            Api.World.BlockAccessor.SearchFluidBlocks(
                new BlockPos(Pos.X, Pos.Y, Pos.Z),
                new BlockPos(Pos.X, Pos.Y, Pos.Z),
                (block, pos) =>
                {
                    if (block.LiquidCode == "water") water = true;
                    return true;
                }
            );

            ItemStack stack = inventory.FirstNonEmptySlot.Itemstack;
            // Only add supplemental info for non-BlockEntities (otherwise it will be wrong or will get into a recursive loop, because right now this BEGroundStorage is the BlockEntity)
            if (contentSummary.Length == 1 && !(stack.Collectible is IContainedCustomName) && stack.Class == EnumItemClass.Block && ((Block)stack.Collectible).EntityClass == null)  
            {
                string detailedInfo = stack.Block.GetPlacedBlockInfo(Api.World, Pos, forPlayer);
                if (detailedInfo != null && detailedInfo.Length > 0) dsc.Append(detailedInfo);
            } else
            {
                foreach (var line in contentSummary) dsc.AppendLine(line);
            }
            dsc.Append(PerishableInfoCompact(Api, inventory.FirstNonEmptySlot, ripenRate));
            if (!water) dsc.Append(DryInfoCompact(Api, inventory.FirstNonEmptySlot, ripenRate));
            if (water) dsc.Append(MeltInfoCompact(Api, inventory.FirstNonEmptySlot, ripenRate));
        }
        public static string PerishableInfoCompact(ICoreAPI Api, ItemSlot contentSlot, float ripenRate, bool withStackName = true)
        {
            if (contentSlot.Empty) return "";

            StringBuilder dsc = new StringBuilder();

            TransitionState[] transitionStates = contentSlot.Itemstack?.Collectible.UpdateAndGetTransitionStates(Api.World, contentSlot);

            if (transitionStates != null)
            {
                bool appendLine = false;
                for (int i = 0; i < transitionStates.Length; i++)
                {
                    TransitionState state = transitionStates[i];

                    TransitionableProperties prop = state.Props;
                    float perishRate = contentSlot.Itemstack.Collectible.GetTransitionRateMul(Api.World, contentSlot, prop.Type);
                    if (perishRate <= 0) continue;

                    float transitionLevel = state.TransitionLevel;
                    float freshHoursLeft = state.FreshHoursLeft / perishRate;

                    switch (prop.Type)
                    {
                        case EnumTransitionType.Perish:

                            appendLine = true;

                            if (transitionLevel > 0)
                            {
                                dsc.Append(Lang.Get("{0}% spoiled", (int)Math.Round(transitionLevel * 100)));
                            }
                            else
                            {
                                double hoursPerday = Api.World.Calendar.HoursPerDay;

                                if (freshHoursLeft / hoursPerday >= Api.World.Calendar.DaysPerYear)
                                {
                                    dsc.Append(Lang.Get("Fresh for {0} years", Math.Round(freshHoursLeft / hoursPerday / Api.World.Calendar.DaysPerYear, 1)));
                                }
                                else if (freshHoursLeft > hoursPerday)
                                {
                                    dsc.Append(Lang.Get("Fresh for {0} days", Math.Round(freshHoursLeft / hoursPerday, 1)));
                                }
                                else
                                {
                                    dsc.Append(Lang.Get("Fresh for {0} hours", Math.Round(freshHoursLeft, 1)));
                                }
                            }
                            break;                        
                    }
                }


                if (appendLine) dsc.AppendLine();
            }

            return dsc.ToString();
        }
        public static string DryInfoCompact(ICoreAPI Api, ItemSlot contentSlot, float ripenRate, bool withStackName = true)
        {
            if (contentSlot.Empty) return "";

            StringBuilder dsc = new StringBuilder();

            TransitionState[] transitionStates = contentSlot.Itemstack?.Collectible.UpdateAndGetTransitionStates(Api.World, contentSlot);

            bool nowSpoiling = false;

            if (transitionStates != null)
            {
                bool appendLine = false;
                for (int i = 0; i < transitionStates.Length; i++)
                {
                    TransitionState state = transitionStates[i];

                    TransitionableProperties prop = state.Props;
                    float perishRate = contentSlot.Itemstack.Collectible.GetTransitionRateMul(Api.World, contentSlot, prop.Type);
                    if (perishRate <= 0) continue;

                    float transitionLevel = state.TransitionLevel;
                    float freshHoursLeft = state.FreshHoursLeft / perishRate;

                    switch (prop.Type)
                    {                        
                        case EnumTransitionType.Dry:
                            if (nowSpoiling) break;

                            appendLine = true;

                            if (transitionLevel > 0)
                            {
                                dsc.Append(Lang.Get("Dries in {1:0.#} days", (int)Math.Round(transitionLevel * 100), (state.TransitionHours - state.TransitionedHours) / Api.World.Calendar.HoursPerDay / perishRate));
                            }
                            else
                            {
                                double hoursPerday = Api.World.Calendar.HoursPerDay;

                                if (freshHoursLeft / hoursPerday >= Api.World.Calendar.DaysPerYear)
                                {
                                    dsc.Append(Lang.Get("Will dry in {0} years", Math.Round(freshHoursLeft / hoursPerday / Api.World.Calendar.DaysPerYear, 1)));
                                }
                                else if (freshHoursLeft > hoursPerday)
                                {
                                    dsc.Append(Lang.Get("Will dry in {0} days", Math.Round(freshHoursLeft / hoursPerday, 1)));
                                }
                                else
                                {
                                    dsc.Append(Lang.Get("Will dry in {0} hours", Math.Round(freshHoursLeft, 1)));
                                }
                            }
                            break;
                    }
                }


                if (appendLine) dsc.AppendLine();
            }

            return dsc.ToString();
        }
        public static string MeltInfoCompact(ICoreAPI Api, ItemSlot contentSlot, float ripenRate, bool withStackName = true)
        {
            if (contentSlot.Empty) return "";

            StringBuilder dsc = new StringBuilder();

            TransitionState[] transitionStates = contentSlot.Itemstack?.Collectible.UpdateAndGetTransitionStates(Api.World, contentSlot);

            bool nowSpoiling = false;

            if (transitionStates != null)
            {
                bool appendLine = false;
                for (int i = 0; i < transitionStates.Length; i++)
                {
                    TransitionState state = transitionStates[i];

                    TransitionableProperties prop = state.Props;
                    float perishRate = contentSlot.Itemstack.Collectible.GetTransitionRateMul(Api.World, contentSlot, prop.Type);
                    if (perishRate <= 0) continue;

                    float transitionLevel = state.TransitionLevel;
                    float freshHoursLeft = state.FreshHoursLeft / perishRate;

                    switch (prop.Type)
                    {
                        case EnumTransitionType.Melt:
                            if (nowSpoiling) break;

                            appendLine = true;

                            if (transitionLevel > 0)
                            {
                                dsc.Append(Lang.Get("Soak in {1:0.#} days", (int)Math.Round(transitionLevel * 100), (state.TransitionHours - state.TransitionedHours) / Api.World.Calendar.HoursPerDay / perishRate));
                            }
                            else
                            {
                                double hoursPerday = Api.World.Calendar.HoursPerDay;

                                if (freshHoursLeft / hoursPerday >= Api.World.Calendar.DaysPerYear)
                                {
                                    dsc.Append(Lang.Get("Will soak in {0} years", Math.Round(freshHoursLeft / hoursPerday / Api.World.Calendar.DaysPerYear, 1)));
                                }
                                else if (freshHoursLeft > hoursPerday)
                                {
                                    dsc.Append(Lang.Get("Will soak in {0} days", Math.Round(freshHoursLeft / hoursPerday, 1)));
                                }
                                else
                                {
                                    dsc.Append(Lang.Get("Will soak in {0} hours", Math.Round(freshHoursLeft, 1)));
                                }
                            }
                            break;
                    }
                }


                if (appendLine) dsc.AppendLine();
            }

            return dsc.ToString();
        }

        public virtual string[] getContentSummary()
        {
            OrderedDictionary<string, int> dict = new OrderedDictionary<string, int>();

            foreach (var slot in inventory)
            {
                if (slot.Empty) continue;
                int cnt;

                string stackName = slot.Itemstack.GetName();

                if (slot.Itemstack.Collectible is IContainedCustomName ccn)
                {
                    stackName = ccn.GetContainedInfo(slot);
                }

                if (!dict.TryGetValue(stackName, out cnt)) cnt = 0;

                dict[stackName] = cnt + slot.StackSize;
            }

            return dict.Select(elem => Lang.Get("{0}x {1}", elem.Value, elem.Key)).ToArray();
        }



        public override bool OnTesselation(ITerrainMeshPool meshdata, ITesselatorAPI tesselator)
        {
            lock (inventoryLock)
            {
                return base.OnTesselation(meshdata, tesselator);
            }
        }


        
        Vec3f rotatedOffset(Vec3f offset, float radY)
        {
            Matrixf mat = new Matrixf();
            mat.Translate(0.5f, 0.5f, 0.5f).RotateY(radY).Translate(-0.5f, -0.5f, -0.5f);
            return mat.TransformVector(new Vec4f(offset.X, offset.Y, offset.Z, 1)).XYZ;
        }


        protected override float[][] genTransformationMatrices()
        {
            float[][] tfMatrices = new float[DisplayedItems][];

            Vec3f[] offs = new Vec3f[DisplayedItems];

            lock (inventoryLock)
            {
                switch (StorageProps.Layout)
                {
                    case AOGEnumGroundStorageLayout.SingleCenter:
                        offs[0] = new Vec3f();
                        break;

                    case AOGEnumGroundStorageLayout.Halves:
                    case AOGEnumGroundStorageLayout.WallHalves:
                        // Left
                        offs[0] = new Vec3f(-0.25f, 0, 0);
                        // Right
                        offs[1] = new Vec3f(0.25f, 0, 0);
                        break;

                    case AOGEnumGroundStorageLayout.Quadrants:
                        // Top left
                        offs[0] = new Vec3f(-0.25f, 0, -0.25f);
                        // Top right
                        offs[1] = new Vec3f(-0.25f, 0, 0.25f);
                        // Bot left
                        offs[2] = new Vec3f(0.25f, 0, -0.25f);
                        // Bot right
                        offs[3] = new Vec3f(0.25f, 0, 0.25f);
                        break;

                    case AOGEnumGroundStorageLayout.Stacking:
                        offs[0] = new Vec3f();
                        break;
                }
            }

            for (int i = 0; i < tfMatrices.Length; i++)
            {
                Vec3f off = offs[i];
                off = new Matrixf().RotateY(MeshAngle).TransformVector(off.ToVec4f(0)).XYZ;

                tfMatrices[i] =
                    new Matrixf()
                    .Translate(off.X, off.Y, off.Z)
                    .Translate(0.5f, 0, 0.5f)
                    .RotateY(MeshAngle)
                    .Translate(-0.5f, 0, -0.5f)
                    .Values
                ;
            }

            return tfMatrices;
        }

        protected override string getMeshCacheKey(ItemStack stack)
        {
            return (StorageProps.ModelItemsToStackSizeRatio > 0 ? stack.StackSize : 1) + "x" + base.getMeshCacheKey(stack);
        }

        protected override MeshData getOrCreateMesh(ItemStack stack, int index)
        {
            if (StorageProps.Layout == AOGEnumGroundStorageLayout.Stacking)
            {
                MeshData mesh = getMesh(stack);
                if (mesh != null) return mesh;

                var loc = StorageProps.StackingModel.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
                nowTesselatingShape = Shape.TryGet(capi, loc);
                nowTesselatingObj = stack.Collectible;

                if (nowTesselatingShape == null)
                {
                    capi.Logger.Error("Stacking model shape for collectible " + stack.Collectible.Code + " not found. Block will be invisible!");
                    return null;
                }

                capi.Tesselator.TesselateShape("storagePile", nowTesselatingShape, out mesh, this, null, 0, 0, 0, (int)Math.Ceiling(StorageProps.ModelItemsToStackSizeRatio * stack.StackSize));

                string key = getMeshCacheKey(stack);
                MeshCache[key] = mesh;

                return mesh;
            }

            return base.getOrCreateMesh(stack, index);
        }


        public bool TryFire()
        {
            foreach (var slot in inventory)
            {
                if (slot.Empty) continue;

            }

            return true;
        }

        public void OnTransformed(IWorldAccessor worldAccessor, ITreeAttribute tree, int degreeRotation, Dictionary<int, AssetLocation> oldBlockIdMapping, Dictionary<int, AssetLocation> oldItemIdMapping, EnumAxis? flipAxis)
        {
            MeshAngle = tree.GetFloat("meshAngle");
            MeshAngle -= degreeRotation * GameMath.DEG2RAD;
            tree.SetFloat("meshAngle", MeshAngle);

            AttachFace = BlockFacing.ALLFACES[tree.GetInt("attachFace")];
            AttachFace = AttachFace.FaceWhenRotatedBy(0, -degreeRotation * GameMath.DEG2RAD, 0);
            tree.SetInt("attachFace", AttachFace?.Index ?? 0);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);
            clientsideFirstPlacement = false;

            forceStorageProps = tree.GetBool("forceStorageProps");
            if (forceStorageProps)
            {
                StorageProps = JsonUtil.FromString<AOGGroundStorageProperties>(tree.GetString("storageProps"));
            }

            overrideLayout = null;
            if (tree.HasAttribute("overrideLayout"))
            {
                overrideLayout = (AOGEnumGroundStorageLayout)tree.GetInt("overrideLayout");
            }

            if (this.Api != null)
            {
                DetermineStorageProperties(null);
            }

            MeshAngle = tree.GetFloat("meshAngle");
            AttachFace = BlockFacing.ALLFACES[tree.GetInt("attachFace")];


            // Do this last!!!  Prevents bug where initially drawn with wrong rotation
            RedrawAfterReceivingTreeAttributes(worldForResolving);     // Redraw on client after we have completed receiving the update from server
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);

            tree.SetBool("forceStorageProps", forceStorageProps);
            if (forceStorageProps)
            {
                tree.SetString("storageProps", JsonUtil.ToString(StorageProps));
            }
            if (overrideLayout != null)
            {
                tree.SetInt("overrideLayout", (int)overrideLayout);
            }

            tree.SetFloat("meshAngle", MeshAngle);
            tree.SetInt("attachFace", AttachFace?.Index ?? 0);
        }

    }
}
