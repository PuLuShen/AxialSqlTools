using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AxialSqlTools
{
    internal sealed class QueryTemplateItem
    {
        public string FullPath { get; set; }
        public string RelativePath { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public DateTime LastWriteTime { get; set; }
        public bool IsFavorite { get; set; }
        public DateTime? LastUsedUtc { get; set; }

        public string FavoriteGlyph => IsFavorite ? "★" : "☆";
        public string CategoryDisplay => string.IsNullOrEmpty(Category) ? "(Root)" : Category;
        public string LastWriteDisplay => LastWriteTime.ToString("g", CultureInfo.CurrentCulture);
        public string SearchText => (Name + " " + Category + " " + RelativePath).ToUpperInvariant();
    }

    internal sealed class QueryTemplateRecentItem
    {
        public string RelativePath { get; set; }
        public DateTime UsedUtc { get; set; }
    }

    internal sealed class QueryTemplateState
    {
        public List<string> Favorites { get; set; } = new List<string>();
        public List<QueryTemplateRecentItem> Recent { get; set; } = new List<QueryTemplateRecentItem>();
    }

    internal sealed class QueryTemplateMigrationResult
    {
        public string SourceFolder { get; set; }
        public string DestinationFolder { get; set; }
        public int CopiedCount { get; set; }
    }

    /// <summary>
    /// Provides a file-system-first query-template catalog. SQL files remain the
    /// source of truth; the registry contains only the selected root and small UI
    /// conveniences such as relative favorite/recent paths.
    /// </summary>
    internal sealed class QueryTemplateLibrary : IDisposable
    {
        private const int RecentLimit = 30;
        private static readonly QueryTemplateLibrary instance = new QueryTemplateLibrary();
        private readonly object syncRoot = new object();
        private readonly NaturalPathComparer pathComparer = new NaturalPathComparer();
        private List<QueryTemplateItem> items = new List<QueryTemplateItem>();
        private QueryTemplateState state;
        private FileSystemWatcher watcher;
        private Timer refreshTimer;
        private bool disposed;

        private QueryTemplateLibrary()
        {
            state = LoadState();
        }

        public static QueryTemplateLibrary Instance => instance;

        public event EventHandler Changed;
        public event EventHandler CatalogChanged;

        public string RootFolder => SettingsManager.GetTemplatesFolder();

        public string LastError { get; private set; }

        public void Initialize()
        {
            ConfigureWatcher();
            Refresh();
        }

        public IReadOnlyList<QueryTemplateItem> Snapshot()
        {
            lock (syncRoot)
            {
                return items.Select(Clone).ToList();
            }
        }

        public bool TrySetRoot(string folder, bool createIfMissing, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(folder))
                    throw new ArgumentException("Select a templates folder.");

                string fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder.Trim()));
                if (!Directory.Exists(fullPath))
                {
                    if (!createIfMissing)
                        throw new DirectoryNotFoundException("The selected templates folder does not exist.");
                    Directory.CreateDirectory(fullPath);
                }

                // Prove that the directory can be enumerated before replacing a
                // known-good setting. Enumeration does not modify user files.
                Directory.EnumerateFileSystemEntries(fullPath).Take(1).ToList();
                if (!SettingsManager.SaveTemplatesFolder(fullPath))
                    throw new InvalidOperationException("The templates folder setting could not be saved.");

                ConfigureWatcher();
                Refresh();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public bool TryMigrateTo(string destinationFolder, out QueryTemplateMigrationResult result, out string error)
        {
            result = null;
            error = null;
            try
            {
                string source = Path.GetFullPath(RootFolder);
                if (!Directory.Exists(source))
                    throw new DirectoryNotFoundException("The current templates folder is unavailable: " + source);
                Refresh();
                if (!string.IsNullOrWhiteSpace(LastError))
                    throw new IOException("The current templates folder could not be read completely: " + LastError);
                if (string.IsNullOrWhiteSpace(destinationFolder))
                    throw new ArgumentException("Select a destination folder.");

                string destination = Path.GetFullPath(Environment.ExpandEnvironmentVariables(destinationFolder.Trim()));
                string sourceWithSeparator = AppendDirectorySeparator(source);
                string destinationWithSeparator = AppendDirectorySeparator(destination);
                if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The destination folder is the current templates folder.");
                if (destinationWithSeparator.StartsWith(sourceWithSeparator, StringComparison.OrdinalIgnoreCase)
                    || sourceWithSeparator.StartsWith(destinationWithSeparator, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Choose a destination outside the current templates folder tree.");

                List<QueryTemplateItem> templates = Snapshot().ToList();
                var conflicts = new List<string>();
                foreach (QueryTemplateItem template in templates)
                {
                    string target = Path.GetFullPath(Path.Combine(destination, template.RelativePath));
                    if (!target.StartsWith(destinationWithSeparator, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("A template has an invalid relative path: " + template.RelativePath);
                    if (File.Exists(target) && !FilesEqual(template.FullPath, target)) conflicts.Add(template.RelativePath);
                }
                if (conflicts.Count > 0)
                {
                    string preview = string.Join(", ", conflicts.Take(5));
                    if (conflicts.Count > 5) preview += " ...";
                    throw new IOException("The destination already contains " + conflicts.Count + " template(s): " + preview);
                }

                Directory.CreateDirectory(destination);
                int copied = 0;
                foreach (QueryTemplateItem template in templates)
                {
                    string target = Path.GetFullPath(Path.Combine(destination, template.RelativePath));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (!File.Exists(target)) File.Copy(template.FullPath, target, false);
                    copied++;
                }

                if (!TrySetRoot(destination, true, out string settingError))
                    throw new InvalidOperationException("Templates were copied, but the folder setting could not be switched: " + settingError);

                result = new QueryTemplateMigrationResult
                {
                    SourceFolder = source,
                    DestinationFolder = destination,
                    CopiedCount = copied
                };
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public void Refresh()
        {
            string root = RootFolder;
            var discovered = new List<QueryTemplateItem>();
            string scanError = null;

            try
            {
                if (!Directory.Exists(root))
                    throw new DirectoryNotFoundException("Templates folder is currently unavailable: " + root);

                EnsureWatcher(root);
                ScanFolder(root, root, discovered, ref scanError);
            }
            catch (Exception ex)
            {
                scanError = ex.Message;
            }

            QueryTemplateState stateSnapshot;
            lock (syncRoot)
            {
                stateSnapshot = new QueryTemplateState
                {
                    Favorites = (state.Favorites ?? new List<string>()).ToList(),
                    Recent = (state.Recent ?? new List<QueryTemplateRecentItem>())
                        .Where(x => x != null)
                        .Select(x => new QueryTemplateRecentItem { RelativePath = x.RelativePath, UsedUtc = x.UsedUtc })
                        .ToList()
                };
            }

            var favorites = new HashSet<string>(stateSnapshot.Favorites ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var recent = (stateSnapshot.Recent ?? new List<QueryTemplateRecentItem>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.RelativePath))
                .GroupBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Max(y => y.UsedUtc), StringComparer.OrdinalIgnoreCase);

            foreach (QueryTemplateItem item in discovered)
            {
                item.IsFavorite = favorites.Contains(item.RelativePath);
                if (recent.TryGetValue(item.RelativePath, out DateTime usedUtc)) item.LastUsedUtc = usedUtc;
            }

            discovered.Sort((left, right) => pathComparer.Compare(left.RelativePath, right.RelativePath));
            lock (syncRoot)
            {
                items = discovered;
                LastError = scanError;
            }
            RaiseCatalogChanged();
            RaiseChanged();
        }

        public string ReadContent(QueryTemplateItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            return ReadContent(item.FullPath);
        }

        public string ReadContent(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                throw new FileNotFoundException("The query template no longer exists.", fullPath);

            using (var reader = new StreamReader(fullPath, Encoding.UTF8, true))
                return reader.ReadToEnd();
        }

        public bool ToggleFavorite(QueryTemplateItem item)
        {
            if (item == null) return false;
            bool isFavorite;
            lock (syncRoot)
            {
                var favorites = state.Favorites ?? (state.Favorites = new List<string>());
                int index = favorites.FindIndex(x => string.Equals(x, item.RelativePath, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    favorites.RemoveAt(index);
                    isFavorite = false;
                }
                else
                {
                    favorites.Add(item.RelativePath);
                    isFavorite = true;
                }
                foreach (QueryTemplateItem existing in items.Where(x => string.Equals(x.RelativePath, item.RelativePath, StringComparison.OrdinalIgnoreCase)))
                    existing.IsFavorite = isFavorite;
                SaveStateUnsafe();
            }
            RaiseChanged();
            return isFavorite;
        }

        public void RecordUsed(string fullPath)
        {
            string relative = TryGetRelativePath(fullPath);
            if (string.IsNullOrEmpty(relative)) return;

            lock (syncRoot)
            {
                var recent = state.Recent ?? (state.Recent = new List<QueryTemplateRecentItem>());
                recent.RemoveAll(x => x == null || string.Equals(x.RelativePath, relative, StringComparison.OrdinalIgnoreCase));
                recent.Insert(0, new QueryTemplateRecentItem { RelativePath = relative, UsedUtc = DateTime.UtcNow });
                if (recent.Count > RecentLimit) recent.RemoveRange(RecentLimit, recent.Count - RecentLimit);
                foreach (QueryTemplateItem existing in items.Where(x => string.Equals(x.RelativePath, relative, StringComparison.OrdinalIgnoreCase)))
                    existing.LastUsedUtc = recent[0].UsedUtc;
                SaveStateUnsafe();
            }
            RaiseChanged();
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                if (disposed) return;
                disposed = true;
                DisposeWatcherUnsafe();
                refreshTimer?.Dispose();
                refreshTimer = null;
            }
        }

        private void ConfigureWatcher()
        {
            lock (syncRoot)
            {
                if (disposed) return;
                DisposeWatcherUnsafe();
                string root = RootFolder;
                if (!Directory.Exists(root)) return;

                watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                watcher.Changed += WatcherChanged;
                watcher.Created += WatcherChanged;
                watcher.Deleted += WatcherChanged;
                watcher.Renamed += WatcherChanged;
                watcher.Error += WatcherError;
                watcher.EnableRaisingEvents = true;
            }
        }

        private void EnsureWatcher(string root)
        {
            lock (syncRoot)
            {
                if (disposed) return;
                if (watcher != null && string.Equals(watcher.Path, root, StringComparison.OrdinalIgnoreCase)) return;
            }
            ConfigureWatcher();
        }

        private void WatcherChanged(object sender, FileSystemEventArgs e)
        {
            if (!IsSqlOrDirectoryChange(e.FullPath)) return;
            ScheduleRefresh();
        }

        private void WatcherError(object sender, ErrorEventArgs e)
        {
            ScheduleRefresh();
        }

        private void ScheduleRefresh()
        {
            lock (syncRoot)
            {
                if (disposed) return;
                if (refreshTimer == null)
                    refreshTimer = new Timer(_ => Refresh(), null, 450, Timeout.Infinite);
                else
                    refreshTimer.Change(450, Timeout.Infinite);
            }
        }

        private static bool IsSqlOrDirectoryChange(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            if (Directory.Exists(path)) return true;
            string extension = Path.GetExtension(path);
            if (File.Exists(path)) return string.Equals(extension, ".sql", StringComparison.OrdinalIgnoreCase);
            // Deleted/renamed paths can no longer be inspected. Refreshing is
            // inexpensive compared with leaving a stale template or category.
            return true;
        }

        private static void ScanFolder(string root, string folder, List<QueryTemplateItem> result, ref string firstError)
        {
            IEnumerable<string> files;
            IEnumerable<string> directories;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.sql", SearchOption.TopDirectoryOnly).ToList();
                directories = Directory.EnumerateDirectories(folder, "*", SearchOption.TopDirectoryOnly).ToList();
            }
            catch (Exception ex)
            {
                if (firstError == null) firstError = folder + ": " + ex.Message;
                return;
            }

            foreach (string file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    if ((info.Attributes & FileAttributes.Hidden) != 0 || (info.Attributes & FileAttributes.Temporary) != 0) continue;
                    string relative = MakeRelativePath(root, info.FullName);
                    result.Add(new QueryTemplateItem
                    {
                        FullPath = info.FullName,
                        RelativePath = relative,
                        Name = Path.GetFileNameWithoutExtension(info.Name),
                        Category = Path.GetDirectoryName(relative) ?? string.Empty,
                        LastWriteTime = info.LastWriteTime
                    });
                }
                catch (Exception ex)
                {
                    if (firstError == null) firstError = file + ": " + ex.Message;
                }
            }

            foreach (string directory in directories)
            {
                try
                {
                    var info = new DirectoryInfo(directory);
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.ReparsePoint)) != 0
                        || string.Equals(info.Name, ".git", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(info.Name, ".vs", StringComparison.OrdinalIgnoreCase)) continue;
                    ScanFolder(root, directory, result, ref firstError);
                }
                catch (Exception ex)
                {
                    if (firstError == null) firstError = directory + ": " + ex.Message;
                }
            }
        }

        private string TryGetRelativePath(string fullPath)
        {
            try
            {
                string root = AppendDirectorySeparator(Path.GetFullPath(RootFolder));
                string candidate = Path.GetFullPath(fullPath);
                if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
                return NormalizeRelative(candidate.Substring(root.Length));
            }
            catch
            {
                return null;
            }
        }

        private static string MakeRelativePath(string root, string fullPath)
        {
            string rootWithSeparator = AppendDirectorySeparator(Path.GetFullPath(root));
            string candidate = Path.GetFullPath(fullPath);
            return NormalizeRelative(candidate.Substring(rootWithSeparator.Length));
        }

        private static string AppendDirectorySeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static string NormalizeRelative(string value)
        {
            return (value ?? string.Empty).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        }

        private static bool FilesEqual(string leftPath, string rightPath)
        {
            var leftInfo = new FileInfo(leftPath);
            var rightInfo = new FileInfo(rightPath);
            if (leftInfo.Length != rightInfo.Length) return false;

            using (FileStream left = File.OpenRead(leftPath))
            using (FileStream right = File.OpenRead(rightPath))
            {
                var leftBuffer = new byte[81920];
                var rightBuffer = new byte[81920];
                while (true)
                {
                    int leftRead = left.Read(leftBuffer, 0, leftBuffer.Length);
                    int rightRead = right.Read(rightBuffer, 0, rightBuffer.Length);
                    if (leftRead != rightRead) return false;
                    if (leftRead == 0) return true;
                    for (int i = 0; i < leftRead; i++)
                        if (leftBuffer[i] != rightBuffer[i]) return false;
                }
            }
        }

        private static QueryTemplateItem Clone(QueryTemplateItem item)
        {
            return new QueryTemplateItem
            {
                FullPath = item.FullPath,
                RelativePath = item.RelativePath,
                Name = item.Name,
                Category = item.Category,
                LastWriteTime = item.LastWriteTime,
                IsFavorite = item.IsFavorite,
                LastUsedUtc = item.LastUsedUtc
            };
        }

        private static QueryTemplateState LoadState()
        {
            try
            {
                string json = SettingsManager.GetQueryTemplateState();
                return string.IsNullOrWhiteSpace(json)
                    ? new QueryTemplateState()
                    : JsonConvert.DeserializeObject<QueryTemplateState>(json) ?? new QueryTemplateState();
            }
            catch
            {
                return new QueryTemplateState();
            }
        }

        private void SaveStateUnsafe()
        {
            SettingsManager.SaveQueryTemplateState(JsonConvert.SerializeObject(state));
        }

        private void DisposeWatcherUnsafe()
        {
            if (watcher == null) return;
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= WatcherChanged;
            watcher.Created -= WatcherChanged;
            watcher.Deleted -= WatcherChanged;
            watcher.Renamed -= WatcherChanged;
            watcher.Error -= WatcherError;
            watcher.Dispose();
            watcher = null;
        }

        private void RaiseChanged()
        {
            EventHandler handler = Changed;
            handler?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseCatalogChanged()
        {
            EventHandler handler = CatalogChanged;
            handler?.Invoke(this, EventArgs.Empty);
        }

        private sealed class NaturalPathComparer : IComparer<string>
        {
            private static readonly Regex Tokens = new Regex("(\\d+)", RegexOptions.Compiled);

            public int Compare(string x, string y)
            {
                string[] left = Tokens.Split(x ?? string.Empty);
                string[] right = Tokens.Split(y ?? string.Empty);
                for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
                {
                    int comparison;
                    if (long.TryParse(left[i], out long leftNumber) && long.TryParse(right[i], out long rightNumber))
                        comparison = leftNumber.CompareTo(rightNumber);
                    else
                        comparison = StringComparer.CurrentCultureIgnoreCase.Compare(left[i], right[i]);
                    if (comparison != 0) return comparison;
                }
                return left.Length.CompareTo(right.Length);
            }
        }
    }
}
