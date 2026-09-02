using Microsoft.VisualStudio;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AxialSqlTools.Completion
{
    internal sealed class CompletionPresenter : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
        [DllImport("user32.dll")] private static extern bool GetCaretPos(out NativePoint point);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;

        private readonly Form window;
        private readonly ListBox list;
        private readonly Label footer;
        private readonly Font kindFont;
        private IReadOnlyList<CompletionItem> items;
        private int textLineHeight;
        private int kindColumnWidth;

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
            kindFont = new Font(list.Font, FontStyle.Bold);
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
                MinimumSize = new Size(360, 220),
                Size = SettingsManager.GetCompletionWindowSize()
            };
            UpdateScaledMetrics();
            window.Controls.Add(list);
            window.Controls.Add(footer);
            list.DoubleClick += (_, __) => CommitRequested?.Invoke();
            list.DrawItem += DrawCompletionItem;
            window.ResizeEnd += (_, __) => SettingsManager.SaveCompletionWindowSize(window.Size);
            window.DpiChanged += (_, __) => UpdateScaledMetrics();
            window.FormClosing += Window_FormClosing;
        }

        public event Action CommitRequested;
        public event Action DismissRequested;
        public bool IsVisible => window.Visible;
        public CompletionItem SelectedItem => list.SelectedIndex >= 0 && items != null && list.SelectedIndex < items.Count ? items[list.SelectedIndex] : null;

        public void Show(IVsTextView textView, IReadOnlyList<CompletionItem> newItems)
        {
            string selectedKey = ItemKey(SelectedItem);
            items = newItems;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var item in newItems) list.Items.Add(item);
            list.EndUpdate();
            if (list.Items.Count == 0) { Hide(); return; }
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
            list.SelectedIndex = restoredIndex >= 0 ? restoredIndex : 0;
            int maximumItems = SettingsManager.GetSqlCompletionSettings().maximumItems;
            footer.Text = newItems.Count >= maximumItems
                ? LocalizationManager.Format("Showing first {0} matches - keep typing to narrow results", maximumItems)
                : LocalizationManager.Format("{0} matches | Up/Down select | Enter/Tab insert | Esc close", newItems.Count);
            if (newItems.Count > 0 && !string.IsNullOrWhiteSpace(newItems[0].ScopeKey))
                footer.Text += " | " + newItems[0].ScopeKey;

            IntPtr handle = textView.GetWindowHandle();
            NativePoint point;
            if (!GetCaretPos(out point)) point = new NativePoint { X = 20, Y = 20 };
            ClientToScreen(handle, ref point);
            Screen screen = Screen.FromPoint(new Point(point.X, point.Y));
            Rectangle area = screen.WorkingArea;
            int maxWidth = Math.Max(window.MinimumSize.Width, area.Width / 2);
            int maxHeight = Math.Max(window.MinimumSize.Height, (area.Height * 3) / 4);
            if (window.Width > maxWidth) window.Width = maxWidth;
            if (window.Height > maxHeight) window.Height = maxHeight;

            int x = Math.Min(point.X, area.Right - window.Width);
            x = Math.Max(area.Left, x);
            int belowY = point.Y + 22;
            int y = belowY + window.Height <= area.Bottom ? belowY : point.Y - window.Height;
            y = Math.Max(area.Top, Math.Min(y, area.Bottom - window.Height));
            window.Location = new Point(x, y);
            if (!window.Visible)
                window.Show(new NativeWindowOwner(handle));

            // A non-activating owned form can remain Visible while falling behind SSMS
            // after an application switch. Refresh its Z-order for every completion update.
            SetWindowPos(window.Handle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow);
            textView.SendExplicitFocus();
        }

        public bool HandleNavigation(uint commandId)
        {
            if (!IsVisible) return false;
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
            kindColumnWidth = Math.Max(52, TextRenderer.MeasureText("Snippet", kindFont).Width + 8);
            list.ItemHeight = (textLineHeight * 2) + 8;
            footer.Height = textLineHeight + 10;
            footer.Padding = new Padding(6, Math.Max(2, (footer.Height - list.Font.Height) / 2), 6, 0);
            list.Invalidate();
        }

        private void DrawCompletionItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || items == null || e.Index >= items.Count) return;
            CompletionItem item = items[e.Index];
            e.DrawBackground();

            Color primary = (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : list.ForeColor;
            Color secondary = (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : SystemColors.GrayText;
            Rectangle bounds = e.Bounds;
            string kind = ShortKindName(item.Kind);
            TextRenderer.DrawText(e.Graphics, kind, kindFont,
                new Rectangle(bounds.Left + 7, bounds.Top + 4, kindColumnWidth - 7, textLineHeight), secondary,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, item.DisplayText ?? string.Empty, list.Font,
                new Rectangle(bounds.Left + kindColumnWidth + 8, bounds.Top + 3, Math.Max(0, bounds.Width - kindColumnWidth - 15), textLineHeight), primary,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, item.Description ?? string.Empty, list.Font,
                new Rectangle(bounds.Left + kindColumnWidth + 8, bounds.Top + 4 + textLineHeight, Math.Max(0, bounds.Width - kindColumnWidth - 15), textLineHeight), secondary,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }

        private static string ShortKindName(CompletionItemKind kind)
        {
            switch (kind)
            {
                case CompletionItemKind.Procedure: return "Proc";
                case CompletionItemKind.Function: return "Func";
                case CompletionItemKind.Keyword: return "Key";
                default: return kind.ToString();
            }
        }

        public void Hide() { if (window.Visible) window.Hide(); }
        public void Dispose()
        {
            kindFont.Dispose();
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
