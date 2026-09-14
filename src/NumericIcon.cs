using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

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
            if (!percent.HasValue) color = Color.FromArgb(150, 157, 166);
            else if (percent <= 20 && !charging) color = Color.FromArgb(255, 144, 98);
            using (Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                string label = percent.HasValue ? percent.Value.ToString() : "--";
                string family = weight == "normal" ? "Segoe UI Semibold" : "Segoe UI";
                FontStyle style = weight == "bold" ? FontStyle.Bold : FontStyle.Regular;
                using (Font font = new Font(family, 15, style, GraphicsUnit.Pixel))
                using (GraphicsPath path = new GraphicsPath())
                using (Brush brush = new SolidBrush(color))
                {
                    path.AddString(label, font.FontFamily, (int)style, 15F, Point.Empty, StringFormat.GenericTypographic);
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
                    Color markerColor = useLightTheme ? Color.FromArgb(15, 132, 67) : Color.FromArgb(101, 224, 149);
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

        private static Color Foreground(string preference, bool lightTaskbar)
        {
            if (preference == "green") return Color.FromArgb(176, 242, 98);
            if (preference == "white") return Color.White;
            if (preference == "black") return Color.FromArgb(24, 28, 32);
            return lightTaskbar ? Color.FromArgb(24, 28, 32) : Color.White;
        }

        private static bool IsLightTaskbar()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key != null && Convert.ToInt32(key.GetValue("SystemUsesLightTheme", 0)) == 1;
            }
            catch { }
            return false;
        }
    }
}
