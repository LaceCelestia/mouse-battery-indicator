using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ViperBattery
{
    // Windows 11 style context menu: rounded window, theme colors, and rounded selection highlights.
    internal static class FluentMenu
    {
        private static readonly FluentMenuRenderer SharedRenderer = new FluentMenuRenderer();

        internal static void Apply(ToolStripDropDown dropDown)
        {
            if (dropDown.Renderer == SharedRenderer) return;
            dropDown.Renderer = SharedRenderer;
            dropDown.Padding = new Padding(0, Px(dropDown, 4), 0, Px(dropDown, 4));
            dropDown.HandleCreated += delegate { Frame(dropDown); };
            dropDown.Opening += delegate { Frame(dropDown); };
            if (dropDown.IsHandleCreated) Frame(dropDown);
            foreach (ToolStripItem item in dropDown.Items) Style(item);
            dropDown.ItemAdded += delegate(object sender, ToolStripItemEventArgs e) { Style(e.Item); };
        }

        private static void Style(ToolStripItem item)
        {
            ToolStrip owner = item.Owner;
            if (item is ToolStripSeparator)
            {
                item.AutoSize = false;
                item.Height = Px(owner, 9);
                return;
            }
            item.Padding = new Padding(Px(owner, 4), Px(owner, 5), Px(owner, 8), Px(owner, 5));
            ToolStripMenuItem menuItem = item as ToolStripMenuItem;
            // Submenus that are filled later (the mouse list) are applied explicitly by the caller.
            if (menuItem != null && menuItem.HasDropDownItems) Apply(menuItem.DropDown);
        }

        private static void Frame(ToolStripDropDown dropDown)
        {
            if (!dropDown.IsHandleCreated) return;
            if (FluentWindow.TryRound(dropDown.Handle, false))
            {
                SharedRenderer.RoundedByDwm = true;
                FluentWindow.TrySetBorder(dropDown.Handle, FluentTheme.Current.WindowStroke);
            }
        }

        internal static int Px(Control control, float value)
        {
            float dpi = 96F;
            try
            {
                using (Graphics graphics = control != null && control.IsHandleCreated ? control.CreateGraphics() : Graphics.FromHwnd(IntPtr.Zero))
                    dpi = graphics.DpiX;
            }
            catch { }
            return (int)Math.Round(value * dpi / 96F);
        }
    }

    internal sealed class FluentMenuRenderer : ToolStripProfessionalRenderer
    {
        private Font glyphFont;
        private float glyphScale;

        internal bool RoundedByDwm { get; set; }

        internal FluentMenuRenderer()
        {
            RoundedEdges = false;
        }

        private static float Scale(Graphics graphics)
        {
            return graphics.DpiX / 96F;
        }

        private Font GlyphFont(float scale)
        {
            if (FluentFonts.Icons == null) return null;
            if (glyphFont == null || Math.Abs(glyphScale - scale) > 0.01F)
            {
                if (glyphFont != null) glyphFont.Dispose();
                glyphFont = new Font(FluentFonts.Icons, 11F * scale, FontStyle.Regular, GraphicsUnit.Pixel);
                glyphScale = scale;
            }
            return glyphFont;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (Brush brush = new SolidBrush(FluentTheme.Current.Background))
                e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // Windows 11 draws the rounded border itself; Windows 10 gets a 1px stroke.
            if (RoundedByDwm) return;
            using (Pen pen = new Pen(FluentTheme.Current.WindowStroke))
                e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            ToolStripMenuItem item = e.Item as ToolStripMenuItem;
            bool open = item != null && item.DropDown.Visible;
            if (!e.Item.Enabled || !(e.Item.Selected || open)) return;
            float scale = Scale(e.Graphics);
            RectangleF box = new RectangleF(4 * scale, scale, e.Item.Width - 8 * scale, e.Item.Height - 2 * scale);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            FluentDraw.FillRounded(e.Graphics, FluentTheme.Current.SubtleHover, box, 4 * scale);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            FluentTheme theme = FluentTheme.Current;
            e.TextColor = e.Item.Enabled ? theme.TextPrimary : theme.TextDisabled;
            // Dropdown items place text near the top once vertical padding is added; center it on the row.
            if (e.ToolStrip is ToolStripDropDown)
            {
                Rectangle text = e.TextRectangle;
                e.TextRectangle = new Rectangle(text.X, (e.Item.Height - text.Height) / 2, text.Width, text.Height);
            }
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            FluentTheme theme = FluentTheme.Current;
            Font font = GlyphFont(Scale(e.Graphics));
            Rectangle box = new Rectangle(e.ImageRectangle.X, (e.Item.Height - e.ImageRectangle.Height) / 2, e.ImageRectangle.Width, e.ImageRectangle.Height);
            Color color = e.Item.Enabled ? theme.TextPrimary : theme.TextDisabled;
            if (font != null) FluentDraw.Glyph(e.Graphics, FluentGlyphs.CheckMark, font, color, box);
            else TextRenderer.DrawText(e.Graphics, "✓", e.Item.Font, box, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            FluentTheme theme = FluentTheme.Current;
            Font font = GlyphFont(Scale(e.Graphics));
            Color color = e.Item != null && !e.Item.Enabled ? theme.TextDisabled : theme.TextSecondary;
            if (font == null)
            {
                e.ArrowColor = color;
                base.OnRenderArrow(e);
                return;
            }
            // Draw the chevron as strokes centered on the row; the icon font's line metrics sit it too low.
            float scale = Scale(e.Graphics);
            float cx = e.ArrowRectangle.X + e.ArrowRectangle.Width / 2F;
            float cy = e.Item != null ? e.Item.Height / 2F : e.ArrowRectangle.Y + e.ArrowRectangle.Height / 2F;
            float half = 3.5F * scale;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(color, Math.Max(1F, scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                e.Graphics.DrawLines(pen, new[] { new PointF(cx - half / 2, cy - half), new PointF(cx + half / 2, cy), new PointF(cx - half / 2, cy + half) });
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            float scale = Scale(e.Graphics);
            int y = e.Item.Height / 2;
            using (Pen pen = new Pen(FluentTheme.Current.IsLight ? FluentTheme.Current.Divider : FluentTheme.Current.WindowStroke))
                e.Graphics.DrawLine(pen, (int)(4 * scale), y, e.Item.Width - (int)(4 * scale), y);
        }
    }
}
