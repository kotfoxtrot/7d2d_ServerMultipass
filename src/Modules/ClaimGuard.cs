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

        internal bool Allows(int entityId, Vector3i position, bool openToParty)
        {
            var client = Players.Client(entityId);
            var data = Players.Data(entityId);
            var world = GameManager.Instance.World;
            if (client == null || data == null || world == null || Players.IsAdmin(client)) return true;
            if (world.GetLandClaimOwner(position, data) != EnumLandClaimOwner.Other) return true;
            if (openToParty && OwnerInParty(entityId, position)) return true;
            Players.PlaySound(client, Players.DeniedSound);
            var now = DateTime.UtcNow;
            if (nextMessage.TryGetValue(entityId, out var next) && next > now) return false;
            nextMessage[entityId] = now.AddSeconds(MessageCooldownSeconds);
            Reply(client, "Denied");
            return false;
        }

        private static bool OwnerInParty(int entityId, Vector3i position)
        {
            if (Players.Entity(entityId)?.Party == null) return false;
            var half = (GameStats.GetInt(EnumGameStats.LandClaimSize) - 1) / 2;
            foreach (var claim in GameManager.Instance.persistentPlayers.m_lpBlockMap)
            {
                var owner = claim.Value;
                if (owner == null || owner.EntityId == -1) continue;
                if (Math.Abs(claim.Key.x - position.x) > half || Math.Abs(claim.Key.z - position.z) > half) continue;
                if (Players.SameParty(entityId, owner.EntityId)) return true;
            }
            return false;
        }

        internal static bool Allow(int entityId, Vector3i position, ref bool result, bool openToParty = false)
        {
            var module = Active;
            if (module == null) return true;
            try
            {
                if (module.Allows(entityId, position, openToParty)) return true;
            }
            catch (Exception e)
            {
                module.Fault(e, "access check");
                return true;
            }
            result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(TileEntity), nameof(TileEntity.OnLockRequestServer))]
    [HarmonyPatchCategory(nameof(ClaimGuard))]
    internal static class ClaimGuardTileEntityPatch
    {
        private static bool Prefix(TileEntity __instance, int _lockingPlayerID, ref bool __result)
        {
            if (ClaimGuard.Active == null || __instance.GetTileEntityType() == TileEntityType.VendingMachine) return true;
            return ClaimGuard.Allow(_lockingPlayerID, __instance.ToWorldPos(), ref __result);
        }
    }

    [HarmonyPatch(typeof(TEFeatureStorage), nameof(TEFeatureStorage.OnLockRequestServer))]
    [HarmonyPatchCategory(nameof(ClaimGuard))]
    internal static class ClaimGuardStoragePatch
    {
        private static bool Prefix(TEFeatureStorage __instance, int _lockingPlayerID, ref bool __result)
        {
            if (ClaimGuard.Active == null) return true;
            return ClaimGuard.Allow(_lockingPlayerID, __instance.ToWorldPos(), ref __result);
        }
    }

    [HarmonyPatch(typeof(Entity), nameof(Entity.OnLockRequestServer))]
    [HarmonyPatchCategory(nameof(ClaimGuard))]
    internal static class ClaimGuardLootBagPatch
    {
        private static bool Prefix(Entity __instance, int _lockingPlayerID, ref bool __result)
        {
            if (ClaimGuard.Active == null || !(__instance is EntityItem) || __instance is EntityBackpack) return true;
            return ClaimGuard.Allow(_lockingPlayerID, World.worldToBlockPos(__instance.position), ref __result, openToParty: true);
        }
    }
}
