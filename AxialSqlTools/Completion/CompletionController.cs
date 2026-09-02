using Microsoft.VisualStudio;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AxialSqlTools.Completion
{
    internal sealed class CompletionController : IDisposable
    {
        private readonly IVsTextView textView;
        private readonly CompletionPresenter presenter = new CompletionPresenter();
        private readonly SynchronizationContext uiContext;
        private CancellationTokenSource requestCancellation;
        private CompletionContext currentContext;
        private int currentLine;
        private readonly List<TextSpan> snippetStops = new List<TextSpan>();
        private int snippetStopIndex = -1;

        public CompletionController(IVsTextView textView)
        {
            this.textView = textView;
            uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            presenter.CommitRequested += Commit;
            presenter.DismissRequested += Dismiss;
        }

        public bool IsVisible => presenter.IsVisible;

        public void Request(bool explicitRequest)
        {
            var settings = SettingsManager.GetSqlCompletionSettings();
            if (!settings.enabled || (!explicitRequest && !settings.automaticPopup)) { presenter.Hide(); return; }
            requestCancellation?.Cancel();
            requestCancellation?.Dispose();
            requestCancellation = new CancellationTokenSource();
            CancellationToken token = requestCancellation.Token;

            string text;
            int line;
            int column;
            if (!TryReadDocument(out text, out line, out column)) return;
            int offset = GetOffset(text, line, column);
            string beforeCaret = text.Substring(0, Math.Min(offset, text.Length));
            CompletionContext context = SqlCompletionAnalyzer.Analyze(beforeCaret, column);
            if (context.Suppress) { presenter.Hide(); return; }
            if (!explicitRequest && context.Kind == CompletionContextKind.General && string.IsNullOrEmpty(context.Prefix)) { presenter.Hide(); return; }

            currentContext = context;
            currentContext.IsExplicitRequest = explicitRequest;
            currentLine = line;
            ScriptFactoryAccess.ConnectionInfo connection = ScriptFactoryAccess.GetCurrentConnectionInfo();
            currentContext.MetadataScope = connection == null ? string.Empty : (connection.ServerName + "|" + connection.Database);
            _ = LoadAndShowAsync(context, connection, token, explicitRequest);
        }

        private async Task LoadAndShowAsync(CompletionContext context, ScriptFactoryAccess.ConnectionInfo connection, CancellationToken token, bool explicitRequest)
        {
            try
            {
                if (!explicitRequest)
                    await Task.Delay(SettingsManager.GetSqlCompletionSettings().delayMilliseconds, token).ConfigureAwait(false);

                if (context.Kind != CompletionContextKind.Member)
                    PostItems(context, SqlCompletionAnalyzer.BuildItems(context, MetadataSnapshot.Empty), token);

                MetadataSnapshot metadata = await SqlMetadataCache.GetAsync(connection, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                PostItems(context, SqlCompletionAnalyzer.BuildItems(context, metadata), token);
            }
            catch (OperationCanceledException) { }
            catch { PostItems(context, SqlCompletionAnalyzer.BuildItems(context, MetadataSnapshot.Empty), token); }
        }

        private void PostItems(CompletionContext context, List<CompletionItem> items, CancellationToken token)
        {
            uiContext.Post(_ =>
            {
                if (token.IsCancellationRequested || !ReferenceEquals(currentContext, context)) return;
                if (items.Count == 0)
                    presenter.Hide();
                else
                    presenter.Show(textView, items);
            }, null);
        }

        public bool HandleNavigation(uint commandId) => presenter.HandleNavigation(commandId);

        public bool TryCommit()
        {
            if (!presenter.IsVisible || presenter.SelectedItem == null) return false;
            Commit();
            return true;
        }

        public bool TryCommitOnCharacter(char value)
        {
            CompletionItem item = presenter.SelectedItem;
            if (!presenter.IsVisible || item == null) return false;
            bool shouldCommit = value == '.' && (item.Kind == CompletionItemKind.Schema || item.Kind == CompletionItemKind.Table || item.Kind == CompletionItemKind.View)
                || value == '(' && (item.Kind == CompletionItemKind.Function || item.Kind == CompletionItemKind.Procedure)
                || value == ' ' && item.Kind == CompletionItemKind.Procedure
                || value == ',' && item.Kind == CompletionItemKind.Column;
            if (!shouldCommit) return false;
            Commit();
            return true;
        }

        public bool TryAdvanceSnippet(bool backwards)
        {
            if (snippetStops.Count == 0) return false;
            snippetStopIndex += backwards ? -1 : 1;
            if (snippetStopIndex < 0) snippetStopIndex = 0;
            if (snippetStopIndex >= snippetStops.Count) { snippetStops.Clear(); snippetStopIndex = -1; return false; }
            TextSpan span = snippetStops[snippetStopIndex];
            textView.SetSelection(span.iStartLine, span.iStartIndex, span.iEndLine, span.iEndIndex);
            return true;
        }

        public void Dismiss() { requestCancellation?.Cancel(); presenter.Hide(); }

        private void Commit()
        {
            CompletionItem item = presenter.SelectedItem;
            if (item == null || currentContext == null) return;
            CompletionUsageStore.Record(item);
            if (textView.GetBuffer(out IVsTextLines lines) != VSConstants.S_OK) return;
            textView.GetCaretPos(out int line, out int column);
            int start = line == currentLine ? Math.Min(column, currentContext.ReplacementStartColumn) : column;
            string insertText = item.InsertText;
            SnippetVariableResult snippetResult = null;
            if (item.Kind == CompletionItemKind.Snippet)
            {
                snippetResult = SnippetVariableProcessor.ProcessVariables(insertText, SettingsManager.GetSnippetSettings().cursorMarker);
                insertText = snippetResult.ProcessedText;
            }
            IntPtr value = Marshal.StringToHGlobalUni(insertText);
            try
            {
                lines.ReplaceLines(line, start, line, column, value, insertText.Length, new TextSpan[1]);
                snippetStops.Clear();
                snippetStopIndex = -1;
                if (snippetResult != null && snippetResult.TabStops.Count > 0)
                {
                    foreach (SnippetTabStop stop in snippetResult.TabStops)
                        snippetStops.Add(ToTextSpan(line, start, insertText, stop.Offset, stop.Length));
                    TryAdvanceSnippet(false);
                }
                else
                {
                    int caretOffset = snippetResult != null && snippetResult.CursorOffset >= 0 ? snippetResult.CursorOffset : insertText.Length;
                    TextSpan caret = ToTextSpan(line, start, insertText, caretOffset, 0);
                    textView.SetCaretPos(caret.iStartLine, caret.iStartIndex);
                }
            }
            finally { Marshal.FreeHGlobal(value); }
            Dismiss();
        }

        private static TextSpan ToTextSpan(int startLine, int startColumn, string text, int offset, int length)
        {
            int line = startLine;
            int column = startColumn;
            int endOffset = Math.Min(text.Length, offset + length);
            int endLine = line;
            int endColumn = column;
            for (int i = 0; i < endOffset; i++)
            {
                bool newline = text[i] == '\n';
                if (newline) { if (i < offset) { line++; column = 0; } endLine++; endColumn = 0; }
                else { if (i < offset) column++; endColumn++; }
            }
            return new TextSpan { iStartLine = line, iStartIndex = column, iEndLine = endLine, iEndIndex = endColumn };
        }

        private bool TryReadDocument(out string text, out int line, out int column)
        {
            text = null; line = column = 0;
            if (textView.GetBuffer(out IVsTextLines lines) != VSConstants.S_OK) return false;
            textView.GetCaretPos(out line, out column);
            if (lines.GetLastLineIndex(out int lastLine, out int lastColumn) != VSConstants.S_OK) return false;
            return lines.GetLineText(0, 0, lastLine, lastColumn, out text) == VSConstants.S_OK;
        }

        private static int GetOffset(string text, int line, int column)
        {
            int offset = 0;
            for (int i = 0; i < line && offset < text.Length; i++)
            {
                int next = text.IndexOf('\n', offset);
                if (next < 0) return text.Length;
                offset = next + 1;
            }
            return Math.Min(text.Length, offset + column);
        }

        public void Dispose()
        {
            requestCancellation?.Cancel();
            requestCancellation?.Dispose();
            presenter.Dispose();
        }
    }
}
