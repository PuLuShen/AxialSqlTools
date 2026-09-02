using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace AxialSqlTools.Completion
{
    internal static class CompletionUsageStore
    {
        private static readonly object Gate = new object();
        private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxialSQL", "completion-usage.json");
        private static Dictionary<string, int> counts = Load();

        public static int GetScore(CompletionItem item)
        {
            if (item == null) return 0;
            lock (Gate) return counts.TryGetValue(Key(item), out int count) ? Math.Min(40, count * 2) : 0;
        }

        public static void Record(CompletionItem item)
        {
            if (item == null) return;
            Dictionary<string, int> snapshot;
            lock (Gate)
            {
                string key = Key(item);
                counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
                snapshot = new Dictionary<string, int>(counts, StringComparer.OrdinalIgnoreCase);
            }
            Task.Run(() => Save(snapshot));
        }

        private static string Key(CompletionItem item) => (item.ScopeKey ?? string.Empty) + "|" + item.Kind + "|" + item.DisplayText;
        private static Dictionary<string, int> Load()
        {
            try
            {
                if (File.Exists(FilePath)) return JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(FilePath))
                    ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        private static void Save(Dictionary<string, int> value)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(value));
            }
            catch { }
        }
    }
}
