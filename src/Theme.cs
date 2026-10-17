using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SessionPurge
{
    internal static class Theme
    {
        internal static readonly Color Background = Color.FromArgb(16, 20, 28);
        internal static readonly Color Surface = Color.FromArgb(24, 30, 41);
        internal static readonly Color Raised = Color.FromArgb(32, 40, 54);
        internal static readonly Color Border = Color.FromArgb(48, 59, 77);
        internal static readonly Color Ink = Color.FromArgb(234, 240, 249);
        internal static readonly Color Muted = Color.FromArgb(158, 174, 196);
        internal static readonly Color Accent = Color.FromArgb(145, 181, 255);
        internal static readonly Color Green = Color.FromArgb(133, 216, 185);
        internal static readonly Color Warning = Color.FromArgb(236, 191, 128);
        internal static readonly Color Selection = Color.FromArgb(39, 57, 84);

        internal static Label Label(string text, float size, bool strong, Color color)
        {
            return new Label { Text = text, Font = new Font(strong ? "Segoe UI Semibold" : "Segoe UI", size), ForeColor = color, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty, UseMnemonic = false };
        }
        internal static TextBox TextBox(bool multiline)
        {
            return new TextBox { BackColor = Surface, ForeColor = Ink, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, Multiline = multiline, Margin = new Padding(8), ShortcutsEnabled = true };
        }
        internal static Control Input(Control child)
        {
            var panel = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(10, 10, 10, 8), Margin = new Padding(0, 0, 10, 0) };
            panel.Controls.Add(child);
            return panel;
        }
    }

    internal sealed class SurfacePanel : Panel
    {
        internal SurfacePanel() { DoubleBuffered = true; BackColor = Theme.Surface; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        }
    }

    internal sealed class ActionButton : Button
    {
        private bool hover;
        internal bool Primary;
        internal bool Destructive;
        internal bool Active;
        internal ActionButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            ForeColor = Theme.Ink;
            BackColor = Theme.Raised;
            Font = new Font("Segoe UI Semibold", 10);
            Cursor = Cursors.Hand;
            Height = 40;
            Margin = new Padding(0, 0, 10, 0);
            UseMnemonic = false;
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Color fill = Active ? Theme.Selection : Primary ? Color.FromArgb(61, 91, 147) : Destructive ? Color.FromArgb(133, 48, 65) : Theme.Raised;
            if (hover && Enabled) fill = ControlPaint.Light(fill, 0.12f);
            if (!Enabled) fill = Theme.Surface;
            e.Graphics.Clear(BackColor);
            using (var brush = new SolidBrush(fill)) e.Graphics.FillRectangle(brush, ClientRectangle);
            using (var pen = new Pen(Active || Focused ? Theme.Accent : Theme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? Theme.Ink : Color.FromArgb(99, 115, 137), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    internal sealed class DarkComboBox : ComboBox
    {
        internal DarkComboBox()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Flat;
            BackColor = Theme.Surface;
            ForeColor = Theme.Ink;
            Dock = DockStyle.Fill;
            IntegralHeight = false;
            DropDownHeight = 300;
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            using (var brush = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Theme.Selection : Theme.Surface)) e.Graphics.FillRectangle(brush, e.Bounds);
            if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, e.Bounds, Theme.Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    internal sealed class SessionGrid : DataGridView
    {
        internal SessionGrid() { DoubleBuffered = true; }
    }

    internal static class WindowTheme
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        internal static void Apply(Form window)
        {
            try { int enabled = 1; DwmSetWindowAttribute(window.Handle, 20, ref enabled, 4); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
    }
}
