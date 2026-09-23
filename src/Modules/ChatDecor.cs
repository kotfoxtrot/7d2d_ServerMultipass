using System;
using System.Collections.Generic;
using System.Linq;

namespace ServerMultipass.Modules
{
    public sealed class ChatDecorPlayer
    {
        public string Id;
        public string Tag;
        public string NameColor;
        public string MessageColor;
    }

    public sealed class ChatDecorSettings
    {
        public bool TagBeforeName;
        [Element("player")] public List<ChatDecorPlayer> Players;
    }

    public sealed class ChatDecor : Module<ChatDecorSettings>
    {
        private Dictionary<string, ChatDecorPlayer> decorated = new Dictionary<string, ChatDecorPlayer>(StringComparer.OrdinalIgnoreCase);

        public override string Name => "ChatDecor";
        protected override bool HasTexts => false;
        protected internal override bool HandlesChat => true;

        protected override void OnEnable()
        {
            Index();
        }

        protected override void OnSettingsChanged()
        {
            Index();
        }

        protected internal override ModEvents.EModEventResult OnChatMessage(ref ModEvents.SChatMessageData data)
        {
            var client = data.ClientInfo;
            if (client == null || data.SenderEntityId == -1 || string.IsNullOrEmpty(data.Message)) return ModEvents.EModEventResult.Continue;
            if (data.ChatType != EChatType.Global && data.ChatType != EChatType.Friends && data.ChatType != EChatType.Party && data.ChatType != EChatType.Whisper)
                return ModEvents.EModEventResult.Continue;
            if (!Find(client, out var decor)) return ModEvents.EModEventResult.Continue;
            var message = Paint(decor.MessageColor, Chat.Escape(data.Message));
            string text;
            int sender;
            if (Settings.TagBeforeName)
            {
                var name = Paint(decor.NameColor, data.MainName ?? Players.Name(client));
                text = Join(decor.Tag, $"{name}: {message}");
                sender = -1;
            }
            else
            {
                text = Join(decor.Tag, message);
                sender = data.SenderEntityId;
            }
            Send(data.ChatType, sender, text, data.RecipientEntityIds);
            return ModEvents.EModEventResult.StopHandlersAndVanilla;
        }

        private void Index()
        {
            var index = new Dictionary<string, ChatDecorPlayer>(StringComparer.OrdinalIgnoreCase);
            foreach (var player in Settings.Players)
            {
                if (string.IsNullOrWhiteSpace(player.Id))
                {
                    Warn("a <player> line without id is ignored");
                    continue;
                }
                player.NameColor = Color(player.NameColor, player.Id);
                player.MessageColor = Color(player.MessageColor, player.Id);
                index[player.Id.Trim()] = player;
            }
            decorated = index;
        }

        private string Color(string value, string id)
        {
            var color = (value ?? "").Trim().Trim('[', ']').TrimStart('#');
            if (color.Length == 0) return "";
            if ((color.Length == 6 || color.Length == 8) && color.All(Uri.IsHexDigit)) return color.ToUpperInvariant();
            Warn($"colour '{value}' of {id} is not RRGGBB and is ignored");
            return "";
        }

        private bool Find(ClientInfo client, out ChatDecorPlayer decor)
        {
            decor = null;
            var cross = client.CrossplatformId?.CombinedString;
            var platform = client.PlatformId?.CombinedString;
            return (cross != null && decorated.TryGetValue(cross, out decor)) || (platform != null && decorated.TryGetValue(platform, out decor));
        }

        private static string Paint(string color, string text)
        {
            return string.IsNullOrEmpty(color) ? text : $"[{color}]{text}[-]";
        }

        private static string Join(string tag, string text)
        {
            return string.IsNullOrEmpty(tag) ? text : tag + " " + text;
        }

        private static void Send(EChatType type, int sender, string text, List<int> recipients)
        {
            NetPackageChat Package()
            {
                return NetPackageManager.GetPackage<NetPackageChat>().Setup(type, sender, text, null, EMessageSender.None,
                    GeneratedTextManager.BbCodeSupportMode.Supported);
            }

            if (recipients != null)
            {
                foreach (var id in recipients) ConnectionManager.Instance.Clients.ForEntityId(id)?.SendPackage(Package());
                return;
            }
            ConnectionManager.Instance.SendPackage(Package(), _onlyClientsAttachedToAnEntity: true);
        }
    }
}
