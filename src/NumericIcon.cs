using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ViperBattery
{
    internal static class NumericIcon
    {
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

        internal static Icon Create(int? percent, string preference, bool charging, string weight = "normal")
        {
            int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            return CreateAtSize(percent, preference, charging, weight, size);
        }

        internal static Icon CreateAtSize(int? percent, string preference, bool charging, string weight, int size, bool? lightTaskbar = null)
        {
            bool showCharging = charging && percent.HasValue;
            bool useLightTheme = lightTaskbar ?? IsLightTaskbar();
            float markerHeight = Math.Max(1F, (float)Math.Round(size * 0.05F));
            // Preserve the established glyph size, independently of the charging marker's height.
            float digitHeight = size - Math.Max(3F, (float)Math.Round(size * 0.15F))
                - Math.Max(1F, (float)Math.Round(size * 0.05F)) - 1F;
            Color color = Foreground(preference, useLightTheme);
            if (!percent.HasValue) color = useLightTheme ? Color.FromArgb(118, 118, 118) : Color.FromArgb(150, 150, 150);
            // Orange tuned per taskbar so the low-battery state keeps its contrast on light taskbars too.
            else if (percent <= 20 && !charging) color = useLightTheme ? Color.FromArgb(192, 79, 0) : Color.FromArgb(255, 159, 87);
            using (Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                string label = percent.HasValue ? percent.Value.ToString() : "--";
                FontStyle style;
                string family = DigitFamily(weight, out style);
                using (FontFamily font = new FontFamily(family))
                using (GraphicsPath path = new GraphicsPath())
                using (Brush brush = new SolidBrush(color))
                {
                    path.AddString(label, font, (int)style, 15F, Point.Empty, StringFormat.GenericTypographic);
                    RectangleF bounds = path.GetBounds();
                    float availableWidth = size - 1F;
                    float availableHeight = digitHeight;
                    float scaleX = availableWidth / bounds.Width;
                    float scaleY = availableHeight / bounds.Height;
                    // Fill the icon height; condensed digits keep both 17 and 100 legible at tray size.
                    float scale = Math.Min(scaleX, scaleY);
                    float sx = scale;
                    float sy = percent.HasValue ? scaleY : scale;
                    using (Matrix transform = new Matrix())
                    {
                        transform.Translate(-bounds.X, -bounds.Y, MatrixOrder.Append);
                        transform.Scale(sx, sy, MatrixOrder.Append);
                        float top = (size - bounds.Height * sy) / 2;
                        transform.Translate((size - bounds.Width * sx) / 2, top, MatrixOrder.Append);
                        path.Transform(transform);
                    }
                    graphics.FillPath(brush, path);
                }
                if (showCharging)
                {
                    int width = Math.Max(2, (int)Math.Round(size * 0.45F) * 2);
                    int left = (size - width) / 2;
                    int height = (int)markerHeight;
                    int top = size - height;
                    Color markerColor = useLightTheme ? Color.FromArgb(16, 124, 16) : Color.FromArgb(108, 203, 95);
                    using (Brush brush = new SolidBrush(markerColor))
                    {
                        graphics.SmoothingMode = SmoothingMode.None;
                        graphics.FillRectangle(brush, left, top, width, height);
                    }
                }
                IntPtr handle = bitmap.GetHicon();
                try { using (Icon borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        // Bahnschrift SemiCondensed (Windows 10 1709+) keeps "100" legible at 16px without distorting the digits.
        internal static string DigitFamily(string weight, out FontStyle style)
        {
            style = FontStyle.Regular;
            if (FluentFonts.Has("Bahnschrift SemiCondensed"))
            {
                if (weight == "bold") { style = FontStyle.Bold; return "Bahnschrift SemiCondensed"; }
                if (weight == "light") return "Bahnschrift SemiCondensed";
                if (FluentFonts.Has("Bahnschrift SemiBold SemiConden")) return "Bahnschrift SemiBold SemiConden";
                return "Bahnschrift SemiCondensed";
            }
            if (weight == "bold") style = FontStyle.Bold;
            return weight == "normal" ? FluentFonts.First("Segoe UI Semibold", "Segoe UI") : FluentFonts.First("Segoe UI", "Arial");
        }

        private static Color Foreground(string preference, bool lightTaskbar)
        {
            if (preference == "green") return lightTaskbar ? Color.FromArgb(16, 137, 62) : Color.FromArgb(76, 227, 138);
            if (preference == "white") return Color.White;
            if (preference == "black") return Color.FromArgb(27, 27, 27);
            return lightTaskbar ? Color.FromArgb(27, 27, 27) : Color.White;
        }

        private static bool IsLightTaskbar()
        {
            return FluentTheme.IsSystemLight();
        }
    }
}
