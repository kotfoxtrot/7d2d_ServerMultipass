using System;
using System.Collections.Generic;
using HarmonyLib;
using Platform;

namespace ServerMultipass.Modules
{
    [HarmonyPatch(typeof(TileEntityVendingMachine), nameof(TileEntityVendingMachine.OnLockResponseServer))]
    [HarmonyPatchCategory(nameof(VendingRental))]
    internal static class VendingRentalLockPatch
    {
        private static void Prefix(TileEntityVendingMachine __instance, int _lockingPlayerID, out List<TraderData.Entry> __state)
        {
            __state = null;
            var module = VendingRental.Active;
            if (module == null || !module.IsRental(__instance)) return;
            try
            {
                if (module.EnsureState(__instance, true)) __instance.SetModified();
            }
            catch (Exception e)
            {
                module.Fault(e, "lock prepare");
            }
            try
            {
                __state = module.OnLocked(__instance, _lockingPlayerID);
            }
            catch (Exception e)
            {
                module.Fault(e, "lock check");
            }
        }

        private static void Finalizer(TileEntityVendingMachine __instance, List<TraderData.Entry> __state)
        {
            if (__state != null && __state.Count > 0) __instance.TraderData.PrimaryInventory.AddRange(__state);
        }
    }

    [HarmonyPatch(typeof(TileEntityVendingMachine), nameof(TileEntityVendingMachine.OnUnlockedServer))]
    [HarmonyPatchCategory(nameof(VendingRental))]
    internal static class VendingRentalUnlockPatch
    {
        private static void Postfix(TileEntityVendingMachine __instance, int _unlockingPlayerID)
        {
            var module = VendingRental.Active;
            if (module == null || !module.IsRental(__instance)) return;
            try
            {
                module.OnUnlocked(__instance, _unlockingPlayerID);
            }
            catch (Exception e)
            {
                module.Fault(e, "rent registration");
            }
        }
    }

    [HarmonyPatch(typeof(TileEntityVendingMachine), nameof(TileEntityVendingMachine.UpdateTick))]
    [HarmonyPatchCategory(nameof(VendingRental))]
    internal static class VendingRentalTickPatch
    {
        private static void Prefix(TileEntityVendingMachine __instance)
        {
            var module = VendingRental.Active;
            if (module == null || !module.IsRental(__instance)) return;
            try
            {
                module.BeforeTileTick(__instance);
            }
            catch (Exception e)
            {
                module.Fault(e, "renewal check");
            }
        }
    }

    [HarmonyPatch(typeof(TileEntityVendingMachine), nameof(TileEntityVendingMachine.ClearVendingMachine))]
    [HarmonyPatchCategory(nameof(VendingRental))]
    internal static class VendingRentalClearPatch
    {
        private static void Prefix(TileEntityVendingMachine __instance, out PlatformUserIdentifierAbs __state)
        {
            __state = __instance.GetOwner();
        }

        private static void Postfix(TileEntityVendingMachine __instance, PlatformUserIdentifierAbs __state)
        {
            var module = VendingRental.Active;
            if (module == null || !module.IsRental(__instance)) return;
            try
            {
                module.OnCleared(__instance, __state);
            }
            catch (Exception e)
            {
                module.Fault(e, "clear tracking");
            }
        }
    }

    [HarmonyPatch(typeof(BlockVendingMachine), nameof(BlockVendingMachine.OnBlockAdded))]
    [HarmonyPatchCategory(nameof(VendingRental))]
    internal static class VendingRentalBlockAddedPatch
    {
        private static void Postfix(WorldBase world, Vector3i _blockPos, BlockValue _blockValue, PlatformUserIdentifierAbs _addedByPlayer)
        {
            var module = VendingRental.Active;
            if (module == null || _addedByPlayer == null || _blockValue.ischild || !ThreadManager.IsMainThread()) return;
            try
            {
                if (world.GetTileEntity(_blockPos) is TileEntityVendingMachine vm && module.IsPlaced(vm)) module.OnPlaced(vm, _addedByPlayer);
            }
            catch (Exception e)
            {
                module.Fault(e, "placement tracking");
            }
        }
    }

    [HarmonyPatch(typeof(BlockVendingMachine), nameof(BlockVendingMachine.OnBlockRemoved))]
    [HarmonyPatchCategory(nameof(VendingRental))]
    internal static class VendingRentalBlockRemovedPatch
    {
        private static void Prefix(WorldBase world, Vector3i _blockPos, BlockValue _blockValue)
        {
            var module = VendingRental.Active;
            if (module == null || _blockValue.ischild || !ThreadManager.IsMainThread()) return;
            try
            {
                if (world.GetTileEntity(_blockPos) is TileEntityVendingMachine vm && module.IsRental(vm)) module.OnRemoved(vm);
            }
            catch (Exception e)
            {
                module.Fault(e, "removal tracking");
            }
        }
    }

    [HarmonyPatch(typeof(NetPackageTraderData), nameof(NetPackageTraderData.ProcessPackage))]
    [HarmonyPatchCategory(nameof(VendingRental))]
    internal static class VendingRentalTraderDataPatch
    {
        private static bool Prefix(NetPackageTraderData __instance, World _world, int ___entityId, Vector3i ___tePosition, TraderData ___traderData)
        {
            var module = VendingRental.Active;
            if (module == null || _world == null || ___traderData == null || ___entityId != -1) return true;
            if (!(_world.GetTileEntity(___tePosition) is TileEntityVendingMachine vm) || !module.IsRental(vm)) return true;
            module.OnTraderData(vm, ___traderData, __instance.Sender);
            return false;
        }
    }

    [HarmonyPatch(typeof(NetPackageTileEntity), nameof(NetPackageTileEntity.ProcessPackage))]
    [HarmonyPatchCategory(nameof(VendingRental))]
    internal static class VendingRentalTileEntityPatch
    {
        private static bool Prefix(NetPackageTileEntity __instance, World _world, byte ___handle, Vector3i ___teWorldPos, int ___teBlockId)
        {
            var module = VendingRental.Active;
            if (module == null || _world == null || !module.MachineBlockTypes.Contains(___teBlockId)) return true;
            if (!(_world.GetTileEntity(___teWorldPos) is TileEntityVendingMachine vm) || !module.IsRental(vm)) return true;
            module.Resync(vm, __instance.Sender, ___handle);
            return false;
        }
    }

    [HarmonyPatch(typeof(NetPackageGameEventRequest), nameof(NetPackageGameEventRequest.ProcessPackage))]
    [HarmonyPatchCategory(nameof(VendingRental))]
    internal static class VendingRentalGameEventPatch
    {
        private static bool Prefix(string ___eventName)
        {
            return VendingRental.Active == null || ___eventName != VendingRental.RemoveContractEvent;
        }
    }
}
