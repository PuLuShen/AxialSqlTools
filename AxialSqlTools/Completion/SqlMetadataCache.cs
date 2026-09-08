using Microsoft.Data.SqlClient;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace AxialSqlTools.Completion
{
    internal static class SqlMetadataCache
    {
        private sealed class Entry
        {
            public DateTime CreatedUtc;
            public Lazy<Task<MetadataSnapshot>> Work;
            public Task<MetadataSnapshot> Task => Work.Value;
        }
        private static readonly ConcurrentDictionary<string, Entry> Cache = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

        public static Task<MetadataSnapshot> GetAsync(ScriptFactoryAccess.ConnectionInfo info, CancellationToken token)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString)) return Task.FromResult(MetadataSnapshot.Empty);
            string key = GetKey(info.FullConnectionString);
            Entry entry = GetOrCreate(key, () => Load(info),
                LocalizationManager.T("Loading SQL completion metadata..."));
            return AwaitWithCancellation(entry.Task, token);
        }

        public static Task<MetadataSnapshot> GetDatabaseAsync(ScriptFactoryAccess.ConnectionInfo info, string database, CancellationToken token)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString) || string.IsNullOrWhiteSpace(database)) return Task.FromResult(MetadataSnapshot.Empty);
            string key = GetKey(info.FullConnectionString, database);
            Entry entry = GetOrCreate(key, () => Load(info, database),
                LocalizationManager.Format("Loading SQL completion metadata for database {0}...", database));
            return AwaitWithCancellation(entry.Task, token);
        }

        public static Task<MetadataSnapshot> GetLinkedServerAsync(ScriptFactoryAccess.ConnectionInfo info, string linkedServer, string database, CancellationToken token)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString) || string.IsNullOrWhiteSpace(linkedServer)) return Task.FromResult(MetadataSnapshot.Empty);
            string key = GetKey(info.FullConnectionString) + "|linked|" + linkedServer + "|" + (database ?? string.Empty);
            Entry entry = GetOrCreate(key, () => LoadLinked(info, linkedServer, database),
                LocalizationManager.Format("Loading SQL completion metadata from linked server {0}...", linkedServer));
            return AwaitWithCancellation(entry.Task, token);
        }

        private static Entry GetOrCreate(string key, Func<MetadataSnapshot> loader, string statusMessage)
        {
            var replacement = new Entry
            {
                CreatedUtc = DateTime.UtcNow,
                Work = new Lazy<Task<MetadataSnapshot>>(
                    () => LoadWithFeedbackAsync(loader, statusMessage),
                    LazyThreadSafetyMode.ExecutionAndPublication)
            };
            return Cache.AddOrUpdate(key, replacement, (_, old) => IsFresh(old) ? old : replacement);
        }

        private static async Task<MetadataSnapshot> LoadWithFeedbackAsync(Func<MetadataSnapshot> loader, string statusMessage)
        {
            using (StatusFeedback.Begin(statusMessage))
                return await Task.Run(loader, CancellationToken.None).ConfigureAwait(false);
        }

        private static bool IsFresh(Entry entry)
        {
            TimeSpan age = DateTime.UtcNow - entry.CreatedUtc;
            if (age >= Lifetime) return false;
            if (!entry.Work.IsValueCreated) return true;
            Task<MetadataSnapshot> task = entry.Task;
            if (task.IsFaulted || task.IsCanceled) return age < TimeSpan.FromSeconds(15);
            if (task.IsCompleted && !string.IsNullOrWhiteSpace(task.Result?.ErrorMessage)) return age < TimeSpan.FromSeconds(30);
            return true;
        }

        public static void Invalidate(ScriptFactoryAccess.ConnectionInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString)) return;
            var builder = new SqlConnectionStringBuilder(info.FullConnectionString);
            string prefix = builder.DataSource + "|";
            foreach (string key in Cache.Keys)
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    Cache.TryRemove(key, out Entry _);
        }

        public static void InvalidateAll() => Cache.Clear();

        internal static bool ShouldInvalidateAfterExecution(string sql)
        {
            string executableSql = SqlTextContext.MaskCommentsAndStrings(sql ?? string.Empty);
            return System.Text.RegularExpressions.Regex.IsMatch(executableSql,
                       @"\b(?:CREATE|ALTER|DROP|RENAME|TRUNCATE)\s+(?:TABLE|VIEW|PROCEDURE|PROC|FUNCTION|SYNONYM|TYPE|SCHEMA)\b",
                       System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                   || System.Text.RegularExpressions.Regex.IsMatch(executableSql, @"\bsp_rename\b",
                       System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        public static bool TryGetCached(ScriptFactoryAccess.ConnectionInfo info, out MetadataSnapshot snapshot)
        {
            snapshot = null;
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString)) return false;
            if (!Cache.TryGetValue(GetKey(info.FullConnectionString), out Entry entry) || !entry.Work.IsValueCreated) return false;
            Task<MetadataSnapshot> task = entry.Task;
            if (!task.IsCompleted || task.IsCanceled || task.IsFaulted) return false;
            snapshot = task.Result;
            return snapshot != null;
        }

        public static async Task<MetadataSnapshot> RefreshAsync(ScriptFactoryAccess.ConnectionInfo info, CancellationToken token)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString)) return MetadataSnapshot.Empty;
            Invalidate(info);
            string key = GetKey(info.FullConnectionString);
            Entry entry = GetOrCreate(key, () => Load(info),
                LocalizationManager.T("Refreshing SQL completion metadata..."));
            return await AwaitWithCancellation(entry.Task, token).ConfigureAwait(false);
        }

        public static async Task ResolveSynonymColumnsAsync(ScriptFactoryAccess.ConnectionInfo info, MetadataSnapshot snapshot, CompletionContext context, CancellationToken token)
        {
            if (info == null || snapshot == null || context == null) return;
            string requested = (context.Qualifier ?? context.TargetObject ?? string.Empty).Replace("[", "").Replace("]", "");
            DatabaseObjectMetadata synonym = snapshot.Objects.FirstOrDefault(o => o.Kind == CompletionItemKind.Synonym && o.Columns.Count == 0
                && !string.IsNullOrWhiteSpace(o.SynonymBaseObjectName)
                && (string.Equals(o.Name, requested, StringComparison.OrdinalIgnoreCase) || requested.EndsWith("." + o.Name, StringComparison.OrdinalIgnoreCase)));
            if (synonym == null) return;
            var columns = await Task.Run(() => DescribeSynonym(info, synonym.SynonymBaseObjectName, token), token).ConfigureAwait(false);
            lock (synonym.Columns)
                if (synonym.Columns.Count == 0) synonym.Columns.AddRange(columns);
        }

        private static List<ColumnMetadata> DescribeSynonym(ScriptFactoryAccess.ConnectionInfo info, string baseObjectName, CancellationToken token)
        {
            var result = new List<ColumnMetadata>();
            using (var connection = CreateMetadataConnection(info))
            using (var command = new SqlCommand("EXEC sys.sp_describe_first_result_set @tsql=@sql, @params=NULL, @browse_information_mode=0;", connection))
            {
                command.CommandTimeout = 10;
                command.Parameters.AddWithValue("@sql", "SELECT * FROM " + baseObjectName);
                using (token.Register(() => { try { command.Cancel(); } catch { } }))
                {
                    token.ThrowIfCancellationRequested();
                connection.Open();
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        if (!reader.IsDBNull(2)) result.Add(new ColumnMetadata { Name = reader.GetString(2), IsNullable = !reader.IsDBNull(3) && reader.GetBoolean(3), DataType = reader.IsDBNull(5) ? "" : reader.GetString(5) });
                }
            }
            return result;
        }

        private static string GetKey(string connectionString)
        {
            var b = new SqlConnectionStringBuilder(connectionString)
            {
                TrustServerCertificate = SettingsManager.GetSqlCompletionSettings().trustServerCertificate
            };
            return string.Join("|", b.DataSource, b.InitialCatalog, b.IntegratedSecurity, b.UserID,
                b.Authentication, b.ApplicationIntent, b.Encrypt, b.TrustServerCertificate);
        }

        private static string GetKey(string connectionString, string database)
        {
            var b = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database };
            return GetKey(b.ConnectionString);
        }

        private static async Task<MetadataSnapshot> AwaitWithCancellation(Task<MetadataSnapshot> task, CancellationToken token)
        {
            var cancelled = new TaskCompletionSource<bool>();
            using (token.Register(() => cancelled.TrySetResult(true)))
            {
                if (task != await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false)) throw new OperationCanceledException(token);
            }
            return await task.ConfigureAwait(false);
        }

        private static MetadataSnapshot Load(ScriptFactoryAccess.ConnectionInfo info, string database = null)
        {
            var builder = new SqlConnectionStringBuilder(info.FullConnectionString);
            if (!string.IsNullOrWhiteSpace(database)) builder.InitialCatalog = database;
            var result = new MetadataSnapshot { LoadedUtc = DateTime.UtcNow, Scope = builder.DataSource + "/" + builder.InitialCatalog };
            var errors = new List<string>();
            try
            {
                using (var connection = CreateMetadataConnection(info, database))
                {
                    connection.Open();
                    TryLoadDatabases(connection, result, errors);
                    TryLoadLinkedServers(connection, result, errors);
                    TryLoadObjects(connection, result, errors);
                    TryLoadDefinitions(connection, result, errors);
                    TryLoadForeignKeys(connection, result, errors);
                    TryLoadColumnDetails(connection, result);
                    TryLoadIndexes(connection, result);
                    TryLoadCheckConstraints(connection, result);
                    TryLoadRowCounts(connection, result);
                    TryLoadParameters(connection, result, errors);
                    TryLoadSupplementalObjects(connection, result, errors);
                    TryLoadCompatibilityLevel(connection, result, errors);
                }
            }
            catch (Exception ex) { RecordFailure("connection", ex, errors); }
            if (errors.Count > 0)
            {
                result.ErrorMessage = string.Join("; ", errors);
                result.IsPartial = result.Objects.Count > 0 || result.Parameters.Count > 0 || result.ForeignKeys.Count > 0;
            }
            return result;
        }

        private static MetadataSnapshot LoadLinked(ScriptFactoryAccess.ConnectionInfo info, string linkedServer, string database)
        {
            var result = new MetadataSnapshot { LoadedUtc = DateTime.UtcNow, Scope = linkedServer + "/" + (database ?? string.Empty) };
            var errors = new List<string>();
            try
            {
                string server = DatabaseIdentifier.SqlServerPart(linkedServer);
                using (var connection = CreateMetadataConnection(info))
                {
                    connection.Open();
                    if (string.IsNullOrWhiteSpace(database))
                    {
                        using (var command = new SqlCommand("SELECT [name] FROM " + server + ".[master].[sys].[databases] WHERE [state]=0 ORDER BY [name];", connection))
                        using (var reader = command.ExecuteReader())
                        {
                            var names = new List<string>();
                            while (reader.Read()) names.Add(reader.GetString(0));
                            result.LinkedServerDatabases[linkedServer] = names;
                        }
                    }
                    else
                    {
                        string catalog = server + "." + DatabaseIdentifier.SqlServerPart(database);
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandTimeout = 20;
                            command.CommandText = "SELECT s.name,o.name,o.type,c.name,ty.name,c.max_length,c.precision,c.scale,c.is_nullable,c.is_identity,c.is_computed,o.is_ms_shipped "
                                + "FROM " + catalog + ".[sys].[all_objects] o JOIN " + catalog + ".[sys].[schemas] s ON s.schema_id=o.schema_id "
                                + "LEFT JOIN " + catalog + ".[sys].[all_columns] c ON c.object_id=o.object_id "
                                + "LEFT JOIN " + catalog + ".[sys].[types] ty ON ty.user_type_id=c.user_type_id "
                                + "WHERE o.type IN ('U','V','P','PC','X','FN','IF','TF','FS','FT') AND (o.is_ms_shipped=0 OR s.name='sys') ORDER BY s.name,o.name,c.column_id;";
                            using (var reader = command.ExecuteReader())
                            {
                                DatabaseObjectMetadata current = null; string objectKey = null;
                                while (reader.Read())
                                {
                                    string schema = reader.GetString(0), name = reader.GetString(1), type = reader.GetString(2), next = schema + "." + name + "|" + type;
                                    if (!string.Equals(objectKey, next, StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (!result.Schemas.Contains(schema)) result.Schemas.Add(schema);
                                        current = new DatabaseObjectMetadata { Server = linkedServer, Database = database, Schema = schema, Name = name, Kind = ToKind(type), IsTableValuedFunction = type == "IF" || type == "TF" || type == "FT", IsSystem = !reader.IsDBNull(11) && reader.GetBoolean(11) };
                                        result.Objects.Add(current); objectKey = next;
                                    }
                                    if (!reader.IsDBNull(3)) current.Columns.Add(new ColumnMetadata { Name = reader.GetString(3), DataType = reader.IsDBNull(4) ? "" : reader.GetString(4), MaxLength = reader.IsDBNull(5) ? (short)0 : reader.GetInt16(5), Precision = reader.IsDBNull(6) ? (byte)0 : reader.GetByte(6), Scale = reader.IsDBNull(7) ? (byte)0 : reader.GetByte(7), IsNullable = !reader.IsDBNull(8) && reader.GetBoolean(8), IsIdentity = !reader.IsDBNull(9) && reader.GetBoolean(9), IsComputed = !reader.IsDBNull(10) && reader.GetBoolean(10) });
                                }
                            }
                        }

                        using (var command = connection.CreateCommand())
                        {
                            command.CommandTimeout = 20;
                            command.CommandText = "SELECT s.name+'.'+o.name,p.name,ty.name,p.is_output,p.max_length,p.precision,p.scale,p.parameter_id,p.has_default_value "
                                + "FROM " + catalog + ".[sys].[all_parameters] p JOIN " + catalog + ".[sys].[all_objects] o ON o.object_id=p.object_id "
                                + "JOIN " + catalog + ".[sys].[schemas] s ON s.schema_id=o.schema_id "
                                + "JOIN " + catalog + ".[sys].[types] ty ON ty.user_type_id=p.user_type_id "
                                + "WHERE o.type IN ('P','PC','X','FN','IF','TF','FS','FT') AND p.parameter_id>0 ORDER BY o.object_id,p.parameter_id;";
                            using (var reader = command.ExecuteReader())
                                while (reader.Read()) result.Parameters.Add(new RoutineParameterMetadata
                                {
                                    ObjectName = linkedServer + "." + database + "." + reader.GetString(0),
                                    Name = reader.GetString(1),
                                    DataType = reader.GetString(2),
                                    IsOutput = reader.GetBoolean(3), MaxLength = reader.GetInt16(4), Precision = reader.GetByte(5), Scale = reader.GetByte(6), Ordinal = reader.GetInt32(7), HasDefaultValue = reader.GetBoolean(8)
                                });
                        }
                    }
                }
            }
            catch (Exception ex) { RecordFailure("linked server " + linkedServer, ex, errors); }
            if (errors.Count > 0) { result.ErrorMessage = string.Join("; ", errors); result.IsPartial = result.Objects.Count > 0; }
            return result;
        }

        private static void TryLoadDatabases(SqlConnection connection, MetadataSnapshot result, List<string> errors)
        {
            try
            {
                using (var command = new SqlCommand("SELECT [name] FROM sys.databases WHERE [state]=0 AND HAS_DBACCESS([name])=1 ORDER BY [name];", connection))
                {
                    command.CommandTimeout = 10;
                    using (var reader = command.ExecuteReader()) while (reader.Read()) result.Databases.Add(reader.GetString(0));
                }
            }
            catch (Exception ex) { RecordFailure("databases", ex, errors); }
        }

        private static void TryLoadLinkedServers(SqlConnection connection, MetadataSnapshot result, List<string> errors)
        {
            try
            {
                using (var command = new SqlCommand("SELECT [name] FROM sys.servers WHERE server_id > 0 AND is_linked=1 ORDER BY [name];", connection))
                using (var reader = command.ExecuteReader()) while (reader.Read()) result.LinkedServers.Add(reader.GetString(0));
            }
            catch (Exception ex) { RecordFailure("linked servers", ex, errors); }
        }

        private static void TryLoadObjects(SqlConnection connection, MetadataSnapshot result, List<string> errors)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    command.CommandText = @"
 SELECT s.name, o.name, o.type, c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed, o.is_ms_shipped, c.column_id
FROM sys.all_objects o
JOIN sys.schemas s ON s.schema_id=o.schema_id
LEFT JOIN sys.all_columns c ON c.object_id=o.object_id
LEFT JOIN sys.types ty ON ty.user_type_id=c.user_type_id
WHERE o.type IN ('U','V','P','PC','X','FN','IF','TF','FS','FT')
  AND (o.is_ms_shipped=0 OR s.name='sys')
ORDER BY s.name,o.name,c.column_id;";
                    using (var reader = command.ExecuteReader())
                    {
                        DatabaseObjectMetadata current = null; string key = null;
                        while (reader.Read())
                        {
                            string schema = reader.GetString(0), name = reader.GetString(1), type = reader.GetString(2);
                            string nextKey = schema + "." + name + "|" + type;
                            if (!string.Equals(key, nextKey, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!result.Schemas.Contains(schema)) result.Schemas.Add(schema);
                                current = new DatabaseObjectMetadata { Database = connection.Database, Schema = schema, Name = name, Kind = ToKind(type), IsTableValuedFunction = type == "IF" || type == "TF" || type == "FT", IsSystem = !reader.IsDBNull(11) && reader.GetBoolean(11) };
                                result.Objects.Add(current); key = nextKey;
                            }
                            if (!reader.IsDBNull(3)) current.Columns.Add(new ColumnMetadata { Name = reader.GetString(3), DataType = reader.IsDBNull(4) ? string.Empty : reader.GetString(4), MaxLength = reader.IsDBNull(5) ? (short)0 : reader.GetInt16(5), Precision = reader.IsDBNull(6) ? (byte)0 : reader.GetByte(6), Scale = reader.IsDBNull(7) ? (byte)0 : reader.GetByte(7), IsNullable = !reader.IsDBNull(8) && reader.GetBoolean(8), IsIdentity = !reader.IsDBNull(9) && reader.GetBoolean(9), IsComputed = !reader.IsDBNull(10) && reader.GetBoolean(10), Ordinal = reader.IsDBNull(12) ? 0 : reader.GetInt32(12) });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RecordFailure("objects and columns", ex, errors);
                TryLoadObjectNamesFallback(connection, result, errors);
            }
        }

        private static void TryLoadDefinitions(SqlConnection connection, MetadataSnapshot result, List<string> errors)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    command.CommandText = @"
SELECT s.name, o.name, OBJECT_DEFINITION(o.object_id)
FROM sys.all_objects o
JOIN sys.schemas s ON s.schema_id=o.schema_id
WHERE o.type IN ('V','P','PC','FN','IF','TF','FS','FT')
  AND (o.is_ms_shipped=0 OR s.name='sys');";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            DatabaseObjectMetadata item = result.Objects.FirstOrDefault(o =>
                                string.Equals(o.Schema, reader.GetString(0), StringComparison.OrdinalIgnoreCase)
                                && string.Equals(o.Name, reader.GetString(1), StringComparison.OrdinalIgnoreCase));
                            if (item != null && !reader.IsDBNull(2)) item.Definition = reader.GetString(2);
                        }
                    }
                }
            }
            catch (Exception ex) { RecordFailure("object definitions", ex, errors); }
        }

        private static void TryLoadObjectNamesFallback(SqlConnection connection, MetadataSnapshot result, List<string> errors)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 10;
                    command.CommandText = @"
SELECT s.name,o.name,o.type,o.is_ms_shipped
FROM sys.all_objects o
JOIN sys.schemas s ON s.schema_id=o.schema_id
WHERE o.type IN ('U','V','P','PC','X','FN','IF','TF','FS','FT')
  AND (o.is_ms_shipped=0 OR s.name='sys')
ORDER BY s.name,o.name;";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string schema = reader.GetString(0);
                            string name = reader.GetString(1);
                            string type = reader.GetString(2);
                            if (!result.Schemas.Contains(schema)) result.Schemas.Add(schema);
                            if (result.Objects.Any(o => string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)
                                && o.Kind == ToKind(type))) continue;
                            result.Objects.Add(new DatabaseObjectMetadata
                            {
                                Database = connection.Database,
                                Schema = schema,
                                Name = name,
                                Kind = ToKind(type),
                                IsTableValuedFunction = type == "IF" || type == "TF" || type == "FT",
                                IsSystem = !reader.IsDBNull(3) && reader.GetBoolean(3)
                            });
                        }
                    }
                }
            }
            catch (Exception ex) { RecordFailure("object names fallback", ex, errors); }
        }

        private static void TryLoadForeignKeys(SqlConnection connection, MetadataSnapshot result, List<string> errors)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    command.CommandText = @"
 SELECT DB_NAME()+'.'+ps.name+'.'+po.name,DB_NAME()+'.'+rs.name+'.'+ro.name,fk.object_id,pc.name,rc.name
FROM sys.foreign_keys fk JOIN sys.objects po ON po.object_id=fk.parent_object_id
JOIN sys.schemas ps ON ps.schema_id=po.schema_id JOIN sys.objects ro ON ro.object_id=fk.referenced_object_id
JOIN sys.schemas rs ON rs.schema_id=ro.schema_id JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
JOIN sys.columns pc ON pc.object_id=po.object_id AND pc.column_id=fkc.parent_column_id
JOIN sys.columns rc ON rc.object_id=ro.object_id AND rc.column_id=fkc.referenced_column_id
ORDER BY fk.object_id,fkc.constraint_column_id;";
                    using (var reader = command.ExecuteReader())
                    {
                        ForeignKeyMetadata current = null; int id = -1;
                        while (reader.Read()) { int next = reader.GetInt32(2); if (next != id) { current = new ForeignKeyMetadata { ParentObject = reader.GetString(0), ReferencedObject = reader.GetString(1) }; result.ForeignKeys.Add(current); id = next; } current.ParentColumns.Add(reader.GetString(3)); current.ReferencedColumns.Add(reader.GetString(4)); }
                    }
                    foreach (ForeignKeyMetadata foreignKey in result.ForeignKeys)
                    {
                        DatabaseObjectMetadata parent = FindObject(result, foreignKey.ParentObject);
                        if (parent == null) continue;
                        foreach (string columnName in foreignKey.ParentColumns)
                        {
                            ColumnMetadata column = parent.Columns.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
                            if (column != null) column.IsForeignKey = true;
                        }
                    }
                }
            }
            catch (Exception ex) { RecordFailure("foreign keys", ex, errors); }
        }

        private static void TryLoadColumnDetails(SqlConnection connection, MetadataSnapshot result)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    command.CommandText = @"
SELECT s.name,o.name,c.name,dc.definition,
       CONVERT(nvarchar(4000),cep.value),CONVERT(nvarchar(4000),oep.value)
FROM sys.all_objects o
JOIN sys.schemas s ON s.schema_id=o.schema_id
LEFT JOIN sys.all_columns c ON c.object_id=o.object_id
LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
LEFT JOIN sys.extended_properties cep ON cep.major_id=o.object_id AND cep.minor_id=c.column_id AND cep.name=N'MS_Description'
LEFT JOIN sys.extended_properties oep ON oep.major_id=o.object_id AND oep.minor_id=0 AND oep.name=N'MS_Description'
WHERE o.type IN ('U','V','P','PC','FN','IF','TF','FS','FT');";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            DatabaseObjectMetadata item = FindObject(result, reader.GetString(0) + "." + reader.GetString(1));
                            if (item == null) continue;
                            if (!reader.IsDBNull(5)) item.Description = reader.GetString(5);
                            if (reader.IsDBNull(2)) continue;
                            ColumnMetadata column = item.Columns.FirstOrDefault(c => string.Equals(c.Name, reader.GetString(2), StringComparison.OrdinalIgnoreCase));
                            if (column == null) continue;
                            if (!reader.IsDBNull(3)) column.DefaultDefinition = reader.GetString(3);
                            if (!reader.IsDBNull(4)) column.Description = reader.GetString(4);
                        }
                    }
                }
            }
            catch (Exception ex) { RecordOptionalFailure("column defaults and descriptions", ex); }
        }

        private static void TryLoadIndexes(SqlConnection connection, MetadataSnapshot result)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    command.CommandText = @"
SELECT s.name,o.name,i.name,i.type_desc,i.is_unique,i.is_primary_key,
       ic.is_included_column,ic.key_ordinal,c.name
FROM sys.indexes i
JOIN sys.objects o ON o.object_id=i.object_id
JOIN sys.schemas s ON s.schema_id=o.schema_id
JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
WHERE i.index_id>0 AND i.is_hypothetical=0
ORDER BY s.name,o.name,i.index_id,ic.is_included_column,ic.key_ordinal,ic.index_column_id;";
                    using (var reader = command.ExecuteReader())
                    {
                        DatabaseObjectMetadata currentObject = null;
                        IndexMetadata currentIndex = null;
                        string currentKey = null;
                        while (reader.Read())
                        {
                            string objectName = reader.GetString(0) + "." + reader.GetString(1);
                            string indexName = reader.GetString(2);
                            string key = objectName + "|" + indexName;
                            if (!string.Equals(key, currentKey, StringComparison.OrdinalIgnoreCase))
                            {
                                currentObject = FindObject(result, objectName);
                                currentIndex = new IndexMetadata
                                {
                                    Name = indexName, TypeDescription = reader.GetString(3),
                                    IsUnique = reader.GetBoolean(4), IsPrimaryKey = reader.GetBoolean(5)
                                };
                                currentObject?.Indexes.Add(currentIndex);
                                currentKey = key;
                            }
                            if (currentObject == null || currentIndex == null) continue;
                            string columnName = reader.GetString(8);
                            if (reader.GetBoolean(6)) currentIndex.IncludedColumns.Add(columnName);
                            else currentIndex.KeyColumns.Add(columnName);
                            ColumnMetadata column = currentObject.Columns.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
                            if (column != null)
                            {
                                column.IsPrimaryKey |= currentIndex.IsPrimaryKey && !reader.GetBoolean(6);
                                column.IsUnique |= currentIndex.IsUnique && !reader.GetBoolean(6);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { RecordOptionalFailure("indexes", ex); }
        }

        private static void TryLoadCheckConstraints(SqlConnection connection, MetadataSnapshot result)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 10;
                    command.CommandText = @"
SELECT s.name,o.name,cc.name,cc.definition
FROM sys.check_constraints cc
JOIN sys.objects o ON o.object_id=cc.parent_object_id
JOIN sys.schemas s ON s.schema_id=o.schema_id
ORDER BY s.name,o.name,cc.name;";
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            FindObject(result, reader.GetString(0) + "." + reader.GetString(1))?.CheckConstraints.Add(
                                new CheckConstraintMetadata { Name = reader.GetString(2), Definition = reader.GetString(3) });
                }
            }
            catch (Exception ex) { RecordOptionalFailure("check constraints", ex); }
        }

        private static void TryLoadRowCounts(SqlConnection connection, MetadataSnapshot result)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 10;
                    command.CommandText = @"
SELECT s.name,o.name,SUM(ps.row_count)
FROM sys.dm_db_partition_stats ps
JOIN sys.objects o ON o.object_id=ps.object_id
JOIN sys.schemas s ON s.schema_id=o.schema_id
WHERE o.type='U' AND ps.index_id IN (0,1)
GROUP BY s.name,o.name;";
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                        {
                            DatabaseObjectMetadata item = FindObject(result, reader.GetString(0) + "." + reader.GetString(1));
                            if (item != null && !reader.IsDBNull(2)) item.EstimatedRowCount = reader.GetInt64(2);
                        }
                }
            }
            catch (Exception ex) { RecordOptionalFailure("estimated row counts", ex); }
        }

        private static DatabaseObjectMetadata FindObject(MetadataSnapshot result, string qualifiedName)
        {
            string clean = DatabaseIdentifier.NormalizeSqlServer(qualifiedName ?? string.Empty);
            string[] parts = clean.Split('.');
            string name = parts.LastOrDefault();
            string schema = parts.Length > 1 ? parts[parts.Length - 2] : string.Empty;
            return result.Objects.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(schema) || string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase)));
        }

        private static void RecordOptionalFailure(string area, Exception exception)
        {
            FeatureDiagnostics.Report("SQL Completion Metadata", "Optional metadata unavailable: " + area, exception);
            AxialSqlToolsPackage._logger?.Error(exception, "Optional SQL completion metadata unavailable: " + area);
        }

        private static void TryLoadParameters(SqlConnection connection, MetadataSnapshot result, List<string> errors)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    command.CommandText = @"
 SELECT DB_NAME()+'.'+s.name+'.'+o.name,p.name,ty.name,p.is_output,p.max_length,p.precision,p.scale,p.parameter_id,p.has_default_value
FROM sys.all_parameters p JOIN sys.all_objects o ON o.object_id=p.object_id
JOIN sys.schemas s ON s.schema_id=o.schema_id JOIN sys.types ty ON ty.user_type_id=p.user_type_id
WHERE o.type IN ('P','PC','X','FN','IF','TF','FS','FT') AND p.parameter_id>0
ORDER BY o.object_id,p.parameter_id;";
                    using (var reader = command.ExecuteReader()) while (reader.Read()) result.Parameters.Add(new RoutineParameterMetadata { ObjectName = reader.GetString(0), Name = reader.GetString(1), DataType = reader.GetString(2), IsOutput = reader.GetBoolean(3), MaxLength = reader.GetInt16(4), Precision = reader.GetByte(5), Scale = reader.GetByte(6), Ordinal = reader.GetInt32(7), HasDefaultValue = reader.GetBoolean(8) });
                }
            }
            catch (Exception ex) { RecordFailure("parameters", ex, errors); }
        }

        private static void RecordFailure(string area, Exception exception, List<string> errors)
        {
            errors.Add(area + ": " + exception.Message);
            FeatureDiagnostics.Report("SQL Completion Metadata", "Could not load " + area, exception);
            AxialSqlToolsPackage._logger?.Error(exception, "SQL completion metadata could not load " + area);
        }

        private static SqlConnection CreateMetadataConnection(ScriptFactoryAccess.ConnectionInfo info, string database = null)
        {
            bool trustServerCertificate = SettingsManager.GetSqlCompletionSettings().trustServerCertificate;
            return info.CreateSqlConnection(database, trustServerCertificate);
        }

        private static void TryLoadSupplementalObjects(SqlConnection connection, MetadataSnapshot result, List<string> errors)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    command.CommandText = @"
SELECT s.name,n.name,'SN',n.base_object_name FROM sys.synonyms n JOIN sys.schemas s ON s.schema_id=n.schema_id
UNION ALL SELECT s.name,q.name,'SQ',NULL FROM sys.sequences q JOIN sys.schemas s ON s.schema_id=q.schema_id
UNION ALL SELECT s.name,t.name,'TY',NULL FROM sys.types t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_user_defined=1;";
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                        {
                            string schema = reader.GetString(0), name = reader.GetString(1), type = reader.GetString(2);
                            if (!result.Schemas.Contains(schema)) result.Schemas.Add(schema);
                            result.Objects.Add(new DatabaseObjectMetadata { Database = connection.Database, Schema = schema, Name = name, Kind = type == "SN" ? CompletionItemKind.Synonym : type == "SQ" ? CompletionItemKind.Sequence : CompletionItemKind.Type, SynonymBaseObjectName = reader.IsDBNull(3) ? null : reader.GetString(3) });
                        }
                }
            }
            catch (Exception ex) { RecordFailure("synonyms, sequences and types", ex, errors); }
        }

        private static void TryLoadCompatibilityLevel(SqlConnection connection, MetadataSnapshot result, List<string> errors)
        {
            try
            {
                using (var command = new SqlCommand("SELECT compatibility_level FROM sys.databases WHERE database_id=DB_ID();", connection))
                {
                    command.CommandTimeout = 5;
                    object value = command.ExecuteScalar();
                    if (value != null && value != DBNull.Value) result.CompatibilityLevel = Convert.ToInt32(value);
                }
            }
            catch (Exception ex) { RecordFailure("database compatibility level", ex, errors); }
        }

        private static CompletionItemKind ToKind(string type)
        {
            // sys.all_objects.type is char(2), so one-character values can be
            // returned as "U ", "V ", or "P ". Normalize them before mapping.
            type = type?.Trim();
            if (type == "U") return CompletionItemKind.Table;
            if (type == "V") return CompletionItemKind.View;
            if (type == "P" || type == "PC" || type == "X") return CompletionItemKind.Procedure;
            return CompletionItemKind.Function;
        }
    }
}
