using System;
using System.Drawing;
using System.Windows.Forms;

namespace SessionPurge
{
    internal sealed class ConfirmWindow : Form
    {
        private readonly Timer expiry = new Timer();
        internal ConfirmWindow(DeletionPlan plan)
        {
            Text = "Review permanent deletion";
            BackColor = Theme.Background; ForeColor = Theme.Ink;
            Font = new Font("Segoe UI", 10);
            Size = new Size(900, 720); MinimumSize = new Size(720, 540);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(24) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.Controls.Add(Theme.Label("Review permanent deletion", 21, true, Theme.Ink), 0, 0);
            var summary = Theme.Label(plan.Sessions.Count + " transcript file(s)  /  " + plan.Bytes.ToString("N0") + " bytes  /  Bypasses the Recycle Bin", 10, false, Theme.Warning);
            layout.Controls.Add(summary, 0, 1);
            var preview = Theme.TextBox(true);
            preview.ReadOnly = true; preview.ScrollBars = ScrollBars.Both; preview.WordWrap = false; preview.Text = plan.Describe();
            layout.Controls.Add(Theme.Input(preview), 0, 2);
            var entry = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(0, 12, 0, 10), Margin = Padding.Empty };
            entry.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210)); entry.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            entry.Controls.Add(Theme.Label("Type DELETE to confirm", 10, true, Theme.Ink), 0, 0);
            var confirmation = Theme.TextBox(false);
            confirmation.AccessibleName = "Type DELETE to confirm";
            entry.Controls.Add(Theme.Input(confirmation), 1, 0);
            layout.Controls.Add(entry, 0, 3);
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            var timerLabel = Theme.Label("Preview expires in two minutes.", 9, false, Theme.Muted);
            var remove = new ActionButton { Text = "Delete permanently", Destructive = true, Enabled = false, DialogResult = DialogResult.OK, Dock = DockStyle.Fill };
            var cancel = new ActionButton { Text = "Cancel", DialogResult = DialogResult.Cancel, Dock = DockStyle.Fill };
            Action update = delegate
            {
                bool valid = DateTime.UtcNow <= plan.Expires;
                remove.Enabled = confirmation.Text == "DELETE" && valid;
                if (!valid) timerLabel.Text = "Preview expired. Cancel and review again.";
            };
            confirmation.TextChanged += delegate { update(); };
            expiry.Interval = 250; expiry.Tick += delegate { update(); }; expiry.Start();
            actions.Controls.Add(timerLabel, 0, 0); actions.Controls.Add(cancel, 1, 0); actions.Controls.Add(remove, 2, 0);
            layout.Controls.Add(actions, 0, 4);
            Controls.Add(layout); CancelButton = cancel; AcceptButton = cancel;
            Shown += delegate { WindowTheme.Apply(this); confirmation.Focus(); };
        }
        protected override void Dispose(bool disposing) { if (disposing) expiry.Dispose(); base.Dispose(disposing); }
    }
}
