using System;
using System.Collections.Generic;
using System.Linq;

namespace ServerMultipass
{
    public class MultipassConsole : ConsoleCmdAbstract
    {
        public override int DefaultPermissionLevel => 0;

        public override string[] getCommands()
        {
            return new[] { "mp", "multipass" };
        }

        public override string getDescription()
        {
            return "Server Multipass: list modules, turn them on or off, reload the files";
        }

        public override string getHelp()
        {
            return "Usage:\n" +
                   "  mp                   modules, their state and chat commands\n" +
                   "  mp enable <module>   turn a module on and save it in ServerMultipass.xml\n" +
                   "  mp disable <module>  turn a module off and save it in ServerMultipass.xml\n" +
                   "  mp reload            read ServerMultipass.xml, Settings and Lang again right now\n" +
                   "Settings and texts are also picked up automatically a few seconds after a file is saved.";
        }

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            try
            {
                var action = _params.Count == 0 ? "status" : _params[0].ToLowerInvariant();
                switch (action)
                {
                    case "status":
                    case "list":
                        Status();
                        return;
                    case "enable":
                    case "on":
                        Toggle(_params, true);
                        return;
                    case "disable":
                    case "off":
                        Toggle(_params, false);
                        return;
                    case "reload":
                        Multipass.ReloadAll();
                        Output("files reloaded");
                        Status();
                        return;
                    default:
                        Output(getHelp());
                        return;
                }
            }
            catch (Exception e)
            {
                Output("error: " + e.Message);
                Multipass.Error("mp command failed: " + e);
            }
        }

        private static void Toggle(List<string> args, bool enabled)
        {
            if (args.Count < 2)
            {
                Output($"Usage: mp {(enabled ? "enable" : "disable")} <module>");
                return;
            }
            var module = Multipass.Find(args[1]);
            if (module == null)
            {
                Output($"Unknown module '{args[1]}'. Modules: {string.Join(", ", Multipass.Modules.Select(m => m.Name))}");
                return;
            }
            Output(Multipass.SetEnabled(module, enabled));
        }

        private static void Status()
        {
            var settings = Multipass.Settings;
            if (settings == null) Output($"Server Multipass {Multipass.Version}: {MainConfig.FilePath} is missing or broken, nothing runs");
            else Output($"Server Multipass {Multipass.Version}, chat prefix \"{settings.ChatPrefix}\", default language {Language.Default}, GeoIP {Language.GeoIpFile ?? "off"}");
            if (!Multipass.Started) Output("The world is not running yet, modules start together with it");
            var width = Multipass.Modules.Max(m => m.Name.Length) + 2;
            foreach (var module in Multipass.Modules)
            {
                var commands = Multipass.Commands.Values
                    .Where(c => c.Owner == module)
                    .Select(c => settings?.ChatPrefix + c.Name)
                    .Concat(module.ConsoleCommands);
                var line = $"  {module.Name.PadRight(width)}{State(module).PadRight(8)} {string.Join(" ", commands)}";
                var hooks = module.HookCount();
                if (hooks.HasValue && (module.Enabled || hooks.Value > 0)) line += $"  hooks: {hooks.Value}";
                if (module.ErrorCount > 0) line += $"  errors: {module.ErrorCount}";
                Output(line.TrimEnd());
            }
            var core = Multipass.Commands.Values.Where(c => c.Owner == null).Select(c => settings?.ChatPrefix + c.Name).ToList();
            if (core.Count > 0) Output("  for everyone: " + string.Join(" ", core));
        }

        private static string State(Module module)
        {
            if (module.Faulted) return "off (turned off after errors)";
            if (module.Enabled) return "on";
            var state = MainConfig.State(module);
            if (Multipass.Settings == null || state == false) return "off";
            if (state == null) return $"off (not listed in {MainConfig.FileName})";
            if (!module.ConfigReady) return $"off ({module.ConfigProblem})";
            return Multipass.Started ? "off" : "on (waits for the world)";
        }

        private static void Output(string text)
        {
            SdtdConsole.Instance.Output(text);
        }
    }
}
