using System;
using System.Collections.Generic;
using System.Linq;

namespace AxialSqlTools.Completion
{
    internal enum CompletionItemKind { Keyword, Server, Database, Schema, Table, View, Column, Parameter, Procedure, Function, Synonym, Sequence, Type, Snippet, Join }

    internal sealed class CompletionItem
    {
        public string DisplayText { get; set; }
        public string InsertText { get; set; }
        public string Description { get; set; }
        public CompletionItemKind Kind { get; set; }
        public bool IsSystem { get; set; }
        public int Score { get; set; }
        public string ScopeKey { get; set; }
        public string DetailText { get; set; }

        public override string ToString() => string.IsNullOrWhiteSpace(Description)
            ? DisplayText
            : DisplayText + "    " + Description;
    }

    internal sealed class DatabaseObjectMetadata
    {
        public string Server { get; set; }
        public string Database { get; set; }
        public string Schema { get; set; }
        public string Name { get; set; }
        public CompletionItemKind Kind { get; set; }
        public bool IsSystem { get; set; }
        public bool IsTableValuedFunction { get; set; }
        public bool IsExternal { get; set; }
        public List<ColumnMetadata> Columns { get; } = new List<ColumnMetadata>();
        public List<IndexMetadata> Indexes { get; } = new List<IndexMetadata>();
        public List<CheckConstraintMetadata> CheckConstraints { get; } = new List<CheckConstraintMetadata>();
        public List<string> ProjectionSources { get; } = new List<string>();
        public string QualifiedName => string.Join(".", new[] { Server, Database, Schema, Name }.Where(p => !string.IsNullOrWhiteSpace(p)));
        public string SynonymBaseObjectName { get; set; }
        public string Definition { get; set; }
        public string Description { get; set; }
        public long? EstimatedRowCount { get; set; }
    }

    internal sealed class ColumnMetadata
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public bool IsNullable { get; set; }
        public bool IsIdentity { get; set; }
        public bool IsComputed { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool IsForeignKey { get; set; }
        public bool IsUnique { get; set; }
        public int Ordinal { get; set; }
        public short MaxLength { get; set; }
        public byte Precision { get; set; }
        public byte Scale { get; set; }
        public string DefaultDefinition { get; set; }
        public string Description { get; set; }
        public string TypeDisplay => SqlTypeDisplay.Format(DataType, MaxLength, Precision, Scale);
    }

    internal sealed class IndexMetadata
    {
        public string Name { get; set; }
        public string TypeDescription { get; set; }
        public bool IsUnique { get; set; }
        public bool IsPrimaryKey { get; set; }
        public List<string> KeyColumns { get; } = new List<string>();
        public List<string> IncludedColumns { get; } = new List<string>();
    }

    internal sealed class CheckConstraintMetadata
    {
        public string Name { get; set; }
        public string Definition { get; set; }
    }

    internal sealed class RoutineParameterMetadata
    {
        public string ObjectName { get; set; }
        public string Name { get; set; }
        public string DataType { get; set; }
        public bool IsOutput { get; set; }
        public bool HasDefaultValue { get; set; }
        public short MaxLength { get; set; }
        public byte Precision { get; set; }
        public byte Scale { get; set; }
        public int Ordinal { get; set; }
        public string TypeDisplay
            => SqlTypeDisplay.Format(DataType, MaxLength, Precision, Scale);
    }

    internal static class SqlTypeDisplay
    {
        public static string Format(string dataType, short maxLength, byte precision, byte scale)
        {
            string type = dataType ?? string.Empty;
            if ((type.IndexOf("char", StringComparison.OrdinalIgnoreCase) >= 0 || type.IndexOf("binary", StringComparison.OrdinalIgnoreCase) >= 0) && maxLength != 0)
            {
                int length = maxLength;
                if (length > 0 && (string.Equals(type, "nvarchar", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "nchar", StringComparison.OrdinalIgnoreCase))) length /= 2;
                return type + "(" + (length < 0 ? "max" : length.ToString()) + ")";
            }
            if ((string.Equals(type, "decimal", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "numeric", StringComparison.OrdinalIgnoreCase)) && precision > 0)
                return type + "(" + precision + "," + scale + ")";
            return type;
        }
    }

    internal sealed class ForeignKeyMetadata
    {
        public string ParentObject { get; set; }
        public string ReferencedObject { get; set; }
        public List<string> ParentColumns { get; } = new List<string>();
        public List<string> ReferencedColumns { get; } = new List<string>();
    }

    internal sealed class MetadataSnapshot
    {
        public static readonly MetadataSnapshot Empty = new MetadataSnapshot();
        public DateTime LoadedUtc { get; set; }
        public string Scope { get; set; }
        public string ErrorMessage { get; set; }
        public bool IsPartial { get; set; }
        public int CompatibilityLevel { get; set; } = 170;
        public List<string> Schemas { get; } = new List<string>();
        public List<string> Databases { get; } = new List<string>();
        public List<string> LinkedServers { get; } = new List<string>();
        public Dictionary<string, List<string>> LinkedServerDatabases { get; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public List<DatabaseObjectMetadata> Objects { get; } = new List<DatabaseObjectMetadata>();
        public List<RoutineParameterMetadata> Parameters { get; } = new List<RoutineParameterMetadata>();
        public List<ForeignKeyMetadata> ForeignKeys { get; } = new List<ForeignKeyMetadata>();
        public string StatusText => !string.IsNullOrWhiteSpace(ErrorMessage)
            ? (IsPartial ? "metadata partially loaded" : "metadata unavailable")
            : $"metadata {Objects.Count} objects / {Objects.Sum(o => o.Columns.Count)} columns";
    }

    internal enum CompletionContextKind
    {
        General, DataSource, Member, Join, InsertBody, InsertColumns, UpdateSet,
        ExecuteObject, ExecuteArguments, ExecuteArgumentValue, FunctionArguments, Predicate, GroupBy, OrderBy, MergeSource,
        SelectList, WindowPartitionBy, WindowOrderBy, Output, Pivot, CreateTableDefinition, ConstraintColumns,
        AlterTableAction, AlterTableColumn, IndexColumns
    }

    internal sealed class CompletionContext
    {
        public CompletionContextKind Kind { get; set; }
        public string Prefix { get; set; }
        public string Qualifier { get; set; }
        public int ReplacementStartColumn { get; set; }
        public Dictionary<string, string> Aliases { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<DatabaseObjectMetadata> LocalObjects { get; } = new List<DatabaseObjectMetadata>();
        public string TargetObject { get; set; }
        public string CurrentStatement { get; set; }
        public string CurrentBatch { get; set; }
        public HashSet<string> UsedParameters { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReferencedColumns { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> GroupByColumns { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public List<string> SelectAliases { get; } = new List<string>();
        public string ActiveColumn { get; set; }
        public string ActiveParameter { get; set; }
        public bool IsExplicitRequest { get; set; }
        public string MetadataScope { get; set; }
        public bool Suppress { get; set; }
        public List<SqlEditorDiagnostic> Diagnostics { get; } = new List<SqlEditorDiagnostic>();
        public bool HasParseErrors { get; set; }
        public int ArgumentIndex { get; set; }
        public bool IsJoinSource { get; set; }
        public bool IsDataSourceMember { get; set; }
        public bool HasOmittedSchemaQualifier { get; set; }
    }
}
