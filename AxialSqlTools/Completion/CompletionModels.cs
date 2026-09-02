using System;
using System.Collections.Generic;

namespace AxialSqlTools.Completion
{
    internal enum CompletionItemKind { Keyword, Schema, Table, View, Column, Parameter, Procedure, Function, Snippet, Join }

    internal sealed class CompletionItem
    {
        public string DisplayText { get; set; }
        public string InsertText { get; set; }
        public string Description { get; set; }
        public CompletionItemKind Kind { get; set; }
        public bool IsSystem { get; set; }
        public int Score { get; set; }
        public string ScopeKey { get; set; }

        public override string ToString() => string.IsNullOrWhiteSpace(Description)
            ? DisplayText
            : DisplayText + "    " + Description;
    }

    internal sealed class DatabaseObjectMetadata
    {
        public string Schema { get; set; }
        public string Name { get; set; }
        public CompletionItemKind Kind { get; set; }
        public bool IsSystem { get; set; }
        public List<ColumnMetadata> Columns { get; } = new List<ColumnMetadata>();
        public string QualifiedName => Schema + "." + Name;
    }

    internal sealed class ColumnMetadata
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public bool IsNullable { get; set; }
        public bool IsIdentity { get; set; }
        public bool IsComputed { get; set; }
    }

    internal sealed class RoutineParameterMetadata
    {
        public string ObjectName { get; set; }
        public string Name { get; set; }
        public string DataType { get; set; }
        public bool IsOutput { get; set; }
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
        public List<string> Schemas { get; } = new List<string>();
        public List<DatabaseObjectMetadata> Objects { get; } = new List<DatabaseObjectMetadata>();
        public List<RoutineParameterMetadata> Parameters { get; } = new List<RoutineParameterMetadata>();
        public List<ForeignKeyMetadata> ForeignKeys { get; } = new List<ForeignKeyMetadata>();
    }

    internal enum CompletionContextKind
    {
        General, DataSource, Member, Join, InsertBody, InsertColumns, UpdateSet,
        ExecuteObject, ExecuteArguments, ExecuteArgumentValue
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
        public string ActiveParameter { get; set; }
        public bool IsExplicitRequest { get; set; }
        public string MetadataScope { get; set; }
        public bool Suppress { get; set; }
    }
}
