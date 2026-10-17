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
        private readonly ComboBox providers = Choice("providers", "Provider", "Claude Code", "Codex");
        private readonly TextBox root = new TextBox { Name = "home", AccessibleName = "Provider home directory", ReadOnly = true, TabStop = false };
        private readonly Button scan = Command("scan", "Scan history");
        private readonly Button cancel = Command("cancel", "Cancel");
        private readonly TextBox search = new TextBox { Name = "search", AccessibleName = "Search" };
        private readonly ComboBox project = Choice("project", "Project", "All projects");
        private readonly ComboBox statusFilter = Choice("statusFilter", "Status", "All sessions", "Eligible only", "Blocked only");
        private readonly ComboBox age = Choice("age", "Last modified", "Any age", "Older than 7 days", "Older than 30 days", "Older than 90 days");
        private readonly CheckBox showNames = new CheckBox { Name = "showNames", AccessibleName = "Show session names", Text = "Show session names / prompt excerpts", AutoSize = true };
        private readonly DataGridView sessions = new DataGridView { Name = "sessions", AccessibleName = "Sessions" };
        private readonly TextBox details = new TextBox { Name = "details", AccessibleName = "Session details", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        private readonly SplitContainer split = new SplitContainer { Name = "split", Dock = DockStyle.Fill };
        private readonly Button select = Command("selectVisible", "Select visible");
        private readonly Button clear = Command("clearSelection", "Clear selection");
        private readonly Button purge = Command("reviewDeletion", "Review deletion");
        private readonly Label selection = new Label { Name = "selection", AutoSize = true, UseMnemonic = false };
        private readonly Label status = new Label { Name = "status", Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft, Text = "Choose a provider. Nothing has been scanned." };
        private readonly TableLayoutPanel filters;
        private bool busy;
        private bool changing;

        internal MainWindow() : this(new HistoryEngine()) { }
        internal MainWindow(HistoryEngine historyEngine)
        {
            engine = historyEngine;
            Text = "SessionPurge";
            Font = SystemFonts.MessageBoxFont;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = Fit(this, 960, 600);
            Size = Fit(this, 1280, 820);
            AutoScroll = true;
            AutoScrollMinSize = LogicalToDeviceUnits(new Size(920, 540));
            KeyPreview = true;
            providers.Width = LogicalToDeviceUnits(140);
            project.Width = LogicalToDeviceUnits(180);
            statusFilter.Width = LogicalToDeviceUnits(120);
            age.Width = LogicalToDeviceUnits(140);
            project.SelectedIndex = 0;
            statusFilter.SelectedIndex = 0;
            age.SelectedIndex = 0;
            ConfigureGrid();
            split.Panel1.Controls.Add(sessions);
            split.Panel2.Controls.Add(details);
            filters = Bar(1, Caption("Search"), search, project, statusFilter, age, showNames);
            var layout = new TableLayoutPanel { Name = "layout", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(LogicalToDeviceUnits(8)) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Font.Height + LogicalToDeviceUnits(10)));
            layout.Controls.Add(Bar(2, Caption("Provider"), providers, root, scan, cancel), 0, 0);
            layout.Controls.Add(filters, 0, 1);
            layout.Controls.Add(split, 0, 2);
            layout.Controls.Add(Bar(2, select, clear, selection, purge), 0, 3);
            layout.Controls.Add(status, 0, 4);
            Controls.Add(layout);
            providers.SelectedIndexChanged += delegate { if (providers.SelectedIndex >= 0) Choose(providers.SelectedIndex == 0 ? Provider.Claude : Provider.Codex); };
            scan.Click += async delegate { await ScanAsync(); };
            cancel.Click += delegate { if (cancellation != null) cancellation.Cancel(); };
            foreach (var control in new[] { project, statusFilter, age }) control.SelectedIndexChanged += delegate { Populate(); };
            search.TextChanged += delegate { Populate(); };
            showNames.CheckedChanged += delegate { Populate(); };
            select.Click += delegate { SelectVisible(); };
            clear.Click += delegate { ClearSelection(); };
            purge.Click += async delegate { await PurgeAsync(); };
            scan.Enabled = false;
            cancel.Enabled = false;
            Load += delegate
            {
                split.SplitterDistance = Math.Max(split.Panel1MinSize, split.Width - split.SplitterWidth - LogicalToDeviceUnits(320));
                split.FixedPanel = FixedPanel.Panel2;
            };
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
            sessions.BackgroundColor = SystemColors.Window;
            sessions.AllowUserToAddRows = false;
            sessions.AllowUserToDeleteRows = false;
            sessions.AllowUserToResizeRows = false;
            sessions.RowHeadersVisible = false;
            sessions.MultiSelect = false;
            sessions.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            sessions.EditMode = DataGridViewEditMode.EditOnEnter;
            sessions.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            sessions.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Select", HeaderText = "Select", AutoSizeMode = DataGridViewAutoSizeColumnMode.ColumnHeader });
            sessions.Columns.Add(Column("Project", "Project", 40));
            sessions.Columns.Add(Column("Name", "Name", 60));
            sessions.Columns.Add(Column("Id", "Session ID", 0));
            sessions.Columns.Add(Column("Modified", "Modified", 0));
            sessions.Columns.Add(Column("Size", "Size", 0));
            sessions.Columns.Add(Column("Status", "Status", 0));
            sessions.Columns["Name"].Visible = false;
            foreach (DataGridViewColumn column in sessions.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            sessions.CurrentCellDirtyStateChanged += delegate { if (sessions.IsCurrentCellDirty) sessions.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            sessions.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (changing || e.RowIndex < 0 || e.ColumnIndex != 0) return;
                var row = sessions.Rows[e.RowIndex];
                if (busy || !((SessionRecord)row.Tag).Eligible) { changing = true; row.Cells[0].Value = false; changing = false; }
                UpdateSelection();
            };
            sessions.CurrentCellChanged += delegate { if (!changing) ShowDetails(); };
        }
        private DataGridViewTextBoxColumn Column(string name, string header, float fill)
        {
            return new DataGridViewTextBoxColumn { Name = name, HeaderText = header, ReadOnly = true, AutoSizeMode = fill > 0 ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.DisplayedCells, FillWeight = fill > 0 ? fill : 100, MinimumWidth = LogicalToDeviceUnits(fill > 0 ? 80 : 40) };
        }
        private void Choose(Provider choice)
        {
            provider = choice;
            root.Text = HistoryEngine.ConfiguredHome(choice);
            changing = true; search.Clear(); showNames.Checked = false; statusFilter.SelectedIndex = 0; age.SelectedIndex = 0; changing = false;
            ResetCatalog();
            scan.Enabled = true;
            status.Text = "Ready to scan " + (choice == Provider.Claude ? "Claude Code" : "Codex") + ". No history has been read yet.";
        }
        private void ResetCatalog()
        {
            catalog = null;
            changing = true;
            sessions.Rows.Clear();
            project.Items.Clear(); project.Items.Add("All projects"); project.SelectedIndex = 0;
            changing = false;
            details.Clear();
            UpdateSelection();
        }
        private async Task ScanAsync()
        {
            if (!provider.HasValue || busy) return;
            Provider chosen = provider.Value;
            string path = root.Text;
            ResetCatalog(); SetBusy(true);
            status.Text = "Scanning. No files are being changed.";
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
                status.Text = catalog.Sessions.Count == 0 ? "No recognized sessions found." : catalog.Sessions.Count + " sessions: " + eligible + " eligible, " + (catalog.Sessions.Count - eligible) + " blocked." + (reason == null ? "" : " " + reason);
            }
            catch (OperationCanceledException) { status.Text = "Scan canceled. No files were changed."; }
            catch (Exception exception) { status.Text = "Scan stopped. " + HistoryEngine.Reason(exception); }
            finally { changing = false; SetBusy(false); }
        }
        private void Populate()
        {
            if (changing || catalog == null) return;
            changing = true;
            try
            {
                sessions.Rows.Clear();
                sessions.Columns["Name"].Visible = showNames.Checked;
                string query = search.Text.Trim();
                int days = new[] { 0, 7, 30, 90 }[age.SelectedIndex];
                foreach (var session in catalog.Sessions.OrderBy(s => s.Project, StringComparer.OrdinalIgnoreCase).ThenByDescending(s => s.Modified).ThenBy(s => s.Id, StringComparer.Ordinal))
                {
                    string haystack = session.Id + " " + session.Project + (showNames.Checked ? " " + session.Name : "");
                    if (query.Length > 0 && haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (project.SelectedIndex > 0 && session.Project != (string)project.SelectedItem) continue;
                    if (statusFilter.SelectedIndex == 1 && !session.Eligible || statusFilter.SelectedIndex == 2 && session.Eligible) continue;
                    if (days > 0 && session.Modified >= engine.Clock().AddDays(-days)) continue;
                    var row = sessions.Rows[sessions.Rows.Add(false, SessionNames.SafeText(session.Project), showNames.Checked ? session.Name : "", session.Id, session.Modified == default(DateTime) ? "Unknown" : session.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), FormatBytes(session.Bytes), session.Eligible ? "Eligible" : "Blocked")];
                    row.Tag = session;
                    row.Cells[0].ReadOnly = !session.Eligible;
                    row.Cells["Status"].ToolTipText = session.Blocked ?? "Eligible. Review deletion lists every affected file.";
                }
                sessions.ClearSelection();
                sessions.CurrentCell = null;
                details.Text = catalog.Notices.Count > 0 ? string.Join("\r\n\r\n", catalog.Notices.Distinct().ToArray()) : "Select a session to see its file and structure.";
            }
            finally { changing = false; UpdateSelection(); }
        }
        private void ShowDetails()
        {
            if (sessions.CurrentRow == null || catalog == null) return;
            var session = (SessionRecord)sessions.CurrentRow.Tag;
            details.Text = (showNames.Checked ? session.Name + "\r\n" + session.NameSource + "\r\n\r\n" : "") +
                "Session ID: " + session.Id + "\r\nProject: " + SessionNames.SafeText(session.Project) + "\r\nFile: " + SessionNames.SafeText(session.RelativePath) +
                "\r\nSize: " + session.Bytes.ToString("N0") + " bytes, " + session.Rows + " records\r\nVersion: " + session.Version +
                "\r\n\r\n" + (session.Blocked ?? "Eligible. Review deletion lists this file and every shared-history update.") +
                (catalog.Notices.Count == 0 ? "" : "\r\n\r\nHome checks:\r\n" + string.Join("\r\n", catalog.Notices.Distinct().ToArray())) +
                "\r\n\r\nStructure (up to 200 records, content hidden):\r\n" + string.Join("\r\n", session.Structure.ToArray());
        }
        private List<SessionRecord> Chosen()
        {
            return sessions.Rows.Cast<DataGridViewRow>().Where(r => Equals(r.Cells[0].Value, true)).Select(r => (SessionRecord)r.Tag).ToList();
        }
        private void SelectVisible()
        {
            if (busy) return;
            changing = true;
            int chosen = 0;
            foreach (DataGridViewRow row in sessions.Rows)
                if (((SessionRecord)row.Tag).Eligible) { row.Cells[0].Value = chosen < 500; chosen++; }
            changing = false; UpdateSelection();
            if (chosen > 500) status.Text = "Selected the first 500 eligible sessions shown. Review this batch before selecting more.";
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
            selection.Text = catalog == null ? "" : sessions.Rows.Count + " of " + catalog.Sessions.Count + " shown, " + chosen.Count + " selected (" + FormatBytes(chosen.Sum(s => s.Bytes)) + ")";
            purge.Enabled = !busy && chosen.Count > 0 && chosen.Count <= 500;
            select.Enabled = !busy && catalog != null && catalog.Sessions.Any(s => s.Eligible);
            clear.Enabled = !busy && chosen.Count > 0;
        }
        private void SetBusy(bool value)
        {
            busy = value;
            if (value) cancellation = new CancellationTokenSource();
            else if (cancellation != null) { cancellation.Dispose(); cancellation = null; }
            foreach (Control control in new Control[] { providers, filters, sessions }) control.Enabled = !value;
            scan.Enabled = !value && provider.HasValue;
            cancel.Enabled = value;
            UpdateSelection();
        }
        private async Task PurgeAsync()
        {
            if (busy || catalog == null) return;
            try
            {
                var plan = engine.Plan(catalog, Chosen());
                using (var dialog = new ConfirmWindow(plan, engine.Clock)) if (dialog.ShowDialog(this) != DialogResult.OK) return;
                SetBusy(true);
                status.Text = "Revalidating and deleting the selected files.";
                var token = cancellation.Token;
                var result = await Task.Run(() => engine.Delete(plan, "DELETE", token));
                ResetCatalog();
                details.Text = result.Describe();
                status.Text = result.Stopped == null ? "Deletion complete. Rescan to see the current history." : "Deletion stopped. Some selected files may remain; rescan before retrying.";
            }
            catch (Exception exception) { status.Text = HistoryEngine.Reason(exception); }
            finally { SetBusy(false); }
        }
        internal static string FormatBytes(long bytes)
        {
            return bytes < 1024 ? bytes.ToString("N0") + " B" : bytes < 1024 * 1024 ? (bytes / 1024d).ToString("N1") + " KB" : (bytes / (1024d * 1024)).ToString("N1") + " MB";
        }
        internal static Size Fit(Control control, int width, int height)
        {
            Size scaled = control.LogicalToDeviceUnits(new Size(width, height));
            Rectangle area = Screen.FromPoint(Control.MousePosition).WorkingArea;
            return new Size(Math.Min(scaled.Width, area.Width), Math.Min(scaled.Height, area.Height));
        }
        internal static TableLayoutPanel Bar(int fill, params Control[] controls)
        {
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = controls.Length, RowCount = 1, Margin = Padding.Empty };
            for (int i = 0; i < controls.Length; i++)
            {
                bar.ColumnStyles.Add(i == fill ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.AutoSize));
                controls[i].Anchor = controls[i] is TextBox ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.Left;
                bar.Controls.Add(controls[i], i, 0);
            }
            return bar;
        }
        private static Label Caption(string text) { return new Label { Text = text, AutoSize = true }; }
        private static Button Command(string name, string text) { return new Button { Name = name, Text = text, AutoSize = true, UseMnemonic = false }; }
        private static ComboBox Choice(string name, string accessibleName, params string[] items)
        {
            var box = new ComboBox { Name = name, AccessibleName = accessibleName, DropDownStyle = ComboBoxStyle.DropDownList };
            box.Items.AddRange(items);
            return box;
        }
        internal bool StartsWithoutProvider { get { return !provider.HasValue && catalog == null && !scan.Enabled; } }
        internal int VisibleSessions { get { return sessions.Rows.Count; } }
        internal async Task ScanFixture(Provider chosen, string path) { providers.SelectedIndex = chosen == Provider.Claude ? 0 : 1; root.Text = path; await ScanAsync(); }
        internal void SetSearch(string query) { search.Text = query; }
    }
}
