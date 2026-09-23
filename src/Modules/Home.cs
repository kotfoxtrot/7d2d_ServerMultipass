using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ServerMultipass.Modules
{
    public sealed class HomeSettings
    {
        [Range(1, 100)] public int Limit;
        [Range(0)] public int Cooldown;
    }

    public sealed class HomePoint
    {
        public string Name;
        public float X;
        public float Y;
        public float Z;
    }

    public sealed class Home : Module<HomeSettings>
    {
        private const string DataFile = "Homes.json";
        private const int MaxNameLength = 24;

        private readonly Dictionary<string, DateTime> nextTeleport = new Dictionary<string, DateTime>();
        private Dictionary<string, List<HomePoint>> homes = new Dictionary<string, List<HomePoint>>();

        public override string Name => "Home";

        protected override void OnEnable()
        {
            homes = LoadWorld<Dictionary<string, List<HomePoint>>>(DataFile) ?? new Dictionary<string, List<HomePoint>>();
        }

        protected override void OnDisable()
        {
            nextTeleport.Clear();
        }

        protected internal override void RegisterCommands(ChatCommands commands)
        {
            commands.Add("home", OnHome);
            commands.Add("sethome", OnSet);
            commands.Add("delhome", OnDelete);
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
            var id = Players.Id(client);
            var list = Points(id);
            var existing = list.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing == null && list.Count >= Settings.Limit)
            {
                Reply(client, "Limit", Settings.Limit);
                return;
            }
            if (existing != null) list.Remove(existing);
            var position = player.position;
            list.Add(new HomePoint { Name = name, X = position.x, Y = position.y, Z = position.z });
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

        private List<HomePoint> Points(string id)
        {
            return id != null && homes.TryGetValue(id, out var list) ? list : new List<HomePoint>();
        }

        private void Save()
        {
            SaveWorld(DataFile, homes);
        }
    }
}
