using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace AxialSqlTools.Completion
{
    internal static class SqlCompletionAnalyzer
    {
        private const string Id = @"(?:\[(?:\]\]|[^\]])+\]|""(?:""""|[^""])+""|[#@\w$]+)";
        private static readonly Regex AliasPattern = new Regex(@"\b(?:FROM|JOIN|APPLY)\s+(?<obj>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})(?:\s+(?:AS\s+)?(?<alias>" + Id + @"))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static CompletionContext Analyze(string textBeforeCaret, int caretColumn, string textAfterCaret = null)
        {
            string source = textBeforeCaret ?? string.Empty;
            string maskedSource = SqlTextContext.MaskCommentsAndStrings(source);
            SqlTextContext text = SqlTextContext.Create(textBeforeCaret);
            string statement = text.StatementText;
            string masked = SqlTextContext.MaskCommentsAndStrings(statement);
            var context = new CompletionContext { Kind = CompletionContextKind.General, CurrentStatement = statement, CurrentBatch = text.BatchText };
            if (SqlTextContext.IsInsideCommentOrString(source, source.Length))
            {
                context.Suppress = true;
                return context;
            }
            if (source.Length > 0 && !char.IsWhiteSpace(source[source.Length - 1]) && maskedSource[source.Length - 1] == ' ')
            {
                context.Suppress = true;
                return context;
            }
            string semanticText = statement + GetSemanticSuffix(textAfterCaret);
            // Session-scoped #temp tables survive GO, so inspect all text before
            // the caret rather than only the current batch.
            AddLocalObjects(source + GetSemanticSuffix(textAfterCaret), text.BatchText, context);
            SqlSemanticModel semanticModel = SqlSemanticModel.Create(semanticText, statement.Length);
            semanticModel.CopyAliasesTo(context);
            foreach (string column in semanticModel.ReferencedColumns) context.ReferencedColumns.Add(column);
            foreach (string column in semanticModel.GroupByColumns) context.GroupByColumns.Add(column);
            context.SelectAliases.AddRange(semanticModel.SelectAliases);
            context.Diagnostics.AddRange(semanticModel.Diagnostics);
            context.HasParseErrors = semanticModel.HasParseErrors;
            if (context.Aliases.Count == 0) AddAliases(masked, context);
            Match word = Regex.Match(statement, @"(?<prefix>[@#\w$\[\]]*)$");
            context.Prefix = Clean(word.Groups["prefix"].Value);
            context.ReplacementStartColumn = Math.Max(0, caretColumn - word.Groups["prefix"].Length);

            bool snippetsEnabled = SettingsManager.GetSnippetSettings().useSnippets;
            if (snippetsEnabled && IsExactSnippetPrefix(context.Prefix, true, SnippetService.GetAllSnippets()))
            {
                // Snippets are editor constructs, not EXEC positional values. An
                // exact prefix therefore wins in every SQL context so Ctrl+Space
                // can present and replace it just like the configured Tab key.
                context.Kind = CompletionContextKind.General;
                context.TargetObject = null;
                return context;
            }
            if (TryAnalyzeDetachedStatementOrSnippet(statement, caretColumn, context)) return context;
            if (TryAnalyzeExecute(statement, caretColumn, context)) return context;
            if (TryAnalyzeAdvancedContext(masked, caretColumn, context)) return context;

            // INSERT column lists use the same "identifier(" shape as a function
            // call, so they must be recognized before generic function arguments.
            Match insert = Regex.Match(masked, @"\bINSERT\s+(?:INTO\s+)?(?<target>(?:" + Id + @"(?:\s*\.\s*" + Id + @"){0,3}|" + Id + @"(?:\s*\.\s*" + Id + @")?\s*\.\s*\.\s*" + Id + @"))\s*\([^)]*$", RegexOptions.IgnoreCase);
            if (insert.Success)
            {
                context.Kind = CompletionContextKind.InsertColumns;
                context.TargetObject = CleanQualified(insert.Groups["target"].Value);
                return context;
            }

            if (TryFindFunctionCall(masked, out string functionTarget, out string functionArguments) && IsFunctionTarget(functionTarget))
            {
                context.Kind = CompletionContextKind.FunctionArguments;
                context.TargetObject = CleanQualified(functionTarget);
                Match argumentPrefix = Regex.Match(functionArguments, @"(?<prefix>[@#\w$]*)$");
                context.Prefix = argumentPrefix.Groups["prefix"].Value;
                context.ReplacementStartColumn = Math.Max(0, caretColumn - context.Prefix.Length);
                context.ArgumentIndex = CountTopLevelArguments(functionArguments);
                return context;
            }

            Match completedInsertTarget = Regex.Match(masked, @"\bINSERT\s+INTO\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})\s+$", RegexOptions.IgnoreCase);
            if (completedInsertTarget.Success)
            {
                context.Kind = CompletionContextKind.InsertBody;
                context.TargetObject = CleanQualified(completedInsertTarget.Groups["target"].Value);
                context.Prefix = string.Empty;
                context.ReplacementStartColumn = caretColumn;
                return context;
            }

            Match omittedSchemaMember = Regex.Match(statement,
                @"(?<qual>" + Id + @"(?:\s*\.\s*" + Id + @")?)\s*\.\s*\.\s*(?<prefix>" + Id + @")?$",
                RegexOptions.IgnoreCase);
            if (omittedSchemaMember.Success)
            {
                context.Kind = CompletionContextKind.Member;
                context.Qualifier = CleanQualified(omittedSchemaMember.Groups["qual"].Value);
                context.Prefix = Clean(omittedSchemaMember.Groups["prefix"].Value);
                context.ReplacementStartColumn = Math.Max(0, caretColumn - omittedSchemaMember.Groups["prefix"].Length);
                context.HasOmittedSchemaQualifier = true;
                context.IsDataSourceMember = IsDataSourceMemberContext(masked, omittedSchemaMember.Index);
                return context;
            }

            Match member = Regex.Match(statement, @"(?<qual>" + Id + @"(?:\s*\.\s*" + Id + @"){0,2})\s*\.\s*(?<prefix>" + Id + @")?$");
            if (member.Success)
            {
                context.Kind = CompletionContextKind.Member;
                context.IsJoinSource = Regex.IsMatch(masked, @"\bJOIN\s+" + Id + @"(?:\s*\.\s*" + Id + @"){0,2}\s*\.\s*(?:" + Id + @")?$", RegexOptions.IgnoreCase);
                context.IsDataSourceMember = context.IsJoinSource || IsDataSourceMemberContext(masked, member.Index);
                context.Qualifier = CleanQualified(member.Groups["qual"].Value);
                context.Prefix = Clean(member.Groups["prefix"].Value);
                context.ReplacementStartColumn = Math.Max(0, caretColumn - member.Groups["prefix"].Length);
                return context;
            }

            string withoutPrefix = statement.Substring(0, Math.Max(0, statement.Length - word.Groups["prefix"].Length));
            Match insertBody = Regex.Match(masked, @"\bINSERT\s+INTO\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})\s+$", RegexOptions.IgnoreCase);
            Match update = Regex.Match(masked, @"\bUPDATE\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})(?:\s+(?:AS\s+)?" + Id + @")?\s+SET\s+[^;]*$", RegexOptions.IgnoreCase);
            if (insertBody.Success) { context.Kind = CompletionContextKind.InsertBody; context.TargetObject = CleanQualified(insertBody.Groups["target"].Value); }
            else if (update.Success) { context.Kind = CompletionContextKind.UpdateSet; context.TargetObject = ResolveAlias(CleanQualified(update.Groups["target"].Value), context); }
            else if (Regex.IsMatch(withoutPrefix, @"\b(?:MERGE|TRUNCATE\s+TABLE|DELETE\s+FROM)\s*$", RegexOptions.IgnoreCase)) context.Kind = CompletionContextKind.DataSource;
            else context.Kind = DetectClauseContext(withoutPrefix);
            if (context.Kind == CompletionContextKind.Predicate)
            {
                Match activeColumn = Regex.Matches(masked, @"(?<column>" + Id + @"(?:\s*\.\s*" + Id + @")?)\s*(?:=|<>|!=|>=|<=|>|<|LIKE|IN|BETWEEN)", RegexOptions.IgnoreCase)
                    .Cast<Match>().LastOrDefault();
                if (activeColumn != null && activeColumn.Success) context.ActiveColumn = CleanQualified(activeColumn.Groups["column"].Value);
            }
            return context;
        }

        private static CompletionContextKind DetectClauseContext(string sql)
        {
            try
            {
                var parser = new TSql170Parser(true);
                IList<TSqlParserToken> all = parser.GetTokenStream(new StringReader(sql ?? string.Empty), out IList<ParseError> _);
                var tokens = all.Where(t => t.TokenType != TSqlTokenType.WhiteSpace && t.TokenType != TSqlTokenType.SingleLineComment
                    && t.TokenType != TSqlTokenType.MultilineComment && t.TokenType != TSqlTokenType.EndOfFile).ToList();
                int depth = 0;
                var depths = new List<int>(tokens.Count);
                foreach (TSqlParserToken token in tokens)
                {
                    if (token.TokenType == TSqlTokenType.RightParenthesis) depth = Math.Max(0, depth - 1);
                    depths.Add(depth);
                    if (token.TokenType == TSqlTokenType.LeftParenthesis) depth++;
                }
                int caretDepth = depth;
                int clause = -1;
                for (int i = 0; i < tokens.Count; i++)
                    if (depths[i] == caretDepth && IsClauseToken(tokens[i].Text)) clause = i;
                if (clause < 0) return CompletionContextKind.General;
                string word = tokens[clause].Text.ToUpperInvariant();
                string previous = clause > 0 && depths[clause - 1] == caretDepth ? tokens[clause - 1].Text.ToUpperInvariant() : string.Empty;
                bool atClauseStart = clause == tokens.Count - 1;
                bool afterSeparator = tokens.Count > 0 && (tokens[tokens.Count - 1].Text == "," || tokens[tokens.Count - 1].Text.Equals("APPLY", StringComparison.OrdinalIgnoreCase));
                if (word == "JOIN" && atClauseStart) return CompletionContextKind.Join;
                if ((word == "FROM" || word == "APPLY" || word == "UPDATE" || word == "INTO") && (atClauseStart || afterSeparator)) return CompletionContextKind.DataSource;
                if (word == "MERGE" && atClauseStart) return CompletionContextKind.DataSource;
                if (word == "USING" && atClauseStart) return CompletionContextKind.MergeSource;
                if (word == "WHERE" || word == "HAVING" || word == "ON") return CompletionContextKind.Predicate;
                if (word == "BY" && previous == "GROUP") return CompletionContextKind.GroupBy;
                if (word == "BY" && previous == "ORDER") return CompletionContextKind.OrderBy;
                if (word == "SELECT") return CompletionContextKind.SelectList;
                return CompletionContextKind.General;
            }
            catch { return CompletionContextKind.General; }
        }

        private static bool IsClauseToken(string value) => ClauseTokens.Contains(value ?? string.Empty);
        private static readonly HashSet<string> ClauseTokens = new HashSet<string>(new[]
        { "SELECT", "FROM", "JOIN", "APPLY", "WHERE", "HAVING", "ON", "GROUP", "ORDER", "BY", "UNION", "EXCEPT", "INTERSECT", "OPTION", "UPDATE", "INTO", "SET", "VALUES", "USING", "WHEN", "MERGE" }, StringComparer.OrdinalIgnoreCase);

        private static bool IsDataSourceMemberContext(string maskedStatement, int memberStart)
        {
            string beforeMember = (maskedStatement ?? string.Empty).Substring(0,
                Math.Max(0, Math.Min(memberStart, (maskedStatement ?? string.Empty).Length)));
            CompletionContextKind kind = DetectClauseContext(beforeMember);
            return kind == CompletionContextKind.DataSource || kind == CompletionContextKind.Join || kind == CompletionContextKind.MergeSource;
        }

        private static bool TryAnalyzeAdvancedContext(string masked, int caretColumn, CompletionContext context)
        {
            Match window = Regex.Match(masked, @"\bOVER\s*\((?<body>[^()]*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (window.Success)
            {
                string body = window.Groups["body"].Value;
                if (Regex.IsMatch(body, @"\bORDER\s+BY\b", RegexOptions.IgnoreCase)) context.Kind = CompletionContextKind.WindowOrderBy;
                else if (Regex.IsMatch(body, @"\bPARTITION\s+BY\b", RegexOptions.IgnoreCase)) context.Kind = CompletionContextKind.WindowPartitionBy;
                else context.Kind = CompletionContextKind.WindowPartitionBy;
                return true;
            }

            Match output = Regex.Match(masked, @"\bOUTPUT\s+(?:(?<qual>inserted|deleted)\s*\.\s*)?(?<prefix>" + Id + @")?$", RegexOptions.IgnoreCase);
            if (output.Success)
            {
                context.Kind = CompletionContextKind.Output;
                context.Qualifier = Clean(output.Groups["qual"].Value);
                context.Prefix = Clean(output.Groups["prefix"].Value);
                context.ReplacementStartColumn = Math.Max(0, caretColumn - output.Groups["prefix"].Length);
                Match target = Regex.Match(masked, @"\b(?:INSERT\s+(?:INTO\s+)?|UPDATE\s+|DELETE\s+FROM\s+|MERGE\s+(?:INTO\s+)?)(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})", RegexOptions.IgnoreCase);
                if (target.Success) context.TargetObject = ResolveAlias(CleanQualified(target.Groups["target"].Value), context);
                return true;
            }

            if (HasUnclosedClause(masked, "PIVOT") || HasUnclosedClause(masked, "UNPIVOT"))
            {
                context.Kind = CompletionContextKind.Pivot;
                return true;
            }

            Match index = Regex.Match(masked, @"\bCREATE\s+(?:UNIQUE\s+)?(?:CLUSTERED\s+|NONCLUSTERED\s+)?INDEX\s+" + Id
                + @"\s+ON\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})\s*\([^)]*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (index.Success)
            {
                context.Kind = CompletionContextKind.IndexColumns;
                context.TargetObject = CleanQualified(index.Groups["target"].Value);
                return true;
            }

            Match alterColumn = Regex.Match(masked, @"\bALTER\s+TABLE\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})\s+(?:ALTER|DROP)\s+COLUMN\s+(?:" + Id + @")?$", RegexOptions.IgnoreCase);
            if (alterColumn.Success)
            {
                context.Kind = CompletionContextKind.AlterTableColumn;
                context.TargetObject = CleanQualified(alterColumn.Groups["target"].Value);
                return true;
            }
            Match alter = Regex.Match(masked, @"\bALTER\s+TABLE\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})\s+(?:" + Id + @")?$", RegexOptions.IgnoreCase);
            if (alter.Success)
            {
                context.Kind = CompletionContextKind.AlterTableAction;
                context.TargetObject = CleanQualified(alter.Groups["target"].Value);
                return true;
            }

            Match create = Regex.Match(masked, @"\bCREATE\s+TABLE\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})\s*\((?<body>[\s\S]*)$", RegexOptions.IgnoreCase);
            if (create.Success)
            {
                context.TargetObject = CleanQualified(create.Groups["target"].Value);
                string body = create.Groups["body"].Value;
                context.Kind = Regex.IsMatch(body, @"\b(?:PRIMARY\s+KEY|UNIQUE|FOREIGN\s+KEY)\s*\([^)]*$", RegexOptions.IgnoreCase)
                    ? CompletionContextKind.ConstraintColumns
                    : CompletionContextKind.CreateTableDefinition;
                return true;
            }
            Match constraint = Regex.Match(masked, @"\bALTER\s+TABLE\s+(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})[\s\S]*\b(?:PRIMARY\s+KEY|UNIQUE|FOREIGN\s+KEY)\s*\([^)]*$", RegexOptions.IgnoreCase);
            if (constraint.Success)
            {
                context.Kind = CompletionContextKind.ConstraintColumns;
                context.TargetObject = CleanQualified(constraint.Groups["target"].Value);
                return true;
            }
            return false;
        }

        private static bool HasUnclosedClause(string sql, string keyword)
        {
            Match match = Regex.Matches(sql ?? string.Empty, @"\b" + keyword + @"\s*\(", RegexOptions.IgnoreCase).Cast<Match>().LastOrDefault();
            if (match == null) return false;
            int depth = 0;
            for (int i = match.Index; i < sql.Length; i++)
            {
                if (sql[i] == '(') depth++;
                else if (sql[i] == ')') depth--;
            }
            return depth > 0;
        }

        private static int CountTopLevelArguments(string value)
        {
            int depth = 0, index = 0;
            foreach (char c in value ?? string.Empty)
            {
                if (c == '(') depth++;
                else if (c == ')') depth = Math.Max(0, depth - 1);
                else if (c == ',' && depth == 0) index++;
            }
            return index;
        }

        internal static string GetSemanticSuffix(string textAfterCaret)
        {
            if (string.IsNullOrEmpty(textAfterCaret)) return string.Empty;
            int depth = 0;
            bool singleQuote = false, doubleQuote = false, bracket = false, lineComment = false, blockComment = false;
            int lineStart = 0;
            for (int i = 0; i < textAfterCaret.Length; i++)
            {
                char c = textAfterCaret[i], next = i + 1 < textAfterCaret.Length ? textAfterCaret[i + 1] : '\0';
                if (lineComment)
                {
                    if (c == '\r' || c == '\n') { lineComment = false; lineStart = i + 1; }
                    continue;
                }
                if (blockComment)
                {
                    if (c == '*' && next == '/') { blockComment = false; i++; }
                    continue;
                }
                if (singleQuote)
                {
                    if (c == '\'' && next == '\'') i++;
                    else if (c == '\'') singleQuote = false;
                    continue;
                }
                if (doubleQuote)
                {
                    if (c == '"' && next == '"') i++;
                    else if (c == '"') doubleQuote = false;
                    continue;
                }
                if (bracket)
                {
                    if (c == ']' && next == ']') i++;
                    else if (c == ']') bracket = false;
                    continue;
                }
                if (c == '-' && next == '-') { lineComment = true; i++; continue; }
                if (c == '/' && next == '*') { blockComment = true; i++; continue; }
                if (c == '\'') { singleQuote = true; continue; }
                if (c == '"') { doubleQuote = true; continue; }
                if (c == '[') { bracket = true; continue; }
                if (c == '(') { depth++; continue; }
                if (c == ')') { depth = Math.Max(0, depth - 1); continue; }
                if (c == ';' && depth == 0) return textAfterCaret.Substring(0, i);
                if ((c == '\r' || c == '\n') && depth == 0)
                {
                    string line = textAfterCaret.Substring(lineStart, i - lineStart).Trim();
                    if (Regex.IsMatch(line, @"^GO(?:\s+\d+)?$", RegexOptions.IgnoreCase)) return textAfterCaret.Substring(0, lineStart);
                    lineStart = i + 1;
                }
            }
            if (depth == 0)
            {
                string lastLine = textAfterCaret.Substring(Math.Min(lineStart, textAfterCaret.Length)).Trim();
                if (Regex.IsMatch(lastLine, @"^GO(?:\s+\d+)?$", RegexOptions.IgnoreCase)) return textAfterCaret.Substring(0, lineStart);
            }
            return textAfterCaret;
        }

        private static bool TryFindFunctionCall(string masked, out string target, out string arguments)
        {
            target = arguments = string.Empty;
            string value = masked ?? string.Empty;
            int nested = 0;
            for (int index = value.Length - 1; index >= 0; index--)
            {
                if (value[index] == ')') { nested++; continue; }
                if (value[index] != '(') continue;
                if (nested > 0) { nested--; continue; }
                Match match = Regex.Match(value.Substring(0, index), @"(?<target>" + Id + @"(?:\s*\.\s*" + Id + @"){0,3})\s*$", RegexOptions.IgnoreCase);
                if (!match.Success) return false;
                string beforeTarget = value.Substring(0, match.Index);
                if (Regex.IsMatch(beforeTarget, @"\b(?:INSERT(?:\s+INTO)?|CREATE\s+TABLE|ALTER\s+TABLE|REFERENCES)\s*$", RegexOptions.IgnoreCase))
                    return false;
                target = match.Groups["target"].Value;
                arguments = value.Substring(index + 1);
                return true;
            }
            return false;
        }

        private static bool IsFunctionTarget(string target)
        {
            string name = CleanQualified(target);
            int dot = name.LastIndexOf('.');
            if (dot >= 0) name = name.Substring(dot + 1);
            return !NonFunctionParentheses.Contains(name);
        }

        private static readonly HashSet<string> NonFunctionParentheses = new HashSet<string>(new[]
        {
            "VALUES", "IN", "EXISTS", "IF", "WHILE", "CHECK", "CONSTRAINT", "PRIMARY", "FOREIGN",
            "REFERENCES", "TABLE", "BEGIN", "OVER", "WITHIN", "GROUPING", "RETURN", "THROW", "RAISERROR"
        }, StringComparer.OrdinalIgnoreCase);

        private static bool TryAnalyzeExecute(string statement, int caretColumn, CompletionContext context)
        {
            // StatementText can include leading comments when the preceding SQL was
            // not terminated with a semicolon.  Do not let an example such as
            // "-- EXEC dbo.usp_Test ..." turn a later SELECT into EXEC argument
            // completion. Masking preserves offsets, so replacement columns remain
            // aligned with the editor text.
            string executableStatement = SqlTextContext.MaskCommentsAndStrings(statement);
            Match activeStatement = Regex.Matches(executableStatement,
                    @"(?im)^[ \t]*(?<keyword>EXEC(?:UTE)?|SELECT|INSERT|UPDATE|DELETE|MERGE|DECLARE|SET|CREATE|ALTER|DROP|TRUNCATE|USE|PRINT|IF|WHILE|BEGIN|DBCC)\b")
                .Cast<Match>()
                .LastOrDefault();
            if (activeStatement != null)
            {
                string keyword = activeStatement.Groups["keyword"].Value;
                if (!keyword.Equals("EXEC", StringComparison.OrdinalIgnoreCase)
                    && !keyword.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase))
                    return false;
                executableStatement = executableStatement.Substring(activeStatement.Index);
            }
            Match exec = Regex.Match(executableStatement, @"\bEXEC(?:UTE)?\b(?<body>[^;]*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
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

        private static bool TryAnalyzeDetachedStatementOrSnippet(string statement, int caretColumn, CompletionContext context)
        {
            string masked = SqlTextContext.MaskCommentsAndStrings(statement ?? string.Empty);
            if (!Regex.IsMatch(masked, @"^\s*EXEC(?:UTE)?\s+" + Id, RegexOptions.IgnoreCase)) return false;

            // A stored procedure may be followed by another statement without a
            // semicolon. While that next token is still incomplete (for example
            // "SEL" or a custom "SSF" snippet), ScriptDom cannot split it yet.
            // Two spaces or a new line are treated as the user's statement
            // separator; named parameters such as "    @Id" stay in EXEC scope.
            Match detached = Regex.Match(masked,
                @"(?:[ \t]{2,}|\r?\n[ \t]*)(?<prefix>[A-Za-z_][\w$]*)$",
                RegexOptions.IgnoreCase);
            if (!detached.Success) return false;

            string prefix = detached.Groups["prefix"].Value;
            bool snippetPrefix = SettingsManager.GetSnippetSettings().useSnippets
                && SnippetService.GetAllSnippets().Any(s => !string.IsNullOrWhiteSpace(s.Prefix)
                    && s.Prefix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            bool statementPrefix = prefix.Length >= 2 && DetachedStatementStarters.Any(s =>
                s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (!snippetPrefix && !statementPrefix) return false;

            context.Kind = CompletionContextKind.General;
            context.Prefix = prefix;
            context.ReplacementStartColumn = Math.Max(0, caretColumn - detached.Groups["prefix"].Length);
            context.TargetObject = null;
            context.UsedParameters.Clear();
            return true;
        }

        internal static bool IsExactSnippetPrefix(string prefix, bool snippetsEnabled, IEnumerable<SnippetItem> snippets)
            => snippetsEnabled && !string.IsNullOrWhiteSpace(prefix) && (snippets ?? Enumerable.Empty<SnippetItem>())
                .Any(s => string.Equals(s?.Prefix, prefix, StringComparison.OrdinalIgnoreCase));

        public static List<CompletionItem> BuildItems(CompletionContext context, MetadataSnapshot metadata)
        {
            metadata = metadata ?? MetadataSnapshot.Empty;
            var result = new List<CompletionItem>();
            var objects = metadata.Objects.Concat(context.LocalObjects).ToList();
            HydrateProjectedColumns(objects, context.LocalObjects);
            AddDeclaredVariables(result, context);
            switch (context.Kind)
            {
                case CompletionContextKind.ExecuteObject:
                    result.AddRange(metadata.Objects.Where(o => o.Kind == CompletionItemKind.Procedure && MatchesContainer(o, context.Qualifier)).Select(o => ObjectItem(o, o.IsSystem ? 25 : 120, !string.IsNullOrEmpty(context.Qualifier)))); break;
                case CompletionContextKind.ExecuteArguments: AddParameters(result, context, metadata); break;
                case CompletionContextKind.ExecuteArgumentValue: AddValues(result, context, metadata); break;
                case CompletionContextKind.FunctionArguments: AddFunctionArguments(result, context, metadata, objects); break;
                case CompletionContextKind.Member:
                    if (context.IsJoinSource) result.AddRange(BuildJoinItems(context, metadata));
                    else AddMembers(result, context, objects, metadata);
                    break;
                case CompletionContextKind.Join: result.AddRange(BuildJoinItems(context, metadata)); break;
                case CompletionContextKind.InsertColumns:
                case CompletionContextKind.UpdateSet: AddWritableColumns(result, context, objects); break;
                case CompletionContextKind.InsertBody: AddInsertTemplate(result, context, objects); break;
                case CompletionContextKind.DataSource:
                    result.AddRange(objects.Where(IsDataSource).Select(o => DataSourceItem(o, context, 40)));
                    result.AddRange(metadata.Schemas.Select(s => ContainerItem(s, CompletionItemKind.Schema, 60)));
                    result.AddRange(metadata.Databases.Select(d => ContainerItem(d, CompletionItemKind.Database, 35)));
                    result.AddRange(metadata.LinkedServers.Select(s => ContainerItem(s, CompletionItemKind.Server, 30)));
                    AddBuiltInTableFunctions(result, metadata.CompatibilityLevel);
                    break;
                case CompletionContextKind.MergeSource: result.AddRange(objects.Where(IsDataSource).Select(o => ObjectItem(o, 55, false))); break;
                case CompletionContextKind.Predicate: AddScopedColumns(result, context, objects, 85, false); AddPredicateItems(result); AddTypedValues(result, context, objects); break;
                case CompletionContextKind.SelectList: AddSelectListItems(result, context, objects); break;
                case CompletionContextKind.WindowPartitionBy:
                case CompletionContextKind.WindowOrderBy: AddWindowItems(result, context, objects); break;
                case CompletionContextKind.Output: AddOutputItems(result, context, objects); break;
                case CompletionContextKind.Pivot: AddPivotItems(result, context, objects); break;
                case CompletionContextKind.CreateTableDefinition: AddCreateTableItems(result); break;
                case CompletionContextKind.ConstraintColumns:
                case CompletionContextKind.IndexColumns:
                case CompletionContextKind.AlterTableColumn: AddTargetColumns(result, context, objects); break;
                case CompletionContextKind.AlterTableAction: AddAlterTableItems(result); break;
                case CompletionContextKind.GroupBy: AddScopedColumns(result, context, objects, 90, true); AddGroupByClause(result, context); break;
                case CompletionContextKind.OrderBy: AddScopedColumns(result, context, objects, 90); AddSelectAliases(result, context); break;
                default: AddGeneral(result, context, objects); break;
            }
            foreach (CompletionItem item in result) item.ScopeKey = context.MetadataScope;
            return FilterAndSort(result.GroupBy(i => i.Kind + "|" + i.DisplayText + "|" + i.InsertText, StringComparer.OrdinalIgnoreCase).Select(g => g.First()), context.Prefix);
        }

        private static void HydrateProjectedColumns(List<DatabaseObjectMetadata> objects, List<DatabaseObjectMetadata> localObjects)
        {
            for (int pass = 0; pass <= localObjects.Count; pass++)
            {
                bool changed = false;
                foreach (DatabaseObjectMetadata local in localObjects.Where(o => o.ProjectionSources.Count > 0))
                    foreach (string sourceName in local.ProjectionSources)
                    {
                        DatabaseObjectMetadata source = objects.FirstOrDefault(o => !ReferenceEquals(o, local) && (ObjectMatches(o.QualifiedName, sourceName) || ObjectMatches(o.Name, sourceName)));
                        if (source == null) continue;
                        foreach (ColumnMetadata column in source.Columns)
                            if (!local.Columns.Any(c => string.Equals(c.Name, column.Name, StringComparison.OrdinalIgnoreCase)))
                            {
                                local.Columns.Add(new ColumnMetadata
                                {
                                    Name = column.Name, DataType = column.DataType, IsNullable = column.IsNullable,
                                    IsIdentity = column.IsIdentity, IsComputed = column.IsComputed,
                                    IsPrimaryKey = column.IsPrimaryKey, IsForeignKey = column.IsForeignKey, IsUnique = column.IsUnique,
                                    Ordinal = column.Ordinal, MaxLength = column.MaxLength, Precision = column.Precision, Scale = column.Scale,
                                    DefaultDefinition = column.DefaultDefinition, Description = column.Description
                                });
                                changed = true;
                            }
                    }
                if (!changed) break;
            }
        }

        private static void AddFunctionArguments(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata, List<DatabaseObjectMetadata> objects)
        {
            bool knownFunction = objects.Any(o => o.Kind == CompletionItemKind.Function && ObjectMatches(o.QualifiedName, context.TargetObject))
                || BuiltInFunctions.Contains(LastPart(context.TargetObject));
            if (!knownFunction)
            {
                AddScopedColumns(result, context, objects, 75);
                return;
            }
            AddScopedColumns(result, context, objects, 80);
            AddValues(result, context, metadata);
        }

        private static void AddBuiltInTableFunctions(List<CompletionItem> result, int compatibilityLevel)
        {
            foreach (string name in new[] { "OPENXML" })
                result.Add(new CompletionItem { DisplayText = name, InsertText = name, Kind = CompletionItemKind.Function, Description = "SQL Server table-valued function", Score = 48 });
            if (compatibilityLevel >= 130)
                foreach (string name in new[] { "OPENJSON", "STRING_SPLIT" })
                    result.Add(new CompletionItem { DisplayText = name, InsertText = name, Kind = CompletionItemKind.Function, Description = "SQL Server table-valued function", Score = 48 });
            if (compatibilityLevel >= 160)
                result.Add(new CompletionItem { DisplayText = "GENERATE_SERIES", InsertText = "GENERATE_SERIES", Kind = CompletionItemKind.Function, Description = "SQL Server table-valued function", Score = 48 });
        }

        private static void AddSelectListItems(List<CompletionItem> result, CompletionContext context, List<DatabaseObjectMetadata> objects)
        {
            AddGeneral(result, context, objects);
            AddScopedColumns(result, context, objects, 95);
            var columns = new List<string>();
            bool qualify = context.Aliases.Count > 1;
            foreach (var source in context.Aliases)
            {
                DatabaseObjectMetadata item = objects.FirstOrDefault(o => ObjectMatches(o.QualifiedName, source.Value) || ObjectMatches(o.Name, source.Value));
                if (item == null) continue;
                columns.AddRange(item.Columns.Select(c => (qualify ? Quote(source.Key) + "." : string.Empty) + Quote(c.Name)));
            }
            if (columns.Count > 1)
                result.Add(new CompletionItem { DisplayText = "(all SELECT columns)", InsertText = string.Join("," + Environment.NewLine + "    ", columns), Kind = CompletionItemKind.Snippet, Description = columns.Count + " columns", Score = 145 });
            foreach (string expression in new[] { "COUNT(*)", "SUM(${1:expression})", "CASE WHEN ${1:condition} THEN ${2:value} ELSE ${3:value} END" })
                result.Add(new CompletionItem { DisplayText = expression.Split('$')[0], InsertText = expression, Kind = CompletionItemKind.Snippet, Description = "SELECT expression", Score = 65 });
        }

        private static void AddWindowItems(List<CompletionItem> result, CompletionContext context, List<DatabaseObjectMetadata> objects)
        {
            AddScopedColumns(result, context, objects, 100);
            if (context.Kind == CompletionContextKind.WindowPartitionBy)
            {
                result.Add(new CompletionItem { DisplayText = "PARTITION BY ... ORDER BY ...", InsertText = "PARTITION BY ${1:column}" + Environment.NewLine + "ORDER BY ${2:column}", Kind = CompletionItemKind.Snippet, Description = "window clause", Score = 130 });
                result.Add(new CompletionItem { DisplayText = "ORDER BY ...", InsertText = "ORDER BY ${1:column}", Kind = CompletionItemKind.Snippet, Description = "window ordering", Score = 115 });
            }
            else
            {
                foreach (string value in new[] { "ASC", "DESC", "ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW", "ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING" })
                    result.Add(new CompletionItem { DisplayText = value, InsertText = value, Kind = CompletionItemKind.Keyword, Description = "window ordering/frame", Score = 75 });
            }
        }

        private static void AddOutputItems(List<CompletionItem> result, CompletionContext context, List<DatabaseObjectMetadata> objects)
        {
            DatabaseObjectMetadata target = FindTarget(context, objects);
            if (target == null) return;
            string statement = context.CurrentStatement ?? string.Empty;
            var qualifiers = !string.IsNullOrWhiteSpace(context.Qualifier) ? new[] { context.Qualifier }
                : Regex.IsMatch(statement, @"\bINSERT\b", RegexOptions.IgnoreCase) ? new[] { "inserted" }
                : Regex.IsMatch(statement, @"\bDELETE\b", RegexOptions.IgnoreCase) ? new[] { "deleted" }
                : new[] { "inserted", "deleted" };
            foreach (string qualifier in qualifiers)
                foreach (ColumnMetadata column in target.Columns)
                {
                    CompletionItem item = ColumnItem(column, 105, target);
                    item.DisplayText = qualifier + "." + column.Name;
                    item.InsertText = string.IsNullOrWhiteSpace(context.Qualifier) ? qualifier + "." + Quote(column.Name) : Quote(column.Name);
                    item.Description = "OUTPUT " + item.Description;
                    result.Add(item);
                }
            if (target.Columns.Count > 1)
            {
                string qualifier = qualifiers[0];
                result.Add(new CompletionItem { DisplayText = "(all OUTPUT columns)", InsertText = string.Join("," + Environment.NewLine + "    ", target.Columns.Select(c => qualifier + "." + Quote(c.Name))), Kind = CompletionItemKind.Snippet, Description = target.Columns.Count + " columns", Score = 135 });
            }
        }

        private static void AddPivotItems(List<CompletionItem> result, CompletionContext context, List<DatabaseObjectMetadata> objects)
        {
            AddScopedColumns(result, context, objects, 90);
            foreach (string value in new[] { "SUM(${1:value}) FOR ${2:pivot_column} IN (${3:values})", "COUNT(${1:value}) FOR ${2:pivot_column} IN (${3:values})", "FOR ${1:pivot_column} IN (${2:values})" })
                result.Add(new CompletionItem { DisplayText = value.StartsWith("FOR", StringComparison.Ordinal) ? "FOR ... IN (...)" : value.Substring(0, value.IndexOf('(')) + "(...) FOR ... IN (...)" , InsertText = value, Kind = CompletionItemKind.Snippet, Description = "PIVOT clause", Score = 120 });
        }

        private static void AddCreateTableItems(List<CompletionItem> result)
        {
            foreach (string value in new[] { "${1:ColumnName} INT NOT NULL", "${1:ColumnName} NVARCHAR(${2:100}) NULL", "${1:ColumnName} DECIMAL(${2:18}, ${3:2}) NULL", "${1:ColumnName} DATETIME2 NULL", "CONSTRAINT ${1:PK_Table} PRIMARY KEY (${2:Id})", "CONSTRAINT ${1:FK_Table_Parent} FOREIGN KEY (${2:ParentId}) REFERENCES ${3:dbo.Parent} (${4:Id})", "CONSTRAINT ${1:CK_Table_Column} CHECK (${2:condition})" })
                result.Add(new CompletionItem { DisplayText = value.Replace("${1:", string.Empty).Split('}')[0] + (value.StartsWith("CONSTRAINT", StringComparison.Ordinal) ? " constraint" : " column"), InsertText = value, Kind = CompletionItemKind.Snippet, Description = "CREATE TABLE definition", Score = 100 });
        }

        private static void AddTargetColumns(List<CompletionItem> result, CompletionContext context, List<DatabaseObjectMetadata> objects)
        {
            DatabaseObjectMetadata target = FindTarget(context, objects);
            if (target != null) result.AddRange(target.Columns.Select(c => ColumnItem(c, 110, target)));
        }

        private static void AddAlterTableItems(List<CompletionItem> result)
        {
            foreach (string value in new[] { "ADD ${1:ColumnName} ${2:INT} NULL", "ALTER COLUMN ${1:ColumnName} ${2:INT} NOT NULL", "DROP COLUMN ${1:ColumnName}", "ADD CONSTRAINT ${1:PK_Table} PRIMARY KEY (${2:Id})", "ADD CONSTRAINT ${1:FK_Table_Parent} FOREIGN KEY (${2:ParentId}) REFERENCES ${3:dbo.Parent} (${4:Id})", "DROP CONSTRAINT ${1:ConstraintName}" })
                result.Add(new CompletionItem { DisplayText = Regex.Replace(value, @"\$\{\d+:([^}]+)\}", "$1"), InsertText = value, Kind = CompletionItemKind.Snippet, Description = "ALTER TABLE action", Score = 120 });
        }

        private static void AddGroupByClause(List<CompletionItem> result, CompletionContext context)
        {
            Match select = Regex.Match(SqlTextContext.MaskCommentsAndStrings(context.CurrentStatement ?? string.Empty), @"\bSELECT\s+(?<list>.*?)\bFROM\b", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!select.Success) return;
            var expressions = SplitTopLevel(select.Groups["list"].Value)
                .Select(RemoveSelectAlias)
                .Where(x => !string.IsNullOrWhiteSpace(x) && !Regex.IsMatch(x, @"\b(?:AVG|COUNT|GROUPING|MAX|MIN|STDEV|STDEVP|STRING_AGG|SUM|VAR|VARP)\s*\(", RegexOptions.IgnoreCase))
                .ToList();
            if (expressions.Count > 0)
                result.Add(new CompletionItem { DisplayText = "(all non-aggregated SELECT expressions)", InsertText = string.Join("," + Environment.NewLine + "    ", expressions), Kind = CompletionItemKind.Snippet, Description = expressions.Count + " expressions", Score = 140 });
        }

        private static string RemoveSelectAlias(string expression)
        {
            string value = (expression ?? string.Empty).Trim();
            Match equals = Regex.Match(value, @"^" + Id + @"\s*=\s*(?<value>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (equals.Success) return equals.Groups["value"].Value.Trim();
            return Regex.Replace(value, @"\s+(?:AS\s+)?" + Id + @"\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        }

        private static DatabaseObjectMetadata FindTarget(CompletionContext context, List<DatabaseObjectMetadata> objects)
            => objects.FirstOrDefault(o => ObjectMatches(o.QualifiedName, context.TargetObject) || ObjectMatches(o.Name, context.TargetObject));

        private static readonly HashSet<string> BuiltInFunctions = new HashSet<string>(new[]
        {
            "ABS", "AVG", "CAST", "CEILING", "COALESCE", "CONCAT", "CONVERT", "COUNT", "DATEADD", "DATEDIFF",
            "DATENAME", "DATEPART", "EOMONTH", "FLOOR", "FORMAT", "GETDATE", "ISNULL", "JSON_VALUE", "LEFT",
            "LEN", "LOWER", "LTRIM", "MAX", "MIN", "NEWID", "NULLIF", "REPLACE", "RIGHT", "ROUND", "RTRIM",
            "ASCII", "CHAR", "CHARINDEX", "DATALENGTH", "DIFFERENCE", "NCHAR", "PATINDEX", "QUOTENAME",
            "REPLICATE", "REVERSE", "SOUNDEX", "SPACE", "STR", "STRING_AGG", "STRING_ESCAPE", "STRING_SPLIT",
            "STUFF", "SUBSTRING", "TRANSLATE", "UNICODE", "SUM", "TRIM", "UPPER",
            "DATEFROMPARTS", "DATETIME2FROMPARTS", "DATETIMEFROMPARTS", "DATETIMEOFFSETFROMPARTS",
            "DAY", "GETUTCDATE", "ISDATE", "MONTH", "SMALLDATETIMEFROMPARTS", "SWITCHOFFSET", "SYSDATETIME",
            "SYSDATETIMEOFFSET", "SYSUTCDATETIME", "TIMEFROMPARTS", "TODATETIMEOFFSET", "YEAR",
            "CHOOSE", "IIF", "ISJSON", "JSON_MODIFY", "JSON_QUERY", "JSON_VALUE", "TRY_CAST", "TRY_CONVERT",
            "APP_NAME", "CONNECTIONPROPERTY", "DB_ID", "DB_NAME", "HOST_NAME", "OBJECT_ID",
            "OBJECT_NAME", "ORIGINAL_LOGIN", "SCOPE_IDENTITY", "SCHEMA_ID", "SCHEMA_NAME", "SERVERPROPERTY",
            "SUSER_SNAME", "USER_NAME", "XACT_STATE"
        }, StringComparer.OrdinalIgnoreCase);

        private static void AddScopedColumns(List<CompletionItem> result, CompletionContext context, List<DatabaseObjectMetadata> objects, int score, bool excludeReferenced = false)
        {
            foreach (var source in context.Aliases)
            {
                DatabaseObjectMetadata obj = objects.FirstOrDefault(o => ObjectMatches(o.QualifiedName, source.Value) || ObjectMatches(o.Name, source.Value));
                if (obj == null) continue;
                bool qualify = context.Aliases.Count > 1;
                foreach (ColumnMetadata column in obj.Columns)
                {
                    if (excludeReferenced && IsGroupedColumn(context, source.Key, source.Value, column.Name)) continue;
                    CompletionItem item = ColumnItem(column, score, obj);
                    if (qualify)
                    {
                        item.InsertText = Quote(source.Key) + "." + Quote(column.Name);
                        item.Description = (item.Description ?? string.Empty) + " · " + source.Key;
                    }
                    result.Add(item);
                }
            }
        }

        private static bool IsGroupedColumn(CompletionContext context, string alias, string objectName, string column)
        {
            if (context.GroupByColumns.Contains(column)) return true;
            return context.GroupByColumns.Contains(alias + "." + column)
                || context.GroupByColumns.Contains(LastPart(objectName) + "." + column);
        }

        private static void AddTypedValues(List<CompletionItem> result, CompletionContext context, List<DatabaseObjectMetadata> objects)
        {
            if (string.IsNullOrWhiteSpace(context.ActiveColumn)) return;
            string columnName = LastPart(context.ActiveColumn);
            string[] activeParts = CleanQualified(context.ActiveColumn).Split('.');
            string activeOwner = activeParts.Length > 1 ? ResolveAlias(activeParts[activeParts.Length - 2], context) : null;
            ColumnMetadata column = null;
            foreach (string source in context.Aliases.Values)
            {
                if (!string.IsNullOrWhiteSpace(activeOwner) && !ObjectMatches(source, activeOwner)) continue;
                DatabaseObjectMetadata owner = objects.FirstOrDefault(o => ObjectMatches(o.QualifiedName, source) || ObjectMatches(o.Name, source));
                column = owner?.Columns.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
                if (column != null) break;
            }
            string type = (column?.DataType ?? string.Empty).ToLowerInvariant();
            if (type == "bit")
            {
                result.Add(new CompletionItem { DisplayText = "0", InsertText = "0", Kind = CompletionItemKind.Keyword, Description = "bit value", Score = 95 });
                result.Add(new CompletionItem { DisplayText = "1", InsertText = "1", Kind = CompletionItemKind.Keyword, Description = "bit value", Score = 95 });
            }
            if (type.Contains("date") || type.Contains("time"))
                result.Add(new CompletionItem { DisplayText = "GETDATE()", InsertText = "GETDATE()", Kind = CompletionItemKind.Function, Description = "current date/time", Score = 90 });
            if (column?.IsNullable == true)
                result.Add(new CompletionItem { DisplayText = "NULL", InsertText = "NULL", Kind = CompletionItemKind.Keyword, Description = type, Score = 80 });
        }

        private static void AddPredicateItems(List<CompletionItem> result)
        {
            foreach (string value in new[] { "=", "<>", ">", ">=", "<", "<=", "LIKE", "IN", "BETWEEN", "IS NULL", "IS NOT NULL", "EXISTS" })
                result.Add(new CompletionItem { DisplayText = value, InsertText = value, Kind = CompletionItemKind.Keyword, Description = "predicate", Score = 45 });
        }

        private static void AddSelectAliases(List<CompletionItem> result, CompletionContext context)
        {
            foreach (string semanticAlias in context.SelectAliases)
                result.Add(new CompletionItem { DisplayText = semanticAlias, InsertText = Quote(semanticAlias), Kind = CompletionItemKind.Column, Description = "SELECT alias", Score = 105 });
            Match select = Regex.Match(SqlTextContext.MaskCommentsAndStrings(context.CurrentStatement ?? string.Empty), @"\bSELECT\s+(?<list>.*?)\bFROM\b", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!select.Success) return;
            foreach (string expression in SplitTopLevel(select.Groups["list"].Value))
            {
                Match alias = Regex.Match(expression, @"(?:^(?<equals>" + Id + @")\s*=|(?:\bAS\s+|\s+)(?<name>" + Id + @")\s*$)", RegexOptions.IgnoreCase);
                string aliasName = alias.Groups["equals"].Success ? Clean(alias.Groups["equals"].Value) : Clean(alias.Groups["name"].Value);
                if (alias.Success) result.Add(new CompletionItem { DisplayText = aliasName, InsertText = Quote(aliasName), Kind = CompletionItemKind.Column, Description = "SELECT alias", Score = 105 });
            }
        }

        private static void AddParameters(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata)
        {
            var parameters = metadata.Parameters.Where(p => ObjectMatches(p.ObjectName, context.TargetObject) && !context.UsedParameters.Contains(p.Name) && !string.IsNullOrEmpty(p.Name)).ToList();
            result.AddRange(parameters.Select(p => new CompletionItem { DisplayText = p.Name, InsertText = p.Name + " = ", Kind = CompletionItemKind.Parameter, Description = p.TypeDisplay + (p.HasDefaultValue ? " optional" : string.Empty) + (p.IsOutput ? " OUTPUT" : string.Empty), Score = 100 }));
            if (parameters.Count > 1 && context.UsedParameters.Count == 0) result.Add(new CompletionItem { DisplayText = "(all parameters)", InsertText = string.Join(", ", parameters.Select((p, i) => p.Name + " = ${" + (i + 1) + ":" + (p.HasDefaultValue ? "DEFAULT" : "NULL") + "}" + (p.IsOutput ? " OUTPUT" : string.Empty))), Kind = CompletionItemKind.Snippet, Description = parameters.Count + " parameters", Score = 130 });
        }

        private static void AddValues(List<CompletionItem> result, CompletionContext context, MetadataSnapshot metadata)
        {
            var matching = metadata.Parameters.Where(x => ObjectMatches(x.ObjectName, context.TargetObject)).OrderBy(x => x.Ordinal).ToList();
            var p = !string.IsNullOrWhiteSpace(context.ActiveParameter)
                ? matching.FirstOrDefault(x => string.Equals(x.Name, context.ActiveParameter, StringComparison.OrdinalIgnoreCase))
                : matching.ElementAtOrDefault(context.ArgumentIndex);
            result.Add(new CompletionItem { DisplayText = "NULL", InsertText = "NULL", Kind = CompletionItemKind.Keyword, Description = p?.DataType, Score = 70 });
            if (p?.HasDefaultValue == true)
                result.Add(new CompletionItem { DisplayText = "DEFAULT", InsertText = "DEFAULT", Kind = CompletionItemKind.Keyword, Description = p.TypeDisplay + " optional parameter", Score = 60 });
            foreach (var variable in GetDeclaredVariables(context.CurrentBatch)) result.Add(new CompletionItem { DisplayText = variable.Name, InsertText = variable.Name, Kind = CompletionItemKind.Column, Description = variable.Type + " variable", Score = 80 });
        }

        private static void AddDeclaredVariables(List<CompletionItem> result, CompletionContext context)
        {
            foreach (var declaration in GetDeclaredVariables(context.CurrentBatch))
                result.Add(new CompletionItem
                {
                    DisplayText = declaration.Name,
                    InsertText = declaration.Name,
                    Kind = CompletionItemKind.Parameter,
                    Description = declaration.Type + " local variable",
                    Score = 88
                });
        }

        private sealed class DeclaredVariable { public string Name; public string Type; }
        private static IEnumerable<DeclaredVariable> GetDeclaredVariables(string currentBatch)
        {
            string batch = SqlTextContext.MaskCommentsAndStrings(currentBatch ?? string.Empty);
            foreach (Match statement in Regex.Matches(batch, @"\bDECLARE\s+(?<body>[^;]+)", RegexOptions.IgnoreCase))
                foreach (string part in SplitTopLevel(statement.Groups["body"].Value))
                {
                    Match declaration = Regex.Match(part.Trim(), @"^(?<name>@\w+)\s+(?<type>[\w]+(?:\s*\([^)]*\))?)", RegexOptions.IgnoreCase);
                    if (declaration.Success) yield return new DeclaredVariable { Name = declaration.Groups["name"].Value, Type = declaration.Groups["type"].Value };
                }
        }

        private static void AddMembers(List<CompletionItem> result, CompletionContext context, List<DatabaseObjectMetadata> objects, MetadataSnapshot metadata)
        {
            string resolved = ResolveAlias(context.Qualifier, context);
            if (context.HasOmittedSchemaQualifier)
            {
                string[] omittedParts = CleanQualified(resolved).Split('.');
                IEnumerable<DatabaseObjectMetadata> omittedObjects = omittedParts.Length == 2
                    ? objects.Where(o => string.Equals(o.Server, omittedParts[0], StringComparison.OrdinalIgnoreCase)
                        && string.Equals(o.Database, omittedParts[1], StringComparison.OrdinalIgnoreCase))
                    : objects.Where(o => string.Equals(o.Database, omittedParts[0], StringComparison.OrdinalIgnoreCase));
                result.AddRange(omittedObjects.Where(o => !context.IsDataSourceMember || IsDataSource(o)).Select(o => ObjectItem(o, 80, true)));
                return;
            }
            var obj = objects.FirstOrDefault(o => ObjectMatches(o.QualifiedName, resolved) || ObjectMatches(o.Name, resolved));
            if (obj != null) result.AddRange(obj.Columns.Select(c => ColumnItem(c, 90, obj)));
            else
            {
                string[] parts = CleanQualified(resolved).Split('.');
                if (parts.Length == 1 && metadata.LinkedServerDatabases.TryGetValue(parts[0], out List<string> linkedDatabases))
                    result.AddRange(linkedDatabases.Select(d => ContainerItem(d, CompletionItemKind.Database, 100)));
                else if (parts.Length == 2 && metadata.LinkedServers.Any(s => string.Equals(s, parts[0], StringComparison.OrdinalIgnoreCase)))
                    result.AddRange(objects.Where(o => string.Equals(o.Server, parts[0], StringComparison.OrdinalIgnoreCase) && string.Equals(o.Database, parts[1], StringComparison.OrdinalIgnoreCase))
                        .Select(o => o.Schema).Distinct(StringComparer.OrdinalIgnoreCase).Select(s => ContainerItem(s, CompletionItemKind.Schema, 95)));
                else if (parts.Length == 1 && metadata.Databases.Any(d => string.Equals(d, parts[0], StringComparison.OrdinalIgnoreCase)))
                    result.AddRange(objects.Where(o => string.Equals(o.Database, parts[0], StringComparison.OrdinalIgnoreCase)).Select(o => o.Schema)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Select(s => ContainerItem(s, CompletionItemKind.Schema, 90)));
                else result.AddRange(objects.Where(o => EndsWith(ContainerName(o), resolved) && (!context.IsDataSourceMember || IsDataSource(o))).Select(o => ObjectItem(o, 70, true)));
            }
        }

        private static void AddWritableColumns(List<CompletionItem> result, CompletionContext context, List<DatabaseObjectMetadata> objects)
        {
            var target = objects.FirstOrDefault(o => ObjectMatches(o.QualifiedName, context.TargetObject) || ObjectMatches(o.Name, context.TargetObject));
            if (target == null) return;
            var writable = target.Columns.Where(c => !c.IsIdentity && !c.IsComputed && !string.Equals(c.DataType, "timestamp", StringComparison.OrdinalIgnoreCase) && !string.Equals(c.DataType, "rowversion", StringComparison.OrdinalIgnoreCase)).ToList();
            result.AddRange(writable.Select(c => ColumnItem(c, 90, target)));
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
            AddScopedColumns(result, context, objects, 60);
            result.AddRange(objects.Where(o => o.Kind == CompletionItemKind.Function).Select(o => ObjectItem(o, o.IsSystem ? 20 : 45, false)));
            result.AddRange(BuiltInFunctions.Select(name => new CompletionItem { DisplayText = name, InsertText = name, Kind = CompletionItemKind.Function, Description = BuiltInFunctionDescription(name), Score = 35 }));
            result.AddRange(Keywords.Select(k => new CompletionItem { DisplayText = k, InsertText = k, Kind = CompletionItemKind.Keyword, Description = "keyword", Score = 20 }));
            foreach (var s in SnippetService.GetAllSnippets()) result.Add(new CompletionItem { DisplayText = s.Prefix, InsertText = s.Body, Kind = CompletionItemKind.Snippet, Description = s.Description, Score = 30 });
        }

        private static string BuiltInFunctionDescription(string name)
        {
            switch ((name ?? string.Empty).ToUpperInvariant())
            {
                case "ISNULL": return "ISNULL(check_expression, replacement_value)";
                case "COALESCE": return "COALESCE(expression, ...)";
                case "DATEADD": return "DATEADD(datepart, number, date)";
                case "DATEDIFF": return "DATEDIFF(datepart, startdate, enddate)";
                case "SUBSTRING": return "SUBSTRING(expression, start, length)";
                case "REPLACE": return "REPLACE(expression, pattern, replacement)";
                case "CAST": return "CAST(expression AS data_type)";
                case "CONVERT": return "CONVERT(data_type, expression [, style])";
                default: return "SQL Server built-in function";
            }
        }

        private static IEnumerable<CompletionItem> BuildJoinItems(CompletionContext context, MetadataSnapshot metadata)
        {
            var sources = context.Aliases.Where(p => !string.Equals(p.Key, LastPart(p.Value), StringComparison.OrdinalIgnoreCase)).ToList();
            if (sources.Count == 0) sources = context.Aliases.ToList();
            foreach (var target in metadata.Objects.Where(o => o.Kind == CompletionItemKind.Table || o.Kind == CompletionItemKind.View))
            {
                if (context.IsJoinSource && !MatchesContainer(target, context.Qualifier)) continue;
                if (sources.Any(s => ObjectMatches(s.Value, target.QualifiedName))) continue;
                string alias = MakeAlias(target, context.Aliases.Keys);
                var relationships = metadata.ForeignKeys.Where(f => (ObjectMatches(f.ParentObject, target.QualifiedName) || ObjectMatches(f.ReferencedObject, target.QualifiedName)) && sources.Any(s => ObjectMatches(f.ParentObject, s.Value) || ObjectMatches(f.ReferencedObject, s.Value))).ToList();
                string targetQualifier = SettingsManager.GetSqlCompletionSettings().autoAddAliases ? Quote(alias) : Quote(target.Name);
                string targetInsert = QualifiedInsert(target) + (SettingsManager.GetSqlCompletionSettings().autoAddAliases ? " AS " + Quote(alias) : string.Empty);
                if (relationships.Count == 0)
                {
                    foreach (var source in sources)
                    {
                        DatabaseObjectMetadata sourceObject = metadata.Objects.FirstOrDefault(o => ObjectMatches(o.QualifiedName, source.Value));
                        List<JoinColumnMatch> inferred = InferJoinColumns(sourceObject, target);
                        if (inferred.Count == 0) continue;
                        string conditions = string.Join(" AND ", inferred.Select(p => Quote(source.Key) + "." + Quote(p.SourceColumn)
                            + " = " + targetQualifier + "." + Quote(p.TargetColumn)));
                        yield return new CompletionItem
                        {
                            DisplayText = DisplayQualifiedName(target), InsertText = targetInsert + " ON " + conditions,
                            Kind = CompletionItemKind.Join,
                            Description = inferred.Any(p => p.IsCustom) ? "custom-rule join" : "same-name column join",
                            DetailText = ObjectDetail(target), Score = inferred.Any(p => p.IsCustom) ? 100 : 70
                        };
                    }
                    yield return new CompletionItem { DisplayText = DisplayQualifiedName(target), InsertText = targetInsert, Kind = CompletionItemKind.Join, Description = "table (no known relationship)", DetailText = ObjectDetail(target), Score = 10 };
                    continue;
                }
                foreach (var fk in relationships)
                {
                    var source = sources.First(s => ObjectMatches(fk.ParentObject, s.Value) || ObjectMatches(fk.ReferencedObject, s.Value));
                    bool sourceParent = ObjectMatches(fk.ParentObject, source.Value);
                    var conditions = fk.ParentColumns.Select((c, i) => Quote(source.Key) + "." + Quote(sourceParent ? c : fk.ReferencedColumns[i]) + " = " + targetQualifier + "." + Quote(sourceParent ? fk.ReferencedColumns[i] : c));
                    yield return new CompletionItem { DisplayText = DisplayQualifiedName(target), InsertText = targetInsert + " ON " + string.Join(" AND ", conditions), Kind = CompletionItemKind.Join, Description = "foreign-key join", DetailText = ObjectDetail(target), Score = 110 };
                }
            }
        }

        internal sealed class JoinColumnMatch { public string SourceColumn; public string TargetColumn; public bool IsCustom; }

        internal static List<JoinColumnMatch> InferJoinColumns(DatabaseObjectMetadata source, DatabaseObjectMetadata target, SettingsManager.SqlCompletionSettings settings = null)
        {
            var result = new List<JoinColumnMatch>();
            if (source == null || target == null) return result;
            settings = settings ?? SettingsManager.GetSqlCompletionSettings();
            foreach (string rule in (settings.joinColumnRules ?? string.Empty).Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] sides = rule.Split('=');
                if (sides.Length != 2) continue;
                string leftObject, leftColumn, rightObject, rightColumn;
                SplitJoinRuleSide(sides[0], out leftObject, out leftColumn);
                SplitJoinRuleSide(sides[1], out rightObject, out rightColumn);
                bool forward = RuleObjectMatches(leftObject, source) && RuleObjectMatches(rightObject, target);
                bool reverse = RuleObjectMatches(leftObject, target) && RuleObjectMatches(rightObject, source);
                string sourceColumn = forward ? leftColumn : reverse ? rightColumn : null;
                string targetColumn = forward ? rightColumn : reverse ? leftColumn : null;
                if (sourceColumn != null && source.Columns.Any(c => c.Name.Equals(sourceColumn, StringComparison.OrdinalIgnoreCase))
                    && target.Columns.Any(c => c.Name.Equals(targetColumn, StringComparison.OrdinalIgnoreCase)))
                    result.Add(new JoinColumnMatch { SourceColumn = sourceColumn, TargetColumn = targetColumn, IsCustom = true });
            }
            if (result.Count > 0) return result;
            foreach (ColumnMetadata column in source.Columns)
            {
                ColumnMetadata match = target.Columns.FirstOrDefault(c => c.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase));
                if (match == null) continue;
                bool keyLike = column.Name.Equals("Id", StringComparison.OrdinalIgnoreCase)
                    || column.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
                    || column.Name.EndsWith("_id", StringComparison.OrdinalIgnoreCase);
                if (keyLike) result.Add(new JoinColumnMatch { SourceColumn = column.Name, TargetColumn = match.Name });
                if (result.Count == 3) break;
            }
            return result;
        }

        private static void SplitJoinRuleSide(string value, out string objectName, out string column)
        {
            string cleaned = CleanQualified(value);
            int dot = cleaned.LastIndexOf('.');
            objectName = dot < 0 ? string.Empty : cleaned.Substring(0, dot);
            column = dot < 0 ? cleaned : cleaned.Substring(dot + 1);
        }

        private static bool RuleObjectMatches(string ruleObject, DatabaseObjectMetadata metadata)
            => string.IsNullOrWhiteSpace(ruleObject) || ObjectMatches(ruleObject, metadata.QualifiedName) || ObjectMatches(ruleObject, metadata.Name);

        private static CompletionItem ColumnItem(ColumnMetadata c, int score, DatabaseObjectMetadata owner = null) => new CompletionItem
        {
            DisplayText = c.Name, InsertText = Quote(c.Name), Kind = CompletionItemKind.Column,
            Description = c.TypeDisplay + (c.IsNullable ? " null" : " not null"),
            DetailText = (owner == null ? string.Empty : owner.QualifiedName + Environment.NewLine) + ColumnDetail(c), Score = score
        };
        private static CompletionItem ContainerItem(string name, CompletionItemKind kind, int score) => new CompletionItem { DisplayText = name, InsertText = Quote(name), Kind = kind, Description = kind.ToString(), Score = score };
        private static CompletionItem ObjectItem(DatabaseObjectMetadata o, int score, bool omitSchema) => new CompletionItem { DisplayText = omitSchema ? o.Name : DisplayQualifiedName(o), InsertText = omitSchema ? Quote(o.Name) : QualifiedInsert(o), Kind = o.Kind, Description = o.Kind + (string.IsNullOrWhiteSpace(o.Database) ? "" : " in " + o.Database), DetailText = ObjectDetail(o), Score = score };
        private static CompletionItem DataSourceItem(DatabaseObjectMetadata o, CompletionContext context, int score)
        {
            CompletionItem item = ObjectItem(o, score, false);
            if (SettingsManager.GetSqlCompletionSettings().autoAddAliases && (o.Kind == CompletionItemKind.Table || o.Kind == CompletionItemKind.View || o.Kind == CompletionItemKind.Synonym))
                item.InsertText += " AS " + Quote(MakeAlias(o, context.Aliases.Keys));
            return item;
        }

        private static string ColumnDetail(ColumnMetadata column)
        {
            string flags = (column.IsPrimaryKey ? "PK " : string.Empty)
                + (column.IsForeignKey ? "FK " : string.Empty)
                + (column.IsUnique && !column.IsPrimaryKey ? "UQ " : string.Empty);
            string detail = (string.IsNullOrWhiteSpace(flags) ? "   " : flags.PadRight(3))
                + column.Name + "  " + column.TypeDisplay + (column.IsNullable ? " NULL" : " NOT NULL")
                + (column.IsIdentity ? "  IDENTITY" : string.Empty) + (column.IsComputed ? "  COMPUTED" : string.Empty)
                + (string.IsNullOrWhiteSpace(column.DefaultDefinition) ? string.Empty : "  DEFAULT " + column.DefaultDefinition);
            if (!string.IsNullOrWhiteSpace(column.Description)) detail += "  -- " + column.Description;
            return detail;
        }

        private static string ObjectDetail(DatabaseObjectMetadata item)
        {
            if (item == null) return string.Empty;
            var lines = new List<string> { item.Kind + "  " + item.QualifiedName };
            if (!string.IsNullOrWhiteSpace(item.Description)) lines.Add(item.Description);
            if (item.EstimatedRowCount.HasValue) lines.Add("Estimated rows: " + item.EstimatedRowCount.Value.ToString("N0"));
            if (item.Indexes.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Indexes:");
                foreach (IndexMetadata index in item.Indexes.Take(30))
                {
                    string flags = index.IsPrimaryKey ? "PK" : index.IsUnique ? "UNIQUE" : index.TypeDescription;
                    string columns = string.Join(", ", index.KeyColumns);
                    string included = index.IncludedColumns.Count == 0 ? string.Empty : " INCLUDE (" + string.Join(", ", index.IncludedColumns) + ")";
                    lines.Add("  " + flags + " " + index.Name + " (" + columns + ")" + included);
                }
            }
            if (item.CheckConstraints.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Check constraints:");
                lines.AddRange(item.CheckConstraints.Take(30).Select(c => "  " + c.Name + " " + c.Definition));
            }
            if (item.Columns.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Columns:");
                lines.AddRange(item.Columns.OrderBy(c => c.Ordinal).Take(200).Select(ColumnDetail));
                if (item.Columns.Count > 200) lines.Add("... " + (item.Columns.Count - 200) + " more columns");
            }
            if (!string.IsNullOrWhiteSpace(item.Definition))
            {
                lines.Add(string.Empty);
                lines.Add(item.Definition.Length > 6000 ? item.Definition.Substring(0, 6000) + Environment.NewLine + "..." : item.Definition);
            }
            return string.Join(Environment.NewLine, lines);
        }
        private static string DisplayQualifiedName(DatabaseObjectMetadata o) => (o.IsExternal && !string.IsNullOrWhiteSpace(o.Server) ? o.Server + "." : "") + (o.IsExternal && !string.IsNullOrWhiteSpace(o.Database) ? o.Database + "." : "") + (string.IsNullOrEmpty(o.Schema) ? o.Name : o.Schema + "." + o.Name);
        private static string QualifiedInsert(DatabaseObjectMetadata o) => (o.IsExternal && !string.IsNullOrWhiteSpace(o.Server) ? Quote(o.Server) + "." : "") + (o.IsExternal && !string.IsNullOrWhiteSpace(o.Database) ? Quote(o.Database) + "." : "") + (string.IsNullOrEmpty(o.Schema) ? Quote(o.Name) : Quote(o.Schema) + "." + Quote(o.Name));
        private static bool IsDataSource(DatabaseObjectMetadata o) => o.Kind == CompletionItemKind.Table || o.Kind == CompletionItemKind.View || (o.Kind == CompletionItemKind.Function && o.IsTableValuedFunction) || o.Kind == CompletionItemKind.Synonym;
        private static bool MatchesContainer(DatabaseObjectMetadata item, string qualifier)
        {
            if (string.IsNullOrWhiteSpace(qualifier)) return true;
            return EndsWith(ContainerName(item), CleanQualified(qualifier));
        }

        private static string ContainerName(DatabaseObjectMetadata item) =>
            (string.IsNullOrWhiteSpace(item.Server) ? "" : item.Server + ".") +
            (string.IsNullOrWhiteSpace(item.Database) ? "" : item.Database + ".") +
            item.Schema;

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

        private static void AddLocalObjects(string sessionText, string currentBatch, CompletionContext context)
        {
            string batch = SqlTextContext.MaskCommentsAndStrings(currentBatch ?? string.Empty);
            foreach (Match m in Regex.Matches(batch, @"\bDECLARE\s+(?<name>@\w+)\s+TABLE\s*\((?<cols>[^;]*)\)", RegexOptions.IgnoreCase | RegexOptions.Singleline)) AddLocal(context, m.Groups["name"].Value, ParseDefinitions(m.Groups["cols"].Value));
            string sql = SqlTextContext.MaskCommentsAndStrings(sessionText ?? string.Empty);
            var events = new List<LocalObjectEvent>();
            foreach (Match m in Regex.Matches(sql, @"\bCREATE\s+TABLE\s+(?<name>\#\#?\w+)\s*\((?<cols>[^;]*)\)", RegexOptions.IgnoreCase | RegexOptions.Singleline)) events.Add(new LocalObjectEvent { Index = m.Index, Name = m.Groups["name"].Value, Columns = ParseDefinitions(m.Groups["cols"].Value).ToList() });
            foreach (Match m in Regex.Matches(sql, @"\bSELECT\s+(?<select>.*?)\s+INTO\s+(?<name>\#\#?\w+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)) events.Add(new LocalObjectEvent { Index = m.Index, Name = m.Groups["name"].Value, Columns = ParseSelect(m.Groups["select"].Value).ToList() });
            foreach (Match m in Regex.Matches(sql, @"\bALTER\s+TABLE\s+(?<name>\#\#?\w+)\s+ADD\s+(?<cols>[^;]+)", RegexOptions.IgnoreCase)) events.Add(new LocalObjectEvent { Index = m.Index, Name = m.Groups["name"].Value, Columns = ParseDefinitions(m.Groups["cols"].Value).ToList(), IsAlter = true });
            foreach (Match m in Regex.Matches(sql, @"\bDROP\s+TABLE(?:\s+IF\s+EXISTS)?\s+(?<name>\#\#?\w+)", RegexOptions.IgnoreCase)) events.Add(new LocalObjectEvent { Index = m.Index, Name = m.Groups["name"].Value, IsDrop = true });
            foreach (LocalObjectEvent item in events.OrderBy(e => e.Index))
            {
                string name = Clean(item.Name);
                if (item.IsDrop) { context.LocalObjects.RemoveAll(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)); context.Aliases.Remove(name); }
                else if (item.IsAlter)
                {
                    DatabaseObjectMetadata existing = context.LocalObjects.LastOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null) foreach (ColumnMetadata column in item.Columns) if (!existing.Columns.Any(c => string.Equals(c.Name, column.Name, StringComparison.OrdinalIgnoreCase))) existing.Columns.Add(column);
                }
                else AddLocal(context, name, item.Columns);
            }
        }

        private sealed class LocalObjectEvent { public int Index; public string Name; public bool IsDrop; public bool IsAlter; public List<ColumnMetadata> Columns; }

        private static IEnumerable<ColumnMetadata> ParseDefinitions(string value) => SplitTopLevel(value).Select(p => Regex.Match(p.Trim(), @"^(?<name>" + Id + @")\s+(?<type>[\w]+)", RegexOptions.IgnoreCase)).Where(m => m.Success).Select(m => new ColumnMetadata { Name = Clean(m.Groups["name"].Value), DataType = m.Groups["type"].Value });
        private static IEnumerable<ColumnMetadata> ParseSelect(string value) => SplitTopLevel(value).Select(p => { Match a = Regex.Match(p.Trim(), @"(?:\bAS\s+|\s+)(?<name>" + Id + @")$", RegexOptions.IgnoreCase); string n = a.Success ? Clean(a.Groups["name"].Value) : Clean(p.Trim().Split('.').Last()); return new ColumnMetadata { Name = n, DataType = string.Empty }; }).Where(c => Regex.IsMatch(c.Name, @"^[#@\w]+$"));
        private static List<string> SplitTopLevel(string value) { var result = new List<string>(); int depth = 0, start = 0; for (int i = 0; i < value.Length; i++) { if (value[i] == '(') depth++; else if (value[i] == ')') depth = Math.Max(0, depth - 1); else if (value[i] == ',' && depth == 0) { result.Add(value.Substring(start, i - start)); start = i + 1; } } result.Add(value.Substring(start)); return result; }
        private static void AddLocal(CompletionContext c, string name, IEnumerable<ColumnMetadata> columns) { var o = new DatabaseObjectMetadata { Schema = string.Empty, Name = Clean(name), Kind = CompletionItemKind.Table }; o.Columns.AddRange(columns); c.LocalObjects.RemoveAll(x => string.Equals(x.Name, o.Name, StringComparison.OrdinalIgnoreCase)); c.LocalObjects.Add(o); c.Aliases[o.Name] = o.Name; }
        private static string ResolveAlias(string value, CompletionContext c) => c.Aliases.TryGetValue(value ?? string.Empty, out string resolved) ? resolved : value;
        private static bool ObjectMatches(string a, string b)
        {
            string left = CleanQualified(a), right = CleanQualified(b);
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
            string[] leftQualified = left.Split('.'), rightQualified = right.Split('.');
            if ((left.Contains("..") || right.Contains("..")) && leftQualified.Length == rightQualified.Length)
                return leftQualified.Zip(rightQualified, (x, y) => string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y)
                    || string.Equals(x, y, StringComparison.OrdinalIgnoreCase)).All(matches => matches);
            int leftParts = left.Count(c => c == '.') + 1, rightParts = right.Count(c => c == '.') + 1;
            if (leftParts == 1 || rightParts == 1) return string.Equals(LastPart(left), LastPart(right), StringComparison.OrdinalIgnoreCase);
            return leftParts > rightParts
                ? left.EndsWith("." + right, StringComparison.OrdinalIgnoreCase)
                : right.EndsWith("." + left, StringComparison.OrdinalIgnoreCase);
        }
        private static bool EndsWith(string a, string b) => string.Equals(CleanQualified(a), CleanQualified(b), StringComparison.OrdinalIgnoreCase) || CleanQualified(a).EndsWith("." + CleanQualified(b), StringComparison.OrdinalIgnoreCase);
        private static string LastPart(string value) => CleanQualified(value).Split('.').LastOrDefault() ?? string.Empty;
        private static string CleanQualified(string value) => DatabaseIdentifier.NormalizeSqlServer(value);
        private static string Clean(string value) => DatabaseIdentifier.UnquoteSqlServerPart(value);
        private static string Quote(string value) => SettingsManager.GetSqlCompletionSettings().useSquareBrackets ? "[" + (value ?? string.Empty).Replace("]", "]]" ) + "]" : value ?? string.Empty;
        internal static string MakeAlias(DatabaseObjectMetadata item, IEnumerable<string> existing, SettingsManager.SqlCompletionSettings settings = null)
        {
            string qualified = CleanQualified(item?.QualifiedName), name = item?.Name ?? "t";
            settings = settings ?? SettingsManager.GetSqlCompletionSettings();
            foreach (string mapping in (settings.customAliases ?? string.Empty).Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = mapping.Split('=');
                if (pair.Length == 2 && (ObjectMatches(pair[0], qualified) || ObjectMatches(pair[0], name)))
                    return UniqueAlias(Clean(pair[1]), existing);
            }
            foreach (string prefix in (settings.aliasPrefixToIgnore ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (name.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase)) { name = name.Substring(prefix.Trim().Length); break; }
            string seed = new string((name ?? "t").Where(char.IsUpper).ToArray()).ToLowerInvariant();
            if (string.IsNullOrEmpty(seed)) seed = (name ?? "t").Substring(0, 1).ToLowerInvariant();
            return UniqueAlias(seed, existing);
        }

        private static string UniqueAlias(string seed, IEnumerable<string> existing)
        {
            if (string.IsNullOrWhiteSpace(seed)) seed = "t";
            var used = new HashSet<string>(existing ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            string result = seed; int n = 2;
            while (used.Contains(result)) result = seed + n++;
            return result;
        }
        private static readonly HashSet<string> ClauseKeywords = new HashSet<string>(new[] { "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "ON", "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT", "OPTION", "OFFSET", "FETCH", "FOR" }, StringComparer.OrdinalIgnoreCase);
        private static readonly string[] DetachedStatementStarters = {
            "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "DECLARE", "SET", "CREATE", "ALTER", "DROP", "TRUNCATE", "USE", "PRINT", "EXEC", "BEGIN", "COMMIT", "ROLLBACK", "GRANT", "DENY", "REVOKE"
        };
        private static readonly string[] Keywords = {
            "SELECT", "FROM", "WHERE", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS APPLY", "OUTER APPLY", "ON", "AS",
            "GROUP BY", "ORDER BY", "HAVING", "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "MERGE", "AND", "OR", "NOT", "NULL",
            "IS NULL", "IS NOT NULL", "CASE", "WHEN", "THEN", "ELSE", "END", "DISTINCT", "TOP", "UNION", "UNION ALL", "EXISTS", "IN", "LIKE",
            "BETWEEN", "DECLARE", "EXEC", "CREATE", "ALTER", "DROP", "CREATE TABLE", "ALTER TABLE", "DROP TABLE", "CREATE VIEW", "ALTER VIEW",
            "CREATE PROCEDURE", "ALTER PROCEDURE", "CREATE FUNCTION", "ALTER FUNCTION", "TRUNCATE TABLE", "BEGIN", "BEGIN TRANSACTION", "COMMIT",
            "ROLLBACK", "BEGIN TRY", "END TRY", "BEGIN CATCH", "END CATCH", "THROW", "GRANT", "DENY", "REVOKE", "CREATE USER", "CREATE ROLE",
            "CURRENT_TIMESTAMP", "CURRENT_USER", "SESSION_USER", "SYSTEM_USER", "XMLNAMESPACES"
        };
    }
}
