using System;
using System.Collections.Generic;
using System.Linq;

namespace ServerMultipass.Modules
{
    public sealed class GiveSettings
    {
        public string[] Aliases;
        [Range(1)] public int MaxCount;
    }

    public sealed class Give : Module<GiveSettings>
    {
        public override string Name => "Give";
        protected internal override IEnumerable<string> ConsoleCommands => ConsoleNames();

        internal IEnumerable<string> ConsoleNames()
        {
            return new[] { "mp-give" }
                .Concat((Settings?.Aliases ?? Array.Empty<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        internal void Execute(List<string> args, CommandSenderInfo sender)
        {
            var console = sender.RemoteClientInfo;

            void Output(string key, params object[] values)
            {
                SdtdConsole.Instance.Output(Chat.Plain(Tagged(console, key, values)));
            }

            if (args.Count < 2)
            {
                Output("Usage");
                return;
            }
            var count = 1;
            var quality = 1;
            var durability = 100;
            if (args.Count > 2 && !TryRange(args[2], 1, Settings.MaxCount, out count))
            {
                Output("BadCount", Settings.MaxCount);
                return;
            }
            if (args.Count > 3 && !TryRange(args[3], 1, 6, out quality))
            {
                Output("BadQuality");
                return;
            }
            if (args.Count > 4 && !TryRange(args[4], 1, 100, out durability))
            {
                Output("BadDurability");
                return;
            }
            var item = ItemClass.GetItem(args[1], true);
            if (item == null || item.IsEmpty() || item.ItemClass == null)
            {
                Output("UnknownItem", args[1]);
                return;
            }
            var stack = new ItemStack(new ItemValue(item.type, quality, quality, false, null, 1f), count);
            if (durability < 100 && stack.itemValue.MaxUseTimes > 0)
                stack.itemValue.UseTimes = stack.itemValue.MaxUseTimes * (1f - durability / 100f);
            var itemName = Items.Name(item.ItemClass, Language.Of(console));
            var by = console == null ? "console" : console.playerName;
            if (args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var delivered = Players.Online().Count(client => Deliver(client, stack, item.ItemClass, count));
                Output("GivenAll", itemName, count, delivered);
                Info($"{by} gave {count} x {item.ItemClass.Name} to {delivered} player(s)");
                return;
            }
            var target = Players.Find(args[0]);
            if (target == null)
            {
                Output("PlayerNotFound", args[0]);
                return;
            }
            if (!Deliver(target, stack, item.ItemClass, count))
            {
                Output("NotSpawned", target.playerName);
                return;
            }
            Output("Given", itemName, count, target.playerName);
            Info($"{by} gave {count} x {item.ItemClass.Name} to {target.playerName} ({Players.Id(target)})");
        }

        private bool Deliver(ClientInfo client, ItemStack stack, ItemClass item, int count)
        {
            if (!Players.GiveItem(client, stack)) return false;
            Reply(client, "Received", Items.Name(item, Language.Of(client)), count);
            return true;
        }

        private static bool TryRange(string text, int min, int max, out int value)
        {
            return int.TryParse(text, out value) && value >= min && value <= max;
        }
    }

    public class GiveConsole : ConsoleCmdAbstract
    {
        public override int DefaultPermissionLevel => 0;

        public override string[] getCommands()
        {
            return Multipass.Get<Give>()?.ConsoleNames().ToArray() ?? new[] { "mp-give" };
        }

        public override string getDescription()
        {
            return "Server Multipass: give an item to a player or to everyone online";
        }

        public override string getHelp()
        {
            return "Usage:\n" +
                   "  mp-give <player|all> <item> [count] [quality] [durability]\n" +
                   "    player      name, entity id, EOS or Steam id, or all for everyone online\n" +
                   "    item        item or block name, for example drinkJarBoiledWater\n" +
                   "    count       how many, 1 by default\n" +
                   "    quality     1 to 6, 1 by default, only for items with quality\n" +
                   "    durability  percent from 1 to 100, 100 by default";
        }

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            var module = Multipass.Get<Give>();
            if (module == null || !module.Ready)
            {
                SdtdConsole.Instance.Output("Module Give is off. Turn it on with: mp enable Give");
                return;
            }
            Multipass.Run(module, "mp-give", () => module.Execute(_params, _senderInfo));
        }
    }
}
