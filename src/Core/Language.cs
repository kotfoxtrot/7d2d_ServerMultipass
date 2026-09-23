using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ServerMultipass
{
    public static class Language
    {
        private const string PlayersFile = "Players.json";

        private static readonly Dictionary<string, string> codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "english", ["ru"] = "russian", ["de"] = "german", ["fr"] = "french", ["es"] = "spanish",
            ["it"] = "italian", ["pl"] = "polish", ["pt"] = "brazilian", ["br"] = "brazilian", ["tr"] = "turkish",
            ["ja"] = "japanese", ["jp"] = "japanese", ["ko"] = "koreana", ["zh"] = "schinese", ["cn"] = "schinese",
            ["uk"] = "ukrainian", ["ua"] = "ukrainian"
        };

        private static readonly Dictionary<string, string> detected = new Dictionary<string, string>(StringComparer.Ordinal);
        private static Dictionary<string, string> chosen = new Dictionary<string, string>(StringComparer.Ordinal);
        private static MaxMindDb geoIp;

        public static string Default
        {
            get
            {
                var name = Multipass.Settings?.DefaultLanguage?.Trim().ToLowerInvariant();
                return string.IsNullOrEmpty(name) ? null : name;
            }
        }

        public static IEnumerable<string> Available => LangTable.AllLanguages;

        internal static string GeoIpFile => geoIp == null ? null : Path.GetFileName(geoIp.Path);

        public static string Of(ClientInfo client)
        {
            var id = Players.Id(client);
            if (id != null)
            {
                if (chosen.TryGetValue(id, out var language)) return language;
                if (detected.TryGetValue(id, out language)) return language;
            }
            return Default;
        }

        public static string Resolve(string input)
        {
            var name = (input ?? "").Trim();
            if (codes.TryGetValue(name, out var byCode)) name = byCode;
            return Available.FirstOrDefault(l => l.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        internal static void Init()
        {
            try
            {
                var loaded = JsonStore.Load<Dictionary<string, string>>(Path.Combine(Multipass.DataDir, PlayersFile));
                if (loaded != null) chosen = new Dictionary<string, string>(loaded, StringComparer.Ordinal);
            }
            catch (Exception e)
            {
                Multipass.Warn($"Data/{PlayersFile} could not be read, saved languages are reset: {e.Message}");
            }
            Configure();
        }

        internal static void Configure()
        {
            var path = DatabasePath();
            if (geoIp != null && geoIp.Path == path) return;
            Close();
            if (path == null) return;
            try
            {
                geoIp = MaxMindDb.Open(path);
                Multipass.Info("GeoIP database: " + Path.GetFileName(path));
            }
            catch (Exception e)
            {
                Multipass.Warn($"GeoIP database {Path.GetFileName(path)} could not be opened: {e.Message}");
            }
        }

        internal static void Close()
        {
            geoIp?.Dispose();
            geoIp = null;
        }

        internal static void Detect(ClientInfo client)
        {
            var id = Players.Id(client);
            if (id == null) return;
            detected.Remove(id);
            if (geoIp == null || chosen.ContainsKey(id)) return;
            var country = geoIp.Country(client.ip);
            if (country == null) return;
            foreach (var pair in Multipass.Settings?.CountryLanguages ?? Array.Empty<string>())
            {
                var parts = pair.Split(':', '=');
                if (parts.Length != 2 || !parts[0].Trim().Equals(country, StringComparison.OrdinalIgnoreCase)) continue;
                var language = parts[1].Trim().ToLowerInvariant();
                if (language.Length > 0) detected[id] = language;
                return;
            }
        }

        internal static void Forget(ClientInfo client)
        {
            var id = Players.Id(client);
            if (id != null) detected.Remove(id);
        }

        internal static void RegisterCommands(ChatCommands commands)
        {
            commands.Add("lang", OnLang);
        }

        private static void OnLang(ChatContext context)
        {
            var client = context.Client;
            var id = Players.Id(client);
            var available = string.Join(", ", Available);
            if (context.Args.Length == 0 || id == null)
            {
                Chat.Send(client, Multipass.Lang.Format(Of(client), "LangCurrent", Of(client), available));
                return;
            }
            if (context.Args[0].Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                chosen.Remove(id);
                Save();
                Detect(client);
                Chat.Send(client, Multipass.Lang.Format(Of(client), "LangAuto", Of(client)));
                return;
            }
            var language = Resolve(context.Args[0]);
            if (language == null)
            {
                Chat.Send(client, Multipass.Lang.Format(Of(client), "LangUnknown", Chat.Escape(context.Args[0]), available));
                return;
            }
            chosen[id] = language.ToLowerInvariant();
            Save();
            Chat.Send(client, Multipass.Lang.Format(language, "LangSet", language));
        }

        private static void Save()
        {
            try
            {
                JsonStore.Save(Path.Combine(Multipass.DataDir, PlayersFile), chosen);
            }
            catch (Exception e)
            {
                Multipass.Warn($"Data/{PlayersFile} could not be saved: {e.Message}");
            }
        }

        private static string DatabasePath()
        {
            var settings = Multipass.Settings;
            if (settings == null || !settings.GeoIp) return null;
            var file = (settings.GeoIpDatabase ?? "").Trim();
            if (file.Length > 0)
            {
                var path = Path.Combine(Multipass.DataDir, file);
                if (File.Exists(path)) return path;
                Multipass.Warn($"GeoIP database was not found: {path}");
                return null;
            }
            var found = Directory.Exists(Multipass.DataDir)
                ? Directory.GetFiles(Multipass.DataDir)
                    .Where(f => f.EndsWith(".mmdb", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault()
                : null;
            if (found == null) Multipass.Info($"GeoIP is on but there is no .mmdb file in {Multipass.DataDir}, languages come from /lang and DefaultLanguage");
            return found;
        }
    }
}
