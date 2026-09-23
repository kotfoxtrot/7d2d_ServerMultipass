using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace ServerMultipass
{
    public sealed class MainSettings
    {
        public string ChatPrefix;
        public string DefaultLanguage;
        public bool GeoIp;
        public string GeoIpDatabase;
        public string[] CountryLanguages;
    }

    internal static class MainConfig
    {
        public const string FileName = "ServerMultipass.xml";

        private const string ModulesElement = "modules";

        private static Dictionary<string, bool> states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static FileStamp stamp;

        public static MainSettings Settings { get; private set; }
        public static string FilePath => Path.Combine(Multipass.ModPath, FileName);

        public static bool? State(Module module)
        {
            return states.TryGetValue(module.Name, out var enabled) ? enabled : (bool?)null;
        }

        public static bool Changed()
        {
            return stamp.Changed(FilePath);
        }

        public static bool Load()
        {
            stamp = FileStamp.Of(FilePath);
            var result = SettingsFile.Load(typeof(MainSettings), FilePath, FileName, new[] { ModulesElement });
            foreach (var warning in result.Warnings) Multipass.Warn(warning);
            if (!result.Ok)
            {
                foreach (var problem in result.Problems) Multipass.Warn(problem);
                Multipass.Warn(Settings == null
                    ? $"nothing runs until {FilePath} is in place and valid"
                    : $"{FileName} is not applied, the previous values stay");
                return false;
            }
            var loaded = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var modules = result.Root[ModulesElement];
            if (modules != null)
            {
                foreach (XmlNode node in modules.ChildNodes)
                {
                    if (!(node is XmlElement element)) continue;
                    var name = element.GetAttribute("name");
                    if (!element.Name.Equals("module", StringComparison.OrdinalIgnoreCase) || Multipass.Find(name) == null)
                    {
                        Multipass.Warn($"{FileName}: unknown module '{name}' is ignored");
                        continue;
                    }
                    if (!SettingsFile.TryParseBool(element.GetAttribute("enabled"), out var enabled))
                    {
                        Multipass.Warn($"{FileName}: module {name} needs enabled=\"true\" or enabled=\"false\"");
                        continue;
                    }
                    loaded[name] = enabled;
                }
            }
            Settings = (MainSettings)result.Value;
            states = loaded;
            return true;
        }

        public static void SaveState(string module, bool enabled)
        {
            if (!File.Exists(FilePath)) throw new FileNotFoundException(FileName + " not found", FilePath);
            var document = new XmlDocument { PreserveWhitespace = true };
            document.Load(FilePath);
            var root = document.DocumentElement ?? throw new FormatException(FileName + " has no root element");
            var modules = root[ModulesElement];
            if (modules == null)
            {
                modules = document.CreateElement(ModulesElement);
                modules.AppendChild(document.CreateWhitespace("\n  "));
                Insert(root, modules, "\n  ");
            }
            XmlElement target = null;
            foreach (XmlNode node in modules.ChildNodes)
            {
                if (node is XmlElement element && element.GetAttribute("name").Equals(module, StringComparison.OrdinalIgnoreCase))
                {
                    target = element;
                    break;
                }
            }
            if (target == null)
            {
                target = document.CreateElement("module");
                target.SetAttribute("name", module);
                Insert(modules, target, "\n    ");
            }
            target.SetAttribute("enabled", enabled ? "true" : "false");
            var temp = FilePath + ".tmp";
            using (var writer = XmlWriter.Create(temp, new XmlWriterSettings { Encoding = TextFile.Utf8, Indent = false }))
                document.Save(writer);
            TextFile.Commit(temp, FilePath);
            states[module] = enabled;
            stamp = FileStamp.Of(FilePath);
        }

        private static void Insert(XmlElement parent, XmlElement child, string indent)
        {
            var document = parent.OwnerDocument;
            if (parent.LastChild is XmlWhitespace last)
            {
                parent.InsertBefore(document.CreateWhitespace(indent), last);
                parent.InsertBefore(child, last);
                return;
            }
            parent.AppendChild(document.CreateWhitespace(indent));
            parent.AppendChild(child);
        }
    }
}
