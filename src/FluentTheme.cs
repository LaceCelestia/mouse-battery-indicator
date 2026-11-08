using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ViperBattery
{
    // Windows 11 style colors that follow the taskbar light/dark mode and the user's accent color.
    internal sealed class FluentTheme
    {
        internal bool IsLight;
        internal Color Background;
        internal Color Footer;
        internal Color Divider;
        internal Color WindowStroke;
        internal Color Card;
        internal Color CardStroke;
        internal Color TextPrimary;
        internal Color TextSecondary;
        internal Color TextTertiary;
        internal Color TextDisabled;
        internal Color Accent;
        internal Color AccentHover;
        internal Color AccentPressed;
        internal Color AccentDisabled;
        internal Color TextOnAccent;
        internal Color TextOnAccentDisabled;
        internal Color SubtleHover;
        internal Color SubtlePressed;
        internal Color ControlFill;
        internal Color ControlStroke;
        internal Color SegmentSelected;
        internal Color Battery;
        internal Color Low;
        internal Color Charging;
        internal Color GridLine;
        internal Color AxisLine;
        internal Color Tooltip;
        internal Color TooltipStroke;

        private static FluentTheme current;
        private static bool? lightOverride;
        private static Color? accentOverride;

        internal static FluentTheme Current
        {
            get
            {
                if (current == null) current = Create(lightOverride ?? IsSystemLight(), accentOverride);
                return current;
            }
        }

        // Re-read the Windows mode and accent color on the next access.
        internal static void Refresh() { current = null; }

        // Tests render both modes deterministically, independently of this PC's settings.
        internal static void Override(bool? light, Color? accent)
        {
            lightOverride = light;
            accentOverride = accent;
            current = null;
        }

        internal static bool IsSystemLight()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key != null && Convert.ToInt32(key.GetValue("SystemUsesLightTheme", 0)) == 1;
            }
            catch { }
            return false;
        }

        internal static FluentTheme Create(bool light, Color? accentOverride)
        {
            FluentTheme t = new FluentTheme();
            t.IsLight = light;
            if (light)
            {
                t.Background = Color.FromArgb(249, 249, 249);
                t.Footer = Color.FromArgb(240, 240, 240);
                t.Divider = Color.FromArgb(229, 229, 229);
                t.WindowStroke = Color.FromArgb(214, 214, 214);
                t.Card = Color.FromArgb(255, 255, 255);
                t.CardStroke = Color.FromArgb(232, 232, 232);
                t.TextPrimary = Color.FromArgb(27, 27, 27);
                t.TextSecondary = Color.FromArgb(96, 96, 96);
                t.TextTertiary = Color.FromArgb(128, 128, 128);
                t.TextDisabled = Color.FromArgb(160, 160, 160);
                t.SubtleHover = Color.FromArgb(237, 237, 237);
                t.SubtlePressed = Color.FromArgb(242, 242, 242);
                t.ControlFill = Color.FromArgb(240, 240, 240);
                t.ControlStroke = Color.FromArgb(229, 229, 229);
                t.SegmentSelected = Color.FromArgb(255, 255, 255);
                t.Battery = Color.FromArgb(16, 137, 62);
                t.Low = Color.FromArgb(192, 79, 0);
                t.Charging = Color.FromArgb(16, 124, 16);
                t.GridLine = Color.FromArgb(238, 238, 238);
                t.AxisLine = Color.FromArgb(214, 214, 214);
                t.Tooltip = Color.FromArgb(255, 255, 255);
                t.TooltipStroke = Color.FromArgb(214, 214, 214);
                t.AccentDisabled = Color.FromArgb(200, 200, 200);
                t.TextOnAccentDisabled = Color.FromArgb(255, 255, 255);
            }
            else
            {
                t.Background = Color.FromArgb(43, 43, 43);
                t.Footer = Color.FromArgb(32, 32, 32);
                t.Divider = Color.FromArgb(29, 29, 29);
                t.WindowStroke = Color.FromArgb(70, 70, 70);
                t.Card = Color.FromArgb(50, 50, 50);
                t.CardStroke = Color.FromArgb(36, 36, 36);
                t.TextPrimary = Color.FromArgb(255, 255, 255);
                t.TextSecondary = Color.FromArgb(200, 200, 200);
                t.TextTertiary = Color.FromArgb(158, 158, 158);
                t.TextDisabled = Color.FromArgb(116, 116, 116);
                t.SubtleHover = Color.FromArgb(56, 56, 56);
                t.SubtlePressed = Color.FromArgb(51, 51, 51);
                t.ControlFill = Color.FromArgb(38, 38, 38);
                t.ControlStroke = Color.FromArgb(30, 30, 30);
                t.SegmentSelected = Color.FromArgb(66, 66, 66);
                t.Battery = Color.FromArgb(76, 227, 138);
                t.Low = Color.FromArgb(255, 159, 87);
                t.Charging = Color.FromArgb(108, 203, 95);
                t.GridLine = Color.FromArgb(60, 60, 60);
                t.AxisLine = Color.FromArgb(82, 82, 82);
                t.Tooltip = Color.FromArgb(44, 44, 44);
                t.TooltipStroke = Color.FromArgb(78, 78, 78);
                t.AccentDisabled = Color.FromArgb(70, 70, 70);
                t.TextOnAccentDisabled = Color.FromArgb(150, 150, 150);
            }
            t.Accent = accentOverride ?? ReadAccent(light);
            t.AccentHover = Blend(t.Accent, t.Footer, 0.9F);
            t.AccentPressed = Blend(t.Accent, t.Footer, 0.8F);
            t.TextOnAccent = Contrast(t.Accent, Color.White) >= Contrast(t.Accent, Color.Black) ? Color.White : Color.Black;
            return t;
        }

        // Windows 11 uses a darker accent step on light surfaces and a lighter one on dark surfaces.
        private static Color ReadAccent(bool light)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    byte[] palette = key == null ? null : key.GetValue("AccentPalette") as byte[];
                    if (palette != null && palette.Length >= 32)
                    {
                        int index = light ? 4 : 1;
                        return Color.FromArgb(palette[index * 4], palette[index * 4 + 1], palette[index * 4 + 2]);
                    }
                }
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    object value = key == null ? null : key.GetValue("AccentColor");
                    if (value is int)
                    {
                        int abgr = (int)value;
                        Color accent = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
                        return light ? Blend(Color.Black, accent, 0.15F) : Blend(Color.White, accent, 0.35F);
                    }
                }
            }
            catch { }
            return light ? Color.FromArgb(0, 95, 184) : Color.FromArgb(96, 205, 255);
        }

        internal static Color Blend(Color top, Color bottom, float opacity)
        {
            return Color.FromArgb(
                (int)Math.Round(top.R * opacity + bottom.R * (1 - opacity)),
                (int)Math.Round(top.G * opacity + bottom.G * (1 - opacity)),
                (int)Math.Round(top.B * opacity + bottom.B * (1 - opacity)));
        }

        internal static Color WithAlpha(Color color, int alpha)
        {
            return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), color.R, color.G, color.B);
        }

        private static double Luminance(Color color)
        {
            Func<int, double> channel = delegate(int value)
            {
                double c = value / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            };
            return 0.2126 * channel(color.R) + 0.7152 * channel(color.G) + 0.0722 * channel(color.B);
        }

        internal static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }
    }

    internal static class FluentFonts
    {
        private static HashSet<string> installed;

        internal static bool Has(string family)
        {
            if (installed == null)
            {
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using (InstalledFontCollection fonts = new InstalledFontCollection())
                        foreach (FontFamily font in fonts.Families) names.Add(font.Name);
                }
                catch { }
                installed = names;
            }
            return installed.Contains(family);
        }

        internal static string First(params string[] families)
        {
            foreach (string family in families) if (Has(family)) return family;
            return families[families.Length - 1];
        }

        // Japanese UI text. Segoe UI links to Meiryo UI first, so name the Windows 10/11 Japanese UI face.
        internal static string Text { get { return First("Yu Gothic UI", "Segoe UI"); } }
        internal static string TextSemibold { get { return First("Yu Gothic UI Semibold", "Segoe UI Semibold", "Segoe UI"); } }
        internal static string Title { get { return First("Segoe UI Semibold", "Segoe UI"); } }
        internal static string Display { get { return First("Segoe UI Variable Display Semib", "Segoe UI Semibold", "Segoe UI"); } }
        internal static string Icons { get { return Has("Segoe Fluent Icons") ? "Segoe Fluent Icons" : (Has("Segoe MDL2 Assets") ? "Segoe MDL2 Assets" : null); } }
    }

    internal static class FluentGlyphs
    {
        internal const string Refresh = "";
        internal const string Settings = "";
        internal const string Close = "";
        internal const string CheckMark = "";
        internal const string ChevronRight = "";
        internal const string Bolt = "";
        internal const string Warning = "";
    }

    internal static class FluentWindow
    {
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwaBorderColor = 34;
        private const int DwmwcpRound = 2;
        private const int DwmwcpRoundSmall = 3;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // Windows 11 rounds the window and draws a matching shadow; Windows 10 rejects the attribute.
        internal static bool TryRound(IntPtr handle, bool small)
        {
            if (handle == IntPtr.Zero) return false;
            try
            {
                int preference = small ? DwmwcpRoundSmall : DwmwcpRound;
                return DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, 4) == 0;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return false;
        }

        internal static void TrySetBorder(IntPtr handle, Color color)
        {
            if (handle == IntPtr.Zero) return;
            try
            {
                int colorRef = color.R | (color.G << 8) | (color.B << 16);
                DwmSetWindowAttribute(handle, DwmwaBorderColor, ref colorRef, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
    }

    internal static class FluentDraw
    {
        internal static GraphicsPath RoundedRect(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            if (d <= 0.5F)
            {
                path.AddRectangle(bounds);
                return path;
            }
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        internal static void FillRounded(Graphics graphics, Color color, RectangleF bounds, float radius)
        {
            using (GraphicsPath path = RoundedRect(bounds, radius))
            using (Brush brush = new SolidBrush(color))
                graphics.FillPath(brush, path);
        }

        internal static void StrokeRounded(Graphics graphics, Color color, float width, RectangleF bounds, float radius)
        {
            using (GraphicsPath path = RoundedRect(bounds, radius))
            using (Pen pen = new Pen(color, width))
                graphics.DrawPath(pen, path);
        }

        internal static void Text(Graphics graphics, string text, Font font, Color color, Rectangle bounds, TextFormatFlags flags)
        {
            if (String.IsNullOrEmpty(text)) return;
            TextRenderer.DrawText(graphics, text, font, bounds, color,
                flags | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }

        // Icons use grayscale anti-aliasing: ClearType tints thin icon strokes with color fringes.
        internal static void Glyph(Graphics graphics, string glyph, Font font, Color color, Rectangle bounds)
        {
            if (font == null) return;
            TextRenderingHint previous = graphics.TextRenderingHint;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (Brush brush = new SolidBrush(color))
            using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                graphics.DrawString(glyph, font, brush, bounds, format);
            }
            graphics.TextRenderingHint = previous;
        }
    }

    internal enum FlyoutButtonKind { Accent, Subtle, Segment }

    // A Windows 11 style button that keeps WinForms keyboard and accessibility behavior.
    internal sealed class FlyoutButton : Button
    {
        private bool hover;
        private bool pressed;
        private bool selected;
        private Font glyphFont;

        internal FlyoutButton(FlyoutButtonKind kind)
        {
            Kind = kind;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            UseMnemonic = false;
            AccessibleRole = AccessibleRole.PushButton;
            UiScale = 1F;
        }

        internal FlyoutButtonKind Kind { get; private set; }
        internal string Glyph { get; set; }
        internal bool ShowText { get; set; }
        internal float UiScale { get; set; }
        internal Color Surface { get; set; }

        internal bool Selected
        {
            get { return selected; }
            set
            {
                if (selected == value) return;
                selected = value;
                AccessibleDescription = value ? "選択中" : null;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            FluentTheme theme = FluentTheme.Current;
            Graphics g = e.Graphics;
            g.Clear(Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF box = new RectangleF(0, 0, Width, Height);
            float radius = 4F * UiScale;
            Color fill = Color.Empty, ink;
            Font textFont = Font;
            switch (Kind)
            {
                case FlyoutButtonKind.Accent:
                    fill = !Enabled ? theme.AccentDisabled : (pressed ? theme.AccentPressed : (hover ? theme.AccentHover : theme.Accent));
                    ink = Enabled ? theme.TextOnAccent : theme.TextOnAccentDisabled;
                    break;
                case FlyoutButtonKind.Segment:
                    if (selected) fill = theme.SegmentSelected;
                    else if (pressed) fill = theme.SubtlePressed;
                    else if (hover) fill = theme.SubtleHover;
                    ink = selected ? theme.TextPrimary : theme.TextSecondary;
                    radius = 3F * UiScale;
                    break;
                default:
                    if (pressed) fill = theme.SubtlePressed;
                    else if (hover) fill = theme.SubtleHover;
                    ink = Enabled ? theme.TextPrimary : theme.TextDisabled;
                    break;
            }
            if (!fill.IsEmpty) FluentDraw.FillRounded(g, fill, box, radius);
            if (Kind == FlyoutButtonKind.Segment && selected)
                FluentDraw.StrokeRounded(g, theme.IsLight ? theme.ControlStroke : FluentTheme.Blend(Color.White, theme.SegmentSelected, 0.06F), 1F, new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F), radius);

            Font glyph = GlyphFont();
            bool hasGlyph = glyph != null && !String.IsNullOrEmpty(Glyph);
            bool drawText = ShowText || !hasGlyph;
            Size textSize = drawText ? TextRenderer.MeasureText(g, Text, textFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix) : Size.Empty;
            int glyphSize = hasGlyph ? (int)Math.Round(16 * UiScale) : 0;
            int gap = hasGlyph && drawText ? (int)Math.Round(8 * UiScale) : 0;
            int contentWidth = glyphSize + gap + textSize.Width;
            int left = (Width - contentWidth) / 2;
            if (hasGlyph)
                FluentDraw.Glyph(g, Glyph, glyph, ink, new Rectangle(left, (Height - glyphSize) / 2, glyphSize, glyphSize));
            if (drawText)
                FluentDraw.Text(g, Text, textFont, ink, new Rectangle(left + glyphSize + gap, 0, Width - (left + glyphSize + gap), Height), TextFormatFlags.VerticalCenter);

            if (Focused && ShowFocusCues)
                FluentDraw.StrokeRounded(g, theme.TextPrimary, 2F * UiScale, new RectangleF(UiScale, UiScale, Width - 2F * UiScale, Height - 2F * UiScale), radius);
        }

        private Font GlyphFont()
        {
            if (glyphFont == null && FluentFonts.Icons != null && !String.IsNullOrEmpty(Glyph))
                glyphFont = new Font(FluentFonts.Icons, 14F * UiScale, FontStyle.Regular, GraphicsUnit.Pixel);
            return glyphFont;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && glyphFont != null)
            {
                glyphFont.Dispose();
                glyphFont = null;
            }
            base.Dispose(disposing);
        }
    }
}
