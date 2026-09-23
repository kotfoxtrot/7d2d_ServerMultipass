using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ServerMultipass
{
    public sealed class LangTable
    {
        private static readonly Dictionary<string, LangTable> active = new Dictionary<string, LangTable>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, Dictionary<string, string>> texts = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> languages = new List<string>();

        private LangTable(string name)
        {
            Name = name;
            FilePath = Path.Combine(Multipass.LangDir, name + ".csv");
        }

        public string Name { get; }
        public string FilePath { get; }
        public string Source => $"Lang/{Name}.csv";
        public string Problem { get; private set; }
        public IEnumerable<string> Languages => languages;

        public static IEnumerable<string> AllLanguages => active.Values
            .SelectMany(t => t.Languages)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase);

        public static LangTable Empty(string name)
        {
            return new LangTable(name);
        }

        public static LangTable Load(string name)
        {
            var table = new LangTable(name);
            if (!File.Exists(table.FilePath))
            {
                table.Problem = table.Source + " not found";
                return table;
            }
            try
            {
                table.Problem = table.Fill(Csv.Parse(File.ReadAllText(table.FilePath, TextFile.Utf8)));
            }
            catch (Exception e)
            {
                table.Problem = $"{table.Source}: {e.Message}";
            }
            if (table.Problem == null) active[name] = table;
            return table;
        }

        public string Get(string language, string key)
        {
            if (Find(language, key, out var text)) return text;
            if (Find(Language.Default, key, out text)) return text;
            foreach (var other in languages)
                if (Find(other, key, out text))
                    return text;
            return null;
        }

        public string Format(string language, string key, params object[] args)
        {
            var text = Get(language, key) ?? key;
            if (args == null || args.Length == 0) return text;
            try
            {
                return string.Format(CultureInfo.InvariantCulture, text, args);
            }
            catch (FormatException)
            {
                return text;
            }
        }

        private bool Find(string language, string key, out string text)
        {
            text = null;
            return language != null
                   && texts.TryGetValue(language, out var values)
                   && values.TryGetValue(key, out text)
                   && !string.IsNullOrEmpty(text);
        }

        private string Fill(List<List<string>> rows)
        {
            if (rows.Count == 0 || rows[0].Count == 0 || !rows[0][0].Trim().Equals("Key", StringComparison.OrdinalIgnoreCase))
                return $"{Source}: the first line must start with Key, for example Key,english,russian";
            var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
            for (var c = 1; c < header.Count; c++)
            {
                if (header[c].Length == 0 || texts.ContainsKey(header[c])) continue;
                texts[header[c]] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                languages.Add(header[c]);
            }
            for (var r = 1; r < rows.Count; r++)
            {
                var row = rows[r];
                var key = row.Count == 0 ? "" : row[0].Trim();
                if (key.Length == 0) continue;
                for (var c = 1; c < row.Count && c < header.Count; c++)
                    if (header[c].Length > 0 && !string.IsNullOrEmpty(row[c]))
                        texts[header[c]][key] = row[c];
            }
            return null;
        }
    }

    internal static class Csv
    {
        public static List<List<string>> Parse(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            var quoted = false;
            var started = false;

            void EndRow()
            {
                if (row.Count > 0 || started || field.Length > 0)
                {
                    row.Add(field.ToString());
                    rows.Add(row);
                }
                row = new List<string>();
                field.Clear();
                started = false;
            }

            for (var i = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quoted)
                {
                    if (c != '"')
                    {
                        field.Append(c);
                    }
                    else if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                    continue;
                }
                switch (c)
                {
                    case '"':
                        quoted = true;
                        started = true;
                        break;
                    case ',':
                        row.Add(field.ToString());
                        field.Clear();
                        started = true;
                        break;
                    case '\r':
                        break;
                    case '\n':
                        EndRow();
                        break;
                    default:
                        field.Append(c);
                        started = true;
                        break;
                }
            }
            EndRow();
            return rows;
        }
    }
}
