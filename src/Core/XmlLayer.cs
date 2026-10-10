using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace ServerMultipass
{
    internal static class XmlLayer
    {
        public const string FolderName = "Xml";
        public const string AlwaysFolder = "Always";

        private const string LocalizationFile = "Localization.csv";
        private const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

        private static readonly Regex Token = new Regex(@"\$\{(\w+)\}", RegexOptions.Compiled);
        private static readonly Dictionary<Module, Dictionary<string, string>> used = new Dictionary<Module, Dictionary<string, string>>();
        private static Mod owner;

        public static void Init(Mod mod)
        {
            owner = mod;
            foreach (var module in Multipass.Modules)
            {
                if (!module.HasXml) continue;
                module.XmlApplied = false;
                if (!Directory.Exists(module.XmlPath))
                {
                    Multipass.Warn($"{module.Name}: {FolderName}/{module.Name} not found, the module stays off");
                    continue;
                }
                LoadLocalization(module);
                if (Multipass.Settings == null || MainConfig.State(module) != true) continue;
                if (!module.ConfigReady)
                {
                    Multipass.Warn($"{module.Name} is on in {MainConfig.FileName} but stays off until a server restart: {module.ConfigProblem}");
                    continue;
                }
                module.XmlApplied = true;
            }
            var on = Multipass.Modules.Where(m => m.XmlApplied).Select(m => m.Name).ToList();
            Multipass.Info("game XML of modules: " + (on.Count == 0 ? "nothing" : string.Join(", ", on)));
            Multipass.Harmony.Patch(AccessTools.Method(typeof(XmlPatcher), nameof(XmlPatcher.LoadAndPatchConfig)),
                new HarmonyMethod(typeof(XmlLayer), nameof(Prefix)));
        }

        public static void CheckChanged(Module module)
        {
            var settings = module.SettingsObject;
            if (settings == null || !used.TryGetValue(module, out var values)) return;
            foreach (var pair in values)
            {
                var field = settings.GetType().GetField(pair.Key, Public);
                if (field == null || Format(field.GetValue(settings)) == pair.Value) continue;
                Multipass.Warn($"{module.SettingsSource}: {pair.Key} reaches the game XML after a server restart, until then it stays {pair.Value}");
            }
        }

        private static void Prefix(string _configName, ref Action<XmlFile> _callback)
        {
            var file = _configName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? _configName : _configName + ".xml";
            var next = _callback;
            _callback = xml =>
            {
                Apply(file, xml);
                next(xml);
            };
        }

        private static void Apply(string file, XmlFile target)
        {
            foreach (var module in Multipass.Modules)
            {
                if (!module.HasXml) continue;
                Patch(module, Path.Combine(module.XmlPath, AlwaysFolder, file), target);
                if (module.XmlApplied) Patch(module, Path.Combine(module.XmlPath, file), target);
            }
        }

        private static void Patch(Module module, string path, XmlFile target)
        {
            if (!File.Exists(path)) return;
            var source = Source(path);
            try
            {
                var text = File.ReadAllText(path, TextFile.Utf8).Replace("@modfolder:", $"@modfolder({owner.Name}):");
                text = Fill(module, text, source);
                if (text == null) return;
                var patch = new XmlFile(text, Path.GetDirectoryName(path), Path.GetFileName(path), true);
                if (XmlPatcher.PatchXml(target, patch.XmlDoc.Root, patch, owner)) Multipass.Info(source + " applied");
                else Multipass.Warn($"{source}: some changes did not apply, see the warnings above");
            }
            catch (Exception e)
            {
                Multipass.Error($"{source} could not be applied: {e.Message}");
            }
        }

        private static string Fill(Module module, string text, string source)
        {
            var settings = module.SettingsObject;
            string unknown = null;
            var result = Token.Replace(text, match =>
            {
                var field = settings?.GetType().GetField(match.Groups[1].Value, Public);
                if (field == null)
                {
                    unknown ??= match.Value;
                    return match.Value;
                }
                var value = Format(field.GetValue(settings));
                if (!used.TryGetValue(module, out var values)) used[module] = values = new Dictionary<string, string>();
                values[field.Name] = value;
                return value;
            });
            if (unknown == null) return result;
            Multipass.Error($"{source}: {unknown} is not a setting in {module.SettingsSource}, the file is not applied");
            return null;
        }

        private static string Format(object value)
        {
            switch (value)
            {
                case null:
                    return "";
                case bool flag:
                    return flag ? "true" : "false";
                case string[] list:
                    return SecurityElement.Escape(string.Join(",", list));
                case int[] numbers:
                    return string.Join(",", numbers.Select(n => n.ToString(CultureInfo.InvariantCulture)));
                case IFormattable number:
                    return number.ToString(null, CultureInfo.InvariantCulture);
                default:
                    return SecurityElement.Escape(value.ToString());
            }
        }

        private static void LoadLocalization(Module module)
        {
            var folder = Path.Combine(module.XmlPath, AlwaysFolder);
            if (!File.Exists(Path.Combine(folder, LocalizationFile))) return;
            try
            {
                Localization.LoadPatchDictionaries($"{owner.Name}/{module.Name}", folder, false);
            }
            catch (Exception e)
            {
                Multipass.Error($"{Source(Path.Combine(folder, LocalizationFile))} could not be loaded: {e.Message}");
            }
        }

        private static string Source(string path)
        {
            return path.Substring(Multipass.ModPath.Length).TrimStart('/', '\\').Replace('\\', '/');
        }
    }
}
