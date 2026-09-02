using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AxialSqlTools
{
    public class QueryHistoryViewModel : INotifyPropertyChanged
    {
        private const int PageSize = 100;
        private sealed class FileEntry { public DateTime StartTime { get; set; } public DateTime FinishTime { get; set; } public string ElapsedTime { get; set; } public long TotalRowsReturned { get; set; } public string ExecResult { get; set; } public string QueryText { get; set; } public string DataSource { get; set; } public string DatabaseName { get; set; } public string LoginName { get; set; } public string WorkstationId { get; set; } }
        private sealed class LoadResult { public List<QueryHistoryRecord> Records = new List<QueryHistoryRecord>(); public int Total; public string Storage; }

        private QueryHistoryRecord _selectedRecord;
        private DateTime? _filterFromDate, _filterToDate;
        private string _filterServer, _filterDatabase, _filterQueryText, _filterLogin, _filterResult = "All";
        private bool _isLoading;
        private string _statusMessage = "Preparing query history...", _statusKind = "Info", _resultSummary = "0 results", _lastRefreshed = "";
        private int _pageNumber = 1, _totalCount;
        private CancellationTokenSource _cancellation;

        public ObservableCollection<QueryHistoryRecord> QueryHistoryRecords { get; } = new ObservableCollection<QueryHistoryRecord>();
        public ObservableCollection<string> ResultOptions { get; } = new ObservableCollection<string> { "All", "Succeeded", "Failed", "Cancelled" };
        public QueryHistoryRecord SelectedRecord { get => _selectedRecord; set => Set(ref _selectedRecord, value, nameof(SelectedRecord)); }
        public DateTime? FilterFromDate { get => _filterFromDate; set => Set(ref _filterFromDate, value, nameof(FilterFromDate)); }
        public DateTime? FilterToDate { get => _filterToDate; set => Set(ref _filterToDate, value, nameof(FilterToDate)); }
        public string FilterServer { get => _filterServer; set => Set(ref _filterServer, value, nameof(FilterServer)); }
        public string FilterDatabase { get => _filterDatabase; set => Set(ref _filterDatabase, value, nameof(FilterDatabase)); }
        public string FilterQueryText { get => _filterQueryText; set => Set(ref _filterQueryText, value, nameof(FilterQueryText)); }
        public string FilterLogin { get => _filterLogin; set => Set(ref _filterLogin, value, nameof(FilterLogin)); }
        public string FilterResult { get => _filterResult; set => Set(ref _filterResult, value, nameof(FilterResult)); }
        public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value, nameof(IsLoading)); }
        public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value, nameof(StatusMessage)); }
        public string StatusKind { get => _statusKind; private set => Set(ref _statusKind, value, nameof(StatusKind)); }
        public string ResultSummary { get => _resultSummary; private set => Set(ref _resultSummary, value, nameof(ResultSummary)); }
        public string LastRefreshed { get => _lastRefreshed; private set => Set(ref _lastRefreshed, value, nameof(LastRefreshed)); }
        public int PageNumber { get => _pageNumber; private set => Set(ref _pageNumber, value, nameof(PageNumber)); }
        public string PageSummary => _totalCount == 0 ? "Page 0 / 0" : $"Page {PageNumber} / {Math.Max(1, (int)Math.Ceiling(_totalCount / (double)PageSize))}";
        public bool CanGoPrevious => PageNumber > 1 && !IsLoading;
        public bool CanGoNext => PageNumber * PageSize < _totalCount && !IsLoading;
        public ICommand RefreshCommand { get; }
        public ICommand ClearFilterCommand { get; }
        public ICommand PreviousPageCommand { get; }
        public ICommand NextPageCommand { get; }

        public QueryHistoryViewModel()
        {
            RefreshCommand = new RelayCommand(() => StartRefresh(true), () => !IsLoading);
            ClearFilterCommand = new RelayCommand(ClearFilters, () => !IsLoading);
            PreviousPageCommand = new RelayCommand(() => { PageNumber--; StartRefresh(false); }, () => CanGoPrevious);
            NextPageCommand = new RelayCommand(() => { PageNumber++; StartRefresh(false); }, () => CanGoNext);
            StartRefresh(true);
        }

        public void ApplyDatePreset(int days) { FilterFromDate = days <= 1 ? DateTime.Today : DateTime.Today.AddDays(-(days - 1)); FilterToDate = DateTime.Today; StartRefresh(true); }
        public void ApplyConnectionFilter(string server, string database) { FilterServer = server ?? ""; FilterDatabase = database ?? ""; StartRefresh(true); }
        private void ClearFilters() { FilterFromDate = FilterToDate = null; FilterServer = FilterDatabase = FilterQueryText = FilterLogin = ""; FilterResult = "All"; StartRefresh(true); }
        private async void StartRefresh(bool resetPage) { if (resetPage) PageNumber = 1; _cancellation?.Cancel(); _cancellation = new CancellationTokenSource(); await RefreshAsync(_cancellation.Token); }

        private async Task RefreshAsync(CancellationToken token)
        {
            if (FilterFromDate.HasValue && FilterToDate.HasValue && FilterFromDate.Value.Date > FilterToDate.Value.Date) { SetStatus("The From date cannot be later than the To date.", "Error"); return; }
            IsLoading = true; SetStatus("Loading query history...", "Loading"); CommandManager.InvalidateRequerySuggested();
            try
            {
                LoadResult data = await Task.Run(() => Load(token), token); token.ThrowIfCancellationRequested();
                QueryHistoryRecords.Clear(); foreach (var record in data.Records) QueryHistoryRecords.Add(record);
                _totalCount = data.Total; SelectedRecord = QueryHistoryRecords.FirstOrDefault();
                ResultSummary = _totalCount == 0 ? "No matching queries" : $"Showing {(PageNumber - 1) * PageSize + 1}-{(PageNumber - 1) * PageSize + data.Records.Count} of {_totalCount:N0}";
                LastRefreshed = $"Last refreshed {DateTime.Now:HH:mm:ss}"; OnPropertyChanged(nameof(PageSummary));
                string saveError = AxialSqlToolsPackage.QueryHistoryLastPersistenceError;
                if (!string.IsNullOrWhiteSpace(saveError)) SetStatus("History loaded, but the latest background save failed: " + saveError, "Warning");
                else if (_totalCount == 0) SetStatus("Recording is active, but no records match the current filters.", "Empty");
                else SetStatus("Recording is active · " + data.Storage, "Success");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { QueryHistoryRecords.Clear(); _totalCount = 0; ResultSummary = "Unable to load history"; SetStatus("Query history could not be loaded: " + ex.Message, "Error"); }
            finally { IsLoading = false; OnPropertyChanged(nameof(CanGoPrevious)); OnPropertyChanged(nameof(CanGoNext)); CommandManager.InvalidateRequerySuggested(); }
        }

        private LoadResult Load(CancellationToken token)
        {
            string mode = SettingsManager.GetQueryHistoryStorageMode();
            if (string.Equals(mode, "Disabled", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("recording is disabled. Enable it in Axial SQL Tools settings.");
            if (string.Equals(mode, "TextFiles", StringComparison.OrdinalIgnoreCase)) return LoadFiles(token);
            if (string.IsNullOrWhiteSpace(SettingsManager.GetQueryHistoryConnectionString())) throw new InvalidOperationException("database storage is selected but no connection is configured.");
            return LoadDatabase(token);
        }

        private LoadResult LoadDatabase(CancellationToken token)
        {
            string table = SettingsManager.GetQueryHistoryTableNameOrDefault(); var clauses = new List<string>(); var values = new List<SqlParameter>(); AddFilters(clauses, values);
            string where = clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses);
            var result = new LoadResult { Storage = "database storage" };
            using (var connection = new SqlConnection(SettingsManager.GetQueryHistoryConnectionString()))
            {
                connection.Open();
                using (var count = new SqlCommand($"SELECT COUNT_BIG(1) FROM {table}{where};", connection) { CommandTimeout = 15 }) { AddClones(count, values); result.Total = Convert.ToInt32(Math.Min(int.MaxValue, Convert.ToInt64(count.ExecuteScalar()))); }
                token.ThrowIfCancellationRequested();
                string sql = $"SELECT [QueryID],[StartTime],[FinishTime],[ElapsedTime],[TotalRowsReturned],[ExecResult],[QueryText],[DataSource],[DatabaseName],[LoginName],[WorkstationId] FROM {table}{where} ORDER BY [QueryID] DESC OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";
                using (var command = new SqlCommand(sql, connection) { CommandTimeout = 30 })
                {
                    AddClones(command, values); command.Parameters.Add("@Offset", SqlDbType.Int).Value = (PageNumber - 1) * PageSize; command.Parameters.Add("@PageSize", SqlDbType.Int).Value = PageSize;
                    using (var reader = command.ExecuteReader()) while (reader.Read()) { token.ThrowIfCancellationRequested(); result.Records.Add(Build(reader.GetInt32(0), reader.GetDateTime(1), reader.GetDateTime(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10))); }
                }
            }
            return result;
        }

        private LoadResult LoadFiles(CancellationToken token)
        {
            string folder = SettingsManager.GetQueryHistoryTextFileFolder(); var records = new List<QueryHistoryRecord>();
            if (Directory.Exists(folder)) foreach (string file in Directory.GetFiles(folder, "*.jsonl").OrderByDescending(x => x)) foreach (string line in File.ReadLines(file))
            {
                token.ThrowIfCancellationRequested(); if (string.IsNullOrWhiteSpace(line)) continue;
                try { var x = JsonConvert.DeserializeObject<FileEntry>(line); if (x != null) records.Add(Build(0, x.StartTime, x.FinishTime, x.ElapsedTime, x.TotalRowsReturned, x.ExecResult, x.QueryText, x.DataSource, x.DatabaseName, x.LoginName, x.WorkstationId)); } catch (JsonException) { }
            }
            var filtered = FilterMemory(records).OrderByDescending(x => x.Date).ToList();
            var page = filtered.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList(); for (int i = 0; i < page.Count; i++) page[i].Id = (PageNumber - 1) * PageSize + i + 1;
            return new LoadResult { Records = page, Total = filtered.Count, Storage = "local text files" };
        }

        private void AddFilters(List<string> clauses, List<SqlParameter> values)
        {
            if (FilterFromDate.HasValue) { clauses.Add("[StartTime]>=@From"); values.Add(new SqlParameter("@From", SqlDbType.DateTime) { Value = FilterFromDate.Value.Date }); }
            if (FilterToDate.HasValue) { clauses.Add("[StartTime]<@To"); values.Add(new SqlParameter("@To", SqlDbType.DateTime) { Value = FilterToDate.Value.Date.AddDays(1) }); }
            AddLike(clauses, values, "DataSource", "Server", FilterServer); AddLike(clauses, values, "DatabaseName", "Database", FilterDatabase); AddLike(clauses, values, "LoginName", "Login", FilterLogin);
            foreach (string term in Terms(FilterQueryText)) AddLike(clauses, values, "QueryText", "Query" + values.Count, term);
            if (!string.IsNullOrWhiteSpace(FilterResult) && FilterResult != "All") clauses.Add(ResultSql(FilterResult));
        }
        private static void AddLike(List<string> clauses, List<SqlParameter> values, string column, string name, string value) { if (string.IsNullOrWhiteSpace(value)) return; clauses.Add($"[{column}] LIKE @{name}"); values.Add(new SqlParameter("@" + name, SqlDbType.NVarChar, 4000) { Value = "%" + value.Trim() + "%" }); }
        private IEnumerable<QueryHistoryRecord> FilterMemory(IEnumerable<QueryHistoryRecord> source)
        {
            if (FilterFromDate.HasValue) source = source.Where(x => x.Date >= FilterFromDate.Value.Date); if (FilterToDate.HasValue) source = source.Where(x => x.Date < FilterToDate.Value.Date.AddDays(1));
            source = Contains(source, x => x.DataSource, FilterServer); source = Contains(source, x => x.DatabaseName, FilterDatabase); source = Contains(source, x => x.LoginName, FilterLogin); foreach (string term in Terms(FilterQueryText)) source = Contains(source, x => x.QueryText, term);
            return string.IsNullOrWhiteSpace(FilterResult) || FilterResult == "All" ? source : source.Where(x => ResultMatches(x.ExecResult, FilterResult));
        }
        private static IEnumerable<QueryHistoryRecord> Contains(IEnumerable<QueryHistoryRecord> source, Func<QueryHistoryRecord, string> pick, string value) => string.IsNullOrWhiteSpace(value) ? source : source.Where(x => (pick(x) ?? "").IndexOf(value.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
        private static string[] Terms(string text) => string.IsNullOrWhiteSpace(text) ? new string[0] : text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        private static string ResultSql(string result) => result == "Succeeded" ? "([ExecResult] LIKE '%success%' OR [ExecResult] LIKE '%succeed%')" : result == "Cancelled" ? "[ExecResult] LIKE '%cancel%'" : "([ExecResult] LIKE '%fail%' OR [ExecResult] LIKE '%error%')";
        private static bool ResultMatches(string value, string result) { value = value ?? ""; return result == "Succeeded" ? Has(value, "success") || Has(value, "succeed") : result == "Cancelled" ? Has(value, "cancel") : Has(value, "fail") || Has(value, "error"); }
        private static bool Has(string value, string term) => value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
        private static void AddClones(SqlCommand command, IEnumerable<SqlParameter> values) { foreach (var p in values) command.Parameters.Add(new SqlParameter(p.ParameterName, p.SqlDbType, p.Size) { Value = p.Value }); }
        private static QueryHistoryRecord Build(int id, DateTime start, DateTime finish, string elapsed, long rows, string result, string query, string server, string database, string login, string workstation)
        {
            query = query ?? ""; string shortText = string.Join(" ", query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)); if (shortText.Length > 180) shortText = shortText.Substring(0, 180);
            return new QueryHistoryRecord { Id = id, Date = start, FinishTime = finish, ElapsedTime = elapsed ?? "", TotalRowsReturned = rows, ExecResult = result ?? "", QueryText = query, QueryTextShort = shortText, DataSource = server ?? "", DatabaseName = database ?? "", LoginName = login ?? "", WorkstationId = workstation ?? "" };
        }
        private void SetStatus(string message, string kind) { StatusMessage = message; StatusKind = kind; }
        private bool Set<T>(ref T field, T value, string name) { if (Equals(field, value)) return false; field = value; OnPropertyChanged(name); return true; }
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
