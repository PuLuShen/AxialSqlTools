using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AxialSqlTools.Completion
{
    internal static class SqlCompletionAnalyzer
    {
        private const string Id = @"(?:\[[^\]]+\]|[#@\w]+)";
        private static readonly Regex AliasPattern = new Regex(@"\b(?:FROM|JOIN|APPLY)\s+(?<obj>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})(?:\s+(?:AS\s+)?(?<alias>" + Id + @"))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static CompletionContext Analyze(string textBeforeCaret, int caretColumn)
        {
            string source = textBeforeCaret ?? string.Empty;
            string maskedSource = SqlTextContext.MaskCommentsAndStrings(source);
            SqlTextContext text = SqlTextContext.Create(textBeforeCaret);
            string statement = text.StatementText;
            string masked = SqlTextContext.MaskCommentsAndStrings(statement);
            var context = new CompletionContext { Kind = CompletionContextKind.General, CurrentStatement = statement, CurrentBatch = text.BatchText };
            if (source.Length > 0 && !char.IsWhiteSpace(source[source.Length - 1]) && maskedSource[source.Length - 1] == ' ')
            {
                context.Suppress = true;
                return context;
            }
            AddLocalObjects(text.BatchText, context);
            AddAliases(masked, context);
            Match word = Regex.Match(statement, @"(?<prefix>[@#\w\[\]]*)$");
            context.Prefix = Clean(word.Groups["prefix"].Value);
            context.ReplacementStartColumn = Math.Max(0, caretColumn - word.Groups["prefix"].Length);

            if (TryAnalyzeExecute(statement, caretColumn, context)) return context;

            Match completedInsertTarget = Regex.Match(masked, @"\bINSERT\s+INTO\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})\s+$", RegexOptions.IgnoreCase);
            if (completedInsertTarget.Success)
            {
                context.Kind = CompletionContextKind.InsertBody;
                context.TargetObject = CleanQualified(completedInsertTarget.Groups["target"].Value);
                context.Prefix = string.Empty;
                context.ReplacementStartColumn = caretColumn;
                return context;
            }

            Match member = Regex.Match(statement, @"(?<qual>" + Id + @")\s*\.\s*(?<prefix>" + Id + @")?$");
            if (member.Success)
            {
                context.Kind = CompletionContextKind.Member;
                context.Qualifier = Clean(member.Groups["qual"].Value);
                context.Prefix = Clean(member.Groups["prefix"].Value);
                context.ReplacementStartColumn = Math.Max(0, caretColumn - member.Groups["prefix"].Length);
                return context;
            }

            string withoutPrefix = statement.Substring(0, Math.Max(0, statement.Length - word.Groups["prefix"].Length));
            Match insert = Regex.Match(masked, @"\bINSERT\s+INTO\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})\s*\([^)]*$", RegexOptions.IgnoreCase);
            Match insertBody = Regex.Match(masked, @"\bINSERT\s+INTO\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})\s+$", RegexOptions.IgnoreCase);
            Match update = Regex.Match(masked, @"\bUPDATE\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})(?:\s+(?:AS\s+)?" + Id + @")?\s+SET\s+[^;]*$", RegexOptions.IgnoreCase);
            if (insert.Success) { context.Kind = CompletionContextKind.InsertColumns; context.TargetObject = CleanQualified(insert.Groups["target"].Value); }
            else if (insertBody.Success) { context.Kind = CompletionContextKind.InsertBody; context.TargetObject = CleanQualified(insertBody.Groups["target"].Value); }
            else if (update.Success) { context.Kind = CompletionContextKind.UpdateSet; context.TargetObject = ResolveAlias(CleanQualified(update.Groups["target"].Value), context); }
            else if (Regex.IsMatch(withoutPrefix, @"\b(?:INNER\s+|LEFT\s+(?:OUTER\s+)?|RIGHT\s+(?:OUTER\s+)?|FULL\s+(?:OUTER\s+)?|CROSS\s+)?JOIN\s*$", RegexOptions.IgnoreCase)) context.Kind = CompletionContextKind.Join;
            else if (Regex.IsMatch(withoutPrefix, @"\b(?:FROM|APPLY|UPDATE|INTO|MERGE\s+INTO|USING)\s*$", RegexOptions.IgnoreCase)) context.Kind = CompletionContextKind.DataSource;
            return context;
        }

        private static bool TryAnalyzeExecute(string statement, int caretColumn, CompletionContext context)
        {
            Match exec = Regex.Match(statement, @"\bEXEC(?:UTE)?\b(?<body>[^;]*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!exec.Success) return false;
            string body = exec.Groups["body"].Value;
            string trimmed = body.TrimStart();
            if (trimmed.StartsWith("(") || Regex.IsMatch(trimmed, @"^AS\b", RegexOptions.IgnoreCase)) return false;
            trimmed = Regex.Replace(trimmed, @"^@\w+\s*=\s*", string.Empty);
            if (!char.IsWhiteSpace(body.LastOrDefault()) && Regex.IsMatch(trimmed, @"^" + Id + @"(?:\s*\.\s*" + Id + @"){0,3}\.?$"))
            {
                int dot = trimmed.LastIndexOf('.');
                context.Kind = CompletionContextKind.ExecuteObject;
                context.Qualifier = dot >= 0 ? CleanQualified(trimmed.Substring(0, dot)) : null;
                context.Prefix = dot >= 0 ? Clean(trimmed.Substring(dot + 1)) : Clean(trimmed);
                context.ReplacementStartColumn = Math.Max(0, caretColumn - context.Prefix.Length);
                return true;
            }
            Match target = Regex.Match(trimmed, @"^(?<obj>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})(?<rest>.*)$", RegexOptions.Singleline);
            if (!target.Success)
            {
                context.Kind = CompletionContextKind.ExecuteObject;
                context.Prefix = Clean(trimmed);
                context.ReplacementStartColumn = Math.Max(0, caretColumn - trimmed.Length);
                return true;
            }
            string objectText = target.Groups["obj"].Value;
            string rest = target.Groups["rest"].Value;
            context.TargetObject = CleanQualified(objectText);
            foreach (Match used in Regex.Matches(rest, @"(?<!@)@\w+\s*=", RegexOptions.IgnoreCase)) context.UsedParameters.Add(used.Value.TrimEnd(' ', '\t', '='));
            Match active = Regex.Match(rest, @"(?<parameter>@\w+)\s*=\s*(?<value>[^,]*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (active.Success)
            {
                context.Kind = CompletionContextKind.ExecuteArgumentValue;
                context.ActiveParameter = active.Groups["parameter"].Value;
                context.Prefix = active.Groups["value"].Value.TrimStart();
                context.ReplacementStartColumn = Math.Max(0, caretColumn - active.Groups["value"].Length);
            }
            else
            {
                context.Kind = CompletionContextKind.ExecuteArguments;
                Match prefix = Regex.Match(rest, @"(?<prefix>@\w*)$");
                context.Prefix = prefix.Success ? prefix.Groups["prefix"].Value : string.Empty;
                context.ReplacementStartColumn = Math.Max(0, caretColumn - context.Prefix.Length);
            }
            return true;
        }

        public static List<CompletionItem> BuildItems(CompletionContext context, MetadataSnapshot metadata)
        {
            metadata = metadata ?? MetadataSnapshot.Empty;
            var result = new List<CompletionItem>();
            var objects = metadata.Objects.Concat(context.LocalObjects).ToList();
            switch (context.Kind)
            {
                case CompletionContextKind.ExecuteObject:
                    result.AddRange(metadata.Objects.Where(o => o.Kind == CompletionItemKind.Procedure && (string.IsNullOrEmpty(context.Qualifier) || EndsWith(o.Schema, context.Qualifier))).Select(o => ObjectItem(o, o.IsSystem ? 25 : 120, !string.IsNullOrEmpty(context.Qualifier)))); break;
                case CompletionContextKind.ExecuteArguments: AddParameters(result, context, metadata); break;
                case CompletionContextKind.ExecuteArgumentValue: AddValues(result, context, metadata); break;
                case CompletionContextKind.Member: AddMembers(result, context, objects, metadata); break;
                case CompletionContextKind.Join: result.AddRange(BuildJoinItems(context, metadata)); break;
                case CompletionContextKind.InsertColumns:
                case CompletionContextKind.UpdateSet: AddWritableColumns(result, context, objects); break;
                case CompletionContextKind.InsertBody: AddInsertTemplate(result, context, objects); break;
                case CompletionContextKind.DataSource: result.AddRange(objects.Where(IsDataSource).Select(o => ObjectItem(o, 40, false))); break;
                default: AddGeneral(result, context, objects); break;
            }
            foreach (CompletionItem item in result) item.ScopeKey = context.MetadataScope;
            return FilterAndSort(result.GroupBy(i => i.Kind + "|" + i.DisplayText, StringComparer.OrdinalIgnoreCase).Select(g => g.First()), context.Prefix);
        }

        private static void AddParameters(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata)
        {
            var parameters = metadata.Parameters.Where(p => ObjectMatches(p.ObjectName, context.TargetObject) && !context.UsedParameters.Contains(p.Name) && !string.IsNullOrEmpty(p.Name)).ToList();
            result.AddRange(parameters.Select(p => new CompletionItem { DisplayText = p.Name, InsertText = p.Name + " = ", Kind = CompletionItemKind.Parameter, Description = p.DataType + (p.IsOutput ? " OUTPUT" : string.Empty), Score = 100 }));
            if (parameters.Count > 1 && context.UsedParameters.Count == 0) result.Add(new CompletionItem { DisplayText = "(all parameters)", InsertText = string.Join(", ", parameters.Select((p, i) => p.Name + " = ${" + (i + 1) + ":NULL}" + (p.IsOutput ? " OUTPUT" : string.Empty))), Kind = CompletionItemKind.Snippet, Description = parameters.Count + " parameters", Score = 130 });
        }

        private static void AddValues(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata)
        {
            var p = metadata.Parameters.FirstOrDefault(x => ObjectMatches(x.ObjectName, context.TargetObject) && string.Equals(x.Name, context.ActiveParameter, StringComparison.OrdinalIgnoreCase));
            result.Add(new CompletionItem { DisplayText = "NULL", InsertText = "NULL", Kind = CompletionItemKind.Keyword, Description = p?.DataType, Score = 70 });
            result.Add(new CompletionItem { DisplayText = "DEFAULT", InsertText = "DEFAULT", Kind = CompletionItemKind.Keyword, Description = p?.DataType, Score = 60 });
            foreach (Match variable in Regex.Matches(SqlTextContext.MaskCommentsAndStrings(context.CurrentBatch ?? string.Empty), @"(?<!@)@\w+")) result.Add(new CompletionItem { DisplayText = variable.Value, InsertText = variable.Value, Kind = CompletionItemKind.Column, Description = "variable", Score = 80 });
        }

        private static void AddMembers(List<CompletionItem> result, CompletionContext context, List<DatabaseObjectMetadata> objects, MetadataSnapshot metadata)
        {
            string resolved = ResolveAlias(context.Qualifier, context);
            var obj = objects.FirstOrDefault(o => ObjectMatches(o.QualifiedName, resolved) || ObjectMatches(o.Name, resolved));
            if (obj != null) result.AddRange(obj.Columns.Select(c => ColumnItem(c, 90)));
            else result.AddRange(objects.Where(o => EndsWith(o.Schema, resolved)).Select(o => ObjectItem(o, 70, true)));
        }

        private static void AddWritableColumns(List<CompletionItem> result, CompletionContext context, List<DatabaseObjectMetadata> objects)
        {
            var target = objects.FirstOrDefault(o => ObjectMatches(o.QualifiedName, context.TargetObject) || ObjectMatches(o.Name, context.TargetObject));
            if (target == null) return;
            var writable = target.Columns.Where(c => !c.IsIdentity && !c.IsComputed && !string.Equals(c.DataType, "timestamp", StringComparison.OrdinalIgnoreCase) && !string.Equals(c.DataType, "rowversion", StringComparison.OrdinalIgnoreCase)).ToList();
            result.AddRange(writable.Select(c => ColumnItem(c, 90)));
            if (writable.Count > 1)
            {
                string insertion = context.Kind == CompletionContextKind.UpdateSet
                    ? string.Join("," + Environment.NewLine, writable.Select((c, i) => Quote(c.Name) + " = ${" + (i + 1) + ":NULL}"))
                    : string.Join(", ", writable.Select(c => Quote(c.Name)));
                result.Add(new CompletionItem { DisplayText = context.Kind == CompletionContextKind.UpdateSet ? "(all column assignments)" : "(all writable columns)", InsertText = insertion, Kind = CompletionItemKind.Snippet, Description = writable.Count + " columns", Score = 120 });
            }
        }

        private static void AddInsertTemplate(List<CompletionItem> result, CompletionContext context, List<DatabaseObjectMetadata> objects)
        {
            var target = objects.FirstOrDefault(o => ObjectMatches(o.QualifiedName, context.TargetObject) || ObjectMatches(o.Name, context.TargetObject));
            if (target == null) return;
            var columns = target.Columns.Where(c => !c.IsIdentity && !c.IsComputed && !string.Equals(c.DataType, "timestamp", StringComparison.OrdinalIgnoreCase) && !string.Equals(c.DataType, "rowversion", StringComparison.OrdinalIgnoreCase)).ToList();
            if (columns.Count == 0) return;
            string names = string.Join("," + Environment.NewLine + "    ", columns.Select(c => Quote(c.Name)));
            string values = string.Join("," + Environment.NewLine + "    ", columns.Select((c, i) => "${" + (i + 1) + ":" + DefaultValue(c) + "}"));
            result.Add(new CompletionItem { DisplayText = "(INSERT columns and VALUES)", InsertText = Environment.NewLine + "(" + Environment.NewLine + "    " + names + Environment.NewLine + ")" + Environment.NewLine + "VALUES" + Environment.NewLine + "(" + Environment.NewLine + "    " + values + Environment.NewLine + ");", Kind = CompletionItemKind.Snippet, Description = columns.Count + " writable columns", Score = 140 });
        }

        private static string DefaultValue(ColumnMetadata column)
        {
            string type = (column.DataType ?? string.Empty).ToLowerInvariant();
            if (type.Contains("char") || type.Contains("text") || type == "xml" || type == "uniqueidentifier") return "''";
            if (type == "date" || type.Contains("time")) return "GETDATE()";
            if (type == "bit") return "0";
            return column.IsNullable ? "NULL" : "0";
        }

        private static void AddGeneral(List<CompletionItem> result, CompletionContext context, List<DatabaseObjectMetadata> objects)
        {
            foreach (string name in context.Aliases.Values.Distinct(StringComparer.OrdinalIgnoreCase)) { var obj = objects.FirstOrDefault(o => ObjectMatches(o.QualifiedName, name) || ObjectMatches(o.Name, name)); if (obj != null) result.AddRange(obj.Columns.Select(c => ColumnItem(c, 60))); }
            result.AddRange(Keywords.Select(k => new CompletionItem { DisplayText = k, InsertText = k, Kind = CompletionItemKind.Keyword, Description = "keyword", Score = 20 }));
            foreach (var s in SnippetService.GetAllSnippets()) result.Add(new CompletionItem { DisplayText = s.Prefix, InsertText = s.Body, Kind = CompletionItemKind.Snippet, Description = s.Description, Score = 30 });
        }

        private static IEnumerable<CompletionItem> BuildJoinItems(CompletionContext context, MetadataSnapshot metadata)
        {
            var sources = context.Aliases.Where(p => !string.Equals(p.Key, LastPart(p.Value), StringComparison.OrdinalIgnoreCase)).ToList();
            if (sources.Count == 0) sources = context.Aliases.ToList();
            foreach (var target in metadata.Objects.Where(o => o.Kind == CompletionItemKind.Table || o.Kind == CompletionItemKind.View))
            {
                if (sources.Any(s => ObjectMatches(s.Value, target.QualifiedName))) continue;
                string alias = MakeAlias(target.Name, context.Aliases.Keys);
                var relationships = metadata.ForeignKeys.Where(f => (ObjectMatches(f.ParentObject, target.QualifiedName) || ObjectMatches(f.ReferencedObject, target.QualifiedName)) && sources.Any(s => ObjectMatches(f.ParentObject, s.Value) || ObjectMatches(f.ReferencedObject, s.Value))).ToList();
                if (relationships.Count == 0) { yield return new CompletionItem { DisplayText = target.QualifiedName, InsertText = QualifiedInsert(target) + " AS " + Quote(alias), Kind = CompletionItemKind.Join, Description = "table (no known relationship)", Score = 10 }; continue; }
                foreach (var fk in relationships)
                {
                    var source = sources.First(s => ObjectMatches(fk.ParentObject, s.Value) || ObjectMatches(fk.ReferencedObject, s.Value));
                    bool sourceParent = ObjectMatches(fk.ParentObject, source.Value);
                    var conditions = fk.ParentColumns.Select((c, i) => Quote(source.Key) + "." + Quote(sourceParent ? c : fk.ReferencedColumns[i]) + " = " + Quote(alias) + "." + Quote(sourceParent ? fk.ReferencedColumns[i] : c));
                    yield return new CompletionItem { DisplayText = target.QualifiedName, InsertText = QualifiedInsert(target) + " AS " + Quote(alias) + " ON " + string.Join(" AND ", conditions), Kind = CompletionItemKind.Join, Description = "foreign-key join", Score = 110 };
                }
            }
        }

        private static CompletionItem ColumnItem(ColumnMetadata c, int score) => new CompletionItem { DisplayText = c.Name, InsertText = Quote(c.Name), Kind = CompletionItemKind.Column, Description = c.DataType + (c.IsNullable ? " null" : string.Empty), Score = score };
        private static CompletionItem ObjectItem(DatabaseObjectMetadata o, int score, bool omitSchema) => new CompletionItem { DisplayText = omitSchema ? o.Name : (string.IsNullOrEmpty(o.Schema) ? o.Name : o.QualifiedName), InsertText = omitSchema ? Quote(o.Name) : QualifiedInsert(o), Kind = o.Kind, Description = o.Kind.ToString(), Score = score };
        private static string QualifiedInsert(DatabaseObjectMetadata o) => string.IsNullOrEmpty(o.Schema) ? Quote(o.Name) : Quote(o.Schema) + "." + Quote(o.Name);
        private static bool IsDataSource(DatabaseObjectMetadata o) => o.Kind == CompletionItemKind.Table || o.Kind == CompletionItemKind.View || o.Kind == CompletionItemKind.Function;

        private static List<CompletionItem> FilterAndSort(IEnumerable<CompletionItem> items, string prefix)
        {
            prefix = Clean(prefix ?? string.Empty).TrimStart('@');
            var settings = SettingsManager.GetSqlCompletionSettings();
            return items.Select(i => new { Item = i, Match = MatchScore((i.DisplayText ?? string.Empty).TrimStart('@'), prefix) }).Where(x => x.Match >= 0).OrderByDescending(x => x.Item.Score + (settings.learnFromUsage ? CompletionUsageStore.GetScore(x.Item) : 0) + x.Match).ThenBy(x => x.Item.DisplayText, StringComparer.OrdinalIgnoreCase).Take(settings.maximumItems).Select(x => x.Item).ToList();
        }

        private static int MatchScore(string value, string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return 0;
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 40;
            string initials = new string(value.Where((c, i) => i == 0 || char.IsUpper(c) || value[i - 1] == '_').ToArray());
            if (initials.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 25;
            if (value.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0) return 10;
            return Distance(value.Length > prefix.Length + 2 ? value.Substring(0, prefix.Length + 2) : value, prefix) <= Math.Max(1, prefix.Length / 3) ? 2 : -1;
        }

        private static int Distance(string a, string b) { int[] row = Enumerable.Range(0, b.Length + 1).ToArray(); for (int i = 1; i <= a.Length; i++) { int prev = row[0]; row[0] = i; for (int j = 1; j <= b.Length; j++) { int old = row[j]; row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), prev + (char.ToUpperInvariant(a[i - 1]) == char.ToUpperInvariant(b[j - 1]) ? 0 : 1)); prev = old; } } return row[b.Length]; }
        private static void AddAliases(string statement, CompletionContext context)
        {
            int caretDepth = GetDepth(statement, statement.Length);
            foreach (Match m in AliasPattern.Matches(statement))
            {
                // Outer aliases remain visible to correlated subqueries; aliases from a
                // completed deeper subquery must not leak back into its parent scope.
                if (GetDepth(statement, m.Index) > caretDepth) continue;
                string obj = CleanQualified(m.Groups["obj"].Value);
                string alias = Clean(m.Groups["alias"].Value);
                context.Aliases[LastPart(obj)] = obj;
                if (!string.IsNullOrEmpty(alias) && !ClauseKeywords.Contains(alias)) context.Aliases[alias] = obj;
            }
        }

        private static int GetDepth(string text, int before)
        {
            int depth = 0;
            for (int i = 0; i < before && i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')') depth = Math.Max(0, depth - 1);
            }
            return depth;
        }

        private static void AddLocalObjects(string batch, CompletionContext context)
        {
            string sql = SqlTextContext.MaskCommentsAndStrings(batch);
            foreach (Match m in Regex.Matches(sql, @"\bDECLARE\s+(?<name>@\w+)\s+TABLE\s*\((?<cols>[^;]*)\)", RegexOptions.IgnoreCase | RegexOptions.Singleline)) AddLocal(context, m.Groups["name"].Value, ParseDefinitions(m.Groups["cols"].Value));
            foreach (Match m in Regex.Matches(sql, @"\bCREATE\s+TABLE\s+(?<name>\#\#?\w+)\s*\((?<cols>[^;]*)\)", RegexOptions.IgnoreCase | RegexOptions.Singleline)) AddLocal(context, m.Groups["name"].Value, ParseDefinitions(m.Groups["cols"].Value));
            foreach (Match m in Regex.Matches(sql, @"(?:\bWITH|,)\s*(?<name>" + Id + @")(?:\s*\((?<explicit>[^)]*)\))?\s+AS\s*\(\s*SELECT\s+(?<select>.*?)\bFROM\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)) AddLocal(context, Clean(m.Groups["name"].Value), string.IsNullOrWhiteSpace(m.Groups["explicit"].Value) ? ParseSelect(m.Groups["select"].Value) : SplitTopLevel(m.Groups["explicit"].Value).Select(x => new ColumnMetadata { Name = Clean(x), DataType = string.Empty }));
            foreach (Match m in Regex.Matches(sql, @"\bSELECT\s+(?<select>.*?)\s+INTO\s+(?<name>\#\#?\w+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)) AddLocal(context, m.Groups["name"].Value, ParseSelect(m.Groups["select"].Value));
            foreach (Match m in Regex.Matches(sql, @"\b(?:FROM|JOIN)\s*\(\s*SELECT\s+(?<select>.*?)\bFROM\b.*?\)\s+(?:AS\s+)?(?<name>" + Id + @")", RegexOptions.IgnoreCase | RegexOptions.Singleline)) AddLocal(context, Clean(m.Groups["name"].Value), ParseSelect(m.Groups["select"].Value));
        }

        private static IEnumerable<ColumnMetadata> ParseDefinitions(string value) => SplitTopLevel(value).Select(p => Regex.Match(p.Trim(), @"^(?<name>" + Id + @")\s+(?<type>[\w]+)", RegexOptions.IgnoreCase)).Where(m => m.Success).Select(m => new ColumnMetadata { Name = Clean(m.Groups["name"].Value), DataType = m.Groups["type"].Value });
        private static IEnumerable<ColumnMetadata> ParseSelect(string value) => SplitTopLevel(value).Select(p => { Match a = Regex.Match(p.Trim(), @"(?:\bAS\s+|\s+)(?<name>" + Id + @")$", RegexOptions.IgnoreCase); string n = a.Success ? Clean(a.Groups["name"].Value) : Clean(p.Trim().Split('.').Last()); return new ColumnMetadata { Name = n, DataType = string.Empty }; }).Where(c => Regex.IsMatch(c.Name, @"^[#@\w]+$"));
        private static List<string> SplitTopLevel(string value) { var result = new List<string>(); int depth = 0, start = 0; for (int i = 0; i < value.Length; i++) { if (value[i] == '(') depth++; else if (value[i] == ')') depth = Math.Max(0, depth - 1); else if (value[i] == ',' && depth == 0) { result.Add(value.Substring(start, i - start)); start = i + 1; } } result.Add(value.Substring(start)); return result; }
        private static void AddLocal(CompletionContext c, string name, IEnumerable<ColumnMetadata> columns) { var o = new DatabaseObjectMetadata { Schema = string.Empty, Name = Clean(name), Kind = CompletionItemKind.Table }; o.Columns.AddRange(columns); c.LocalObjects.RemoveAll(x => string.Equals(x.Name, o.Name, StringComparison.OrdinalIgnoreCase)); c.LocalObjects.Add(o); c.Aliases[o.Name] = o.Name; }
        private static string ResolveAlias(string value, CompletionContext c) => c.Aliases.TryGetValue(value ?? string.Empty, out string resolved) ? resolved : value;
        private static bool ObjectMatches(string a, string b) => string.Equals(CleanQualified(a), CleanQualified(b), StringComparison.OrdinalIgnoreCase) || string.Equals(LastPart(a), LastPart(b), StringComparison.OrdinalIgnoreCase);
        private static bool EndsWith(string a, string b) => string.Equals(CleanQualified(a), CleanQualified(b), StringComparison.OrdinalIgnoreCase) || CleanQualified(a).EndsWith("." + CleanQualified(b), StringComparison.OrdinalIgnoreCase);
        private static string LastPart(string value) => CleanQualified(value).Split('.').LastOrDefault() ?? string.Empty;
        private static string CleanQualified(string value) => string.Join(".", (value ?? string.Empty).Split('.').Select(Clean));
        private static string Clean(string value) => (value ?? string.Empty).Trim().Trim('[', ']');
        private static string Quote(string value) => SettingsManager.GetSqlCompletionSettings().useSquareBrackets ? "[" + (value ?? string.Empty).Replace("]", "]]" ) + "]" : value ?? string.Empty;
        private static string MakeAlias(string name, IEnumerable<string> existing) { string seed = new string((name ?? "t").Where(char.IsUpper).ToArray()).ToLowerInvariant(); if (string.IsNullOrEmpty(seed)) seed = (name ?? "t").Substring(0, 1).ToLowerInvariant(); var used = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase); string result = seed; int n = 2; while (used.Contains(result)) result = seed + n++; return result; }
        private static readonly HashSet<string> ClauseKeywords = new HashSet<string>(new[] { "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "ON", "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT", "OPTION", "OFFSET", "FETCH", "FOR" }, StringComparer.OrdinalIgnoreCase);
        private static readonly string[] Keywords = { "SELECT", "FROM", "WHERE", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS APPLY", "OUTER APPLY", "ON", "AS", "GROUP BY", "ORDER BY", "HAVING", "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "MERGE", "AND", "OR", "NOT", "NULL", "IS NULL", "IS NOT NULL", "CASE", "WHEN", "THEN", "ELSE", "END", "DISTINCT", "TOP", "UNION", "UNION ALL", "EXISTS", "IN", "LIKE", "BETWEEN", "DECLARE", "EXEC", "CREATE", "ALTER" };
    }
}
