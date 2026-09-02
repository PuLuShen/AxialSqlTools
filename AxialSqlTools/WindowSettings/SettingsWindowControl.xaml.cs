namespace AxialSqlTools
{
    using Microsoft.Data.SqlClient;
    using Newtonsoft.Json.Linq;
    using System;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.IO.Compression;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Windows;
    using System.Windows.Controls;
    using System.Collections.ObjectModel;
    using System.Windows.Media;
    using System.Windows.Navigation;
    using Microsoft.VisualBasic;
    using static AxialSqlTools.AxialSqlToolsPackage;

    /// <summary>
    /// Interaction logic for SettingsWindowControl.
    /// </summary>
    public partial class SettingsWindowControl : UserControl
    {
        private const string QueryHistoryStorageModeDatabase = "Database";
        private const string QueryHistoryStorageModeTextFiles = "TextFiles";
        private const string QueryHistoryStorageModeDisabled = "Disabled";

        private string _queryHistoryConnectionString;
        private readonly ToolWindowThemeController _themeController;
        private bool updateResultSubscribed;
        private ObservableCollection<SettingsManager.ConnectionColorRule> _connectionColorRules;
        private SettingsManager.ConnectionColorRule _editingConnectionColorRule;

        private string tsqlFormatExample = @"while (1=0) 
begin 
select top 10
    c.CustomerID, getDate(),
    CASE WHEN o.TotalAmount > 1000 THEN 'High' ELSE 'Low' END AS OrderSize
FROM Customers c
JOIN Orders o ON c.CustomerID = o.CustomerID CROSS JOIN Regions r
WHERE c.IsActive = 1;

SELECT dbo.func(p.ProductID), p.ProductName FROM Products p; EXEC dbo.test @a = 0, @b = 1;
end
if 1=0 begin select 1; declare @a int, @b varchar(10) = ''
end
go
create procedure dbo.test @a int, @b int = 0
as select 1;
";
        /// <summary>
        /// Initializes a new instance of the <see cref="SettingsWindowControl"/> class.
        /// </summary>
        public SettingsWindowControl()
        {
            this.InitializeComponent();

            _connectionColorRules = new ObservableCollection<SettingsManager.ConnectionColorRule>();
            ConnectionColorRulesListView.ItemsSource = _connectionColorRules;
            UpdateConnectionColorRuleButtons();
            SetConnectionColorRulesDirty(false);

            _themeController = new ToolWindowThemeController(this, ApplyThemeBrushResources);

            this.Loaded += UserControl_Loaded;
            this.Unloaded += UserControl_Unloaded;

            SourceQueryPreview.Text = tsqlFormatExample;

            formatTSqlExample();

        }

        private void UserControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            SubscribeToUpdateResultChanges();
            LoadSavedSettings();
        }

        private void UserControl_Unloaded(object sender, System.Windows.RoutedEventArgs e)
        {
            UnsubscribeFromUpdateResultChanges();
        }

        private void SubscribeToUpdateResultChanges()
        {
            if (updateResultSubscribed)
            {
                return;
            }

            UpdateChecker.LastUpdateResultChanged += UpdateChecker_LastUpdateResultChanged;
            updateResultSubscribed = true;
        }

        private void UnsubscribeFromUpdateResultChanges()
        {
            if (!updateResultSubscribed)
            {
                return;
            }

            UpdateChecker.LastUpdateResultChanged -= UpdateChecker_LastUpdateResultChanged;
            updateResultSubscribed = false;
        }

        private void ApplyThemeBrushResources()
        {
            ToolWindowThemeResources.ApplySharedTheme(this);

            ApplyGoogleSheetsAuthorizationBrush();
        }

        private void Button_ApplyLanguage_Click(object sender, RoutedEventArgs e)
        {
            string language = UiLanguageSelector.SelectedValue as string;
            LocalizationManager.SetLanguage(language);
            LocalizationManager.Apply(this);
        }

        private Brush GetThemedStatusBrush(bool isSuccess)
        {
            string key = isSuccess ? "AxialThemeStatusSuccessBrush" : "AxialThemeStatusErrorBrush";
            return Resources[key] as Brush
                ?? (isSuccess ? new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)) : new SolidColorBrush(Color.FromRgb(0xA1, 0x26, 0x0D)));
        }

        private void ApplyGoogleSheetsAuthorizationBrush()
        {
            if (GoogleSheetsRefreshTokenLabel == null)
            {
                return;
            }

            bool isAuthorized = string.Equals(GoogleSheetsRefreshTokenLabel.Text, LocalizationManager.T("Authorized"), StringComparison.OrdinalIgnoreCase);
            GoogleSheetsRefreshTokenLabel.Foreground = GetThemedStatusBrush(isAuthorized);
        }

        private void LoadSavedSettings()
        {
            try
            {

                UiLanguageSelector.SelectedValue = SettingsManager.GetUiLanguage();
                ScriptObjectShortcut.Text = SettingsManager.GetScriptObjectShortcut();

                ScriptFolder.Text = SettingsManager.GetTemplatesFolder();

                var snippetSettings = SettingsManager.GetSnippetSettings();
                UseSnippets.IsChecked = snippetSettings.useSnippets;
                SnippetFolder.Text = snippetSettings.snippetFolder;
                SnippetReplaceMode.SelectedValue = snippetSettings.replaceKey.ToString();
                var asteriskSettings = SettingsManager.GetAsteriskExpansionSettings();
                UseAsteriskExpansion.IsChecked = asteriskSettings.useAsteriskExpansion;
                AsteriskExpansionTriggerMode.SelectedValue = asteriskSettings.triggerKey.ToString();
                var completionSettings = SettingsManager.GetSqlCompletionSettings();
                UseSqlCompletion.IsChecked = completionSettings.enabled;
                AutomaticSqlCompletion.IsChecked = completionSettings.automaticPopup;
                CompletionSquareBrackets.IsChecked = completionSettings.useSquareBrackets;
                CompletionUsageLearning.IsChecked = completionSettings.learnFromUsage;
                CompletionDelay.Text = completionSettings.delayMilliseconds.ToString();
                CompletionMaximumItems.Text = completionSettings.maximumItems.ToString();

                _queryHistoryConnectionString = SettingsManager.GetQueryHistoryConnectionString();
                QueryHistoryTableName.Text = SettingsManager.GetQueryHistoryTableName();
                QueryHistoryTextFilesInfo.Text = SettingsManager.GetQueryHistoryTextFileFolder();
                SelectQueryHistoryStorageType(SettingsManager.GetQueryHistoryStorageMode());
                SelectQueryHistoryShortcut(SettingsManager.GetQueryHistoryShortcut());
                UpdateQueryHistoryStorageControls();
                UpdateQueryHistoryConnectionDetails();

                RefreshQueryHistoryCreateScript();

                MyEmailAddress.Text = SettingsManager.GetMyEmail();

                SettingsManager.SmtpSettings smtpSettings = SettingsManager.GetSmtpSettings();

                SMTP_Server.Text = smtpSettings.ServerName;
                SMTP_Port.Text = smtpSettings.Port.ToString();
                SMTP_UserName.Text = smtpSettings.Username;
                SMTP_Password.Password = smtpSettings.Password;
                SMTP_EnableSSL.IsChecked = smtpSettings.EnableSsl;

                var tsqlCodeFormatSettings = SettingsManager.GetTSqlCodeFormatSettings();
                PreserveComments.IsChecked = tsqlCodeFormatSettings.preserveComments;
                RemoveNewLineAfterJoin.IsChecked = tsqlCodeFormatSettings.removeNewLineAfterJoin;
                AddTabAfterJoinOn.IsChecked = tsqlCodeFormatSettings.addTabAfterJoinOn;
                MoveCrossJoinToNewLine.IsChecked = tsqlCodeFormatSettings.moveCrossJoinToNewLine;
                FormatCaseAsMultiline.IsChecked = tsqlCodeFormatSettings.formatCaseAsMultiline;
                AddNewLineBetweenStatementsInBlocks.IsChecked = tsqlCodeFormatSettings.addNewLineBetweenStatementsInBlocks;
                BreakSprocParametersPerLine.IsChecked = tsqlCodeFormatSettings.breakSprocParametersPerLine;
                UppercaseBuiltInFunctions.IsChecked = tsqlCodeFormatSettings.uppercaseBuiltInFunctions;
                UnindentBeginEndBlocks.IsChecked = tsqlCodeFormatSettings.unindentBeginEndBlocks;
                BreakVariableDefinitionsPerLine.IsChecked = tsqlCodeFormatSettings.breakVariableDefinitionsPerLine;  
                BreakSprocDefinitionParametersPerLine.IsChecked = tsqlCodeFormatSettings.breakSprocDefinitionParametersPerLine;
                // BreakSelectFieldsAfterTopAndUnindent.IsChecked = tsqlCodeFormatSettings.breakSelectFieldsAfterTopAndUnindent;

                OpenAiApiKey.Password = SettingsManager.GetOpenAiApiKey();

                // Excel export settings
                var excelSettings = SettingsManager.GetExcelExportSettings();
                ExcelExportIncludeSourceQuery.IsChecked = excelSettings.includeSourceQuery;
                ExcelExportAddAutoFilter.IsChecked = excelSettings.addAutofilter;
                ExcelExportBoolsAsNumbers.IsChecked = excelSettings.exportBoolsAsNumbers;
                ExcelExportDefaultDirectory.Text = excelSettings.defaultDirectory;
                ExcelExportDefaultFilename.Text = excelSettings.defaultFileName;

                var googleSettings = SettingsManager.GetGoogleSheetsSettings();
                GoogleSheetsIncludeSourceQuery.IsChecked = googleSettings.includeSourceQuery;
                GoogleSheetsExportBoolsAsNumbers.IsChecked = googleSettings.exportBoolsAsNumbers;
                GoogleSheetsDefaultSpreadsheetName.Text = googleSettings.defaultSpreadsheetName;
                GoogleSheetsClientId.Text = googleSettings.clientId;
                GoogleSheetsClientSecret.Password = googleSettings.clientSecret;
                UpdateGoogleSheetsStatus(googleSettings.refreshToken);

                EnableUpdateChecks.IsChecked = SettingsManager.GetEnableUpdateChecks();
                UpdateUpdateStatus();

                LoadConnectionColorRules();

            }
            catch (Exception ex)
            {
                _logger.Error(ex, "An exception occurred while loading settings");

                string msg = $"Error message: {ex.Message} \nInnerException: {ex.InnerException}";
                LocalizedMessageBox.Show(msg, "Error");
            }

            try
            {
                GitHubToken.Password = WindowsCredentialHelper.LoadToken("AxialSqlTools_GitHubToken");
            }
            catch 
            {
                // ??
            }

        }

        private void UpdateQueryHistoryConnectionDetails()
        {

            if (string.IsNullOrWhiteSpace(_queryHistoryConnectionString))
            {
                Label_QueryHistoryConnectionInfo.Text = " < not configured > ";
            }
            else
            {
                try
                {

                    SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(_queryHistoryConnectionString);

                    string msg = string.Format("Server: {0}; Database: {1}; User ID: {2}", builder.DataSource, builder.InitialCatalog, builder.UserID);

                    Label_QueryHistoryConnectionInfo.Text = msg;

                }
                catch (Exception ex)
                {
                    Label_QueryHistoryConnectionInfo.Text = ex.Message;
                }
            }

        }

        private void Button_SaveScriptFolder_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.SaveTemplatesFolder(ScriptFolder.Text);

            SavedMessage();
        }


        private void Button_SaveSnippetFolder_Click(object sender, RoutedEventArgs e)
        {
            var snippetSettings = SettingsManager.GetSnippetSettings();
            snippetSettings.useSnippets = UseSnippets.IsChecked.GetValueOrDefault();
            snippetSettings.snippetFolder = SnippetFolder.Text;
            snippetSettings.replaceKey = GetSelectedSnippetReplaceKey();

            SettingsManager.SaveSnippetSettings(snippetSettings);

            SettingsManager.SaveAsteriskExpansionSettings(new SettingsManager.AsteriskExpansionSettings
            {
                useAsteriskExpansion = UseAsteriskExpansion.IsChecked.GetValueOrDefault(),
                triggerKey = GetSelectedAsteriskExpansionTriggerKey()
            });

            var completionSettings = SettingsManager.GetSqlCompletionSettings();
            completionSettings.enabled = UseSqlCompletion.IsChecked.GetValueOrDefault();
            completionSettings.automaticPopup = AutomaticSqlCompletion.IsChecked.GetValueOrDefault();
            completionSettings.useSquareBrackets = CompletionSquareBrackets.IsChecked.GetValueOrDefault();
            completionSettings.learnFromUsage = CompletionUsageLearning.IsChecked.GetValueOrDefault();
            if (int.TryParse(CompletionDelay.Text, out int delay)) completionSettings.delayMilliseconds = delay;
            if (int.TryParse(CompletionMaximumItems.Text, out int maximumItems)) completionSettings.maximumItems = maximumItems;
            SettingsManager.SaveSqlCompletionSettings(completionSettings);

            SavedMessage();
        }

        private void buttonDownloadAxialScripts_Click(object sender, RoutedEventArgs e)
        {
            string repoUrl = "https://github.com/Axial-SQL/AxialSqlTools/archive/main.zip";
            string targetFolderPath = "AxialSqlTools-main/query-library"; // Relative path inside the zip
            string targetPath = SettingsManager.GetTemplatesFolder();

            try
            {
                // Download the repo zip
                string tempZipPath = DownloadGitHubRepoZip(repoUrl);

                // Extract the specific folder from the zip
                ExtractSpecificFolderFromZip(tempZipPath, targetFolderPath, targetPath);

                LocalizedMessageBox.Show("Axial SQL Tool Query Library has been downloaded", "Done");

            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show(
                    string.Format(System.Globalization.CultureInfo.CurrentUICulture, "An error occurred: '{0}'", ex.Message),
                    "Error");
            }

        }

        private SettingsManager.SnippetReplaceKey GetSelectedSnippetReplaceKey()
        {
            var selectedValue = SnippetReplaceMode.SelectedValue as string;
            if (Enum.TryParse(selectedValue, out SettingsManager.SnippetReplaceKey key))
            {
                return key;
            }

            return SettingsManager.SnippetReplaceKey.Enter;
        }

        private SettingsManager.SnippetReplaceKey GetSelectedAsteriskExpansionTriggerKey()
        {
            var selectedValue = AsteriskExpansionTriggerMode.SelectedValue as string;
            if (Enum.TryParse(selectedValue, out SettingsManager.SnippetReplaceKey key))
            {
                return key;
            }

            return SettingsManager.SnippetReplaceKey.Tab;
        }

        static string DownloadGitHubRepoZip(string url)
        {
            using (HttpClient client = new HttpClient())
            {              
                // Mimic a browser's User-Agent string
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.3");
                client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
                client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.5");

                string tempPath = Path.GetTempFileName() + ".zip";
                byte[] data = client.GetByteArrayAsync(url).GetAwaiter().GetResult();
                File.WriteAllBytes(tempPath, data);
                return tempPath;
            }
        }

        static void ExtractSpecificFolderFromZip(string zipPath, string folderPath, string destinationPath)
        {
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase))
                    {
                        string path = Path.Combine(destinationPath, entry.FullName.Substring(folderPath.Length + 1));

                        // Create subdirectory structure in destination, if needed
                        if (entry.FullName.EndsWith("/"))
                        {
                            Directory.CreateDirectory(path);
                        }
                        else
                        {
                            // Ensure directory exists
                            Directory.CreateDirectory(Path.GetDirectoryName(path));
                            // Check if file exists to avoid IOException
                            if (File.Exists(path))
                            {
                                File.Delete(path); // Delete the file if it exists.
                            }
                            entry.ExtractToFile(path, true);
                        }
                    }
                }
            }
            // Delete the temporary zip file after extraction
            File.Delete(zipPath);
        }

        private void SavedMessage()
        {
            LocalizedMessageBox.Show(
                string.Format(System.Globalization.CultureInfo.CurrentUICulture, "The change has been saved", this.ToString()),
                "Setting saved");
        }

        private void buttonWikiPage_Click(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        private void ButtonSaveSmtpSettings_Click(object sender, RoutedEventArgs e)
        {
            
            SettingsManager.SmtpSettings smtpSettings = new SettingsManager.SmtpSettings()
            {
                ServerName = SMTP_Server.Text,
                Username = SMTP_UserName.Text,
                Password = SMTP_Password.Password,
                EnableSsl = SMTP_EnableSSL.IsChecked.GetValueOrDefault()
            };

            int smptPort = 587;
            bool success = int.TryParse(SMTP_Port.Text, out smptPort);
            smtpSettings.Port = smptPort;

            SettingsManager.SaveSmtpSettings(smtpSettings);

            SettingsManager.SaveMyEmail(MyEmailAddress.Text);

            SavedMessage();

        }

        private void Button_SaveApplyAdditionalFormat_Click(object sender, RoutedEventArgs e)
        {
            var settings = new SettingsManager.TSqlCodeFormatSettings
            {
                preserveComments = PreserveComments.IsChecked.GetValueOrDefault(false),
                removeNewLineAfterJoin = RemoveNewLineAfterJoin.IsChecked.GetValueOrDefault(false),
                addTabAfterJoinOn = AddTabAfterJoinOn.IsChecked.GetValueOrDefault(false),
                moveCrossJoinToNewLine = MoveCrossJoinToNewLine.IsChecked.GetValueOrDefault(false),
                formatCaseAsMultiline = FormatCaseAsMultiline.IsChecked.GetValueOrDefault(false),
                addNewLineBetweenStatementsInBlocks = AddNewLineBetweenStatementsInBlocks.IsChecked.GetValueOrDefault(false),
                breakSprocParametersPerLine = BreakSprocParametersPerLine.IsChecked.GetValueOrDefault(false),
                uppercaseBuiltInFunctions = UppercaseBuiltInFunctions.IsChecked.GetValueOrDefault(false),
                unindentBeginEndBlocks = UnindentBeginEndBlocks.IsChecked.GetValueOrDefault(false),
                breakVariableDefinitionsPerLine = BreakVariableDefinitionsPerLine.IsChecked.GetValueOrDefault(false),
                breakSprocDefinitionParametersPerLine = BreakSprocDefinitionParametersPerLine.IsChecked.GetValueOrDefault(false),
                // breakSelectFieldsAfterTopAndUnindent = BreakSelectFieldsAfterTopAndUnindent.IsChecked.GetValueOrDefault(false)
            };

            SettingsManager.SaveTSqlCodeFormatSettings(settings);
            SavedMessage();
        }

        private void button_SaveExcelExportSettings_Click(object sender, RoutedEventArgs e)
        {
            var settings = new SettingsManager.ExcelExportSettings
            {
                includeSourceQuery = ExcelExportIncludeSourceQuery.IsChecked.GetValueOrDefault(false),
                addAutofilter = ExcelExportAddAutoFilter.IsChecked.GetValueOrDefault(false),
                exportBoolsAsNumbers = ExcelExportBoolsAsNumbers.IsChecked.GetValueOrDefault(false),
                defaultDirectory = ExcelExportDefaultDirectory.Text,
                defaultFileName = ExcelExportDefaultFilename.Text
            };

            SettingsManager.SaveExcelExportSettings(settings);
            SavedMessage();
        }

        private void button_SaveGoogleSheetsSettings_Click(object sender, RoutedEventArgs e)
        {
            var settings = BuildGoogleSheetsSettings();
            SettingsManager.SaveGoogleSheetsSettings(settings);
            UpdateGoogleSheetsStatus(settings.refreshToken);
            SavedMessage();
        }

        private void button_SaveUpdateSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.SaveEnableUpdateChecks(EnableUpdateChecks.IsChecked.GetValueOrDefault(true));
            SavedMessage();
        }

        private void button_CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            UpdateChecker.CheckNow(AxialSqlToolsPackage.PackageInstance, ignoreSettings: true);
            UpdateUpdateStatus();
        }

        private void UpdateChecker_LastUpdateResultChanged()
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(UpdateUpdateStatus));
            }
            catch
            {
            }
        }

        private void UpdateUpdateStatus()
        {
            if (UpdateCheckStatus != null)
            {
                UpdateCheckStatus.Text = UpdateChecker.LastUpdateResult;
            }
        }

        private async void button_AuthorizeGoogleSheets_Click(object sender, RoutedEventArgs e)
        {
            var settings = BuildGoogleSheetsSettings();

            if (!settings.HasClientConfiguration())
            {
                LocalizedMessageBox.Show("Client ID and Client Secret are required before authorizing Google Sheets.", "Google Sheets", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string authorizationUrl = GoogleSheetsExport.BuildAuthorizationUrl(settings);
                Process.Start(new ProcessStartInfo(authorizationUrl) { UseShellExecute = true });

                string authorizationCode = Interaction.InputBox("Paste the authorization code provided by Google after granting access.", "Google Sheets Authorization");
                if (string.IsNullOrWhiteSpace(authorizationCode))
                {
                    return;
                }

                var authResult = await GoogleSheetsExport.ExchangeAuthorizationCodeAsync(settings, authorizationCode.Trim(), CancellationToken.None);

                if (!string.IsNullOrWhiteSpace(authResult.RefreshToken))
                {
                    settings.refreshToken = authResult.RefreshToken;
                }

                SettingsManager.SaveGoogleSheetsSettings(settings);
                UpdateGoogleSheetsStatus(settings.refreshToken);
                SavedMessage();
            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show($"Authorization failed: {ex.Message}", "Google Sheets", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private SettingsManager.GoogleSheetsSettings BuildGoogleSheetsSettings()
        {
            return new SettingsManager.GoogleSheetsSettings
            {
                includeSourceQuery = GoogleSheetsIncludeSourceQuery.IsChecked.GetValueOrDefault(false),
                exportBoolsAsNumbers = GoogleSheetsExportBoolsAsNumbers.IsChecked.GetValueOrDefault(false),
                defaultSpreadsheetName = GoogleSheetsDefaultSpreadsheetName.Text,
                clientId = GoogleSheetsClientId.Text,
                clientSecret = GoogleSheetsClientSecret.Password,
                refreshToken = SettingsManager.GetGoogleSheetsSettings().refreshToken
            };
        }

        private void UpdateGoogleSheetsStatus(string refreshToken)
        {
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                GoogleSheetsRefreshTokenLabel.Text = LocalizationManager.T("Not authorized");
                GoogleSheetsRefreshTokenLabel.Foreground = new SolidColorBrush(Colors.DarkRed);
            }
            else
            {
                GoogleSheetsRefreshTokenLabel.Text = LocalizationManager.T("Authorized");
                GoogleSheetsRefreshTokenLabel.Foreground = new SolidColorBrush(Colors.DarkGreen);
            }
        }

        private void Hyperlink_RequestNavigateFormatQueryWiki(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        private void Button_SaveOpenAi_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.SaveOpenAiApiKey(OpenAiApiKey.Password);

            SavedMessage();
        }

        private void Button_SaveQueryHistory_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.SaveQueryHistoryConnectionString(_queryHistoryConnectionString);
            SettingsManager.SaveQueryHistoryTableName(QueryHistoryTableName.Text);
            SettingsManager.SaveQueryHistoryStorageMode(GetSelectedQueryHistoryStorageType());
            string shortcut = QueryHistoryShortcut.SelectedValue?.ToString() ?? string.Empty;
            SettingsManager.SaveQueryHistoryShortcut(shortcut);
            if (!ShortcutManager.ApplyQueryHistoryShortcut(shortcut, out string shortcutError))
            {
                LocalizedMessageBox.Show("Settings were saved, but the shortcut could not be applied: " + shortcutError, "Query History");
                return;
            }

            SavedMessage();

            RefreshQueryHistoryCreateScript();

        }

        private void Button_SelectDatabaseFromObjectExplorer_Click(object sender, RoutedEventArgs e)
        {

            var ci = ScriptFactoryAccess.GetCurrentConnectionInfo();

            _queryHistoryConnectionString = ci.FullConnectionString;

            UpdateQueryHistoryConnectionDetails();

        }

        private void SelectQueryHistoryShortcut(string shortcut)
        {
            foreach (var item in QueryHistoryShortcut.Items)
            {
                if (item is ComboBoxItem option && string.Equals(option.Tag?.ToString() ?? string.Empty, shortcut ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    QueryHistoryShortcut.SelectedItem = option;
                    return;
                }
            }
            QueryHistoryShortcut.SelectedIndex = 0;
        }

        private void Button_ApplyScriptObjectShortcut_Click(object sender, RoutedEventArgs e)
        {
            string shortcut = NormalizeShortcut(ScriptObjectShortcut.Text);
            if (shortcut == null)
            {
                LocalizedMessageBox.Show("Enter a shortcut such as F12, Ctrl+F12, or Ctrl+Shift+O. Enter None to remove it.",
                    "Keyboard shortcuts", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!ShortcutManager.ApplyScriptObjectShortcut(shortcut, out string error))
            {
                LocalizedMessageBox.Show("The shortcut could not be applied: " + error,
                    "Keyboard shortcuts", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!SettingsManager.SaveScriptObjectShortcut(shortcut))
            {
                LocalizedMessageBox.Show("The shortcut was applied, but the setting could not be saved.",
                    "Keyboard shortcuts", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ScriptObjectShortcut.Text = string.IsNullOrEmpty(shortcut) ? "None" : shortcut;
            SavedMessage();
        }

        private static string NormalizeShortcut(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (string.Equals(value, "None", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string[] parts = value.Replace(" ", string.Empty).Split('+');
            if (parts.Length == 0 || parts.Length > 4)
                return null;

            var modifiers = new System.Collections.Generic.List<string>();
            string key = null;
            foreach (string rawPart in parts)
            {
                string part = rawPart.Trim();
                if (string.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase) || string.Equals(part, "Control", StringComparison.OrdinalIgnoreCase))
                    AddShortcutModifier(modifiers, "Ctrl");
                else if (string.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase))
                    AddShortcutModifier(modifiers, "Shift");
                else if (string.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase))
                    AddShortcutModifier(modifiers, "Alt");
                else if (key == null && IsValidShortcutKey(part))
                    key = part.ToUpperInvariant();
                else
                    return null;
            }

            if (key == null)
                return null;
            modifiers.Add(key);
            return string.Join("+", modifiers);
        }

        private static void AddShortcutModifier(System.Collections.Generic.List<string> modifiers, string modifier)
        {
            if (!modifiers.Contains(modifier))
                modifiers.Add(modifier);
        }

        private static bool IsValidShortcutKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            if (key.Length == 1 && char.IsLetterOrDigit(key[0])) return true;
            if (key.Length > 1 && char.ToUpperInvariant(key[0]) == 'F' && int.TryParse(key.Substring(1), out int functionKey))
                return functionKey >= 1 && functionKey <= 24;
            return string.Equals(key, "Insert", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "Delete", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "Home", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "End", StringComparison.OrdinalIgnoreCase);
        }

        private async void Button_TestQueryHistory_Click(object sender, RoutedEventArgs e)
        {
            button_TestQueryHistory.IsEnabled = false;
            try
            {
                string mode = GetSelectedQueryHistoryStorageType();
                if (string.Equals(mode, QueryHistoryStorageModeDisabled, StringComparison.OrdinalIgnoreCase))
                {
                    LocalizedMessageBox.Show("Query history recording is disabled.", "Query History");
                    return;
                }
                if (string.Equals(mode, QueryHistoryStorageModeTextFiles, StringComparison.OrdinalIgnoreCase))
                {
                    string folder = SettingsManager.GetQueryHistoryTextFileFolder();
                    await System.Threading.Tasks.Task.Run(() => Directory.CreateDirectory(folder));
                    LocalizedMessageBox.Show("Configuration is ready. Query history will be stored in:\n" + folder, "Query History");
                    return;
                }
                if (string.IsNullOrWhiteSpace(_queryHistoryConnectionString))
                    throw new InvalidOperationException("Select a database connection first.");

                string tableName = EffectiveQueryHistoryTableName();
                bool tableExists;
                using (var connection = new SqlConnection(_queryHistoryConnectionString))
                {
                    await connection.OpenAsync();
                    using (var command = new SqlCommand("SELECT CASE WHEN OBJECT_ID(@TableName, 'U') IS NULL THEN 0 ELSE 1 END", connection))
                    {
                        command.CommandTimeout = 15;
                        command.Parameters.AddWithValue("@TableName", tableName);
                        tableExists = Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
                    }
                }
                LocalizedMessageBox.Show(tableExists
                    ? "Connection succeeded and the query history table is ready."
                    : "Connection succeeded. The table does not exist yet; it will be created when the next query is recorded.", "Query History");
            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show("Configuration test failed: " + ex.Message, "Query History");
            }
            finally { button_TestQueryHistory.IsEnabled = true; }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select templates folder";
                dialog.ShowNewFolderButton = true;

                // Show the dialog and check if the user selected a folder
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    // Set the selected folder path to the TextBox
                    ScriptFolder.Text = dialog.SelectedPath;
                }
            }
        }

        private void SnippetsBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select snippets folder";
                dialog.ShowNewFolderButton = true;

                // Show the dialog and check if the user selected a folder
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    // Set the selected folder path to the TextBox
                    SnippetFolder.Text = dialog.SelectedPath;
                }
            }
        }

        private string GetSelectedQueryHistoryStorageType()
        {
            if (QueryHistoryStorageType.SelectedItem is ComboBoxItem item)
            {
                return item.Tag?.ToString() ?? QueryHistoryStorageModeDatabase;
            }

            return QueryHistoryStorageModeDatabase;
        }

        private void SelectQueryHistoryStorageType(string storageType)
        {
            string mode = string.IsNullOrWhiteSpace(storageType) ? QueryHistoryStorageModeDatabase : storageType;

            foreach (var obj in QueryHistoryStorageType.Items)
            {
                if (obj is ComboBoxItem item && string.Equals(item.Tag?.ToString(), mode, StringComparison.OrdinalIgnoreCase))
                {
                    QueryHistoryStorageType.SelectedItem = item;
                    return;
                }
            }

            QueryHistoryStorageType.SelectedIndex = 0;
        }

        private void UpdateQueryHistoryStorageControls()
        {
            bool isDisabledStorage = string.Equals(GetSelectedQueryHistoryStorageType(), QueryHistoryStorageModeDisabled, StringComparison.OrdinalIgnoreCase);
            bool isDatabaseStorage = string.Equals(GetSelectedQueryHistoryStorageType(), QueryHistoryStorageModeDatabase, StringComparison.OrdinalIgnoreCase);
            Label_QueryHistoryConnectionInfoTitle.Visibility = isDatabaseStorage ? Visibility.Visible : Visibility.Collapsed;
            Label_QueryHistoryConnectionInfo.Visibility = isDatabaseStorage ? Visibility.Visible : Visibility.Collapsed;
            button_SelectDatabaseFromObjectExplorer.Visibility = isDatabaseStorage ? Visibility.Visible : Visibility.Collapsed;
            Label_QueryHistoryTargetTableName.Visibility = isDatabaseStorage ? Visibility.Visible : Visibility.Collapsed;
            QueryHistoryTableName.Visibility = isDatabaseStorage ? Visibility.Visible : Visibility.Collapsed;
            Label_QueryHistoryTargetTableHint.Visibility = isDatabaseStorage ? Visibility.Visible : Visibility.Collapsed;
            Group_QueryHistoryCreateScript.Visibility = isDatabaseStorage ? Visibility.Visible : Visibility.Collapsed;
            QueryHistoryTextFilesPanel.Visibility = (!isDatabaseStorage && !isDisabledStorage) ? Visibility.Visible : Visibility.Collapsed;
            Label_QueryHistoryTextFilesInfo.Visibility = (!isDatabaseStorage && !isDisabledStorage) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void QueryHistoryStorageType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateQueryHistoryStorageControls();
        }

        private void Button_OpenQueryHistoryFolder_Click(object sender, RoutedEventArgs e)
        {
            string folderPath = SettingsManager.GetQueryHistoryTextFileFolder();
            Directory.CreateDirectory(folderPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true
            });
        }

        private void formatTSqlExample()
        {
            var settings = new SettingsManager.TSqlCodeFormatSettings
            {
                preserveComments = PreserveComments.IsChecked.GetValueOrDefault(false),
                removeNewLineAfterJoin = RemoveNewLineAfterJoin.IsChecked.GetValueOrDefault(false),
                addTabAfterJoinOn = AddTabAfterJoinOn.IsChecked.GetValueOrDefault(false),
                moveCrossJoinToNewLine = MoveCrossJoinToNewLine.IsChecked.GetValueOrDefault(false),
                formatCaseAsMultiline = FormatCaseAsMultiline.IsChecked.GetValueOrDefault(false),
                addNewLineBetweenStatementsInBlocks = AddNewLineBetweenStatementsInBlocks.IsChecked.GetValueOrDefault(false),
                breakSprocParametersPerLine = BreakSprocParametersPerLine.IsChecked.GetValueOrDefault(false),
                uppercaseBuiltInFunctions = UppercaseBuiltInFunctions.IsChecked.GetValueOrDefault(false),
                unindentBeginEndBlocks = UnindentBeginEndBlocks.IsChecked.GetValueOrDefault(false),
                breakVariableDefinitionsPerLine = BreakVariableDefinitionsPerLine.IsChecked.GetValueOrDefault(false),
                breakSprocDefinitionParametersPerLine = BreakSprocDefinitionParametersPerLine.IsChecked.GetValueOrDefault(false),
                // breakSelectFieldsAfterTopAndUnindent = BreakSelectFieldsAfterTopAndUnindent.IsChecked.GetValueOrDefault(false)
            };

            FormattedQueryPreview.Text = TSqlFormatter.FormatCode(SourceQueryPreview.Text, settings);
        }

        private void formatSetting_Checked(object sender, RoutedEventArgs e)
        {
            formatTSqlExample();
        }

        private void formatSetting_Unchecked(object sender, RoutedEventArgs e)
        {
            formatTSqlExample();
        }

        private void buttonSaveGitHubSettings_Click(object sender, RoutedEventArgs e)
        {

            WindowsCredentialHelper.SaveToken("AxialSqlTools_GitHubToken", "AxialSqlTools_GitHubToken", GitHubToken.Password);

            SavedMessage();

        }


        private static string DefaultQueryHistoryTableName => "[dbo].[QueryHistory]";

        private void QueryHistoryTableName_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshQueryHistoryCreateScript();
        }

        private string EffectiveQueryHistoryTableName()
        {
            var name = QueryHistoryTableName?.Text;
            return string.IsNullOrWhiteSpace(name) ? DefaultQueryHistoryTableName : name.Trim();
        }

        private string GenerateQueryHistoryCreateTableScript(string tableName)
        {
            // Deterministic index names for display-only purposes
            string indexNameGuid = Guid.NewGuid().ToString();

            return $@"
IF OBJECT_ID(N'{tableName}', N'U') IS NULL
BEGIN
    CREATE TABLE {tableName} (
        [QueryID]           INT            IDENTITY (1, 1) NOT NULL,
        [StartTime]         DATETIME       NOT NULL,
        [FinishTime]        DATETIME       NOT NULL,
        [ElapsedTime]       VARCHAR (15)   NOT NULL,
        [TotalRowsReturned] BIGINT         NOT NULL,
        [ExecResult]        VARCHAR (100)  NOT NULL,
        [QueryText]         NVARCHAR (MAX) NOT NULL,
        [DataSource]        NVARCHAR (128) NOT NULL,
        [DatabaseName]      NVARCHAR (128) NOT NULL,
        [LoginName]         NVARCHAR (128) NOT NULL,
        [WorkstationId]     NVARCHAR (128) NOT NULL,
        PRIMARY KEY CLUSTERED ([QueryID]),
        INDEX [IDX_{indexNameGuid}_1] ([StartTime]),
        INDEX [IDX_{indexNameGuid}_2] ([FinishTime]),
        INDEX [IDX_{indexNameGuid}_3] ([DataSource]),
        INDEX [IDX_{indexNameGuid}_4] ([DatabaseName])
    );
    ALTER INDEX ALL ON {tableName} REBUILD WITH (DATA_COMPRESSION = PAGE);
END
".Trim();
        }

        private void RefreshQueryHistoryCreateScript()
        {
            try
            {
                QueryHistoryCreateScript.Text = GenerateQueryHistoryCreateTableScript(EffectiveQueryHistoryTableName());
            }
            catch (Exception ex)
            {
                QueryHistoryCreateScript.Text = $"-- Failed to generate script: {ex.Message}";
            }
        }

        private void LoadConnectionColorRules()
        {
            _connectionColorRules = new ObservableCollection<SettingsManager.ConnectionColorRule>(SettingsManager.GetConnectionColorRules());
            ConnectionColorRulesListView.ItemsSource = _connectionColorRules;
            CancelConnectionColorRuleEdit();
            SetConnectionColorRulesDirty(false);
            UpdateConnectionColorRuleButtons();
        }

        private void EnsureConnectionColorRulesLoaded()
        {
            if (_connectionColorRules == null)
            {
                _connectionColorRules = new ObservableCollection<SettingsManager.ConnectionColorRule>();
                ConnectionColorRulesListView.ItemsSource = _connectionColorRules;
            }
        }

        private string PickColor(string currentHex)
        {
            var dialog = new System.Windows.Forms.ColorDialog();
            try
            {
                dialog.Color = System.Drawing.ColorTranslator.FromHtml(currentHex);
            }
            catch { }
            dialog.FullOpen = true;

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                return System.Drawing.ColorTranslator.ToHtml(dialog.Color);
            }
            return null;
        }

        private void NewRuleColorPreview_Click(object sender, RoutedEventArgs e)
        {
            var currentBrush = NewRuleColorPreview.Background as SolidColorBrush;
            string currentHex = currentBrush != null
                ? string.Format("#{0:X2}{1:X2}{2:X2}", currentBrush.Color.R, currentBrush.Color.G, currentBrush.Color.B)
                : "#FF4444";

            string picked = PickColor(currentHex);
            if (picked != null)
            {
                try
                {
                    var color = System.Drawing.ColorTranslator.FromHtml(picked);
                    NewRuleColorPreview.Background = new SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(color.R, color.G, color.B));
                }
                catch { }
            }
        }

        private void NewRuleColorPreview_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            NewRuleColorPreview_Click(sender, (RoutedEventArgs)e);
        }

        private void RuleColorPreview_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is SettingsManager.ConnectionColorRule rule)
            {
                string picked = PickColor(rule.StatusBarColor);
                if (picked != null)
                {
                    rule.StatusBarColor = picked;
                    ConnectionColorRulesListView.Items.Refresh();
                    SetConnectionColorRulesDirty(true);
                }
            }
        }

        private void ButtonAddColorRule_Click(object sender, RoutedEventArgs e)
        {
            EnsureConnectionColorRulesLoaded();

            string serverPattern = NewRuleServerPattern.Text?.Trim();
            string databasePattern = NewRuleDatabasePattern.Text?.Trim();

            if (string.IsNullOrEmpty(serverPattern) && string.IsNullOrEmpty(databasePattern))
            {
                LocalizedMessageBox.Show("Fill in at least the server name or the database name.", "Connection Colors");
                return;
            }

            var brush = NewRuleColorPreview.Background as SolidColorBrush;
            string hex = "#FF4444";
            if (brush != null)
            {
                hex = string.Format("#{0:X2}{1:X2}{2:X2}", brush.Color.R, brush.Color.G, brush.Color.B);
            }

            if (_editingConnectionColorRule != null)
            {
                _editingConnectionColorRule.ServerNamePattern = serverPattern ?? string.Empty;
                _editingConnectionColorRule.DatabaseNamePattern = databasePattern ?? string.Empty;
                _editingConnectionColorRule.StatusBarColor = hex;
                ConnectionColorRulesListView.Items.Refresh();
                ConnectionColorRulesListView.SelectedItem = _editingConnectionColorRule;
            }
            else
            {
                var newRule = new SettingsManager.ConnectionColorRule
                {
                    ServerNamePattern = serverPattern ?? string.Empty,
                    DatabaseNamePattern = databasePattern ?? string.Empty,
                    StatusBarColor = hex,
                    IsEnabled = true
                };
                _connectionColorRules.Add(newRule);
                ConnectionColorRulesListView.SelectedItem = newRule;
            }

            SetConnectionColorRulesDirty(true);
            CancelConnectionColorRuleEdit();
            UpdateConnectionColorRuleButtons();
        }

        private void ButtonEditColorRule_Click(object sender, RoutedEventArgs e)
        {
            if (ConnectionColorRulesListView.SelectedItem is SettingsManager.ConnectionColorRule selectedRule)
            {
                NewRuleServerPattern.Text = selectedRule.ServerNamePattern;
                NewRuleDatabasePattern.Text = selectedRule.DatabaseNamePattern;

                try
                {
                    var color = System.Drawing.ColorTranslator.FromHtml(selectedRule.StatusBarColor);
                    NewRuleColorPreview.Background = new SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(color.R, color.G, color.B));
                }
                catch { }

                _editingConnectionColorRule = selectedRule;
                ConnectionColorRuleEditor.Header = LocalizationManager.T("Edit rule");
                button_CommitColorRule.Content = LocalizationManager.T("Save changes");
                button_CancelColorRuleEdit.Visibility = Visibility.Visible;
                NewRuleServerPattern.Focus();
            }
        }

        private void ButtonRemoveColorRule_Click(object sender, RoutedEventArgs e)
        {
            if (ConnectionColorRulesListView.SelectedItem is SettingsManager.ConnectionColorRule selectedRule)
            {
                _connectionColorRules.Remove(selectedRule);
                SetConnectionColorRulesDirty(true);
                CancelConnectionColorRuleEdit();
                UpdateConnectionColorRuleButtons();
            }
        }

        private void ButtonCancelColorRuleEdit_Click(object sender, RoutedEventArgs e) => CancelConnectionColorRuleEdit();

        private void CancelConnectionColorRuleEdit()
        {
            _editingConnectionColorRule = null;
            if (ConnectionColorRuleEditor == null)
                return;

            ConnectionColorRuleEditor.Header = LocalizationManager.T("Add new rule");
            button_CommitColorRule.Content = LocalizationManager.T("+ Add");
            button_CancelColorRuleEdit.Visibility = Visibility.Collapsed;
            NewRuleServerPattern.Text = string.Empty;
            NewRuleDatabasePattern.Text = string.Empty;
        }

        private void ButtonMoveColorRuleUp_Click(object sender, RoutedEventArgs e) => MoveSelectedConnectionColorRule(-1);

        private void ButtonMoveColorRuleDown_Click(object sender, RoutedEventArgs e) => MoveSelectedConnectionColorRule(1);

        private void MoveSelectedConnectionColorRule(int offset)
        {
            int oldIndex = ConnectionColorRulesListView.SelectedIndex;
            int newIndex = oldIndex + offset;
            if (oldIndex < 0 || newIndex < 0 || newIndex >= _connectionColorRules.Count)
                return;

            var selectedRule = _connectionColorRules[oldIndex];
            _connectionColorRules.Move(oldIndex, newIndex);
            ConnectionColorRulesListView.SelectedItem = selectedRule;
            SetConnectionColorRulesDirty(true);
            UpdateConnectionColorRuleButtons();
        }

        private void ConnectionColorRulesListView_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateConnectionColorRuleButtons();

        private void ConnectionColorRulesListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ConnectionColorRulesListView.SelectedItem != null)
                ButtonEditColorRule_Click(sender, e);
        }

        private void ConnectionColorRuleEnabled_Click(object sender, RoutedEventArgs e)
        {
            SetConnectionColorRulesDirty(true);
        }

        private void UpdateConnectionColorRuleButtons()
        {
            if (button_EditColorRule == null)
                return;

            int index = ConnectionColorRulesListView.SelectedIndex;
            bool hasSelection = index >= 0;
            button_EditColorRule.IsEnabled = hasSelection;
            button_RemoveColorRule.IsEnabled = hasSelection;
            button_MoveColorRuleUp.IsEnabled = hasSelection && index > 0;
            button_MoveColorRuleDown.IsEnabled = hasSelection && index < _connectionColorRules.Count - 1;
        }

        private void SetConnectionColorRulesDirty(bool dirty)
        {
            if (button_SaveConnectionColorRules == null)
                return;

            button_SaveConnectionColorRules.IsEnabled = dirty;
            ConnectionColorSaveStatus.Text = LocalizationManager.T(dirty ? "Unsaved changes" : "No unsaved changes");
        }

        private void Button_SaveConnectionColorRules_Click(object sender, RoutedEventArgs e)
        {
            var rules = new System.Collections.Generic.List<SettingsManager.ConnectionColorRule>(_connectionColorRules);
            if (!SettingsManager.SaveConnectionColorRules(rules))
            {
                LocalizedMessageBox.Show("The connection color rules could not be saved. Please try again.", "Connection Colors",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            GridAccess.ColorAllDocumentTabs();
            GridAccess.ScheduleReapplyAllTabColors();
            SetConnectionColorRulesDirty(false);
            SavedMessage();
        }

    }
}
