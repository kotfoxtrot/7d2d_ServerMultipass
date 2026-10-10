using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;

namespace ServerMultipass
{
    public abstract class Module
    {
        public const int MaxErrorsPerMinute = 10;

        private readonly Queue<DateTime> recentErrors = new Queue<DateTime>();
        private FileStamp settingsStamp;
        private FileStamp textsStamp;
        private bool settingsLoaded;
        private bool textsLoaded;
        private string settingsProblem;
        private string textsProblem;

        public abstract string Name { get; }
        public bool Enabled { get; private set; }
        public bool Faulted { get; private set; }
        public bool Ready => Enabled && !Faulted;
        public int ErrorCount { get; private set; }
        public LangTable Lang { get; private set; }
        public bool ConfigReady => (SettingsType == null || settingsLoaded) && (!HasTexts || textsLoaded);

        public string ConfigProblem
        {
            get
            {
                if (SettingsType != null && !settingsLoaded) return settingsProblem;
                return HasTexts && !textsLoaded ? textsProblem : null;
            }
        }

        internal virtual Type SettingsType => null;
        internal virtual object SettingsObject { get => null; set { } }
        internal string SettingsSource => $"Settings/{Name}.xml";
        internal string SettingsPath => Path.Combine(Multipass.SettingsDir, Name + ".xml");
        internal string TextsPath => Path.Combine(Multipass.LangDir, Name + ".csv");
        internal string XmlPath => Path.Combine(Multipass.XmlDir, Name);
        internal bool XmlApplied { get; set; }

        protected virtual bool HasPatches => false;
        protected virtual bool HasTexts => true;
        protected internal virtual bool HasXml => false;
        protected internal virtual bool HandlesChat => false;
        protected internal virtual IEnumerable<string> ConsoleCommands => Enumerable.Empty<string>();

        protected virtual void OnEnable()
        {
        }

        protected virtual void OnDisable()
        {
        }

        protected virtual void OnSettingsChanged()
        {
        }

        protected internal virtual void RegisterCommands(ChatCommands commands)
        {
        }

        protected internal virtual void OnSecond(Tick tick)
        {
        }

        protected internal virtual void OnPlayerJoined(ClientInfo client)
        {
        }

        protected internal virtual void OnPlayerSpawned(ClientInfo client, RespawnType reason)
        {
        }

        protected internal virtual void OnPlayerDisconnected(ClientInfo client)
        {
        }

        protected internal virtual ModEvents.EModEventResult OnChatMessage(ref ModEvents.SChatMessageData data)
        {
            return ModEvents.EModEventResult.Continue;
        }

        public string Text(ClientInfo client, string key, params object[] args)
        {
            return Lang.Format(Language.Of(client), key, args);
        }

        public string Tagged(ClientInfo client, string key, params object[] args)
        {
            var language = Language.Of(client);
            var tag = Lang.Get(language, "Tag");
            var text = Lang.Format(language, key, args);
            return string.IsNullOrEmpty(tag) ? text : tag + " " + text;
        }

        public void Reply(ClientInfo client, string key, params object[] args)
        {
            if (client == null) return;
            Chat.Send(client, Tagged(client, key, args));
        }

        public void Broadcast(string key, params object[] args)
        {
            foreach (var client in Players.Online()) Reply(client, key, args);
        }

        protected void Info(string message)
        {
            Multipass.Info($"{Name}: {message}");
        }

        protected void Warn(string message)
        {
            Multipass.Warn($"{Name}: {message}");
        }

        protected T LoadWorld<T>(string file) where T : class
        {
            if (Multipass.WorldPath == null) return null;
            var path = Path.Combine(Multipass.WorldPath, file);
            try
            {
                return JsonStore.Load<T>(path);
            }
            catch (Exception e)
            {
                var broken = path + ".broken";
                File.Copy(path, broken, true);
                Warn($"{path} could not be read ({e.Message}), a copy was kept as {broken}, starting empty");
                return null;
            }
        }

        protected void SaveWorld(string file, object value)
        {
            if (Multipass.WorldPath == null) return;
            JsonStore.Save(Path.Combine(Multipass.WorldPath, file), value);
        }

        internal bool Start()
        {
            if (!ConfigReady) return false;
            Faulted = false;
            recentErrors.Clear();
            try
            {
                if (HasPatches) Multipass.Harmony.PatchCategory(typeof(Module).Assembly, Name);
                OnEnable();
                Enabled = true;
                return true;
            }
            catch (Exception e)
            {
                Multipass.Error($"{Name} could not be turned on: {e}");
                try
                {
                    OnDisable();
                }
                catch
                {
                }
                Unpatch();
                return false;
            }
        }

        internal void Stop()
        {
            if (!Enabled) return;
            Enabled = false;
            try
            {
                OnDisable();
            }
            catch (Exception e)
            {
                Multipass.Error($"{Name} failed while turning off: {e}");
            }
            Unpatch();
        }

        internal void Fault(Exception e, string what)
        {
            var root = e is System.Reflection.TargetInvocationException && e.InnerException != null ? e.InnerException : e;
            ErrorCount++;
            Multipass.Error($"{Name}: {what} failed: {root}");
            var now = DateTime.UtcNow;
            recentErrors.Enqueue(now);
            while (recentErrors.Count > 0 && (now - recentErrors.Peek()).TotalSeconds > 60) recentErrors.Dequeue();
            if (recentErrors.Count < MaxErrorsPerMinute || Faulted || HasXml) return;
            Faulted = true;
            Multipass.RequestDisable(this);
        }

        internal bool SettingsChanged()
        {
            return SettingsType != null && settingsStamp.Changed(SettingsPath);
        }

        internal bool TextsChanged()
        {
            return HasTexts && textsStamp.Changed(TextsPath);
        }

        internal void LoadSettings(bool reload, bool quiet)
        {
            if (SettingsType == null) return;
            settingsStamp = FileStamp.Of(SettingsPath);
            var result = SettingsFile.Load(SettingsType, SettingsPath, SettingsSource);
            foreach (var warning in result.Warnings) Multipass.Warn(warning);
            if (!result.Ok)
            {
                settingsProblem = result.Problems[0];
                foreach (var problem in result.Problems) Multipass.Warn(problem);
                if (settingsLoaded) Multipass.Warn($"{SettingsSource} is not applied, the previous values stay");
                return;
            }
            SettingsObject = result.Value;
            settingsLoaded = true;
            settingsProblem = null;
            if (!reload) return;
            if (!quiet) Multipass.Info(SettingsSource + " reloaded");
            if (HasXml) XmlLayer.CheckChanged(this);
            if (Enabled) Multipass.Run(this, "settings", OnSettingsChanged);
        }

        internal void LoadTexts(bool reload, bool quiet)
        {
            if (!HasTexts)
            {
                Lang ??= LangTable.Empty(Name);
                return;
            }
            textsStamp = FileStamp.Of(TextsPath);
            var table = LangTable.Load(Name);
            if (table.Problem != null)
            {
                textsProblem = table.Problem;
                Multipass.Warn(table.Problem);
                if (textsLoaded) Multipass.Warn($"{table.Source} is not applied, the previous texts stay");
                else Lang = table;
                return;
            }
            Lang = table;
            textsLoaded = true;
            textsProblem = null;
            if (reload && !quiet) Multipass.Info(table.Source + " reloaded");
        }

        internal int? HookCount()
        {
            if (!HasPatches) return null;
            var harmony = Multipass.Harmony;
            var count = 0;
            foreach (var original in harmony.GetPatchedMethods())
            {
                var info = Harmony.GetPatchInfo(original);
                if (info == null) continue;
                count += info.Prefixes.Concat(info.Postfixes)
                    .Count(p => p.owner == harmony.Id && Category(p.PatchMethod) == Name);
            }
            return count;
        }

        private static string Category(System.Reflection.MethodInfo method)
        {
            var data = method?.DeclaringType?.GetCustomAttributesData()
                .FirstOrDefault(a => a.AttributeType == typeof(HarmonyPatchCategory));
            return data == null || data.ConstructorArguments.Count == 0 ? null : data.ConstructorArguments[0].Value as string;
        }

        private void Unpatch()
        {
            if (!HasPatches) return;
            try
            {
                Multipass.Harmony.UnpatchCategory(typeof(Module).Assembly, Name);
            }
            catch (Exception e)
            {
                Multipass.Error($"{Name} patches could not be removed: {e}");
            }
        }
    }

    public abstract class Module<TSettings> : Module where TSettings : class
    {
        public TSettings Settings { get; private set; }

        internal override Type SettingsType => typeof(TSettings);

        internal override object SettingsObject
        {
            get => Settings;
            set => Settings = (TSettings)value;
        }
    }
}
