using Microsoft.VisualStudio;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using System.Linq;
using System.Drawing.Drawing2D;

namespace AxialSqlTools.Completion
{
    internal sealed class CompletionPresenter : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
        [DllImport("user32.dll")] private static extern bool GetCaretPos(out NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;

        private readonly Form window;
        private readonly SplitContainer contentSplit;
        private readonly ListBox list;
        private readonly ComboBox category;
        private readonly TextBox details;
        private readonly RichTextBox parameterInfo;
        private readonly Label footer;
        private readonly Font iconFont;
        private IReadOnlyList<CompletionItem> items;
        private IReadOnlyList<CompletionItem> allItems = new List<CompletionItem>();
        private IVsTextView lastTextView;
        private MetadataSnapshot lastMetadata;
        private CompletionContext lastContext;
        private bool updatingCategory;
        private bool updatingSplitter;
        private int textLineHeight;
        private int iconSize;
        private Color selectionBackColor = SystemColors.Highlight;
        private Color selectionForeColor = SystemColors.HighlightText;

        public CompletionPresenter()
        {
            list = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9F),
                DrawMode = DrawMode.OwnerDrawFixed
            };
            category = new ComboBox
            {
                Dock = DockStyle.Top,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Height = 27,
                Font = new Font("Segoe UI", 9F)
            };
            details = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9F)
            };
            parameterInfo = new RichTextBox
            {
                Dock = DockStyle.Top,
                Height = 58,
                ReadOnly = true,
                DetectUrls = false,
                WordWrap = false,
                BorderStyle = BorderStyle.FixedSingle,
                ScrollBars = RichTextBoxScrollBars.Horizontal,
                Font = new Font("Consolas", 9F),
                Visible = false,
                TabStop = false
            };
            contentSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                Size = new Size(780, 500),
                SplitterWidth = 6,
                Panel1MinSize = 260,
                Panel2MinSize = 220
            };
            list.AccessibleName = LocalizationManager.T("SQL completion suggestions");
            iconFont = new Font("Segoe UI", 7.5F, FontStyle.Bold);
            footer = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 28,
                Padding = new Padding(6, 4, 6, 0),
                Text = LocalizationManager.T("Up/Down select | Enter/Tab insert | Esc close"),
                AutoEllipsis = true
            };
            window = new CompletionForm
            {
                Text = LocalizationManager.T("SQL completion"),
                FormBorderStyle = FormBorderStyle.SizableToolWindow,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                TopMost = false,
                AutoScaleMode = AutoScaleMode.Dpi,
                MinimumSize = new Size(640, 220),
                Size = SettingsManager.GetCompletionWindowSize()
            };
            window.AccessibleName = LocalizationManager.T("SQL completion");
            UpdateScaledMetrics();
            contentSplit.Panel1.Controls.Add(list);
            contentSplit.Panel2.Controls.Add(details);
            window.Controls.Add(contentSplit);
            window.Controls.Add(parameterInfo);
            window.Controls.Add(category);
            window.Controls.Add(footer);
            list.DoubleClick += (_, __) => CommitRequested?.Invoke();
            list.DrawItem += DrawCompletionItem;
            list.SelectedIndexChanged += (_, __) => UpdateDetails();
            category.SelectedIndexChanged += (_, __) =>
            {
                if (!updatingCategory && lastTextView != null)
                    Show(lastTextView, allItems, lastMetadata, lastContext);
            };
            window.ResizeEnd += (_, __) => SettingsManager.SaveCompletionWindowSize(window.Size);
            contentSplit.SplitterMoved += (_, __) =>
            {
                if (!updatingSplitter && !contentSplit.Panel2Collapsed)
                    SettingsManager.SaveCompletionDetailsWidth(contentSplit.Panel2.Width);
            };
            window.DpiChanged += (_, __) => UpdateScaledMetrics();
            window.FormClosing += Window_FormClosing;
        }

        public event Action CommitRequested;
        public event Action DismissRequested;
        public bool IsVisible => window.Visible;
        public CompletionItem SelectedItem => list.SelectedIndex >= 0 && items != null && list.SelectedIndex < items.Count ? items[list.SelectedIndex] : null;
        public List<CompletionItem> SelectedItems => list.SelectedItems.Cast<CompletionItem>().ToList();

        public void Show(IVsTextView textView, IReadOnlyList<CompletionItem> newItems, MetadataSnapshot metadata, CompletionContext context)
        {
            ApplySsmsTheme();
            lastTextView = textView;
            lastMetadata = metadata;
            lastContext = context;
            allItems = newItems?.ToList() ?? new List<CompletionItem>();
            UpdateCategories();
            newItems = FilterCategory(allItems);
            var completionSettings = SettingsManager.GetSqlCompletionSettings();
            list.SelectionMode = completionSettings.enableColumnPicker && string.Equals(category.SelectedItem as string, "Columns", StringComparison.Ordinal)
                ? SelectionMode.MultiExtended : SelectionMode.One;
            contentSplit.Panel2Collapsed = !completionSettings.showObjectDetails;
            UpdateParameterInfo(metadata, context);
            string selectedKey = ItemKey(SelectedItem);
            list.BeginUpdate();
            try
            {
                // Update the existing native list in place. Clearing it first
                // briefly paints an empty popup and looks like a close/reopen.
                int sharedCount = Math.Min(list.Items.Count, newItems.Count);
                for (int i = 0; i < sharedCount; i++)
                {
                    if (!string.Equals(ItemKey(list.Items[i] as CompletionItem), ItemKey(newItems[i]), StringComparison.Ordinal)
                        || !string.Equals((list.Items[i] as CompletionItem)?.Description, newItems[i].Description, StringComparison.Ordinal))
                        list.Items[i] = newItems[i];
                }
                while (list.Items.Count > newItems.Count)
                    list.Items.RemoveAt(list.Items.Count - 1);
                for (int i = list.Items.Count; i < newItems.Count; i++)
                    list.Items.Add(newItems[i]);
                items = newItems;
            }
            finally { list.EndUpdate(); }
            if (list.Items.Count == 0 && !parameterInfo.Visible) { Hide(); return; }
            int restoredIndex = -1;
            if (!string.IsNullOrEmpty(selectedKey))
            {
                for (int i = 0; i < newItems.Count; i++)
                {
                    if (string.Equals(ItemKey(newItems[i]), selectedKey, StringComparison.Ordinal))
                    {
                        restoredIndex = i;
                        break;
                    }
                }
            }
            list.SelectedIndex = list.Items.Count == 0 ? -1 : restoredIndex >= 0 ? restoredIndex : 0;
            UpdateDetails();
            int maximumItems = SettingsManager.GetSqlCompletionSettings().maximumItems;
            footer.Text = newItems.Count >= maximumItems
                ? LocalizationManager.Format("Showing first {0} matches - keep typing to narrow results", maximumItems)
                : LocalizationManager.Format("{0} matches | Up/Down select | Enter/Tab insert | Esc close", newItems.Count);
            if (newItems.Count > 0 && !string.IsNullOrWhiteSpace(newItems[0].ScopeKey))
                footer.Text += " | " + newItems[0].ScopeKey;
            if (metadata != null && metadata != MetadataSnapshot.Empty)
                footer.Text += " | " + metadata.StatusText;
            SqlEditorDiagnostic diagnostic = context?.Diagnostics?.Find(d => d.Code != "SQL_PARSE");
            if (diagnostic != null) footer.Text += " | Warning: " + diagnostic.Message;

            IntPtr handle = textView.GetWindowHandle();
            IntPtr coordinateWindow = GetFocus();
            if (coordinateWindow == IntPtr.Zero) coordinateWindow = handle;
            NativePoint point;
            if (!GetCaretPos(out point)) point = new NativePoint { X = 20, Y = 20 };
            ClientToScreen(coordinateWindow, ref point);
            Screen screen = Screen.FromPoint(new Point(point.X, point.Y));
            Rectangle area = screen.WorkingArea;
            int maxWidth = Math.Max(window.MinimumSize.Width, (area.Width * 9) / 10);
            int maxHeight = Math.Max(window.MinimumSize.Height, (area.Height * 3) / 4);
            if (window.Width > maxWidth) window.Width = maxWidth;
            if (window.Height > maxHeight) window.Height = maxHeight;
            ApplyDetailsWidth();

            int x = Math.Min(point.X, area.Right - window.Width);
            x = Math.Max(area.Left, x);
            int caretClearance = Math.Max(4, textLineHeight + 4);
            int belowY = point.Y + caretClearance;
            int y = belowY + window.Height <= area.Bottom ? belowY : point.Y - window.Height - 4;
            y = Math.Max(area.Top, Math.Min(y, area.Bottom - window.Height));
            window.Location = new Point(x, y);
            if (!window.Visible)
                window.Show(new NativeWindowOwner(handle));

            // A non-activating owned form can remain Visible while falling behind SSMS
            // after an application switch. Refresh its Z-order for every completion update.
            SetWindowPos(window.Handle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow);
        }

        public bool HandleNavigation(uint commandId)
        {
            if (!IsVisible) return false;
            bool control = (Control.ModifierKeys & Keys.Control) == Keys.Control;
            if (control && (commandId == (uint)VSConstants.VSStd2KCmdID.LEFT || commandId == (uint)VSConstants.VSStd2KCmdID.RIGHT))
            {
                CycleCategory(commandId == (uint)VSConstants.VSStd2KCmdID.RIGHT ? 1 : -1);
                return true;
            }
            if (commandId == (uint)VSConstants.VSStd2KCmdID.UP) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); return true; }
            if (commandId == (uint)VSConstants.VSStd2KCmdID.DOWN) { list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1); return true; }
            if (commandId == (uint)VSConstants.VSStd2KCmdID.PAGEUP) { MoveSelection(-VisibleItemCount); return true; }
            if (commandId == (uint)VSConstants.VSStd2KCmdID.PAGEDN) { MoveSelection(VisibleItemCount); return true; }
            if (commandId == (uint)VSConstants.VSStd2KCmdID.BOL || commandId == (uint)VSConstants.VSStd2KCmdID.HOME) { list.SelectedIndex = 0; return true; }
            if (commandId == (uint)VSConstants.VSStd2KCmdID.EOL || commandId == (uint)VSConstants.VSStd2KCmdID.END) { list.SelectedIndex = list.Items.Count - 1; return true; }
            if (commandId == (uint)VSConstants.VSStd2KCmdID.CANCEL) { Hide(); return true; }
            return false;
        }

        private int VisibleItemCount => Math.Max(1, list.ClientSize.Height / Math.Max(1, list.ItemHeight));

        private void MoveSelection(int offset)
        {
            if (list.Items.Count == 0) return;
            list.SelectedIndex = Math.Max(0, Math.Min(list.Items.Count - 1, list.SelectedIndex + offset));
        }

        private static string ItemKey(CompletionItem item) => item == null ? null : item.Kind + "|" + item.DisplayText;

        private void UpdateCategories()
        {
            string selected = category.SelectedItem as string ?? "All";
            var available = new List<string> { "All" };
            AddCategory(available, "Columns", CompletionItemKind.Column);
            if (allItems.Any(i => i.Kind == CompletionItemKind.Table || i.Kind == CompletionItemKind.View || i.Kind == CompletionItemKind.Synonym)) available.Add("Objects");
            if (allItems.Any(i => i.Kind == CompletionItemKind.Procedure || i.Kind == CompletionItemKind.Function)) available.Add("Routines");
            if (allItems.Any(i => i.Kind == CompletionItemKind.Server || i.Kind == CompletionItemKind.Database || i.Kind == CompletionItemKind.Schema)) available.Add("Containers");
            AddCategory(available, "Joins", CompletionItemKind.Join);
            AddCategory(available, "Snippets", CompletionItemKind.Snippet);
            AddCategory(available, "Keywords", CompletionItemKind.Keyword);
            updatingCategory = true;
            try
            {
                category.Items.Clear();
                category.Items.AddRange(available.Cast<object>().ToArray());
                category.SelectedItem = available.Contains(selected) ? selected : "All";
            }
            finally { updatingCategory = false; }
        }

        private void AddCategory(List<string> available, string name, CompletionItemKind kind)
        {
            if (allItems.Any(i => i.Kind == kind)) available.Add(name);
        }

        private IReadOnlyList<CompletionItem> FilterCategory(IReadOnlyList<CompletionItem> source)
        {
            string selected = category.SelectedItem as string ?? "All";
            if (selected == "All") return source;
            return source.Where(i => selected == "Columns" ? i.Kind == CompletionItemKind.Column
                : selected == "Objects" ? i.Kind == CompletionItemKind.Table || i.Kind == CompletionItemKind.View || i.Kind == CompletionItemKind.Synonym
                : selected == "Routines" ? i.Kind == CompletionItemKind.Procedure || i.Kind == CompletionItemKind.Function
                : selected == "Containers" ? i.Kind == CompletionItemKind.Server || i.Kind == CompletionItemKind.Database || i.Kind == CompletionItemKind.Schema
                : selected == "Joins" ? i.Kind == CompletionItemKind.Join
                : selected == "Snippets" ? i.Kind == CompletionItemKind.Snippet
                : selected == "Keywords" && i.Kind == CompletionItemKind.Keyword).ToList();
        }

        private void CycleCategory(int direction)
        {
            if (category.Items.Count <= 1) return;
            int index = category.SelectedIndex < 0 ? 0 : category.SelectedIndex;
            category.SelectedIndex = (index + direction + category.Items.Count) % category.Items.Count;
        }

        private void UpdateDetails()
        {
            CompletionItem item = SelectedItem;
            details.Text = item == null ? string.Empty : (!string.IsNullOrWhiteSpace(item.DetailText) ? item.DetailText : item.Description ?? string.Empty);
        }

        private void ApplyDetailsWidth()
        {
            if (contentSplit.Panel2Collapsed || contentSplit.ClientSize.Width <= 0) return;
            updatingSplitter = true;
            try
            {
                contentSplit.SplitterDistance = CalculateSplitterDistance(contentSplit.ClientSize.Width,
                    SettingsManager.GetCompletionDetailsWidth(), contentSplit.Panel1MinSize,
                    contentSplit.Panel2MinSize, contentSplit.SplitterWidth);
            }
            finally { updatingSplitter = false; }
        }

        internal static int CalculateSplitterDistance(int totalWidth, int detailsWidth, int minimumLeft, int minimumRight, int splitterWidth)
        {
            int available = Math.Max(0, totalWidth - splitterWidth);
            int right = Math.Max(minimumRight, Math.Min(detailsWidth, Math.Max(minimumRight, available - minimumLeft)));
            return Math.Max(minimumLeft, Math.Min(available - minimumRight, available - right));
        }

        private void Window_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason != CloseReason.UserClosing)
                return;

            e.Cancel = true;
            window.Hide();
            DismissRequested?.Invoke();
        }

        private void UpdateScaledMetrics()
        {
            textLineHeight = Math.Max(list.Font.Height + 2, TextRenderer.MeasureText("Ag", list.Font).Height);
            iconSize = Math.Max(16, textLineHeight - 1);
            list.ItemHeight = textLineHeight + 8;
            footer.Height = textLineHeight + 10;
            parameterInfo.Height = (textLineHeight * 2) + 14;
            footer.Padding = new Padding(6, Math.Max(2, (footer.Height - list.Font.Height) / 2), 6, 0);
            list.Invalidate();
        }

        private void DrawCompletionItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || items == null || e.Index >= items.Count) return;
            CompletionItem item = items[e.Index];
            e.DrawBackground();

            bool selected = (e.State & DrawItemState.Selected) != 0;
            if (selected)
            {
                using (var selectionBrush = new SolidBrush(selectionBackColor))
                {
                    e.Graphics.FillRectangle(selectionBrush, e.Bounds);
                }
            }
            Color primary = selected ? selectionForeColor : list.ForeColor;
            Rectangle bounds = e.Bounds;
            int iconLeft = bounds.Left + 7;
            int iconTop = bounds.Top + Math.Max(2, (bounds.Height - iconSize) / 2);
            DrawKindIcon(e.Graphics, new Rectangle(iconLeft, iconTop, iconSize, iconSize), item.Kind);
            int textLeft = iconLeft + iconSize + 6;
            TextRenderer.DrawText(e.Graphics, item.DisplayText ?? string.Empty, list.Font,
                new Rectangle(textLeft, bounds.Top, Math.Max(0, bounds.Right - textLeft - 7), bounds.Height), primary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }

        private void DrawKindIcon(Graphics graphics, Rectangle bounds, CompletionItemKind kind)
        {
            Color background = KindColor(kind);
            SmoothingMode oldMode = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                using (var brush = new SolidBrush(background))
                    graphics.FillEllipse(brush, bounds);
                TextRenderer.DrawText(graphics, KindGlyph(kind), iconFont, bounds, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            finally { graphics.SmoothingMode = oldMode; }
        }

        private static Color KindColor(CompletionItemKind kind)
        {
            switch (kind)
            {
                case CompletionItemKind.Table: return Color.FromArgb(40, 126, 166);
                case CompletionItemKind.View: return Color.FromArgb(37, 145, 115);
                case CompletionItemKind.Procedure: return Color.FromArgb(112, 85, 170);
                case CompletionItemKind.Function: return Color.FromArgb(190, 108, 35);
                case CompletionItemKind.Column: return Color.FromArgb(60, 130, 65);
                case CompletionItemKind.Database: return Color.FromArgb(38, 110, 190);
                case CompletionItemKind.Server: return Color.FromArgb(76, 99, 120);
                case CompletionItemKind.Schema: return Color.FromArgb(75, 125, 145);
                case CompletionItemKind.Parameter: return Color.FromArgb(165, 82, 118);
                case CompletionItemKind.Snippet: return Color.FromArgb(180, 137, 20);
                case CompletionItemKind.Keyword: return Color.FromArgb(85, 85, 85);
                case CompletionItemKind.Join: return Color.FromArgb(0, 130, 145);
                default: return Color.FromArgb(105, 105, 105);
            }
        }

        private static string KindGlyph(CompletionItemKind kind)
        {
            switch (kind)
            {
                case CompletionItemKind.Table: return "T";
                case CompletionItemKind.View: return "V";
                case CompletionItemKind.Procedure: return "P";
                case CompletionItemKind.Function: return "ƒ";
                case CompletionItemKind.Column: return "C";
                case CompletionItemKind.Database: return "D";
                case CompletionItemKind.Server: return "S";
                case CompletionItemKind.Schema: return "□";
                case CompletionItemKind.Parameter: return "@";
                case CompletionItemKind.Synonym: return "↗";
                case CompletionItemKind.Sequence: return "#";
                case CompletionItemKind.Type: return "{}";
                case CompletionItemKind.Snippet: return "‹›";
                case CompletionItemKind.Join: return "⋈";
                default: return "K";
            }
        }

        private void ApplySsmsTheme()
        {
            System.Windows.Media.Brush background = VsThemeBrushResolver.ResolveBrush(null, EnvironmentColors.ToolWindowBackgroundBrushKey);
            System.Windows.Media.Brush foreground = VsThemeBrushResolver.ResolveBrush(null, EnvironmentColors.ToolWindowTextBrushKey);
            System.Windows.Media.Brush selected = VsThemeBrushResolver.ResolveEnvironmentBrushByName(null, "SystemHighlightBrushKey");
            System.Windows.Media.Brush selectedText = VsThemeBrushResolver.ResolveEnvironmentBrushByName(null, "SystemHighlightTextBrushKey");
            Color back = ToDrawingColor(background, SystemColors.Window);
            Color fore = ToDrawingColor(foreground, SystemColors.WindowText);
            selectionBackColor = ToDrawingColor(selected, SystemColors.Highlight);
            selectionForeColor = ToDrawingColor(selectedText, SystemColors.HighlightText);
            window.BackColor = back;
            window.ForeColor = fore;
            list.BackColor = back;
            list.ForeColor = fore;
            category.BackColor = back;
            category.ForeColor = fore;
            details.BackColor = back;
            details.ForeColor = fore;
            parameterInfo.BackColor = back;
            parameterInfo.ForeColor = fore;
            footer.BackColor = back;
            footer.ForeColor = fore;
        }

        private static Color ToDrawingColor(System.Windows.Media.Brush brush, Color fallback)
        {
            var solid = brush as System.Windows.Media.SolidColorBrush;
            return solid == null ? fallback : Color.FromArgb(solid.Color.A, solid.Color.R, solid.Color.G, solid.Color.B);
        }

        internal static string BuildParameterInfo(MetadataSnapshot metadata, CompletionContext context)
        {
            if (metadata == null || context == null || string.IsNullOrWhiteSpace(context.TargetObject)) return null;
            string target = DatabaseIdentifier.NormalizeSqlServer(context.TargetObject);
            var allParameters = metadata.Parameters
                .Where(p => NameMatches(p.ObjectName, target))
                .OrderBy(p => p.Ordinal)
                .ToList();
            var parameters = allParameters.Select((p, index) =>
                (IsActiveParameter(p, index, context) ? "> " : "  ") + p.Name + " " + p.TypeDisplay
                + (p.HasDefaultValue ? " = default" : string.Empty) + (p.IsOutput ? " OUTPUT" : string.Empty)).ToList();
            if (parameters.Count == 0) return null;
            return target + Environment.NewLine + string.Join("    ", parameters);
        }

        internal static bool HasParameterInfo(MetadataSnapshot metadata, CompletionContext context)
            => !string.IsNullOrWhiteSpace(BuildParameterInfo(metadata, context));

        private void UpdateParameterInfo(MetadataSnapshot metadata, CompletionContext context)
        {
            string value = BuildParameterInfo(metadata, context);
            parameterInfo.Visible = !string.IsNullOrWhiteSpace(value);
            parameterInfo.Text = value ?? string.Empty;
            if (!parameterInfo.Visible) return;
            int marker = parameterInfo.Text.IndexOf("> ", StringComparison.Ordinal);
            if (marker < 0) return;
            int end = parameterInfo.Text.IndexOf("    ", marker, StringComparison.Ordinal);
            if (end < 0) end = parameterInfo.Text.Length;
            parameterInfo.Select(marker, end - marker);
            parameterInfo.SelectionBackColor = selectionBackColor;
            parameterInfo.SelectionColor = selectionForeColor;
            parameterInfo.Select(0, 0);
        }

        private static bool IsActiveParameter(RoutineParameterMetadata parameter, int index, CompletionContext context)
            => !string.IsNullOrWhiteSpace(context.ActiveParameter)
                ? string.Equals(parameter.Name, context.ActiveParameter, StringComparison.OrdinalIgnoreCase)
                : index == context.ArgumentIndex;

        private static bool NameMatches(string left, string right)
        {
            left = DatabaseIdentifier.NormalizeSqlServer(left);
            right = DatabaseIdentifier.NormalizeSqlServer(right);
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
                || left.EndsWith("." + right, StringComparison.OrdinalIgnoreCase)
                || right.EndsWith("." + left, StringComparison.OrdinalIgnoreCase);
        }

        public void Hide() { if (window.Visible) window.Hide(); }
        public void Dispose()
        {
            iconFont.Dispose();
            window.Dispose();
        }

        private sealed class NativeWindowOwner : IWin32Window
        {
            public NativeWindowOwner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }

        private sealed class CompletionForm : Form
        {
            protected override bool ShowWithoutActivation => true;
        }
    }
}
