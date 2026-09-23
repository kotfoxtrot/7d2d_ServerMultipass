using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ServerMultipass.Modules
{
    public sealed class ClaimGuard : Module
    {
        private const double MessageCooldownSeconds = 3;

        private static ClaimGuard instance;
        private readonly Dictionary<int, DateTime> nextMessage = new Dictionary<int, DateTime>();

        public override string Name => "ClaimGuard";
        protected override bool HasPatches => true;

        internal static ClaimGuard Active => instance != null && instance.Ready ? instance : null;

        protected override void OnEnable()
        {
            instance = this;
        }

        protected override void OnDisable()
        {
            instance = null;
            nextMessage.Clear();
        }

        protected internal override void OnPlayerDisconnected(ClientInfo client)
        {
            nextMessage.Remove(client.entityId);
        }

        internal bool Allows(int entityId, Vector3i position)
        {
            var client = Players.Client(entityId);
            var data = Players.Data(entityId);
            var world = GameManager.Instance.World;
            if (client == null || data == null || world == null || Players.IsAdmin(client)) return true;
            if (world.GetLandClaimOwner(position, data) != EnumLandClaimOwner.Other) return true;
            Players.PlaySound(client, Players.DeniedSound);
            var now = DateTime.UtcNow;
            if (nextMessage.TryGetValue(entityId, out var next) && next > now) return false;
            nextMessage[entityId] = now.AddSeconds(MessageCooldownSeconds);
            Reply(client, "Denied");
            return false;
        }

        internal static void Check(int entityId, Vector3i position, ref bool result)
        {
            var module = Active;
            if (module == null || !result) return;
            try
            {
                if (!module.Allows(entityId, position)) result = false;
            }
            catch (Exception e)
            {
                module.Fault(e, "access check");
            }
        }
    }

    [HarmonyPatch(typeof(TileEntity), nameof(TileEntity.CanLockOnServer))]
    [HarmonyPatchCategory(nameof(ClaimGuard))]
    internal static class ClaimGuardTileEntityPatch
    {
        private static void Postfix(TileEntity __instance, int _lockingPlayerId, ref bool __result)
        {
            if (!__result || ClaimGuard.Active == null || __instance.GetTileEntityType() == TileEntityType.VendingMachine) return;
            ClaimGuard.Check(_lockingPlayerId, __instance.ToWorldPos(), ref __result);
        }
    }

    [HarmonyPatch(typeof(TEFeatureAbs), nameof(TEFeatureAbs.CanLockOnServer))]
    [HarmonyPatchCategory(nameof(ClaimGuard))]
    internal static class ClaimGuardStoragePatch
    {
        private static void Postfix(TEFeatureAbs __instance, int _lockingPlayerID, ref bool __result)
        {
            if (!__result || ClaimGuard.Active == null || !(__instance is TEFeatureStorage)) return;
            ClaimGuard.Check(_lockingPlayerID, __instance.ToWorldPos(), ref __result);
        }
    }

    [HarmonyPatch(typeof(Entity), nameof(Entity.CanLockOnServer))]
    [HarmonyPatchCategory(nameof(ClaimGuard))]
    internal static class ClaimGuardLootBagPatch
    {
        private static void Postfix(Entity __instance, int _lockingPlayerID, ref bool __result)
        {
            if (!__result || ClaimGuard.Active == null || !(__instance is EntityItem) || __instance is EntityBackpack) return;
            ClaimGuard.Check(_lockingPlayerID, World.worldToBlockPos(__instance.position), ref __result);
        }
    }
}
