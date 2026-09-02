using Microsoft.Data.SqlClient;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace AxialSqlTools.Completion
{
    internal static class SqlMetadataCache
    {
        private sealed class Entry { public DateTime CreatedUtc; public Task<MetadataSnapshot> Task; }
        private static readonly ConcurrentDictionary<string, Entry> Cache = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

        public static Task<MetadataSnapshot> GetAsync(ScriptFactoryAccess.ConnectionInfo info, CancellationToken token)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString)) return Task.FromResult(MetadataSnapshot.Empty);
            string key = GetKey(info.FullConnectionString);
            Entry entry;
            if (!Cache.TryGetValue(key, out entry) || DateTime.UtcNow - entry.CreatedUtc >= Lifetime)
            {
                var replacement = new Entry { CreatedUtc = DateTime.UtcNow, Task = Task.Run(() => Load(info.FullConnectionString), CancellationToken.None) };
                entry = Cache.AddOrUpdate(key, replacement, (_, old) => DateTime.UtcNow - old.CreatedUtc < Lifetime ? old : replacement);
            }
            return AwaitWithCancellation(entry.Task, token);
        }

        public static void Invalidate(ScriptFactoryAccess.ConnectionInfo info)
        {
            if (info != null && !string.IsNullOrWhiteSpace(info.FullConnectionString)) Cache.TryRemove(GetKey(info.FullConnectionString), out Entry _);
        }

        private static string GetKey(string connectionString)
        {
            var b = new SqlConnectionStringBuilder(connectionString);
            return string.Join("|", b.DataSource, b.InitialCatalog, b.IntegratedSecurity, b.UserID);
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

        private static MetadataSnapshot Load(string connectionString)
        {
            var result = new MetadataSnapshot();
            TryLoadObjects(connectionString, result);
            TryLoadForeignKeys(connectionString, result);
            TryLoadParameters(connectionString, result);
            return result;
        }

        private static void TryLoadObjects(string connectionString, MetadataSnapshot result)
        {
            try
            {
                using (var connection = new SqlConnection(connectionString))
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    command.CommandText = @"
SELECT s.name, o.name, o.type, c.name, ty.name, c.is_nullable, c.is_identity, c.is_computed, o.is_ms_shipped
FROM sys.all_objects o
JOIN sys.schemas s ON s.schema_id=o.schema_id
LEFT JOIN sys.all_columns c ON c.object_id=o.object_id
LEFT JOIN sys.types ty ON ty.user_type_id=c.user_type_id
WHERE o.type IN ('U','V','P','PC','X','FN','IF','TF','FS','FT')
  AND (o.is_ms_shipped=0 OR s.name='sys')
ORDER BY s.name,o.name,c.column_id;";
                    connection.Open();
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
                                current = new DatabaseObjectMetadata { Schema = schema, Name = name, Kind = ToKind(type), IsSystem = !reader.IsDBNull(8) && reader.GetBoolean(8) };
                                result.Objects.Add(current); key = nextKey;
                            }
                            if (!reader.IsDBNull(3)) current.Columns.Add(new ColumnMetadata { Name = reader.GetString(3), DataType = reader.IsDBNull(4) ? string.Empty : reader.GetString(4), IsNullable = !reader.IsDBNull(5) && reader.GetBoolean(5), IsIdentity = !reader.IsDBNull(6) && reader.GetBoolean(6), IsComputed = !reader.IsDBNull(7) && reader.GetBoolean(7) });
                        }
                    }
                }
            }
            catch (Exception ex) { Trace.WriteLine("Axial SQL completion object metadata: " + ex); }
        }

        private static void TryLoadForeignKeys(string connectionString, MetadataSnapshot result)
        {
            try
            {
                using (var connection = new SqlConnection(connectionString))
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    command.CommandText = @"
SELECT ps.name+'.'+po.name,rs.name+'.'+ro.name,fk.object_id,pc.name,rc.name
FROM sys.foreign_keys fk JOIN sys.objects po ON po.object_id=fk.parent_object_id
JOIN sys.schemas ps ON ps.schema_id=po.schema_id JOIN sys.objects ro ON ro.object_id=fk.referenced_object_id
JOIN sys.schemas rs ON rs.schema_id=ro.schema_id JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
JOIN sys.columns pc ON pc.object_id=po.object_id AND pc.column_id=fkc.parent_column_id
JOIN sys.columns rc ON rc.object_id=ro.object_id AND rc.column_id=fkc.referenced_column_id
ORDER BY fk.object_id,fkc.constraint_column_id;";
                    connection.Open();
                    using (var reader = command.ExecuteReader())
                    {
                        ForeignKeyMetadata current = null; int id = -1;
                        while (reader.Read()) { int next = reader.GetInt32(2); if (next != id) { current = new ForeignKeyMetadata { ParentObject = reader.GetString(0), ReferencedObject = reader.GetString(1) }; result.ForeignKeys.Add(current); id = next; } current.ParentColumns.Add(reader.GetString(3)); current.ReferencedColumns.Add(reader.GetString(4)); }
                    }
                }
            }
            catch (Exception ex) { Trace.WriteLine("Axial SQL completion foreign-key metadata: " + ex); }
        }

        private static void TryLoadParameters(string connectionString, MetadataSnapshot result)
        {
            try
            {
                using (var connection = new SqlConnection(connectionString))
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    command.CommandText = @"
SELECT s.name+'.'+o.name,p.name,ty.name,p.is_output
FROM sys.all_parameters p JOIN sys.all_objects o ON o.object_id=p.object_id
JOIN sys.schemas s ON s.schema_id=o.schema_id JOIN sys.types ty ON ty.user_type_id=p.user_type_id
WHERE o.type IN ('P','PC','X','FN','IF','TF','FS','FT') AND p.parameter_id>0
ORDER BY o.object_id,p.parameter_id;";
                    connection.Open();
                    using (var reader = command.ExecuteReader()) while (reader.Read()) result.Parameters.Add(new RoutineParameterMetadata { ObjectName = reader.GetString(0), Name = reader.GetString(1), DataType = reader.GetString(2), IsOutput = reader.GetBoolean(3) });
                }
            }
            catch (Exception ex) { Trace.WriteLine("Axial SQL completion parameter metadata: " + ex); }
        }

        private static CompletionItemKind ToKind(string type) { if (type == "U") return CompletionItemKind.Table; if (type == "V") return CompletionItemKind.View; if (type == "P" || type == "PC" || type == "X") return CompletionItemKind.Procedure; return CompletionItemKind.Function; }
    }
}
