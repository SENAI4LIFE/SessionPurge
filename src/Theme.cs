using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SessionPurge
{
    internal static class Theme
    {
        internal static readonly Color Background = Color.FromArgb(0xF3, 0xF6, 0xFA);
        internal static readonly Color Surface = Color.White;
        internal static readonly Color Text = Color.FromArgb(0x16, 0x20, 0x2E);
        internal static readonly Color Muted = Color.FromArgb(0x4F, 0x5B, 0x6B);
        internal static readonly Color Accent = Color.FromArgb(0x1E, 0x4E, 0x8C);
        internal static readonly Color AccentHover = Color.FromArgb(0x2A, 0x5F, 0xA3);
        internal static readonly Color AccentPressed = Color.FromArgb(0x16, 0x3B, 0x6B);
        internal static readonly Color Danger = Color.FromArgb(0xC0, 0x36, 0x2C);
        internal static readonly Color DangerHover = Color.FromArgb(0xA8, 0x2E, 0x25);
        internal static readonly Color DangerPressed = Color.FromArgb(0x8E, 0x26, 0x1F);
        internal static readonly Color Border = Color.FromArgb(0xC9, 0xD2, 0xDE);
        internal static readonly Color Neutral = Color.FromArgb(0xE4, 0xE9, 0xF0);
        internal static readonly Color NeutralHover = Color.FromArgb(0xD6, 0xDE, 0xE8);
        internal static readonly Color NeutralPressed = Color.FromArgb(0xC9, 0xD2, 0xDE);
        private static Icon icon;

        internal static Icon AppIcon
        {
            get
            {
                if (icon == null)
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SessionPurge.ico"))
                        if (stream != null) icon = new Icon(stream);
                return icon;
            }
        }

        internal static void Apply(Form form, bool highContrast, Button primary, Button danger)
        {
            if (AppIcon != null) form.Icon = AppIcon;
            foreach (var button in Descendants(form).OfType<Button>())
            {
                var target = button;
                Color tone = button == primary ? Accent : button == danger ? Danger : Neutral;
                button.EnabledChanged += delegate { if (target.FlatStyle == FlatStyle.Flat) Tone(target, tone); };
            }
            Restyle(form, highContrast, primary, danger);
            UserPreferenceChangedEventHandler changed = delegate
            {
                try { if (form.IsHandleCreated && !form.IsDisposed) form.BeginInvoke((Action)delegate { if (!form.IsDisposed) Restyle(form, SystemInformation.HighContrast, primary, danger); }); }
                catch (InvalidOperationException) { }
            };
            SystemEvents.UserPreferenceChanged += changed;
            form.Disposed += delegate { SystemEvents.UserPreferenceChanged -= changed; };
        }

        internal static void Restyle(Form form, bool highContrast, Button primary, Button danger)
        {
            if (highContrast) { form.ResetBackColor(); form.ResetForeColor(); }
            else { form.BackColor = Background; form.ForeColor = Text; }
            foreach (Control control in Descendants(form))
            {
                var button = control as Button;
                var grid = control as DataGridView;
                var combo = control as ComboBox;
                if (button != null)
                {
                    button.FlatStyle = highContrast ? FlatStyle.Standard : FlatStyle.Flat;
                    button.UseVisualStyleBackColor = highContrast;
                    if (highContrast) { button.ResetBackColor(); button.ResetForeColor(); }
                    else Tone(button, button == primary ? Accent : button == danger ? Danger : Neutral);
                }
                else if (grid != null) StyleGrid(grid, highContrast);
                else if (combo != null) { combo.FlatStyle = highContrast ? FlatStyle.Standard : FlatStyle.Flat; Colors(combo, highContrast, Surface, Text); }
                else if (control is TextBox) Colors(control, highContrast, Surface, Text);
                else if (control is Label && control.Name == "status") { if (highContrast) control.ResetForeColor(); else control.ForeColor = Muted; }
            }
        }

        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                yield return control;
                foreach (Control child in Descendants(control)) yield return child;
            }
        }

        private static void Colors(Control control, bool highContrast, Color back, Color fore)
        {
            if (highContrast) { control.ResetBackColor(); control.ResetForeColor(); }
            else { control.BackColor = back; control.ForeColor = fore; }
        }

        private static void Tone(Button button, Color tone)
        {
            bool strong = button.Enabled && tone != Neutral;
            button.BackColor = strong ? tone : Neutral;
            button.ForeColor = strong ? Surface : Text;
            button.FlatAppearance.BorderColor = strong ? tone : Border;
            button.FlatAppearance.MouseOverBackColor = strong ? (tone == Accent ? AccentHover : DangerHover) : NeutralHover;
            button.FlatAppearance.MouseDownBackColor = strong ? (tone == Accent ? AccentPressed : DangerPressed) : NeutralPressed;
        }

        private static void StyleGrid(DataGridView grid, bool highContrast)
        {
            grid.BackgroundColor = highContrast ? SystemColors.Window : Surface;
            grid.GridColor = highContrast ? SystemColors.ControlDark : Border;
            grid.EnableHeadersVisualStyles = highContrast;
            grid.ColumnHeadersBorderStyle = highContrast ? DataGridViewHeaderBorderStyle.Raised : DataGridViewHeaderBorderStyle.Single;
            grid.DefaultCellStyle.BackColor = highContrast ? SystemColors.Window : Surface;
            grid.DefaultCellStyle.ForeColor = highContrast ? SystemColors.ControlText : Text;
            grid.DefaultCellStyle.SelectionBackColor = highContrast ? SystemColors.Highlight : Accent;
            grid.DefaultCellStyle.SelectionForeColor = highContrast ? SystemColors.HighlightText : Surface;
            grid.ColumnHeadersDefaultCellStyle.BackColor = highContrast ? SystemColors.Control : Background;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = highContrast ? SystemColors.WindowText : Text;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = highContrast ? SystemColors.Highlight : Background;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = highContrast ? SystemColors.HighlightText : Text;
        }

        internal static double Contrast(Color first, Color second)
        {
            double a = Luminance(first), b = Luminance(second);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }

        private static double Luminance(Color color)
        {
            Func<int, double> channel = value => { double c = value / 255d; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); };
            return 0.2126 * channel(color.R) + 0.7152 * channel(color.G) + 0.0722 * channel(color.B);
        }
    }
}
