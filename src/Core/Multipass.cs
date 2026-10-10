using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HarmonyLib;
using ServerMultipass.Modules;

namespace ServerMultipass
{
    public static class Multipass
    {
        public const string Version = "1.5.0";

        private const string HarmonyId = "kotfoxtrot.servermultipass";
        private const string LogTag = "[Multipass] ";
        private const string CoreTexts = "ServerMultipass";
        private const long SecondMs = 1000L;
        private const long WatchMs = 2000L;

        private static readonly List<Module> modules = new List<Module>();
        private static readonly Dictionary<string, ChatCommand> commands = new Dictionary<string, ChatCommand>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<Module> faulted = new Queue<Module>();
        private static readonly Stopwatch clock = new Stopwatch();
        private static FileStamp coreTextsStamp;
        private static long nextSecond;
        private static long nextWatch;
        private static bool initialized;
        private static bool lateChatRegistered;

        public static string ModPath { get; private set; }
        public static string WorldPath { get; private set; }
        public static bool Started { get; private set; }
        public static IReadOnlyList<Module> Modules => modules;
        public static IReadOnlyDictionary<string, ChatCommand> Commands => commands;
        public static MainSettings Settings => MainConfig.Settings;
        public static LangTable Lang { get; private set; }
        internal static Harmony Harmony { get; private set; }

        public static string SettingsDir => Path.Combine(ModPath, "Settings");
        public static string LangDir => Path.Combine(ModPath, "Lang");
        public static string DataDir => Path.Combine(ModPath, "Data");
        public static string XmlDir => Path.Combine(ModPath, XmlLayer.FolderName);

        internal static void Init(Mod mod)
        {
            if (initialized) return;
            initialized = true;
            try
            {
                ModPath = Path.GetFullPath(mod.Path);
                Harmony = new Harmony(HarmonyId);
                modules.AddRange(new Module[]
                {
                    new Home(), new Tpa(), new BloodMoon(), new Welcome(), new ChestSort(), new Give(),
                    new Shutdown(), new ClaimGuard(), new ClaimLimit(), new BackpackGuard(), new PoiGuard(), new ChatDecor(), new Autolock(),
                    new VendingRental()
                });
                LoadCoreTexts(false);
                foreach (var module in modules)
                {
                    module.LoadSettings(false, true);
                    module.LoadTexts(false, true);
                }
                MainConfig.Load();
                XmlLayer.Init(mod);
                Language.Init();

                ModEvents.ChatMessage.RegisterHandler(OnChatCommand);
                ModEvents.GameStartDone.RegisterHandler(OnGameStartDone);
                ModEvents.WorldShuttingDown.RegisterHandler(OnWorldShuttingDown);
                ModEvents.GameShutdown.RegisterHandler(OnGameShutdown);
                ModEvents.GameUpdate.RegisterHandler(OnGameUpdate);
                ModEvents.PlayerJoinedGame.RegisterHandler(OnPlayerJoined);
                ModEvents.PlayerSpawnedInWorld.RegisterHandler(OnPlayerSpawned);
                ModEvents.PlayerDisconnected.RegisterHandler(OnPlayerDisconnected);

                Info($"{Version} loaded, {modules.Count} modules, config {MainConfig.FilePath}");
            }
            catch (Exception e)
            {
                Error("init failed: " + e);
            }
        }

        public static Module Find(string name)
        {
            return modules.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public static T Get<T>() where T : Module
        {
            return modules.OfType<T>().FirstOrDefault();
        }

        public static string SetEnabled(Module module, bool enabled)
        {
            MainConfig.SaveState(module.Name, enabled);
            if (enabled && !module.ConfigReady) return $"{module.Name} is saved as on, but it stays off: {module.ConfigProblem}";
            if (module.HasXml)
                return enabled == module.XmlApplied
                    ? $"{module.Name} is {OnOff(enabled)}"
                    : $"{module.Name} is saved as {OnOff(enabled)} and turns {OnOff(enabled)} after a server restart, as it changes the game XML";
            if (!Started || Settings == null) return $"{module.Name} is saved as {OnOff(enabled)} and will apply when the world starts";
            if (enabled)
            {
                if (module.Ready) return $"{module.Name} is already on";
                if (module.Enabled) module.Stop();
                if (!module.Start())
                {
                    RebuildCommands();
                    return $"{module.Name} could not be turned on, see the server log";
                }
            }
            else if (module.Enabled)
            {
                module.Stop();
            }
            RebuildCommands();
            Info($"{module.Name} {OnOff(enabled)}");
            return $"{module.Name} is {OnOff(enabled)}";
        }

        public static void ReloadAll()
        {
            if (MainConfig.Load()) Language.Configure();
            LoadCoreTexts(true);
            foreach (var module in modules)
            {
                module.LoadSettings(true, true);
                module.LoadTexts(true, true);
            }
            ApplyStates(false);
            Info("all files reloaded by the console");
        }

        public static string OnOff(bool value)
        {
            return value ? "on" : "off";
        }

        internal static void RequestDisable(Module module)
        {
            faulted.Enqueue(module);
        }

        internal static void Run(Module module, string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                if (module != null) module.Fault(e, what);
                else Error($"{what} failed: {e}");
            }
        }

        internal static void RebuildCommands()
        {
            commands.Clear();
            if (Settings == null) return;
            var list = new ChatCommands(commands);
            Language.RegisterCommands(list);
            foreach (var module in modules)
            {
                if (!module.Ready) continue;
                list.Owner = module;
                Run(module, "commands", () => module.RegisterCommands(list));
            }
        }

        public static void Info(string message)
        {
            Log.Out(LogTag + message);
        }

        public static void Warn(string message)
        {
            Log.Warning(LogTag + message);
        }

        public static void Error(string message)
        {
            Log.Error(LogTag + message);
        }

        private static void LoadCoreTexts(bool reload)
        {
            coreTextsStamp = FileStamp.Of(Path.Combine(LangDir, CoreTexts + ".csv"));
            var table = LangTable.Load(CoreTexts);
            if (table.Problem == null)
            {
                Lang = table;
                if (reload) Info(table.Source + " reloaded");
                return;
            }
            Warn(table.Problem);
            if (Lang == null) Lang = table;
            else Warn($"{table.Source} is not applied, the previous texts stay");
        }

        private static void ApplyStates(bool quiet)
        {
            if (!Started) return;
            foreach (var module in modules)
            {
                var state = MainConfig.State(module);
                var wanted = module.HasXml ? module.XmlApplied : Settings != null && state == true;
                if (Settings != null && state == null) Warn($"{module.Name} is not listed in {MainConfig.FileName} and stays off");
                else if (module.HasXml && !quiet && Settings != null && state != module.XmlApplied)
                    Warn($"{module.Name} is {OnOff(state == true)} in {MainConfig.FileName} and turns {OnOff(state == true)} after a server restart, as it changes the game XML");
                if (wanted && !module.Enabled)
                {
                    if (!module.ConfigReady)
                    {
                        Warn($"{module.Name} is on in {MainConfig.FileName} but stays off: {module.ConfigProblem}");
                        continue;
                    }
                    if (module.Start() && !quiet) Info(module.Name + " on");
                }
                else if (!wanted && module.Enabled)
                {
                    module.Stop();
                    if (!quiet) Info(module.Name + " off");
                }
            }
            RebuildCommands();
        }

        private static void Stop()
        {
            if (!Started) return;
            Started = false;
            foreach (var module in modules)
                if (module.Enabled) module.Stop();
            commands.Clear();
            clock.Stop();
        }

        private static void Watch()
        {
            var apply = false;
            var rebuild = false;
            if (MainConfig.Changed())
            {
                if (MainConfig.Load())
                {
                    Info(MainConfig.FileName + " reloaded");
                    Language.Configure();
                }
                apply = true;
            }
            if (coreTextsStamp.Changed(Path.Combine(LangDir, CoreTexts + ".csv"))) LoadCoreTexts(true);
            foreach (var module in modules)
            {
                var wasReady = module.ConfigReady;
                if (module.SettingsChanged())
                {
                    module.LoadSettings(true, false);
                    rebuild = true;
                }
                if (module.TextsChanged()) module.LoadTexts(true, false);
                if (module.ConfigReady != wasReady) apply = true;
            }
            if (apply) ApplyStates(false);
            else if (rebuild) RebuildCommands();
        }

        private static void OnGameStartDone(ref ModEvents.SGameStartDoneData _data)
        {
            try
            {
                WorldPath = Path.Combine(GameIO.GetSaveGameDir(), "ServerMultipass");
                Started = true;
                if (!lateChatRegistered)
                {
                    lateChatRegistered = true;
                    ModEvents.ChatMessage.RegisterHandler(OnChatLate);
                }
                ApplyStates(true);
                clock.Restart();
                nextSecond = 0L;
                nextWatch = WatchMs;
                var on = modules.Where(m => m.Enabled).Select(m => m.Name).ToList();
                Info("Leeloo Dallas Multipass. On: " + (on.Count == 0 ? "nothing" : string.Join(", ", on)));
            }
            catch (Exception e)
            {
                Error("start failed: " + e);
            }
        }

        private static void OnWorldShuttingDown(ref ModEvents.SWorldShuttingDownData _data)
        {
            Stop();
        }

        private static void OnGameShutdown(ref ModEvents.SGameShutdownData _data)
        {
            Stop();
            Language.Close();
        }

        private static void OnGameUpdate(ref ModEvents.SGameUpdateData _data)
        {
            if (!Started) return;
            while (faulted.Count > 0)
            {
                var module = faulted.Dequeue();
                if (!module.Enabled) continue;
                module.Stop();
                RebuildCommands();
                Error($"{module.Name} was turned off after {Module.MaxErrorsPerMinute} errors in a minute. Fix the cause and run 'mp enable {module.Name}'");
            }
            var now = clock.ElapsedMilliseconds;
            if (now >= nextWatch)
            {
                nextWatch = now + WatchMs;
                Watch();
            }
            if (now < nextSecond) return;
            nextSecond = now + SecondMs;
            var tick = Tick.Capture();
            if (tick == null) return;
            foreach (var module in modules)
                if (module.Ready) Run(module, "tick", () => module.OnSecond(tick));
        }

        private static ModEvents.EModEventResult OnChatCommand(ref ModEvents.SChatMessageData _data)
        {
            var client = _data.ClientInfo;
            var prefix = Settings?.ChatPrefix;
            if (!Started || client == null || string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(_data.Message))
                return ModEvents.EModEventResult.Continue;
            var text = _data.Message.Trim();
            if (text.Length <= prefix.Length || !text.StartsWith(prefix, StringComparison.Ordinal)) return ModEvents.EModEventResult.Continue;
            var parts = text.Substring(prefix.Length).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !commands.TryGetValue(parts[0], out var command)) return ModEvents.EModEventResult.Continue;
            if (!Players.MayUse(client, command))
            {
                Chat.Send(client, Lang.Format(Language.Of(client), "NoPermission"));
                return ModEvents.EModEventResult.StopHandlersAndVanilla;
            }
            var context = new ChatContext(client, command.Name, parts.Skip(1).ToArray());
            Run(command.Owner, prefix + command.Name, () => command.Handler(context));
            return ModEvents.EModEventResult.StopHandlersAndVanilla;
        }

        private static ModEvents.EModEventResult OnChatLate(ref ModEvents.SChatMessageData _data)
        {
            if (!Started) return ModEvents.EModEventResult.Continue;
            foreach (var module in modules)
            {
                if (!module.Ready || !module.HandlesChat) continue;
                try
                {
                    var result = module.OnChatMessage(ref _data);
                    if (result != ModEvents.EModEventResult.Continue) return result;
                }
                catch (Exception e)
                {
                    module.Fault(e, "chat");
                }
            }
            return ModEvents.EModEventResult.Continue;
        }

        private static void OnPlayerJoined(ref ModEvents.SPlayerJoinedGameData _data)
        {
            var client = _data.ClientInfo;
            if (client == null) return;
            try
            {
                Language.Detect(client);
            }
            catch (Exception e)
            {
                Warn("language detection failed: " + e.Message);
            }
            if (!Started) return;
            foreach (var module in modules)
                if (module.Ready) Run(module, "player join", () => module.OnPlayerJoined(client));
        }

        private static void OnPlayerSpawned(ref ModEvents.SPlayerSpawnedInWorldData _data)
        {
            var client = _data.ClientInfo;
            var reason = _data.RespawnType;
            if (!Started || client == null) return;
            foreach (var module in modules)
                if (module.Ready) Run(module, "player spawn", () => module.OnPlayerSpawned(client, reason));
        }

        private static void OnPlayerDisconnected(ref ModEvents.SPlayerDisconnectedData _data)
        {
            var client = _data.ClientInfo;
            if (client == null) return;
            if (Started)
            {
                foreach (var module in modules)
                    if (module.Ready) Run(module, "player leave", () => module.OnPlayerDisconnected(client));
            }
            Language.Forget(client);
        }
    }
}
