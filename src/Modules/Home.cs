using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ServerMultipass.Modules
{
    public sealed class HomeSettings
    {
        [Range(1, 100)] public int Limit;
        [Range(0)] public int Cooldown;
        public bool OnlyInClaim;
        public bool RemoveWithClaim;
    }

    public sealed class HomePoint
    {
        public string Name;
        public float X;
        public float Y;
        public float Z;
        public bool InClaim;
    }

    public sealed class Home : Module<HomeSettings>
    {
        private const string DataFile = "Homes.json";
        private const string NoticesFile = "HomesRemoved.json";
        private const int MaxNameLength = 24;
        private const double ClaimCheckSeconds = 60;

        private static Home instance;
        private readonly Dictionary<string, DateTime> nextTeleport = new Dictionary<string, DateTime>();
        private Dictionary<string, List<HomePoint>> homes = new Dictionary<string, List<HomePoint>>();
        private Dictionary<string, List<string>> notices = new Dictionary<string, List<string>>();
        private bool claimsChanged;
        private DateTime nextClaimCheck;

        public override string Name => "Home";
        protected override bool HasPatches => true;

        internal static Home Active => instance != null && instance.Ready ? instance : null;

        protected override void OnEnable()
        {
            homes = LoadWorld<Dictionary<string, List<HomePoint>>>(DataFile) ?? new Dictionary<string, List<HomePoint>>();
            notices = LoadWorld<Dictionary<string, List<string>>>(NoticesFile) ?? new Dictionary<string, List<string>>();
            claimsChanged = true;
            instance = this;
        }

        protected override void OnDisable()
        {
            instance = null;
            nextTeleport.Clear();
        }

        protected override void OnSettingsChanged()
        {
            claimsChanged = true;
        }

        protected internal override void RegisterCommands(ChatCommands commands)
        {
            commands.Add("home", OnHome);
            commands.Add("sethome", OnSet);
            commands.Add("delhome", OnDelete);
        }

        protected internal override void OnSecond(Tick tick)
        {
            if (!Settings.RemoveWithClaim) return;
            var now = DateTime.UtcNow;
            if (!claimsChanged && now < nextClaimCheck) return;
            claimsChanged = false;
            nextClaimCheck = now.AddSeconds(ClaimCheckSeconds);
            CheckClaims();
        }

        protected internal override void OnPlayerSpawned(ClientInfo client, RespawnType reason)
        {
            var id = Players.Id(client);
            if (id == null || !notices.TryGetValue(id, out var names)) return;
            notices.Remove(id);
            SaveWorld(NoticesFile, notices);
            foreach (var name in names) Reply(client, "RemovedWithClaim", name);
        }

        internal void ClaimsChanged()
        {
            claimsChanged = true;
        }

        private void OnHome(ChatContext context)
        {
            var client = context.Client;
            var id = Players.Id(client);
            var list = Points(id);
            if (context.Args.Length == 0)
            {
                if (list.Count == 0) Reply(client, "Empty");
                else Reply(client, "List", string.Join(", ", list.Select(p => p.Name)), list.Count, Settings.Limit);
                return;
            }
            var point = list.FirstOrDefault(p => p.Name.Equals(context.Args[0], StringComparison.OrdinalIgnoreCase));
            if (point == null)
            {
                Reply(client, "NotFound", Chat.Escape(context.Args[0]));
                return;
            }
            var player = context.Player;
            if (!Players.IsAlive(player)) return;
            if (Players.InVehicle(player))
            {
                Reply(client, "InVehicle");
                return;
            }
            var now = DateTime.UtcNow;
            if (nextTeleport.TryGetValue(id, out var next) && next > now)
            {
                Reply(client, "Cooldown", Durations.Format(next - now));
                return;
            }
            nextTeleport[id] = now.AddSeconds(Settings.Cooldown);
            Players.Teleport(client, new Vector3(point.X, point.Y, point.Z));
            Reply(client, "Teleported", point.Name);
        }

        private void OnSet(ChatContext context)
        {
            var client = context.Client;
            if (context.Args.Length == 0)
            {
                Reply(client, "UsageSet");
                return;
            }
            var name = context.Args[0];
            if (name.Length > MaxNameLength || name.IndexOfAny(new[] { '[', ']' }) >= 0)
            {
                Reply(client, "BadName", MaxNameLength);
                return;
            }
            var player = context.Player;
            if (!Players.IsAlive(player)) return;
            var position = player.position;
            var inClaim = InOwnClaim(Players.Data(client.entityId), position);
            if (Settings.OnlyInClaim && !inClaim)
            {
                Reply(client, "NotInClaim");
                return;
            }
            var id = Players.Id(client);
            var list = Points(id);
            var existing = list.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing == null && list.Count >= Settings.Limit)
            {
                Reply(client, "Limit", Settings.Limit);
                return;
            }
            if (existing != null) list.Remove(existing);
            list.Add(new HomePoint { Name = name, X = position.x, Y = position.y, Z = position.z, InClaim = inClaim });
            homes[id] = list;
            Save();
            Reply(client, "Saved", name);
        }

        private void OnDelete(ChatContext context)
        {
            var client = context.Client;
            if (context.Args.Length == 0)
            {
                Reply(client, "UsageDelete");
                return;
            }
            var id = Players.Id(client);
            var list = Points(id);
            if (list.RemoveAll(p => p.Name.Equals(context.Args[0], StringComparison.OrdinalIgnoreCase)) == 0)
            {
                Reply(client, "NotFound", Chat.Escape(context.Args[0]));
                return;
            }
            if (list.Count == 0) homes.Remove(id);
            Save();
            Reply(client, "Removed", Chat.Escape(context.Args[0]));
        }

        private void CheckClaims()
        {
            var players = GameManager.Instance?.persistentPlayers;
            if (players == null) return;
            var changed = false;
            var noticesChanged = false;
            foreach (var entry in homes.ToList())
            {
                var data = players.GetPlayerData(PlatformUserIdentifierAbs.FromCombinedString(entry.Key, false));
                if (data == null) continue;
                var lost = new List<HomePoint>();
                foreach (var point in entry.Value)
                {
                    if (InOwnClaim(data, new Vector3(point.X, point.Y, point.Z)))
                    {
                        if (point.InClaim) continue;
                        point.InClaim = true;
                        changed = true;
                    }
                    else if (point.InClaim)
                    {
                        lost.Add(point);
                    }
                }
                if (lost.Count == 0) continue;
                changed = true;
                entry.Value.RemoveAll(lost.Contains);
                if (entry.Value.Count == 0) homes.Remove(entry.Key);
                var client = data.EntityId == -1 ? null : Players.Client(data.EntityId);
                var online = client != null && Players.Entity(client) != null;
                foreach (var point in lost)
                {
                    Info($"{data.PlayerName?.AuthoredName?.Text} ({entry.Key}) lost home {point.Name} at {(int)point.X} {(int)point.Y} {(int)point.Z}: no land claim of the owner covers it anymore");
                    if (online)
                    {
                        Reply(client, "RemovedWithClaim", point.Name);
                        continue;
                    }
                    if (!notices.TryGetValue(entry.Key, out var names)) notices[entry.Key] = names = new List<string>();
                    names.Add(point.Name);
                    noticesChanged = true;
                }
            }
            if (changed) Save();
            if (noticesChanged) SaveWorld(NoticesFile, notices);
        }

        private static bool InOwnClaim(PersistentPlayerData data, Vector3 position)
        {
            var claims = data?.LPBlocks;
            var map = GameManager.Instance?.persistentPlayers?.m_lpBlockMap;
            if (claims == null || map == null) return false;
            var block = World.worldToBlockPos(position);
            var half = (GameStats.GetInt(EnumGameStats.LandClaimSize) - 1) / 2;
            foreach (var claim in claims)
            {
                if (Math.Abs(claim.x - block.x) > half || Math.Abs(claim.z - block.z) > half) continue;
                if (map.TryGetValue(claim, out var owner) && owner == data) return true;
            }
            return false;
        }

        private List<HomePoint> Points(string id)
        {
            return id != null && homes.TryGetValue(id, out var list) ? list : new List<HomePoint>();
        }

        private void Save()
        {
            SaveWorld(DataFile, homes);
        }
    }

    [HarmonyPatch(typeof(PersistentPlayerList), nameof(PersistentPlayerList.RemoveLandProtectionBlock))]
    [HarmonyPatchCategory(nameof(Home))]
    internal static class HomeClaimRemovedPatch
    {
        private static void Postfix()
        {
            Home.Active?.ClaimsChanged();
        }
    }

    [HarmonyPatch(typeof(PersistentPlayerList), nameof(PersistentPlayerList.PlaceLandProtectionBlock))]
    [HarmonyPatchCategory(nameof(Home))]
    internal static class HomeClaimPlacedPatch
    {
        private static void Postfix()
        {
            Home.Active?.ClaimsChanged();
        }
    }
}
