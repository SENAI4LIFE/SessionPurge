using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SessionPurge
{
    internal sealed class MainWindow : Form
    {
        private readonly HistoryEngine engine;
        private Provider? provider;
        private Catalog catalog;
        private CancellationTokenSource cancellation;
        private readonly TextBox root = Theme.TextBox(false);
        private readonly TextBox search = Theme.TextBox(false);
        private readonly DarkComboBox project = new DarkComboBox();
        private readonly DarkComboBox statusFilter = new DarkComboBox();
        private readonly DarkComboBox age = new DarkComboBox();
        private readonly CheckBox showNames = new CheckBox();
        private readonly SessionGrid sessions = new SessionGrid();
        private readonly TextBox details = Theme.TextBox(true);
        private readonly Label detailTitle = Theme.Label("Session details", 15, true, Theme.Ink);
        private readonly Label detailSource = Theme.Label("Select a session to inspect its file.", 9, false, Theme.Muted);
        private readonly Label status = Theme.Label("Choose a provider to begin. Nothing has been scanned.", 9, false, Theme.Muted);
        private readonly Label selection = Theme.Label("0 selected", 10, true, Theme.Ink);
        private readonly Label count = Theme.Label("YOUR SESSIONS", 10, true, Theme.Muted);
        private readonly Label empty = Theme.Label("Choose a provider\r\n\r\nSelect Claude Code or Codex above, then scan its local history.", 12, false, Theme.Muted);
        private readonly ActionButton scan = new ActionButton { Text = "Scan history", Primary = true, Dock = DockStyle.Fill };
        private readonly ActionButton purge = new ActionButton { Text = "Review deletion", Destructive = true, Dock = DockStyle.Fill };
        private readonly ActionButton cancel = new ActionButton { Text = "Cancel scan", Dock = DockStyle.Fill };
        private readonly ActionButton claude = new ActionButton { Text = "Claude Code", Dock = DockStyle.Fill };
        private readonly ActionButton codex = new ActionButton { Text = "Codex", Dock = DockStyle.Fill };
        private readonly ActionButton select = new ActionButton { Text = "Select visible", Dock = DockStyle.Fill };
        private readonly ActionButton clear = new ActionButton { Text = "Clear selection", Dock = DockStyle.Fill };
        private readonly SplitContainer split = new SplitContainer();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Panel tableHost = new Panel();
        private readonly Panel bodyHost = new Panel();
        private readonly Panel compactContent = new Panel();
        private readonly TableLayoutPanel compactLayout = new TableLayoutPanel();
        private readonly SurfacePanel detailPanel = new SurfacePanel();
        private readonly SurfacePanel sourcePanel = new SurfacePanel();
        private readonly TableLayoutPanel sourceLayout = new TableLayoutPanel();
        private readonly ActionButton sessionsTab = new ActionButton { Text = "Sessions", Dock = DockStyle.Fill, Active = true };
        private readonly ActionButton detailsTab = new ActionButton { Text = "Session details", Dock = DockStyle.Fill };
        private readonly TableLayoutPanel mainLayout = new TableLayoutPanel();
        private readonly TableLayoutPanel filters = new TableLayoutPanel();
        private readonly Label notice = Theme.Label("Verified transcripts and supported shared JSON history. Unknown stores stay protected.", 9, false, Theme.Muted);
        private bool busy;
        private bool changing;
        private bool adaptive;
        private bool layingOut;
        private bool splitConfigured;

        internal MainWindow() : this(new HistoryEngine()) { }
        internal MainWindow(HistoryEngine historyEngine)
        {
            engine = historyEngine;
            Text = "SessionPurge";
            Font = new Font("Segoe UI", 10);
            BackColor = Theme.Background;
            ForeColor = Theme.Ink;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(900, 760);
            Size = new Size(1360, 920);
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;
            var layout = mainLayout;
            layout.Dock = DockStyle.Fill; layout.ColumnCount = 1; layout.RowCount = 8; layout.Padding = new Padding(24, 18, 24, 16);
            foreach (float height in new[] { 66f, 96f, 76f, 38f }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            Controls.Add(layout);
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            header.Controls.Add(Theme.Label("SessionPurge", 25, true, Theme.Ink), 0, 0);
            header.Controls.Add(Theme.Label("A clearer view of your local conversation history.", 10, false, Theme.Muted), 0, 1);
            var offline = Theme.Label("\u25cf  OFFLINE BY DESIGN", 9, true, Theme.Green);
            offline.TextAlign = ContentAlignment.MiddleRight;
            header.Controls.Add(offline, 1, 0);
            layout.Controls.Add(header, 0, 0);

            var source = sourcePanel;
            source.Dock = DockStyle.Fill; source.Padding = new Padding(8); source.Margin = new Padding(0, 8, 0, 0);
            sourceLayout.Dock = DockStyle.Fill; sourceLayout.ColumnCount = 4; sourceLayout.RowCount = 2; sourceLayout.Margin = Padding.Empty;
            sourceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
            sourceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
            sourceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            sourceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 154));
            sourceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            sourceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sourceLayout.Controls.Add(claude, 0, 0);
            sourceLayout.Controls.Add(codex, 1, 0);
            var providerHint = Theme.Label("1  Choose a provider     2  Scan history", 9, false, Theme.Muted);
            providerHint.Padding = new Padding(12, 0, 0, 0);
            sourceLayout.Controls.Add(providerHint, 2, 0);
            sourceLayout.Controls.Add(scan, 3, 0);
            root.ReadOnly = true;
            root.TabStop = false;
            root.AccessibleName = "Provider home directory";
            root.Text = "Provider home will appear here after selection.";
            root.ForeColor = Theme.Muted;
            root.Margin = new Padding(0, 8, 0, 0);
            sourceLayout.Controls.Add(root, 0, 1);
            sourceLayout.SetColumnSpan(root, 4);
            source.Controls.Add(sourceLayout);
            layout.Controls.Add(source, 0, 1);
            claude.Click += delegate { Choose(Provider.Claude); };
            codex.Click += delegate { Choose(Provider.Codex); };
            scan.Click += async delegate { await ScanAsync(); };
            scan.Enabled = false;

            filters.Dock = DockStyle.Fill;
            filters.ColumnCount = 4;
            filters.RowCount = 2;
            filters.Margin = new Padding(0, 12, 0, 0);
            filters.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            filters.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (float width in new[] { 38f, 26f, 19f, 17f }) filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
            string[] labels = { "SEARCH", "PROJECT", "STATUS", "LAST MODIFIED" };
            Control[] inputs = { search, project, statusFilter, age };
            for (int i = 0; i < labels.Length; i++)
            {
                filters.Controls.Add(Theme.Label(labels[i], 8, true, Theme.Muted), i, 0);
                inputs[i].AccessibleName = labels[i];
                filters.Controls.Add(Theme.Input(inputs[i]), i, 1);
            }
            project.Items.Add("All projects"); project.SelectedIndex = 0;
            statusFilter.Items.AddRange(new object[] { "All sessions", "Eligible only", "Blocked only" }); statusFilter.SelectedIndex = 0;
            age.Items.AddRange(new object[] { "Any age", "Older than 7 days", "Older than 30 days", "Older than 90 days" }); age.SelectedIndex = 0;
            foreach (var control in new[] { project, statusFilter, age }) control.SelectedIndexChanged += delegate { Populate(); };
            search.TextChanged += delegate { Populate(); };
            layout.Controls.Add(filters, 0, 2);

            var listHeading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            listHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            listHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
            listHeading.Controls.Add(count, 0, 0);
            showNames.Text = "Show session names / prompt excerpts";
            showNames.AccessibleName = "Show session names";
            showNames.Dock = DockStyle.Fill;
            showNames.TextAlign = ContentAlignment.MiddleRight;
            showNames.CheckAlign = ContentAlignment.MiddleRight;
            showNames.ForeColor = Theme.Muted;
            showNames.UseVisualStyleBackColor = false;
            showNames.CheckedChanged += delegate { Populate(); };
            listHeading.Controls.Add(showNames, 1, 0);
            layout.Controls.Add(listHeading, 0, 3);

            ConfigureGrid();
            tableHost.Dock = DockStyle.Fill;
            tableHost.BackColor = Theme.Surface;
            tableHost.Controls.Add(sessions);
            empty.TextAlign = ContentAlignment.MiddleCenter;
            tableHost.Controls.Add(empty);
            empty.BringToFront();
            split.Dock = DockStyle.Fill;
            split.BackColor = Theme.Background;
            split.SplitterWidth = 12;
            split.Margin = Padding.Empty;
            split.Panel1.Controls.Add(tableHost);
            detailPanel.Dock = DockStyle.Fill; detailPanel.Padding = new Padding(18);
            var detailLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = Padding.Empty };
            detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            detailLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            detailTitle.AccessibleName = "Session detail title";
            detailLayout.Controls.Add(detailTitle, 0, 0);
            detailLayout.Controls.Add(detailSource, 0, 1);
            details.ReadOnly = true;
            details.ScrollBars = ScrollBars.Vertical;
            details.Margin = new Padding(0, 10, 0, 0);
            details.AccessibleName = "Session file details";
            details.Text = "Inspect a session before selecting it for deletion.\r\n\r\nNames are hidden until enabled. Full conversation bodies and tool outputs remain hidden.\r\n\r\nSupported shared JSON history is updated together with transcript deletion. Unsupported databases and uncertain layouts remain protected.";
            detailLayout.Controls.Add(details, 0, 2);
            detailPanel.Controls.Add(detailLayout);
            split.Panel2.Controls.Add(detailPanel);
            bodyHost.Dock = DockStyle.Fill; bodyHost.Margin = Padding.Empty;
            bodyHost.Controls.Add(split);
            compactLayout.Dock = DockStyle.Fill; compactLayout.ColumnCount = 1; compactLayout.RowCount = 2; compactLayout.Margin = Padding.Empty;
            compactLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); compactLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var navigation = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 6) };
            navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170)); navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            navigation.Controls.Add(sessionsTab, 0, 0); navigation.Controls.Add(detailsTab, 1, 0);
            sessionsTab.Click += delegate { CompactPage(false); };
            detailsTab.Click += delegate { CompactPage(true); };
            compactContent.Dock = DockStyle.Fill;
            compactLayout.Controls.Add(navigation, 0, 0); compactLayout.Controls.Add(compactContent, 0, 1);
            compactLayout.Visible = false; bodyHost.Controls.Add(compactLayout);
            layout.Controls.Add(bodyHost, 0, 4);
            notice.Padding = new Padding(0, 4, 0, 0);
            layout.Controls.Add(notice, 0, 5);

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, Padding = new Padding(0, 10, 0, 6), Margin = Padding.Empty };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 166));
            actions.Controls.Add(select, 0, 0);
            actions.Controls.Add(clear, 1, 0);
            selection.Padding = new Padding(8, 0, 0, 0);
            actions.Controls.Add(selection, 2, 0);
            actions.Controls.Add(cancel, 3, 0);
            actions.Controls.Add(purge, 4, 0);
            layout.Controls.Add(actions, 0, 6);
            cancel.Visible = false;
            cancel.Click += delegate { if (cancellation != null) cancellation.Cancel(); };
            select.Click += delegate { SelectVisible(); };
            clear.Click += delegate { ClearSelection(); };
            purge.Click += async delegate { await PurgeAsync(); };
            var statusPanel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            progress.Dock = DockStyle.Top; progress.Height = 3; progress.Style = ProgressBarStyle.Marquee; progress.Visible = false;
            status.Dock = DockStyle.Fill; status.Padding = new Padding(0, 5, 0, 0);
            statusPanel.Controls.Add(status); statusPanel.Controls.Add(progress);
            layout.Controls.Add(statusPanel, 0, 7);
            Shown += delegate { WindowTheme.Apply(this); AdaptLayout(); };
            bodyHost.SizeChanged += delegate { AdaptLayout(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy) { cancellation.Cancel(); e.Cancel = true; status.Text = "Stopping safely. Close again after the operation finishes."; }
            };
            KeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.F) { search.Focus(); e.SuppressKeyPress = true; }
                if (e.KeyCode == Keys.F5 && scan.Enabled) { e.SuppressKeyPress = true; await ScanAsync(); }
            };
            UpdateSelection();
        }
        private void ConfigureGrid()
        {
            sessions.Dock = DockStyle.Fill;
            sessions.AccessibleName = "Sessions grouped by project";
            sessions.BackgroundColor = Theme.Surface;
            sessions.BorderStyle = BorderStyle.None;
            sessions.GridColor = Theme.Border;
            sessions.AllowUserToAddRows = false;
            sessions.AllowUserToDeleteRows = false;
            sessions.AllowUserToResizeRows = false;
            sessions.AllowUserToOrderColumns = false;
            sessions.RowHeadersVisible = false;
            sessions.AutoGenerateColumns = false;
            sessions.MultiSelect = false;
            sessions.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            sessions.EditMode = DataGridViewEditMode.EditOnEnter;
            sessions.EnableHeadersVisualStyles = false;
            sessions.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            sessions.ColumnHeadersHeight = 36;
            sessions.ColumnHeadersDefaultCellStyle.BackColor = Theme.Raised;
            sessions.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
            sessions.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Raised;
            sessions.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9);
            sessions.DefaultCellStyle.BackColor = Theme.Surface;
            sessions.DefaultCellStyle.ForeColor = Theme.Ink;
            sessions.DefaultCellStyle.SelectionBackColor = Theme.Selection;
            sessions.DefaultCellStyle.SelectionForeColor = Theme.Ink;
            sessions.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
            sessions.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            sessions.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            sessions.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Select", HeaderText = "", Width = 38, Resizable = DataGridViewTriState.False });
            sessions.Columns.Add(new DataGridViewTextBoxColumn { Name = "Session", HeaderText = "SESSION / ID", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 260, ReadOnly = true });
            sessions.Columns.Add(new DataGridViewTextBoxColumn { Name = "Modified", HeaderText = "MODIFIED", Width = 150, ReadOnly = true });
            sessions.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = "SIZE", Width = 84, ReadOnly = true });
            sessions.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "STATUS", Width = 92, ReadOnly = true });
            foreach (DataGridViewColumn column in sessions.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            sessions.CurrentCellDirtyStateChanged += delegate { if (sessions.IsCurrentCellDirty) sessions.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            sessions.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (changing || e.RowIndex < 0 || e.ColumnIndex != 0) return;
                var row = sessions.Rows[e.RowIndex];
                var session = row.Tag as SessionRecord;
                if (busy || session == null || !session.Eligible) { changing = true; row.Cells[0].Value = false; changing = false; }
                UpdateSelection();
            };
            sessions.SelectionChanged += delegate { if (!changing) ShowDetails(); };
            sessions.CurrentCellChanged += delegate { if (!changing) ShowDetails(); };
            sessions.CellPainting += PaintCell;
        }
        private void PaintCell(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var row = sessions.Rows[e.RowIndex];
            var session = row.Tag as SessionRecord;
            if (session == null)
            {
                using (var brush = new SolidBrush(Theme.Raised)) e.Graphics.FillRectangle(brush, e.CellBounds);
                if (e.ColumnIndex == 1) using (var font = new Font("Segoe UI Semibold", 9)) TextRenderer.DrawText(e.Graphics, Convert.ToString(e.Value), font, e.CellBounds, Theme.Accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                e.Handled = true;
                return;
            }
            if (e.ColumnIndex == 0)
            {
                e.PaintBackground(e.ClipBounds, true);
                var box = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - 14) / 2, e.CellBounds.Y + (e.CellBounds.Height - 14) / 2, 14, 14);
                bool selected = Equals(row.Cells[0].Value, true);
                using (var brush = new SolidBrush(selected ? Theme.Accent : Theme.Surface)) e.Graphics.FillRectangle(brush, box);
                using (var pen = new Pen(session.Eligible ? Theme.Muted : Theme.Border)) e.Graphics.DrawRectangle(pen, box);
                if (selected) TextRenderer.DrawText(e.Graphics, "\u2713", Font, box, Theme.Background, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                e.Handled = true;
            }
            if (e.ColumnIndex == 1)
            {
                e.PaintBackground(e.ClipBounds, true);
                var bounds = e.CellBounds; bounds.X += 8; bounds.Width -= 16; bounds.Y += 7; bounds.Height = 24;
                TextRenderer.DrawText(e.Graphics, showNames.Checked ? session.Name : "Session names hidden", Font, bounds, Theme.Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                bounds.Y += 24; bounds.Height = 18;
                using (var small = new Font("Segoe UI", 8)) TextRenderer.DrawText(e.Graphics, session.Id, small, bounds, Theme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                e.Handled = true;
            }
        }
        private void AdaptLayout()
        {
            if (layingOut || !Visible || bodyHost.Height < 100 || bodyHost.Width < 400) return;
            layingOut = true;
            try
            {
                bool narrow = ClientSize.Width < 1120 * DeviceDpi / 96;
                if (!splitConfigured || narrow != adaptive)
                {
                    float scale = DeviceDpi / 96f;
                    if (narrow)
                    {
                        compactContent.Controls.Add(detailPanel); compactContent.Controls.Add(tableHost);
                        split.Visible = false; compactLayout.Visible = true; compactLayout.BringToFront(); CompactPage(false);
                    }
                    else
                    {
                        split.Panel1.Controls.Add(tableHost); split.Panel2.Controls.Add(detailPanel);
                        tableHost.Visible = true; detailPanel.Visible = true;
                        compactLayout.Visible = false; split.Visible = true; split.BringToFront();
                        split.Panel1MinSize = 0; split.Panel2MinSize = 0;
                        split.Orientation = Orientation.Vertical;
                        mainLayout.PerformLayout();
                        split.SplitterDistance = Math.Max(150, split.Width - split.SplitterWidth - (int)(348 * scale));
                    }
                    adaptive = narrow;
                    splitConfigured = true;
                }
                else if (!adaptive && split.Width - split.SplitterDistance < 240) split.SplitterDistance = Math.Max(150, split.Width - 348 * DeviceDpi / 96);
            }
            finally { layingOut = false; }
        }
        private void CompactPage(bool detail)
        {
            tableHost.Visible = !detail; detailPanel.Visible = detail;
            if (detail) detailPanel.BringToFront(); else tableHost.BringToFront();
            sessionsTab.Active = !detail; detailsTab.Active = detail;
            sessionsTab.Invalidate(); detailsTab.Invalidate();
        }
        private void Choose(Provider choice)
        {
            provider = choice;
            claude.Active = choice == Provider.Claude; codex.Active = choice == Provider.Codex;
            claude.Invalidate(); codex.Invalidate();
            root.Text = HistoryEngine.ConfiguredHome(choice);
            changing = true; search.Clear(); showNames.Checked = false; statusFilter.SelectedIndex = 0; age.SelectedIndex = 0; changing = false;
            ResetCatalog();
            scan.Enabled = true;
            empty.Text = "Ready to scan " + (choice == Provider.Claude ? "Claude Code" : "Codex") + "\r\n\r\nScan the configured local home to browse its sessions.";
            status.Text = "Provider selected. No history has been accessed yet.";
        }
        private void ResetCatalog()
        {
            catalog = null;
            changing = true;
            sessions.Rows.Clear();
            project.Items.Clear(); project.Items.Add("All projects"); project.SelectedIndex = 0;
            changing = false;
            count.Text = "YOUR SESSIONS";
            empty.Visible = true;
            detailTitle.Text = "Session details";
            detailSource.Text = "Select a session to inspect its file.";
            details.Clear();
            notice.Text = "Verified transcripts and supported shared JSON history. Unknown stores stay protected."; notice.ForeColor = Theme.Muted;
            UpdateSelection();
        }
        private async Task ScanAsync()
        {
            if (!provider.HasValue || busy) return;
            Provider chosen = provider.Value;
            string path = root.Text;
            ResetCatalog(); SetBusy(true);
            empty.Text = "Scanning local history\r\n\r\nNo files are being changed.";
            status.Text = "Checking transcript files and session-name metadata\u2026";
            try
            {
                var token = cancellation.Token;
                catalog = await Task.Run(() => engine.Scan(chosen, path, token));
                changing = true;
                foreach (string label in catalog.Sessions.Select(s => s.Project).Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) project.Items.Add(label);
                changing = false;
                Populate();
                int eligible = catalog.Sessions.Count(s => s.Eligible);
                string reason = catalog.Notices.FirstOrDefault() ?? catalog.Sessions.Where(s => !s.Eligible).Select(s => s.Blocked).FirstOrDefault();
                notice.Text = eligible + " eligible  /  " + (catalog.Sessions.Count - eligible) + " blocked. " + (reason ?? "Review the exact transcripts and shared-record changes before deletion.");
                notice.ForeColor = eligible == 0 && catalog.Sessions.Count > 0 ? Theme.Warning : Theme.Muted;
                status.Text = "Scan complete. " + catalog.Sessions.Count + " session(s). Names stay hidden until enabled.";
                details.Text = catalog.Notices.Count > 0 ? string.Join("\r\n\r\n", catalog.Notices.Distinct().ToArray()) : "Select a session to inspect its exact file, eligibility, and structural preview.";
            }
            catch (OperationCanceledException) { empty.Text = "Scan canceled\r\n\r\nNo files were changed. Scan again when ready."; status.Text = "Scan canceled. No files were changed."; }
            catch (Exception exception) { empty.Text = "History could not be scanned\r\n\r\n" + HistoryEngine.Reason(exception); status.Text = "Scan stopped safely. Check the provider home and try again."; }
            finally { changing = false; SetBusy(false); }
        }
        private void Populate()
        {
            if (changing || catalog == null) return;
            changing = true;
            sessions.SuspendLayout();
            try
            {
                sessions.Rows.Clear();
                string query = search.Text.Trim();
                int days = age.SelectedIndex == 1 ? 7 : age.SelectedIndex == 2 ? 30 : age.SelectedIndex == 3 ? 90 : 0;
                string lastProject = null;
                int visible = 0;
                foreach (var session in catalog.Sessions.OrderBy(s => s.Project, StringComparer.OrdinalIgnoreCase).ThenByDescending(s => s.Modified).ThenBy(s => s.Id, StringComparer.Ordinal))
                {
                    string haystack = session.Id + " " + session.Project + (showNames.Checked ? " " + session.Name : "");
                    if (query.Length > 0 && haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (project.SelectedIndex > 0 && session.Project != (string)project.SelectedItem) continue;
                    if (statusFilter.SelectedIndex == 1 && !session.Eligible || statusFilter.SelectedIndex == 2 && session.Eligible) continue;
                    if (days > 0 && session.Modified >= DateTime.UtcNow.AddDays(-days)) continue;
                    if (lastProject != session.Project)
                    {
                        var heading = sessions.Rows[sessions.Rows.Add(false, SafeFileSystem.SafeText(session.Project), "", "", "")];
                        heading.ReadOnly = true; heading.Height = 32; lastProject = session.Project;
                    }
                    int index = sessions.Rows.Add(false, showNames.Checked ? session.Name : "Session names hidden", session.Modified == default(DateTime) ? "Unknown" : session.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), FormatBytes(session.Bytes), session.Eligible ? "Eligible" : "Blocked");
                    var row = sessions.Rows[index]; row.Tag = session; row.Height = 62;
                    row.Cells[0].ReadOnly = !session.Eligible;
                    row.Cells[4].Style.ForeColor = session.Eligible ? Theme.Green : Theme.Warning;
                    row.Cells[4].ToolTipText = session.Blocked ?? "Verified transcript; supported shared records included in review";
                    visible++;
                }
                sessions.ClearSelection(); sessions.CurrentCell = null;
                empty.Visible = visible == 0;
                empty.Text = catalog.Sessions.Count == 0 ? "No sessions found\r\n\r\nThe selected home contains no recognized transcript filenames." : "No matching sessions\r\n\r\nTry a different search or filter.";
                count.Text = visible + " OF " + catalog.Sessions.Count + " SESSIONS";
                detailTitle.Text = "Session details";
                detailSource.Text = showNames.Checked ? "Titles may include user prompt excerpts." : "Session names are hidden.";
                details.Text = "Select a session for its affected file and structural preview.\r\n\r\nChanging filters or name visibility clears the selection.";
            }
            finally { sessions.ResumeLayout(); changing = false; UpdateSelection(); }
        }
        private void ShowDetails()
        {
            if (sessions.CurrentRow == null) return;
            var session = sessions.CurrentRow.Tag as SessionRecord;
            if (session == null) return;
            detailTitle.Text = showNames.Checked ? session.Name : "Session names hidden";
            detailSource.Text = showNames.Checked ? session.NameSource : "Enable names to show a title or prompt excerpt.";
            details.Text = "SESSION ID\r\n" + session.Id + "\r\n\r\nPROJECT\r\n" + SafeFileSystem.SafeText(session.Project) + "\r\n\r\nAFFECTED FILE\r\n" + SafeFileSystem.SafeText(session.RelativePath) +
                "\r\n\r\n" + session.Bytes.ToString("N0") + " bytes  /  " + session.Rows + " records\r\nProvider version: " + session.Version +
                "\r\n\r\n" + (session.Blocked ?? "Eligible transcript. Review deletion to see this file and every supported shared-history update. Directories are preserved.") +
                (catalog == null || catalog.Notices.Count == 0 ? "" : "\r\n\r\nHOME SAFETY CHECKS\r\n" + string.Join("\r\n\r\n", catalog.Notices.Distinct().ToArray())) +
                "\r\n\r\nSTRUCTURAL PREVIEW\r\nUp to 200 records; message bodies hidden.\r\n" + string.Join("\r\n", session.Structure.ToArray());
        }
        private List<SessionRecord> Chosen()
        {
            return sessions.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is SessionRecord && Equals(r.Cells[0].Value, true)).Select(r => (SessionRecord)r.Tag).ToList();
        }
        private void SelectVisible()
        {
            if (busy) return;
            changing = true;
            int chosen = 0;
            foreach (DataGridViewRow row in sessions.Rows)
            {
                var session = row.Tag as SessionRecord;
                if (session != null && session.Eligible) { row.Cells[0].Value = chosen < 500; chosen++; }
            }
            changing = false; UpdateSelection();
            if (chosen > 500) status.Text = "Selected the first 500 visible eligible sessions. Review this batch before selecting more.";
        }
        private void ClearSelection()
        {
            if (busy) return;
            changing = true;
            foreach (DataGridViewRow row in sessions.Rows) row.Cells[0].Value = false;
            changing = false; UpdateSelection();
        }
        private void UpdateSelection()
        {
            var chosen = Chosen();
            selection.Text = chosen.Count + " selected  /  " + FormatBytes(chosen.Sum(s => s.Bytes));
            purge.Enabled = !busy && chosen.Count > 0 && chosen.Count <= 500;
            select.Enabled = !busy && catalog != null && catalog.Sessions.Any(s => s.Eligible);
            clear.Enabled = !busy && chosen.Count > 0;
        }
        private void SetBusy(bool value)
        {
            busy = value;
            if (value) cancellation = new CancellationTokenSource();
            else if (cancellation != null) { cancellation.Dispose(); cancellation = null; }
            foreach (Control control in new Control[] { claude, codex, filters, showNames, sessions }) control.Enabled = !value;
            scan.Enabled = !value && provider.HasValue;
            cancel.Visible = value; cancel.Enabled = value;
            progress.Visible = value;
            UpdateSelection();
        }
        private async Task PurgeAsync()
        {
            if (busy || catalog == null) return;
            try
            {
                var plan = engine.Plan(catalog, Chosen());
                using (var dialog = new ConfirmWindow(plan)) if (dialog.ShowDialog(this) != DialogResult.OK) return;
                SetBusy(true); cancel.Text = "Stop deletion";
                status.Text = "Revalidating selected files before permanent deletion\u2026";
                var token = cancellation.Token;
                var result = await Task.Run(() => engine.Delete(plan, "DELETE", token));
                ResetCatalog();
                empty.Text = result.Deleted + " file(s) permanently deleted\r\n\r\nScan again to view the current history.";
                detailTitle.Text = result.Stopped == null ? "Deletion complete" : "Deletion stopped";
                details.Text = result.Describe();
                notice.Text = result.Stopped == null ? "Selected files were removed. Rescan before another selection." : "Some selected files may remain. Inspect the result and rescan.";
                status.Text = "Operation finished. No recovery copy was created.";
            }
            catch (Exception exception) { status.Text = HistoryEngine.Reason(exception); }
            finally { cancel.Text = "Cancel scan"; SetBusy(false); }
        }
        internal static string FormatBytes(long bytes)
        {
            return bytes < 1024 ? bytes.ToString("N0") + " B" : bytes < 1024 * 1024 ? (bytes / 1024d).ToString("N1") + " KB" : (bytes / (1024d * 1024)).ToString("N1") + " MB";
        }
        internal bool StartsWithoutProvider { get { return !provider.HasValue && catalog == null && !scan.Enabled; } }
        internal int VisibleSessions { get { return sessions.Rows.Cast<DataGridViewRow>().Count(r => r.Tag is SessionRecord); } }
        internal async Task ScanFixture(Provider chosen, string path) { Choose(chosen); root.Text = path; await ScanAsync(); }
        internal void SetSearch(string query) { search.Text = query; }
    }
}
