namespace AxialSqlTools
{
    using Microsoft.Data.SqlClient;
    using Microsoft.VisualStudio.Shell;
    using System;
    using System.Data;
    using System.Data.Common;
    using System.Collections;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Documents;

    /// <summary>Transactional SQL Server-to-SQL Server data transfer.</summary>
    public partial class DataTransferWindowControl : UserControl
    {
        private readonly ToolWindowThemeController themeController;
        private ScriptFactoryAccess.ConnectionInfo source;
        private ScriptFactoryAccess.ConnectionInfo target;
        private CancellationTokenSource cancellation;

        public DataTransferWindowControl()
        {
            InitializeComponent();
            themeController = new ToolWindowThemeController(this, () => ToolWindowThemeResources.ApplySharedTheme(this));
        }

        private void Button_SelectSource_Click(object sender, RoutedEventArgs e)
        {
            source = ScriptFactoryAccess.GetCurrentConnectionInfoFromObjectExplorer();
            Label_SourceDescription.Text = source?.DisplayName ?? "No SQL Server source selected";
            UpdateAvailability();
        }

        private void Button_SelectTarget_Click(object sender, RoutedEventArgs e)
        {
            target = ScriptFactoryAccess.GetCurrentConnectionInfoFromObjectExplorer();
            Label_TargetDescription.Text = target?.DisplayName ?? "No SQL Server target selected";
            UpdateAvailability();
        }

        private void TextBox_TargetTable_TextChanged(object sender, TextChangedEventArgs e) => UpdateAvailability();

        private void UpdateAvailability()
        {
            if (Button_CopyData != null)
                Button_CopyData.IsEnabled = cancellation == null && source != null && target != null && !string.IsNullOrWhiteSpace(TextBox_TargetTable?.Text);
        }

        private async void ButtonCopyData_Click(object sender, RoutedEventArgs e)
        {
            if (source == null || target == null || string.IsNullOrWhiteSpace(TextBox_TargetTable.Text)) return;
            cancellation = new CancellationTokenSource();
            SetBusy(true);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                string sql = new TextRange(RichTextBox_SourceQuery.Document.ContentStart, RichTextBox_SourceQuery.Document.ContentEnd).Text.Trim();
                if (string.IsNullOrWhiteSpace(sql)) throw new InvalidOperationException("Source query cannot be empty.");
                long copied = await CopyAsync(sql, TextBox_TargetTable.Text.Trim(), cancellation.Token);
                Label_CopyProgress.Text = $"Completed: {copied:#,0} rows committed in {stopwatch.Elapsed.TotalSeconds:#,0.0} seconds.";
            }
            catch (OperationCanceledException) { Label_CopyProgress.Text = "Cancelled. SQL Server rolled back the target transaction."; }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("Data Transfer", "SQL Server transfer failed", ex);
                Label_CopyProgress.Text = "Transfer failed: " + ex.Message;
                LocalizedMessageBox.Show(ex.Message, "SQL Server Data Transfer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                stopwatch.Stop();
                cancellation.Dispose();
                cancellation = null;
                SetBusy(false);
            }
        }

        private async Task<long> CopyAsync(string sourceSql, string targetName, CancellationToken token)
        {
            string quotedTarget = DatabaseIdentifier.SqlServerLocalObject(targetName);
            string literalTarget = quotedTarget.Replace("'", "''");
            using (var sourceConnection = new SqlConnection(source.FullConnectionString))
            using (var targetConnection = new SqlConnection(target.FullConnectionString))
            {
                await sourceConnection.OpenAsync(token);
                await targetConnection.OpenAsync(token);
                using (var sourceCommand = new SqlCommand(sourceSql, sourceConnection) { CommandTimeout = 120 })
                using (SqlDataReader sourceReader = await sourceCommand.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token))
                using (var reader = new CountingDataReader(sourceReader))
                using (SqlTransaction transaction = targetConnection.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    DataTable schema = reader.GetSchemaTable() ?? throw new InvalidOperationException("The source query did not return a tabular result.");
                    string[] sourceColumns = schema.Rows.Cast<DataRow>().Select(row => Convert.ToString(row["ColumnName"])).ToArray();
                    if (sourceColumns.Any(string.IsNullOrWhiteSpace))
                        throw new InvalidOperationException("Every source expression must have a column name or alias.");
                    if (sourceColumns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sourceColumns.Length)
                        throw new InvalidOperationException("The source query returns duplicate column names. Add unique aliases before copying.");
                    if (CheckBox_CreateTargetTable.IsChecked == true)
                    {
                        string definitions = string.Join("," + Environment.NewLine, schema.Rows.Cast<DataRow>().Select(row =>
                            DatabaseIdentifier.SqlServerPart(Convert.ToString(row["ColumnName"])) + " " + GridAccess.GetColumnSqlType(row)));
                        using (var create = new SqlCommand($"IF OBJECT_ID(N'{literalTarget}','U') IS NULL CREATE TABLE {quotedTarget} ({definitions});", targetConnection, transaction))
                        {
                            create.CommandTimeout = 120;
                            await create.ExecuteNonQueryAsync(token);
                        }
                    }
                    using (var verify = new SqlCommand($"IF OBJECT_ID(N'{literalTarget}','U') IS NULL THROW 50000,'Target table does not exist.',1;", targetConnection, transaction))
                        await verify.ExecuteNonQueryAsync(token);
                    if (CheckBox_TruncateTargetTable.IsChecked == true)
                        using (var truncate = new SqlCommand($"TRUNCATE TABLE {quotedTarget};", targetConnection, transaction)) await truncate.ExecuteNonQueryAsync(token);

                    SqlBulkCopyOptions options = SqlBulkCopyOptions.TableLock | (CheckBox_KeepIdentity.IsChecked == true ? SqlBulkCopyOptions.KeepIdentity : SqlBulkCopyOptions.Default);
                    if (CheckBox_CheckConstraints.IsChecked == true) options |= SqlBulkCopyOptions.CheckConstraints;
                    if (CheckBox_FireTriggers.IsChecked == true) options |= SqlBulkCopyOptions.FireTriggers;
                    using (var bulk = new SqlBulkCopy(targetConnection, options, transaction))
                    {
                        bulk.DestinationTableName = quotedTarget;
                        bulk.BatchSize = 5000;
                        bulk.NotifyAfter = 5000;
                        bulk.BulkCopyTimeout = 120;
                        foreach (DataRow row in schema.Rows)
                        {
                            string name = Convert.ToString(row["ColumnName"]);
                            bulk.ColumnMappings.Add(name, name);
                        }
                        bulk.SqlRowsCopied += (s, e) => Dispatcher.BeginInvoke(new Action(() => Label_CopyProgress.Text = $"Copied {e.RowsCopied:#,0} rows; transaction not committed yet..."));
                        await bulk.WriteToServerAsync(reader, token);
                    }
                    transaction.Commit();
                    return reader.RowsRead;
                }
            }
        }

        private sealed class CountingDataReader : DbDataReader
        {
            private readonly DbDataReader inner;
            public CountingDataReader(DbDataReader inner) { this.inner = inner ?? throw new ArgumentNullException(nameof(inner)); }
            public long RowsRead { get; private set; }
            public override bool Read() { bool value = inner.Read(); if (value) RowsRead++; return value; }
            public override async Task<bool> ReadAsync(CancellationToken cancellationToken) { bool value = await inner.ReadAsync(cancellationToken); if (value) RowsRead++; return value; }
            public override bool NextResult() => inner.NextResult();
            public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => inner.NextResultAsync(cancellationToken);
            public override int Depth => inner.Depth;
            public override int FieldCount => inner.FieldCount;
            public override bool HasRows => inner.HasRows;
            public override bool IsClosed => inner.IsClosed;
            public override int RecordsAffected => inner.RecordsAffected;
            public override object this[int ordinal] => inner[ordinal];
            public override object this[string name] => inner[name];
            public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
            public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
            public override long GetBytes(int ordinal, long dataOffset, byte[] buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
            public override char GetChar(int ordinal) => inner.GetChar(ordinal);
            public override long GetChars(int ordinal, long dataOffset, char[] buffer, int bufferOffset, int length) => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
            public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
            public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
            public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
            public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
            public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
            public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
            public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
            public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
            public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
            public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
            public override string GetName(int ordinal) => inner.GetName(ordinal);
            public override int GetOrdinal(string name) => inner.GetOrdinal(name);
            public override string GetString(int ordinal) => inner.GetString(ordinal);
            public override object GetValue(int ordinal) => inner.GetValue(ordinal);
            public override int GetValues(object[] values) => inner.GetValues(values);
            public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
            public override DataTable GetSchemaTable() => inner.GetSchemaTable();
            public override IEnumerator GetEnumerator() => ((IEnumerable)inner).GetEnumerator();
            public override void Close() => inner.Close();
        }

        private void Button_Cancel_Click(object sender, RoutedEventArgs e)
        {
            cancellation?.Cancel();
            Label_CopyProgress.Text = "Cancelling...";
        }

        private void SetBusy(bool busy)
        {
            Button_SelectSource.IsEnabled = !busy;
            Button_SelectTarget.IsEnabled = !busy;
            TextBox_TargetTable.IsEnabled = !busy;
            Button_CopyData.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            Button_Cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            UpdateAvailability();
        }
    }
}
