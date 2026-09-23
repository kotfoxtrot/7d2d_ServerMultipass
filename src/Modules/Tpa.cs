using System;
using System.Collections.Generic;
using System.Linq;

namespace ServerMultipass.Modules
{
    public sealed class TpaSettings
    {
        [Range(10, 3600)] public int RequestTimeout;
        [Range(0)] public int Cooldown;
    }

    public sealed class Tpa : Module<TpaSettings>
    {
        private readonly Dictionary<string, Request> pending = new Dictionary<string, Request>();
        private readonly Dictionary<string, DateTime> nextTeleport = new Dictionary<string, DateTime>();

        public override string Name => "Tpa";

        protected override void OnDisable()
        {
            pending.Clear();
            nextTeleport.Clear();
        }

        protected internal override void RegisterCommands(ChatCommands commands)
        {
            commands.Add("tp", OnRequest);
            commands.Add("tpa", OnAccept);
            commands.Add("tpd", OnDeny);
        }

        protected internal override void OnPlayerDisconnected(ClientInfo client)
        {
            var id = Players.Id(client);
            if (id == null) return;
            pending.Remove(id);
            foreach (var key in pending.Where(p => p.Value.SenderId == id).Select(p => p.Key).ToList())
                pending.Remove(key);
        }

        private void OnRequest(ChatContext context)
        {
            var client = context.Client;
            if (context.Args.Length == 0)
            {
                Reply(client, "Usage");
                return;
            }
            var id = Players.Id(client);
            var now = DateTime.UtcNow;
            if (nextTeleport.TryGetValue(id, out var next) && next > now)
            {
                Reply(client, "Cooldown", Durations.Format(next - now));
                return;
            }
            if (Players.InVehicle(context.Player))
            {
                Reply(client, "InVehicle");
                return;
            }
            var query = string.Join(" ", context.Args);
            var matches = Match(query);
            if (matches.Count == 0)
            {
                Reply(client, "NotFound", Chat.Escape(query));
                return;
            }
            if (matches.Count > 1)
            {
                Reply(client, "Many", Chat.Escape(query));
                return;
            }
            var target = matches[0];
            if (target.entityId == client.entityId)
            {
                Reply(client, "Self");
                return;
            }
            pending[Players.Id(target)] = new Request
            {
                SenderEntityId = client.entityId,
                SenderId = id,
                SenderName = Players.Name(client),
                Expires = now.AddSeconds(Settings.RequestTimeout)
            };
            Reply(client, "Sent", Players.Name(target));
            Reply(target, "Received", Players.Name(client));
        }

        private void OnAccept(ChatContext context)
        {
            var target = context.Client;
            var id = Players.Id(target);
            if (!pending.TryGetValue(id, out var request))
            {
                Reply(target, "NoRequest");
                return;
            }
            pending.Remove(id);
            var now = DateTime.UtcNow;
            if (request.Expires < now)
            {
                Reply(target, "Expired");
                return;
            }
            var sender = Players.Client(request.SenderEntityId);
            if (sender == null || Players.Id(sender) != request.SenderId)
            {
                Reply(target, "Offline", request.SenderName);
                return;
            }
            var senderPlayer = Players.Entity(sender);
            var targetPlayer = context.Player;
            if (!Players.IsAlive(senderPlayer) || !Players.IsAlive(targetPlayer))
            {
                Reply(target, "Unavailable");
                return;
            }
            if (Players.InVehicle(senderPlayer))
            {
                Reply(sender, "InVehicle");
                Reply(target, "SenderInVehicle", request.SenderName);
                return;
            }
            Players.Teleport(sender, targetPlayer.position);
            nextTeleport[request.SenderId] = now.AddSeconds(Settings.Cooldown);
            Reply(sender, "Accepted", Players.Name(target));
            Reply(target, "YouAccepted", request.SenderName);
        }

        private void OnDeny(ChatContext context)
        {
            var target = context.Client;
            var id = Players.Id(target);
            if (!pending.TryGetValue(id, out var request))
            {
                Reply(target, "NoRequest");
                return;
            }
            pending.Remove(id);
            Reply(target, "YouDenied", request.SenderName);
            var sender = Players.Client(request.SenderEntityId);
            if (sender != null && Players.Id(sender) == request.SenderId) Reply(sender, "Denied", Players.Name(target));
        }

        private static List<ClientInfo> Match(string query)
        {
            var online = Players.Online();
            var exact = online.Where(c => string.Equals(c.playerName, query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0) return exact;
            return online.Where(c => c.playerName != null && c.playerName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }

        private sealed class Request
        {
            public int SenderEntityId;
            public string SenderId;
            public string SenderName;
            public DateTime Expires;
        }
    }
}
