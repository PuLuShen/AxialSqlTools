using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.VisualStudio.Shell;
using System;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml;

namespace AxialSqlTools
{
    public partial class QueryHistoryWindowControl : UserControl
    {
        private readonly ToolWindowThemeController _themeController;

        public QueryHistoryWindowControl()
        {
            InitializeComponent();
            _themeController = new ToolWindowThemeController(this, ApplyThemeBrushResources);
            DataContext = new QueryHistoryViewModel();
            LoadSqlHighlighting();
        }

        private void ApplyThemeBrushResources() => ToolWindowThemeResources.ApplySharedTheme(this);

        private void Filter_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (DataContext is QueryHistoryViewModel vm && vm.RefreshCommand.CanExecute(null)) vm.RefreshCommand.Execute(null);
            e.Handled = true;
        }

        private void DatePreset_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (sender is Button button && int.TryParse(button.Tag?.ToString(), out int days) && DataContext is QueryHistoryViewModel vm) vm.ApplyDatePreset(days);
        }

        private void CurrentConnection_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var connection = ScriptFactoryAccess.GetCurrentConnectionInfo();
                if (DataContext is QueryHistoryViewModel vm)
                    vm.ApplyConnectionFilter(connection.ServerName, connection.Database);
            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show("Could not read the current connection: " + ex.Message, "Query History");
            }
        }

        private void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SqlEditor.Text = (DataContext as QueryHistoryViewModel)?.SelectedRecord?.QueryText ?? string.Empty;
        }

        private void HistoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if ((DataContext as QueryHistoryViewModel)?.SelectedRecord != null) OpenSelectedQuery();
        }

        private void CopyQuery_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            string sql = (DataContext as QueryHistoryViewModel)?.SelectedRecord?.QueryText;
            if (!string.IsNullOrEmpty(sql)) System.Windows.Clipboard.SetText(sql);
        }

        private void OpenQuery_Click(object sender, System.Windows.RoutedEventArgs e) => OpenSelectedQuery();

        private void OpenSelectedQuery()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            QueryHistoryRecord record = (DataContext as QueryHistoryViewModel)?.SelectedRecord;
            if (record == null || string.IsNullOrWhiteSpace(record.QueryText)) return;
            try
            {
                var current = ScriptFactoryAccess.GetCurrentConnectionInfo();
                ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql, current.ActiveConnectionInfo, null);
                var document = (EnvDTE.TextDocument)ServiceCache.ExtensibilityModel.Application.ActiveDocument.Object(null);
                document.EndPoint.CreateEditPoint().Insert($"-- Query history source: {record.DataSource} / {record.DatabaseName}{Environment.NewLine}" + record.QueryText);
            }
            catch (Exception ex)
            {
                LocalizedMessageBox.Show("Could not open the query: " + ex.Message, "Query History");
            }
        }

        private void WrapSql_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (SqlEditor != null) SqlEditor.WordWrap = WrapSql.IsChecked == true;
        }

        private void LoadSqlHighlighting()
        {
            try
            {
                using (Stream stream = typeof(QueryHistoryWindowControl).Assembly.GetManifestResourceStream("AxialSqlTools.QuickSearch.sql.xshd"))
                using (var reader = new XmlTextReader(stream))
                    SqlEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
            catch { }
        }
    }
}
