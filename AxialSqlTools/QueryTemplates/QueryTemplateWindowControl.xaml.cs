using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;

namespace AxialSqlTools
{
    public partial class QueryTemplateWindowControl : UserControl
    {
        private readonly ToolWindowThemeController themeController;
        private bool subscribed;

        public QueryTemplateWindowControl()
        {
            InitializeComponent();
            themeController = new ToolWindowThemeController(this, ApplyThemeBrushResources);
            TryLoadSqlHighlighting();
            SetActionState(null);
        }

        public void ActivateSearch()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }));
        }

        private void Control_Loaded(object sender, RoutedEventArgs e)
        {
            Subscribe();
            QueryTemplateLibrary.Instance.Refresh();
            RefreshView();
            ActivateSearch();
        }

        private void Control_Unloaded(object sender, RoutedEventArgs e)
        {
            Unsubscribe();
        }

        private void Subscribe()
        {
            if (subscribed) return;
            QueryTemplateLibrary.Instance.Changed += Library_Changed;
            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!subscribed) return;
            QueryTemplateLibrary.Instance.Changed -= Library_Changed;
            subscribed = false;
        }

        private void Library_Changed(object sender, EventArgs e)
        {
            if (Dispatcher.CheckAccess()) RefreshView();
            else Dispatcher.BeginInvoke(new Action(RefreshView));
        }

        private void RefreshView()
        {
            string selectedPath = SelectedTemplate?.FullPath;
            IEnumerable<QueryTemplateItem> query = QueryTemplateLibrary.Instance.Snapshot();
            string search = (SearchBox.Text ?? string.Empty).Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(search)) query = query.Where(x => x.SearchText.Contains(search));

            string scope = (ScopeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
            if (scope == "Favorites") query = query.Where(x => x.IsFavorite);
            if (scope == "Recent") query = query.Where(x => x.LastUsedUtc.HasValue).OrderByDescending(x => x.LastUsedUtc.Value);

            List<QueryTemplateItem> filtered = query.ToList();
            TemplateGrid.ItemsSource = filtered;
            RootFolderText.Text = QueryTemplateLibrary.Instance.RootFolder;

            QueryTemplateItem selection = filtered.FirstOrDefault(x => string.Equals(x.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (selection == null) selection = filtered.FirstOrDefault();
            TemplateGrid.SelectedItem = selection;
            if (selection != null) TemplateGrid.ScrollIntoView(selection);

            string status = string.Format(LocalizationManager.T("{0} template(s)"), filtered.Count);
            if (!string.IsNullOrWhiteSpace(QueryTemplateLibrary.Instance.LastError))
                status += "  |  " + QueryTemplateLibrary.Instance.LastError;
            StatusText.Text = status;
        }

        private QueryTemplateItem SelectedTemplate => TemplateGrid.SelectedItem as QueryTemplateItem;

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (IsLoaded) RefreshView();
        }

        private void ScopeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsLoaded) RefreshView();
        }

        private void TemplateGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            QueryTemplateItem item = SelectedTemplate;
            SetActionState(item);
            if (item == null)
            {
                PreviewEditor.Text = string.Empty;
                SelectedPathText.Text = string.Empty;
                return;
            }

            SelectedPathText.Text = item.RelativePath;
            try
            {
                PreviewEditor.Text = QueryTemplateLibrary.Instance.ReadContent(item);
                PreviewEditor.ScrollToHome();
            }
            catch (Exception ex)
            {
                PreviewEditor.Text = "-- " + LocalizationManager.T("Preview unavailable") + Environment.NewLine + "-- " + ex.Message;
            }
        }

        private void TemplateGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SelectedTemplate != null && FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject) != null)
                InsertSelected(false);
        }

        private void InsertButton_Click(object sender, RoutedEventArgs e) => InsertSelected(false);
        private void NewQueryButton_Click(object sender, RoutedEventArgs e) => InsertSelected(true);

        private void InsertSelected(bool newQuery)
        {
            QueryTemplateItem item = SelectedTemplate;
            if (item == null) return;
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                QueryTemplateInsertionService.InsertFile(item.FullPath, newQuery);
                StatusText.Text = newQuery
                    ? LocalizationManager.T("Template opened in a new query.")
                    : LocalizationManager.T("Template inserted.");
            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show(LocalizationManager.T("Could not insert the query template:") + " " + ex.Message,
                    "Query Templates", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void FavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            QueryTemplateItem item = SelectedTemplate;
            if (item == null) return;
            QueryTemplateLibrary.Instance.ToggleFavorite(item);
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            QueryTemplateLibrary.Instance.Refresh();
        }

        private void CreateTemplateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                string initialContent = QueryTemplateInsertionService.GetCurrentEditorText();
                List<string> categories = QueryTemplateLibrary.Instance.Snapshot()
                    .Select(x => x.Category)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                var dialog = new CreateQueryTemplateDialog(initialContent, categories);
                Window owner = Window.GetWindow(this);
                if (owner != null) dialog.Owner = owner;
                if (dialog.ShowDialog() != true) return;

                string root = Path.GetFullPath(QueryTemplateLibrary.Instance.RootFolder);
                string folder = string.IsNullOrEmpty(dialog.Category) ? root : Path.Combine(root, dialog.Category);
                string target = Path.GetFullPath(Path.Combine(folder, dialog.TemplateName + ".sql"));
                string rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The template path is outside the configured templates folder.");

                if (File.Exists(target))
                {
                    MessageBoxResult overwrite = LocalizedMessageBox.Show(
                        "A template with this name already exists. Replace it?", "Query Templates",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (overwrite != MessageBoxResult.Yes) return;
                }

                Directory.CreateDirectory(folder);
                File.WriteAllText(target, dialog.TemplateContent, new UTF8Encoding(false));
                ScopeBox.SelectedIndex = 0;
                SearchBox.Text = dialog.TemplateName;
                QueryTemplateLibrary.Instance.Refresh();
                StatusText.Text = LocalizationManager.T("Template created: ") + target;
            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show(LocalizationManager.T("Could not create the query template:") + " " + ex.Message,
                    "Query Templates", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MigrateFolderButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = LocalizationManager.T("Select the new templates folder");
                dialog.ShowNewFolderButton = true;
                dialog.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                if (!QueryTemplateLibrary.Instance.TryMigrateTo(dialog.SelectedPath, out QueryTemplateMigrationResult result, out string error))
                {
                    LocalizedMessageBox.Show(error, "Query Templates", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                RootFolderText.Text = result.DestinationFolder;
                LocalizedMessageBox.Show(
                    string.Format(LocalizationManager.T("Migrated {0} template(s) to the new folder. The original folder was kept at:\n{1}"),
                        result.CopiedCount, result.SourceFolder),
                    "Query Templates", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = LocalizationManager.T("Select templates folder");
                dialog.ShowNewFolderButton = true;
                dialog.SelectedPath = Directory.Exists(QueryTemplateLibrary.Instance.RootFolder)
                    ? QueryTemplateLibrary.Instance.RootFolder
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                if (!QueryTemplateLibrary.Instance.TrySetRoot(dialog.SelectedPath, true, out string error))
                    LocalizedMessageBox.Show(error, "Query Templates", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFolder(QueryTemplateLibrary.Instance.RootFolder, null);
        }

        private void ShowFileButton_Click(object sender, RoutedEventArgs e)
        {
            QueryTemplateItem item = SelectedTemplate;
            if (item != null) OpenFolder(Path.GetDirectoryName(item.FullPath), item.FullPath);
        }

        private static void OpenFolder(string folder, string selectFile)
        {
            try
            {
                if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Templates folder is currently unavailable: " + folder);
                var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                startInfo.Arguments = string.IsNullOrEmpty(selectFile) ? Quote(folder) : "/select," + Quote(selectFile);
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show(ex.Message, "Query Templates", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Control_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || SelectedTemplate == null) return;
            if (!SearchBox.IsKeyboardFocusWithin && !TemplateGrid.IsKeyboardFocusWithin) return;
            e.Handled = true;
            InsertSelected((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control);
        }

        private void SetActionState(QueryTemplateItem item)
        {
            bool enabled = item != null;
            InsertButton.IsEnabled = enabled;
            NewQueryButton.IsEnabled = enabled;
            FavoriteButton.IsEnabled = enabled;
            ShowFileButton.IsEnabled = enabled;
            FavoriteButton.Content = item?.IsFavorite == true
                ? LocalizationManager.T("Remove favorite")
                : LocalizationManager.T("Add favorite");
        }

        private void TryLoadSqlHighlighting()
        {
            try
            {
                using (var stream = typeof(QueryTemplateWindowControl).Assembly.GetManifestResourceStream("AxialSqlTools.QuickSearch.sql.xshd"))
                using (var reader = new XmlTextReader(stream))
                    PreviewEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
            catch
            {
                // Preview remains useful as plain text if the optional highlighter cannot load.
            }
        }

        private void ApplyThemeBrushResources()
        {
            ToolWindowThemeResources.ApplySharedTheme(this);
        }

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "") + "\"";

        private static T FindVisualParent<T>(DependencyObject source) where T : DependencyObject
        {
            while (source != null)
            {
                if (source is T match) return match;
                source = VisualTreeHelper.GetParent(source);
            }
            return null;
        }
    }
}
