using System;
using System.Collections.Generic;
using UnityEngine;

namespace ServerMultipass
{
    public sealed class Tick
    {
        public DateTime Now { get; private set; }
        public ulong WorldTime { get; private set; }
        public int Day { get; private set; }
        public int Hour { get; private set; }
        public int BloodMoonDay { get; private set; }
        public bool BloodMoon { get; private set; }

        internal static Tick Capture()
        {
            var world = GameManager.Instance?.World;
            if (world == null) return null;
            var time = world.worldTime;
            var bloodMoonDay = GameStats.GetInt(EnumGameStats.BloodMoonDay);
            var duskDawn = GameUtils.CalcDuskDawnHours(GameStats.GetInt(EnumGameStats.DayLightLength));
            return new Tick
            {
                Now = DateTime.Now,
                WorldTime = time,
                Day = GameUtils.WorldTimeToDays(time),
                Hour = GameUtils.WorldTimeToHours(time),
                BloodMoonDay = bloodMoonDay,
                BloodMoon = GameUtils.IsBloodMoonTime(time, duskDawn, bloodMoonDay)
            };
        }
    }

    public static class Durations
    {
        public static string Format(TimeSpan span)
        {
            var seconds = (long)Math.Ceiling(Math.Max(0d, span.TotalSeconds));
            return $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}";
        }
    }

    public static class Items
    {
        public static string Name(ItemClass item, string language)
        {
            if (item == null) return "";
            var key = item.GetItemName();
            if (!string.IsNullOrEmpty(language) && Localization.languageToColumnIndex.ContainsKey(language) && Localization.Exists(key))
            {
                var text = Localization.Get(key, false, language);
                if (!string.IsNullOrEmpty(text) && text != key) return text;
            }
            return item.GetLocalizedItemName() ?? key;
        }
    }

    public static class Chat
    {
        public static void Send(ClientInfo client, string message)
        {
            if (client == null || string.IsNullOrEmpty(message)) return;
            client.SendPackage(NetPackageManager.GetPackage<NetPackageChat>().Setup(EChatType.Global, -1, message, null,
                EMessageSender.None, GeneratedTextManager.BbCodeSupportMode.Supported));
        }

        public static string Escape(string text)
        {
            return string.IsNullOrEmpty(text) ? text ?? "" : Utils.EscapeBbCodes(text);
        }

        public static string Plain(string text)
        {
            return string.IsNullOrEmpty(text) ? text ?? "" : ColorCodes.Replace(text, "");
        }

        private static readonly System.Text.RegularExpressions.Regex ColorCodes =
            new System.Text.RegularExpressions.Regex(@"\[(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8}|-)\]", System.Text.RegularExpressions.RegexOptions.Compiled);
    }

    public static class Players
    {
        public const string DeniedSound = "ui_denied";

        public static string Id(ClientInfo client)
        {
            return client?.InternalId?.CombinedString;
        }

        public static string Name(ClientInfo client)
        {
            return Chat.Escape(client?.playerName ?? "");
        }

        public static List<ClientInfo> Online()
        {
            var result = new List<ClientInfo>();
            var clients = ConnectionManager.Instance?.Clients?.list;
            if (clients == null) return result;
            foreach (var client in clients)
                if (client != null && client.loginDone && client.entityId != -1)
                    result.Add(client);
            return result;
        }

        public static ClientInfo Client(int entityId)
        {
            var client = ConnectionManager.Instance?.Clients?.ForEntityId(entityId);
            return client != null && client.loginDone ? client : null;
        }

        public static ClientInfo Find(string nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId)) return null;
            var clients = ConnectionManager.Instance?.Clients;
            if (clients == null) return null;
            var client = clients.GetForNameOrId(nameOrId);
            if (client == null && int.TryParse(nameOrId, out var entityId)) client = clients.ForEntityId(entityId);
            return client != null && client.loginDone ? client : null;
        }

        public static EntityPlayer Entity(int entityId)
        {
            var world = GameManager.Instance?.World;
            return world != null && world.Players.dict.TryGetValue(entityId, out var player) ? player : null;
        }

        public static EntityPlayer Entity(ClientInfo client)
        {
            return client == null ? null : Entity(client.entityId);
        }

        public static PersistentPlayerData Data(int entityId)
        {
            return GameManager.Instance?.persistentPlayers?.GetPlayerDataFromEntityID(entityId);
        }

        public static bool IsAlive(EntityPlayer player)
        {
            return player != null && player.IsSpawned() && !player.IsDead();
        }

        public static bool InVehicle(EntityPlayer player)
        {
            return player != null && player.AttachedToEntity != null;
        }

        public static int PermissionLevel(ClientInfo client)
        {
            var admin = GameManager.Instance?.adminTools;
            return admin == null || client == null ? 1000 : admin.Users.GetUserPermissionLevel(client);
        }

        public static bool IsAdmin(ClientInfo client)
        {
            return client != null && PermissionLevel(client) <= 0;
        }

        public static bool MayUse(ClientInfo client, ChatCommand command)
        {
            var admin = GameManager.Instance?.adminTools;
            if (admin == null) return true;
            var key = new[] { "mp." + command.Name };
            var required = admin.Commands.IsPermissionDefined(key) ? admin.Commands.GetCommandPermissionLevel(key) : command.Permission;
            return PermissionLevel(client) <= required;
        }

        public static bool AreFriends(int entityA, int entityB)
        {
            var a = Data(entityA);
            var b = Data(entityB);
            return a != null && b != null && a.IsAlly(b);
        }

        public static bool SameParty(int entityA, int entityB)
        {
            var a = Entity(entityA);
            var b = Entity(entityB);
            return a != null && b != null && a.IsInParty() && a.Party.MemberList.Contains(b);
        }

        public static void Teleport(ClientInfo client, Vector3 position)
        {
            if (client == null) return;
            LockManager.Instance.ForceUnlockByPlayer(client.entityId);
            client.SendPackage(NetPackageManager.GetPackage<NetPackageTeleportPlayer>().Setup(position));
        }

        public static void Kick(ClientInfo client, string reason)
        {
            if (client == null) return;
            GameUtils.KickPlayerForClientInfo(client, new GameUtils.KickPlayerData(GameUtils.EKickReason.ManualKick, 0, default, reason));
        }

        public static void PlaySound(ClientInfo client, string sound)
        {
            var player = Entity(client);
            if (player == null) return;
            client.SendPackage(NetPackageManager.GetPackage<NetPackageSoundAtPosition>()
                .Setup(player.position, sound, AudioRolloffMode.Linear, 20, player.entityId, 1f));
        }

        public static bool GiveItem(ClientInfo client, ItemStack stack)
        {
            var world = GameManager.Instance?.World;
            var player = Entity(client);
            if (world == null || !IsAlive(player) || stack == null || stack.IsEmpty()) return false;
            var entity = EntityFactory.CreateEntity(new EntityCreationData
            {
                entityClass = EntityClass.FromString("item"),
                id = EntityFactory.nextEntityID++,
                itemStack = stack.Clone(),
                pos = player.position,
                rot = new Vector3(20f, 0f, 20f),
                lifetime = 60f,
                belongsPlayerId = client.entityId
            });
            world.SpawnEntityInWorld(entity);
            client.SendPackage(NetPackageManager.GetPackage<NetPackageEntityCollect>().Setup(entity.entityId, client.entityId));
            world.RemoveEntity(entity.entityId, EnumRemoveEntityReason.Despawned);
            return true;
        }
    }

    public sealed class ChatCommand
    {
        public ChatCommand(string name, Module owner, Action<ChatContext> handler, int permission)
        {
            Name = name;
            Owner = owner;
            Handler = handler;
            Permission = permission;
        }

        public string Name { get; }
        public Module Owner { get; }
        public Action<ChatContext> Handler { get; }
        public int Permission { get; }
    }

    public sealed class ChatContext
    {
        public ChatContext(ClientInfo client, string command, string[] args)
        {
            Client = client;
            Command = command;
            Args = args;
        }

        public ClientInfo Client { get; }
        public string Command { get; }
        public string[] Args { get; }
        public EntityPlayer Player => Players.Entity(Client);
    }

    public sealed class ChatCommands
    {
        private readonly Dictionary<string, ChatCommand> map;

        internal ChatCommands(Dictionary<string, ChatCommand> map)
        {
            this.map = map;
        }

        internal Module Owner { get; set; }

        public void Add(string name, Action<ChatContext> handler, int permission = 1000)
        {
            if (string.IsNullOrWhiteSpace(name) || handler == null) return;
            name = name.Trim().TrimStart('/');
            if (map.TryGetValue(name, out var existing))
            {
                Multipass.Warn($"chat command {name} of {Owner?.Name ?? "Multipass"} is already taken by {existing.Owner?.Name ?? "Multipass"}, skipped");
                return;
            }
            map[name] = new ChatCommand(name, Owner, handler, permission);
        }
    }
}
