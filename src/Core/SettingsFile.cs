using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;

namespace ServerMultipass
{
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class RangeAttribute : Attribute
    {
        public RangeAttribute(double min, double max = double.MaxValue)
        {
            Min = min;
            Max = max;
        }

        public double Min { get; }
        public double Max { get; }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class ElementAttribute : Attribute
    {
        public ElementAttribute(string name)
        {
            Name = name;
        }

        public string Name { get; }
    }

    internal sealed class SettingsResult
    {
        public object Value;
        public XmlElement Root;
        public readonly List<string> Problems = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public bool Ok => Problems.Count == 0;
    }

    internal static class SettingsFile
    {
        private const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance;

        public static SettingsResult Load(Type type, string path, string source, ICollection<string> ignored = null)
        {
            if (!File.Exists(path))
            {
                var missing = new SettingsResult();
                missing.Problems.Add(source + " not found");
                return missing;
            }
            try
            {
                var document = new XmlDocument();
                document.Load(path);
                return Read(type, document.DocumentElement, source, ignored);
            }
            catch (Exception e)
            {
                var broken = new SettingsResult();
                broken.Problems.Add($"{source}: {e.Message}");
                return broken;
            }
        }

        public static SettingsResult Read(Type type, XmlElement root, string source, ICollection<string> ignored = null)
        {
            var result = new SettingsResult { Root = root };
            if (root == null)
            {
                result.Problems.Add(source + ": the file has no root element");
                return result;
            }
            var value = Activator.CreateInstance(type);
            var fields = new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase);
            var lists = new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in type.GetFields(Public))
            {
                var element = field.GetCustomAttribute<ElementAttribute>();
                if (element == null)
                {
                    fields[field.Name] = field;
                    continue;
                }
                field.SetValue(value, Activator.CreateInstance(field.FieldType));
                lists[element.Name] = field;
            }
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode node in root.ChildNodes)
            {
                if (!(node is XmlElement element)) continue;
                if (element.Name.Equals("property", StringComparison.OrdinalIgnoreCase))
                {
                    ReadProperty(value, fields, found, element, source, result);
                    continue;
                }
                if (lists.TryGetValue(element.Name, out var list))
                {
                    var item = ReadItem(list.FieldType.GetGenericArguments()[0], element, source, result);
                    ((IList)list.GetValue(value)).Add(item);
                    continue;
                }
                if (ignored != null && ignored.Contains(element.Name)) continue;
                result.Warnings.Add($"{source}: unknown element <{element.Name}> is ignored");
            }
            foreach (var name in fields.Keys.Where(n => !found.Contains(n)))
                result.Problems.Add($"{source}: property {name} is missing");
            result.Value = value;
            return result;
        }

        public static bool TryParseBool(string raw, out bool value)
        {
            switch ((raw ?? "").Trim().ToLowerInvariant())
            {
                case "true":
                case "1":
                case "yes":
                case "on":
                    value = true;
                    return true;
                case "false":
                case "0":
                case "no":
                case "off":
                    value = false;
                    return true;
                default:
                    value = false;
                    return false;
            }
        }

        private static void ReadProperty(object target, Dictionary<string, FieldInfo> fields, HashSet<string> found, XmlElement element,
            string source, SettingsResult result)
        {
            var name = element.GetAttribute("name");
            if (!fields.TryGetValue(name, out var field))
            {
                result.Warnings.Add($"{source}: unknown property '{name}' is ignored");
                return;
            }
            found.Add(field.Name);
            if (!element.HasAttribute("value"))
            {
                result.Problems.Add($"{source}: property {field.Name} has no value attribute");
                return;
            }
            var raw = element.GetAttribute("value");
            if (!TryParse(field.FieldType, raw, out var value))
            {
                result.Problems.Add($"{source}: '{raw}' is not a valid value for {field.Name}");
                return;
            }
            var range = field.GetCustomAttribute<RangeAttribute>();
            if (range != null && !InRange(value, range))
            {
                result.Problems.Add($"{source}: {field.Name} must be {Describe(range)}, not '{raw}'");
                return;
            }
            field.SetValue(target, value);
        }

        private static object ReadItem(Type type, XmlElement element, string source, SettingsResult result)
        {
            var item = Activator.CreateInstance(type);
            var fields = type.GetFields(Public).ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            foreach (XmlAttribute attribute in element.Attributes)
            {
                if (!fields.TryGetValue(attribute.Name, out var field))
                {
                    result.Warnings.Add($"{source}: <{element.Name}> has an unknown attribute '{attribute.Name}'");
                    continue;
                }
                if (!TryParse(field.FieldType, attribute.Value, out var value))
                {
                    result.Problems.Add($"{source}: '{attribute.Value}' is not a valid value for {attribute.Name} in <{element.Name}>");
                    continue;
                }
                field.SetValue(item, value);
            }
            return item;
        }

        private static bool TryParse(Type type, string raw, out object value)
        {
            value = null;
            raw ??= "";
            var text = raw.Trim();
            if (type == typeof(string))
            {
                value = raw;
                return true;
            }
            if (type == typeof(bool))
            {
                var ok = TryParseBool(text, out var b);
                value = b;
                return ok;
            }
            if (type == typeof(int))
            {
                var ok = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i);
                value = i;
                return ok;
            }
            if (type == typeof(double))
            {
                var ok = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d);
                value = d;
                return ok;
            }
            var parts = text.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            if (type == typeof(string[]))
            {
                value = parts;
                return true;
            }
            if (type == typeof(int[]))
            {
                var numbers = new int[parts.Length];
                for (var i = 0; i < parts.Length; i++)
                    if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]))
                        return false;
                value = numbers;
                return true;
            }
            return false;
        }

        private static bool InRange(object value, RangeAttribute range)
        {
            switch (value)
            {
                case int i:
                    return i >= range.Min && i <= range.Max;
                case double d:
                    return d >= range.Min && d <= range.Max;
                case int[] numbers:
                    return numbers.All(n => n >= range.Min && n <= range.Max);
                default:
                    return true;
            }
        }

        private static string Describe(RangeAttribute range)
        {
            var min = range.Min.ToString(CultureInfo.InvariantCulture);
            return range.Max >= double.MaxValue ? $"at least {min}" : $"from {min} to {range.Max.ToString(CultureInfo.InvariantCulture)}";
        }
    }
}
