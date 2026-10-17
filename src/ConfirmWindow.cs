using System;
using System.Drawing;
using System.Windows.Forms;

namespace SessionPurge
{
    internal sealed class ConfirmWindow : Form
    {
        private readonly Timer expiry = new Timer { Interval = 250 };
        private readonly DeletionPlan plan;
        private readonly Func<DateTime> clock;
        private readonly TextBox confirmation = new TextBox { Name = "confirmation", AccessibleName = "Type DELETE to confirm" };
        private readonly Button remove = new Button { Name = "deletePermanently", Text = "Delete permanently", AutoSize = true, Enabled = false, DialogResult = DialogResult.OK };
        private readonly Label state = new Label { Name = "expiry", Text = "This preview expires two minutes after it was created.", AutoSize = true, UseMnemonic = false };
        internal ConfirmWindow(DeletionPlan deletion, Func<DateTime> now)
        {
            plan = deletion;
            clock = now;
            Text = "Review permanent deletion";
            Font = SystemFonts.MessageBoxFont;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimumSize = MainWindow.Fit(this, 640, 420);
            Size = MainWindow.Fit(this, 860, 620);
            AutoScroll = true;
            AutoScrollMinSize = LogicalToDeviceUnits(new Size(600, 360));
            var summary = new Label { Name = "summary", AutoSize = true, UseMnemonic = false, Text = "Permanently delete " + plan.Sessions.Count + " session(s): " + plan.Files + " file(s), " + plan.Bytes.ToString("N0") + " bytes. This bypasses the Recycle Bin and cannot be undone." };
            var preview = new TextBox { Name = "preview", AccessibleName = "Deletion preview", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Text = plan.Describe() };
            var cancel = new Button { Name = "cancel", Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(LogicalToDeviceUnits(8)) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(MainWindow.Bar(0, summary), 0, 0);
            layout.Controls.Add(preview, 0, 1);
            layout.Controls.Add(MainWindow.Bar(1, new Label { Text = "Type DELETE to confirm", AutoSize = true }, confirmation), 0, 2);
            layout.Controls.Add(MainWindow.Bar(0, state, cancel, remove), 0, 3);
            Controls.Add(layout);
            AcceptButton = cancel;
            CancelButton = cancel;
            confirmation.TextChanged += delegate { Recheck(); };
            expiry.Tick += delegate { Recheck(); };
            expiry.Start();
            Shown += delegate { confirmation.Focus(); };
        }
        private void Recheck()
        {
            bool valid = clock() <= plan.Expires;
            remove.Enabled = valid && confirmation.Text == "DELETE";
            if (!valid) state.Text = "Preview expired. Cancel and review again.";
        }
        protected override void Dispose(bool disposing) { if (disposing) expiry.Dispose(); base.Dispose(disposing); }
    }
}
