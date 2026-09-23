using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace ServerMultipass
{
    internal struct FileStamp
    {
        private bool exists;
        private long length;
        private long ticks;

        public static FileStamp Of(string path)
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new FileStamp { exists = true, length = info.Length, ticks = info.LastWriteTimeUtc.Ticks }
                : default;
        }

        public bool Changed(string path)
        {
            var now = Of(path);
            return now.exists != exists || now.length != length || now.ticks != ticks;
        }
    }

    internal static class TextFile
    {
        public static readonly Encoding Utf8 = new UTF8Encoding(false);

        public static void Write(string path, string text)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, text, Utf8);
            Commit(temp, path);
        }

        public static void Commit(string temp, string path)
        {
            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }
            try
            {
                File.Replace(temp, path, null);
            }
            catch (Exception)
            {
                File.Copy(temp, path, true);
                File.Delete(temp);
            }
        }
    }

    internal static class JsonStore
    {
        public static T Load<T>(string path) where T : class
        {
            return File.Exists(path) ? JsonConvert.DeserializeObject<T>(File.ReadAllText(path, TextFile.Utf8)) : null;
        }

        public static void Save(string path, object value)
        {
            TextFile.Write(path, JsonConvert.SerializeObject(value, Formatting.Indented));
        }
    }
}
