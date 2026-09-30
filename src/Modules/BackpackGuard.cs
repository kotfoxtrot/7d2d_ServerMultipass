using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ServerMultipass.Modules
{
    public sealed class BackpackGuardSettings
    {
        [Range(0)] public double ProtectionMinutes;
        public bool ExemptFriends;
    }

    public sealed class BackpackGuard : Module<BackpackGuardSettings>
    {
        private const double MessageCooldownSeconds = 3;

        private static BackpackGuard instance;
        private readonly Dictionary<int, DateTime> nextMessage = new Dictionary<int, DateTime>();

        public override string Name => "BackpackGuard";
        protected override bool HasPatches => true;

        internal static BackpackGuard Active => instance != null && instance.Ready ? instance : null;

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

        internal bool Allows(EntityBackpack backpack, int entityId)
        {
            var client = Players.Client(entityId);
            var world = GameManager.Instance.World;
            if (client == null || world == null || Players.IsAdmin(client)) return true;
            var owner = Owner(backpack.entityId, out var dropped);
            if (owner == null || owner.EntityId == entityId) return true;
            var data = Players.Data(entityId);
            if (Settings.ExemptFriends && data != null && owner.IsAlly(data)) return true;
            var dayLength = GameStats.GetInt(EnumGameStats.DayNightLength);
            if (dayLength <= 0) dayLength = 60;
            var protectedMinutes = Settings.ProtectionMinutes * 1440d / dayLength;
            var age = (double)GameUtils.WorldTimeToTotalMinutes(world.worldTime) - dropped;
            if (age >= protectedMinutes) return true;
            var now = DateTime.UtcNow;
            if (nextMessage.TryGetValue(entityId, out var next) && next > now) return false;
            nextMessage[entityId] = now.AddSeconds(MessageCooldownSeconds);
            var left = Math.Max(1, (int)Math.Ceiling((protectedMinutes - age) * dayLength / 1440d));
            Reply(client, "Protected", Chat.Escape(owner.PlayerName?.DisplayName ?? "?"), left);
            return false;
        }

        private static PersistentPlayerData Owner(int backpackId, out uint dropped)
        {
            var players = GameManager.Instance.persistentPlayers?.Players;
            if (players != null)
            {
                foreach (var data in players.Values)
                {
                    if (!data.backpacksByID.TryGetValue(backpackId, out var backpack)) continue;
                    dropped = backpack.Timestamp;
                    return data;
                }
            }
            dropped = 0;
            return null;
        }
    }

    [HarmonyPatch(typeof(Entity), nameof(Entity.OnLockRequestServer))]
    [HarmonyPatchCategory(nameof(BackpackGuard))]
    internal static class BackpackGuardPatch
    {
        private static bool Prefix(Entity __instance, int _lockingPlayerID, ref bool __result)
        {
            if (!(__instance is EntityBackpack backpack)) return true;
            var module = BackpackGuard.Active;
            if (module == null) return true;
            try
            {
                if (module.Allows(backpack, _lockingPlayerID)) return true;
            }
            catch (Exception e)
            {
                module.Fault(e, "backpack check");
                return true;
            }
            __result = false;
            return false;
        }
    }
}
