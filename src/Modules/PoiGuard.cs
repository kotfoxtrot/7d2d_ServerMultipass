using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ServerMultipass.Modules
{
    public sealed class PoiGuardSettings
    {
        public bool EvictStrangers;
        public bool BlockClaims;
        public bool BlockBedrolls;
    }

    public sealed class PoiGuard : Module<PoiGuardSettings>
    {
        private const int EvictAttempts = 20;
        private const double MessageCooldownSeconds = 5;
        private const string ClaimIndexName = "lpblock";

        private static PoiGuard instance;
        private readonly Dictionary<PrefabInstance, HashSet<int>> guarded = new Dictionary<PrefabInstance, HashSet<int>>();
        private readonly Dictionary<int, Vector3> lastSafe = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, int> failedEvictions = new Dictionary<int, int>();
        private readonly Dictionary<int, DateTime> nextMessage = new Dictionary<int, DateTime>();
        private readonly System.Random random = new System.Random();

        public override string Name => "PoiGuard";
        protected override bool HasPatches => true;

        internal static PoiGuard Active => instance != null && instance.Ready ? instance : null;

        protected override void OnEnable()
        {
            instance = this;
        }

        protected override void OnDisable()
        {
            instance = null;
            guarded.Clear();
            lastSafe.Clear();
            failedEvictions.Clear();
            nextMessage.Clear();
        }

        protected internal override void OnPlayerDisconnected(ClientInfo client)
        {
            lastSafe.Remove(client.entityId);
            failedEvictions.Remove(client.entityId);
            nextMessage.Remove(client.entityId);
        }

        protected internal override void OnSecond(Tick tick)
        {
            if (!Settings.EvictStrangers) return;
            var world = GameManager.Instance.World;
            var evicted = new HashSet<int>();
            foreach (var client in Players.Online())
            {
                var player = Players.Entity(client);
                if (!Players.IsAlive(player)) continue;
                var poi = world.GetPOIAtPosition(player.position);
                var questLock = poi?.lockInstance;
                if (questLock == null || questLock.CheckQuestLock())
                {
                    lastSafe[player.entityId] = player.position;
                    continue;
                }
                if (!guarded.TryGetValue(poi, out var owners)) guarded[poi] = owners = new HashSet<int>();
                if (questLock.IsLocked) owners.UnionWith(questLock.LockedByEntities);
                if (owners.Contains(player.entityId) || Players.IsAdmin(client) || IsRelated(player.entityId, owners)) continue;
                Evict(client, player, poi);
                evicted.Add(player.entityId);
            }
            foreach (var poi in guarded.Keys.Where(p => p.lockInstance == null || p.lockInstance.CheckQuestLock()).ToList())
                guarded.Remove(poi);
            foreach (var entityId in failedEvictions.Keys.Where(id => !evicted.Contains(id)).ToList())
                failedEvictions.Remove(entityId);
        }

        internal void CheckPlacement(GameManager game, PlatformUserIdentifierAbs playerId, List<BlockChangeInfo> changes)
        {
            if (!Settings.BlockClaims && !Settings.BlockBedrolls) return;
            if (!ConnectionManager.Instance.IsServer) return;
            var world = game.World;
            var data = game.persistentPlayers?.GetPlayerData(playerId);
            if (world == null || data == null || data.EntityId == -1) return;
            ClientInfo client = null;
            for (var i = changes.Count - 1; i >= 0; i--)
            {
                var change = changes[i];
                if (change == null || !change.bChangeBlockValue || change.blockValue.isair || change.blockValue.ischild) continue;
                var block = change.blockValue.Block;
                if (block == null) continue;
                string reason;
                if (Settings.BlockClaims && block.IndexName == ClaimIndexName) reason = "ClaimBlocked";
                else if (Settings.BlockBedrolls && block is BlockSleepingBag) reason = "BedrollBlocked";
                else continue;
                if (!change.blockValueRef.TryGetBlockPos(out var position)) continue;
                var inside = reason == "ClaimBlocked"
                    ? ClaimReachesPoi(world, position)
                    : world.GetPOIAtPosition(new Vector3(position.x, position.y, position.z)) != null;
                if (!inside) continue;
                client ??= Players.Client(data.EntityId);
                if (client == null || Players.IsAdmin(client)) return;
                changes.RemoveAt(i);
                world.SetBlockRPC(change.blockValueRef, BlockValue.Air);
                var item = ItemClass.GetItem(block.GetBlockName(), true);
                if (item != null && !item.IsEmpty()) Players.GiveItem(client, new ItemStack(item, 1));
                Reply(client, reason);
                Info($"{client.playerName} ({Players.Id(client)}) could not place {block.GetBlockName()} at {position} inside a location, the item was returned");
            }
        }

        private static bool ClaimReachesPoi(World world, Vector3i position)
        {
            var half = GameStats.GetInt(EnumGameStats.LandClaimSize) / 2;
            var pois = new List<PrefabInstance>();
            world.GetPOIsAtXZ(position.x - half, position.x + half, position.z - half, position.z + half, pois);
            return pois.Any(p => p?.prefab != null && !p.prefab.Tags.Test_AnySet(DynamicPrefabDecorator.streetTileTag));
        }

        private static bool IsRelated(int entityId, HashSet<int> owners)
        {
            foreach (var owner in owners)
                if (owner != entityId && (Players.SameParty(entityId, owner) || Players.AreFriends(entityId, owner)))
                    return true;
            return false;
        }

        private void Evict(ClientInfo client, EntityPlayer player, PrefabInstance poi)
        {
            Vector3? target;
            string how;
            if (lastSafe.TryGetValue(player.entityId, out var safe))
            {
                target = safe;
                how = "last safe position";
            }
            else
            {
                failedEvictions.TryGetValue(player.entityId, out var failed);
                failedEvictions[player.entityId] = failed + 1;
                target = Around(poi, player.entityId, failed);
                how = "random spot around";
            }
            if (target.HasValue)
            {
                Players.Teleport(client, target.Value);
                Info($"{client.playerName} ({player.entityId}) moved out of {poi.name} to {target.Value} ({how})");
            }
            else
            {
                Warn($"{client.playerName} ({player.entityId}) is inside {poi.name} but no free spot was found around it");
            }
            var now = DateTime.UtcNow;
            if (nextMessage.TryGetValue(player.entityId, out var next) && next > now) return;
            nextMessage[player.entityId] = now.AddSeconds(MessageCooldownSeconds);
            Reply(client, "Evicted");
        }

        private Vector3? Around(PrefabInstance poi, int entityId, int extra)
        {
            var world = GameManager.Instance.World;
            var data = Players.Data(entityId);
            var centerX = poi.boundingBoxPosition.x + poi.boundingBoxSize.x / 2f;
            var centerZ = poi.boundingBoxPosition.z + poi.boundingBoxSize.z / 2f;
            var minRange = Math.Max(poi.boundingBoxSize.x, poi.boundingBoxSize.z) / 2 + 2 + extra;
            for (var attempt = 0; attempt < EvictAttempts; attempt++)
            {
                var angle = random.NextDouble() * Math.PI * 2.0;
                var distance = minRange + random.NextDouble() * 8.0;
                var x = (int)Math.Floor(centerX + Math.Cos(angle) * distance);
                var z = (int)Math.Floor(centerZ + Math.Sin(angle) * distance);
                var y = world.GetTerrainHeight(x, z) + 1;
                var candidate = new Vector3(x + 0.5f, y, z + 0.5f);
                if (!world.CanPlayersSpawnAtPos(candidate) || world.IsWater(candidate)) continue;
                if (world.GetPOIAtPosition(candidate) != null) continue;
                if (data != null && world.GetLandClaimOwner(new Vector3i(x, y, z), data) == EnumLandClaimOwner.Other) continue;
                return candidate;
            }
            return null;
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.ChangeBlocks))]
    [HarmonyPatchCategory(nameof(PoiGuard))]
    internal static class PoiGuardPlacementPatch
    {
        private static void Prefix(GameManager __instance, PlatformUserIdentifierAbs persistentPlayerId, List<BlockChangeInfo> _blocksToChange)
        {
            if (persistentPlayerId == null || _blocksToChange == null) return;
            var module = PoiGuard.Active;
            if (module == null) return;
            try
            {
                module.CheckPlacement(__instance, persistentPlayerId, _blocksToChange);
            }
            catch (Exception e)
            {
                module.Fault(e, "placement check");
            }
        }
    }
}
