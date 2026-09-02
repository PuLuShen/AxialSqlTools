using System;
using System.Text;
using System.Text.RegularExpressions;

namespace AxialSqlTools.Completion
{
    internal sealed class SqlTextContext
    {
        public string BatchText { get; private set; }
        public string StatementText { get; private set; }

        public static SqlTextContext Create(string textBeforeCaret)
        {
            string source = textBeforeCaret ?? string.Empty;
            string masked = MaskCommentsAndStrings(source);
            int batchStart = 0;
            foreach (Match match in Regex.Matches(masked, @"(?im)^\s*GO(?:\s+\d+)?\s*(?:\r?\n|$)"))
                batchStart = match.Index + match.Length;

            string batch = source.Substring(Math.Min(batchStart, source.Length));
            string maskedBatch = masked.Substring(Math.Min(batchStart, masked.Length));
            int statementStart = FindStatementStart(maskedBatch);
            return new SqlTextContext
            {
                BatchText = batch,
                StatementText = batch.Substring(Math.Min(statementStart, batch.Length))
            };
        }

        private static int FindStatementStart(string sql)
        {
            int depth = 0;
            int start = 0;
            for (int i = 0; i < sql.Length; i++)
            {
                if (sql[i] == '(') depth++;
                else if (sql[i] == ')') depth = Math.Max(0, depth - 1);
                else if (sql[i] == ';' && depth == 0) start = i + 1;
            }
            return start;
        }

        public static string MaskCommentsAndStrings(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return string.Empty;
            var output = new StringBuilder(sql);
            bool lineComment = false, blockComment = false, quoted = false;
            for (int i = 0; i < sql.Length; i++)
            {
                char c = sql[i];
                char next = i + 1 < sql.Length ? sql[i + 1] : '\0';
                if (lineComment)
                {
                    if (c == '\r' || c == '\n') lineComment = false; else output[i] = ' ';
                }
                else if (blockComment)
                {
                    output[i] = c == '\r' || c == '\n' ? c : ' ';
                    if (c == '*' && next == '/') { output[i + 1] = ' '; blockComment = false; i++; }
                }
                else if (quoted)
                {
                    output[i] = c == '\r' || c == '\n' ? c : ' ';
                    if (c == '\'' && next == '\'') { output[i + 1] = ' '; i++; }
                    else if (c == '\'') quoted = false;
                }
                else if (c == '-' && next == '-') { output[i] = output[i + 1] = ' '; lineComment = true; i++; }
                else if (c == '/' && next == '*') { output[i] = output[i + 1] = ' '; blockComment = true; i++; }
                else if (c == '\'') { output[i] = ' '; quoted = true; }
            }
            return output.ToString();
        }
    }
}
