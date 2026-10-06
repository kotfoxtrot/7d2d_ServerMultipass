using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ServerMultipass.Modules
{
    public sealed class ClaimLimit : Module
    {
        private const string ClaimIndexName = "lpblock";

        private static ClaimLimit instance;

        public override string Name => "ClaimLimit";
        protected override bool HasPatches => true;

        internal static ClaimLimit Active => instance != null && instance.Ready ? instance : null;

        private static int Limit => GamePrefs.GetInt(EnumGamePrefs.LandClaimCount);

        protected override void OnEnable()
        {
            instance = this;
            SetGameLimit(Limit + 1);
        }

        protected override void OnDisable()
        {
            instance = null;
            SetGameLimit(Limit);
        }

        protected internal override void OnSecond(Tick tick)
        {
            SetGameLimit(Limit + 1);
        }

        private void SetGameLimit(int value)
        {
            if (GameStats.GetInt(EnumGameStats.LandClaimCount) == value) return;
            GameStats.Set(EnumGameStats.LandClaimCount, value);
            ConnectionManager.Instance?.SendPackage(NetPackageManager.GetPackage<NetPackageGameStats>().Setup(GameStats.Instance));
            Info($"game land claim limit set to {value}, LandClaimCount is {Limit}");
        }

        internal void CheckPlacement(GameManager game, List<BlockChangeInfo> changes)
        {
            if (!ConnectionManager.Instance.IsServer) return;
            var world = game.World;
            var players = game.persistentPlayers;
            if (world == null || players == null) return;
            var limit = Limit;
            Dictionary<int, int> placing = null;
            for (var i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                if (change == null || !change.bChangeBlockValue || change.blockValue.isair || change.blockValue.ischild) continue;
                var block = change.blockValue.Block;
                if (block == null || block.IndexName != ClaimIndexName) continue;
                if (!change.blockValueRef.TryGetBlockPos(out var position)) continue;
                if (world.GetBlock(position).type == change.blockValue.type) continue;
                var owner = players.GetPlayerDataFromEntityID(change.changedByEntityId);
                var client = owner == null ? null : Players.Client(owner.EntityId);
                if (client == null) continue;
                placing ??= new Dictionary<int, int>();
                placing.TryGetValue(owner.EntityId, out var pending);
                var count = (owner.LPBlocks?.Count ?? 0) + pending;
                if (count < limit)
                {
                    placing[owner.EntityId] = pending + 1;
                    continue;
                }
                changes.RemoveAt(i--);
                world.SetBlockRPC(change.blockValueRef, BlockValue.Air);
                var item = ItemClass.GetItem(block.GetBlockName(), true);
                if (item != null && !item.IsEmpty()) Players.GiveItem(client, new ItemStack(item, 1));
                Players.PlaySound(client, Players.DeniedSound);
                Reply(client, "LimitReached", count, limit);
                Info($"{client.playerName} ({Players.Id(client)}) could not place {block.GetBlockName()} at {position}: {count}/{limit} land claims, the item was returned");
            }
        }

        internal void Placed(PersistentPlayerList list, Vector3i position, PlatformUserIdentifierAbs ownerId)
        {
            if (!ConnectionManager.Instance.IsServer) return;
            var owner = list.GetPlayerData(ownerId);
            if (owner?.LPBlocks == null || !owner.LPBlocks.Contains(position)) return;
            var client = Players.Client(owner.EntityId);
            if (client == null) return;
            Reply(client, "Placed", owner.LPBlocks.Count, Limit);
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.ChangeBlocks))]
    [HarmonyPatchCategory(nameof(ClaimLimit))]
    internal static class ClaimLimitPlacementPatch
    {
        private static void Prefix(GameManager __instance, PlatformUserIdentifierAbs persistentPlayerId, List<BlockChangeInfo> _blocksToChange)
        {
            if (persistentPlayerId == null || _blocksToChange == null) return;
            var module = ClaimLimit.Active;
            if (module == null) return;
            try
            {
                module.CheckPlacement(__instance, _blocksToChange);
            }
            catch (Exception e)
            {
                module.Fault(e, "placement check");
            }
        }
    }

    [HarmonyPatch(typeof(PersistentPlayerList), nameof(PersistentPlayerList.PlaceLandProtectionBlock))]
    [HarmonyPatchCategory(nameof(ClaimLimit))]
    internal static class ClaimLimitPlacedPatch
    {
        private static void Postfix(PersistentPlayerList __instance, Vector3i pos, PlatformUserIdentifierAbs owner)
        {
            var module = ClaimLimit.Active;
            if (module == null || owner == null) return;
            try
            {
                module.Placed(__instance, pos, owner);
            }
            catch (Exception e)
            {
                module.Fault(e, "placement message");
            }
        }
    }
}
