using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SessionPurge.Tests
{
    internal static partial class TestMain
    {
        private static void IconLoading()
        {
            byte[] bytes;
            using (Stream stream = typeof(Theme).Assembly.GetManifestResourceStream("SessionPurge.ico"))
            {
                Assert(stream != null, "Icon resource is not embedded.");
                bytes = new byte[stream.Length];
                stream.Read(bytes, 0, bytes.Length);
            }
            int count = BitConverter.ToUInt16(bytes, 4);
            var sizes = Enumerable.Range(0, count).Select(i => bytes[6 + i * 16] == 0 ? 256 : bytes[6 + i * 16]).OrderBy(s => s).ToArray();
            Assert(sizes.SequenceEqual(new[] { 16, 24, 32, 48, 64, 128, 256 }), "Icon frames missing: " + string.Join(",", sizes.Select(s => s.ToString()).ToArray()));
            using (var stream = new MemoryStream(bytes))
            using (var frame = new Icon(stream, 16, 16))
                Assert(frame.Width == 16 && frame.Height == 16, "16 px icon frame did not load.");
            int large = Enumerable.Range(0, count).Single(i => bytes[6 + i * 16] == 0);
            int length = BitConverter.ToInt32(bytes, 6 + large * 16 + 8), offset = BitConverter.ToInt32(bytes, 6 + large * 16 + 12);
            using (var stream = new MemoryStream(bytes, offset, length))
            using (var frame = new Bitmap(stream))
                Assert(frame.Width == 256 && frame.Height == 256, "256 px icon frame did not load.");
            Assert(Theme.AppIcon != null && ReferenceEquals(Theme.AppIcon, Theme.AppIcon), "Application icon not loaded once.");
            using (var window = new MainWindow(Engine)) Assert(ReferenceEquals(window.Icon, Theme.AppIcon), "Main window does not use the application icon.");
            string home = Home(); Claude(home, A);
            using (var dialog = new ConfirmWindow(Plan(Scan(home, Provider.Claude), A), Engine.Clock)) Assert(ReferenceEquals(dialog.Icon, Theme.AppIcon), "Confirmation window does not use the application icon.");
        }
        private static void ThemeColors()
        {
            foreach (var pair in new[] { new[] { Theme.Text, Theme.Background }, new[] { Theme.Text, Theme.Surface }, new[] { Theme.Muted, Theme.Background }, new[] { Theme.Surface, Theme.Accent }, new[] { Theme.Surface, Theme.AccentHover }, new[] { Theme.Surface, Theme.AccentPressed }, new[] { Theme.Surface, Theme.Danger }, new[] { Theme.Surface, Theme.DangerHover }, new[] { Theme.Surface, Theme.DangerPressed }, new[] { Theme.Text, Theme.Neutral }, new[] { Theme.Text, Theme.NeutralHover }, new[] { Theme.Text, Theme.NeutralPressed } })
                Assert(Theme.Contrast(pair[0], pair[1]) >= 4.5, "Palette pair " + pair[0] + " on " + pair[1] + " is below 4.5:1.");
            Assert(Math.Abs(Theme.Contrast(Color.Black, Color.White) - 21) < 0.01, "Contrast formula incorrect.");
            using (var window = new MainWindow(Engine))
            {
                if (SystemInformation.HighContrast) { Assert(window.BackColor == SystemColors.Control, "High contrast mode was overridden."); return; }
                var controls = AllControls(window).ToList();
                foreach (var button in controls.OfType<Button>()) button.Enabled = true;
                Assert(Find<Button>(window, "scan").BackColor == Theme.Accent && Find<Button>(window, "reviewDeletion").BackColor == Theme.Danger, "Button roles not themed.");
                foreach (var button in controls.OfType<Button>())
                    foreach (Color back in new[] { button.BackColor, button.FlatAppearance.MouseOverBackColor, button.FlatAppearance.MouseDownBackColor })
                        Assert(Theme.Contrast(button.ForeColor, back) >= 4.5, "Button " + button.Name + " text contrast below 4.5:1.");
                foreach (var control in controls.Where(c => c is Label || c is CheckBox)) Assert(Theme.Contrast(control.ForeColor, window.BackColor) >= 4.5, "Label contrast below 4.5:1.");
                foreach (var control in controls.Where(c => c is TextBox || c is ComboBox)) Assert(Theme.Contrast(control.ForeColor, control.BackColor) >= 4.5, "Input contrast below 4.5:1.");
                var grid = Find<DataGridView>(window, "sessions");
                foreach (var style in new[] { grid.DefaultCellStyle, grid.ColumnHeadersDefaultCellStyle })
                    Assert(Theme.Contrast(style.ForeColor, style.BackColor) >= 4.5 && Theme.Contrast(style.SelectionForeColor, style.SelectionBackColor) >= 4.5, "Grid contrast below 4.5:1.");
            }
            using (var form = new Form())
            {
                var button = new Button(); var grid = new DataGridView(); var box = new TextBox();
                form.Controls.AddRange(new Control[] { button, grid, box });
                Theme.Apply(form, true, button, button);
                Assert(form.BackColor == SystemColors.Control && form.ForeColor == SystemColors.ControlText, "High contrast form colors overridden.");
                Assert(button.FlatStyle == FlatStyle.Standard && button.BackColor == SystemColors.Control && box.BackColor == SystemColors.Window, "High contrast control colors overridden.");
                Assert(grid.EnableHeadersVisualStyles && grid.DefaultCellStyle.BackColor == SystemColors.Window, "High contrast grid colors overridden.");
                Assert(ReferenceEquals(form.Icon, Theme.AppIcon), "High contrast skipped the application icon.");
                Theme.Restyle(form, false, button, button);
                Assert(form.BackColor == Theme.Background && button.FlatStyle == FlatStyle.Flat && box.BackColor == Theme.Surface && !grid.EnableHeadersVisualStyles, "Leaving high contrast did not restore the theme.");
                Theme.Restyle(form, true, button, button);
                button.Enabled = false; button.Enabled = true;
                Assert(form.BackColor == SystemColors.Control && button.FlatStyle == FlatStyle.Standard && button.BackColor == SystemColors.Control && box.BackColor == SystemColors.Window, "Entering high contrast left theme colors.");
                Assert(grid.EnableHeadersVisualStyles && grid.DefaultCellStyle.SelectionBackColor == SystemColors.Highlight && grid.BackgroundColor == SystemColors.Window, "Entering high contrast left grid theme colors.");
            }
        }
    }
}
