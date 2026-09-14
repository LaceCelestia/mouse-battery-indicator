using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace ViperBattery
{
    internal sealed class BatteryPopup : Form
    {
        private static readonly Color Background = Color.FromArgb(20, 23, 27);
        private static readonly Color Surface = Color.FromArgb(31, 35, 40);
        private static readonly Color Muted = Color.FromArgb(143, 153, 165);
        private static readonly Color Accent = Color.FromArgb(183, 243, 107);
        private readonly Button refreshButton;
        private readonly Button settingsButton;
        private readonly Button closeButton;
        private BatteryReading reading;
        private bool refreshing;
        private int pollSeconds = 60;
        private float scale = 1F;

        internal BatteryPopup(Action refresh, Action settings)
        {
            Text = Program.AppName;
            AccessibleName = "マウスのバッテリー残量";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Background;
            ForeColor = Color.White;
            Font = new Font("Yu Gothic UI", 9F);
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            KeyPreview = true;
            using (Graphics graphics = CreateGraphics()) scale = graphics.DpiX / 96F;
            ClientSize = new Size(S(352), S(300));
            refreshButton = MakeButton("今すぐ更新", new Rectangle(24, 242, 214, 36), true);
            refreshButton.Click += delegate { refresh(); };
            settingsButton = MakeButton("設定", new Rectangle(246, 242, 82, 36), false);
            settingsButton.Click += delegate { settings(); };
            closeButton = MakeButton("×", new Rectangle(305, 14, 28, 28), false);
            closeButton.AccessibleName = "閉じる（通知領域に常駐）";
            closeButton.BackColor = Background;
            closeButton.Click += delegate { Hide(); };
            Deactivate += delegate { Hide(); };
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Hide(); };
        }

        private int S(float value) { return (int)Math.Round(value * scale); }

        private Button MakeButton(string text, Rectangle bounds, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.AccessibleRole = AccessibleRole.PushButton;
            button.SetBounds(S(bounds.X), S(bounds.Y), S(bounds.Width), S(bounds.Height));
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(203, 255, 143) : Color.FromArgb(47, 54, 62);
            button.BackColor = primary ? Accent : Surface;
            button.ForeColor = primary ? Background : Color.FromArgb(225, 231, 238);
            button.Font = new Font("Yu Gothic UI", 12F * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            Controls.Add(button);
            return button;
        }

        internal void UpdateReading(BatteryReading value, bool busy, int interval)
        {
            reading = value;
            refreshing = busy;
            pollSeconds = interval;
            refreshButton.Enabled = !busy;
            refreshButton.Text = busy ? "更新中…" : "今すぐ更新";
            AccessibleDescription = "バッテリー残量 " + MouseBatteryService.LevelText(value)
                + (value != null && value.Connected && value.IsCharging == true ? "、充電中" : "");
            Invalidate();
        }

        internal void ShowNearTray()
        {
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(Math.Max(area.Left, area.Right - Width - S(12)), Math.Max(area.Top, area.Bottom - Height - S(12)));
            Show();
            Activate();
        }

        internal void SavePreview(string path)
        {
            IntPtr unused = Handle;
            using (Bitmap bitmap = new Bitmap(Width, Height))
            {
                DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
                foreach (Control child in Controls) child.DrawToBitmap(bitmap, child.Bounds);
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnFormClosing(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.ScaleTransform(scale, scale);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (Pen border = new Pen(Color.FromArgb(54, 61, 68))) g.DrawRectangle(border, 0, 0, 351, 299);

            bool connected = reading != null && reading.Connected;
            bool valid = connected && reading.Percentage.HasValue;
            bool charging = connected && reading.IsCharging == true;
            bool low = valid && reading.Percentage <= 20 && !charging;
            Color accent = low ? Color.FromArgb(255, 163, 110) : Accent;
            string device = reading != null && !String.IsNullOrWhiteSpace(reading.DeviceName) ? reading.DeviceName : "マウス";
            Label(g, device, "Segoe UI", 12, FontStyle.Bold, Color.FromArgb(225, 231, 238), new RectangleF(24, 20, 276, 24));
            using (Brush dot = new SolidBrush(connected ? accent : Muted)) g.FillEllipse(dot, 25, 55, 6, 6);
            string state = connected ? (charging ? "充電中" : (String.IsNullOrEmpty(reading.Connection) ? "接続中" : reading.Connection)) : (reading == null ? "確認中…" : "残量を取得できません");
            if (connected && reading.IsApproximate) state += " · 推定値";
            Label(g, state, "Yu Gothic UI", 9, FontStyle.Regular, connected ? accent : Muted, new RectangleF(39, 47, 280, 24));

            if (!valid && connected && !String.IsNullOrEmpty(reading.BatteryLevelText))
                Label(g, reading.BatteryLevelText, "Yu Gothic UI", 23, FontStyle.Bold, Color.FromArgb(244, 247, 249), new RectangleF(24, 111, 304, 70));
            else
                Label(g, valid ? reading.Percentage.Value.ToString() : "--", "Segoe UI", 76, FontStyle.Bold, Color.FromArgb(244, 247, 249), new RectangleF(17, 72, 235, 112));
            if (valid) Label(g, "%", "Segoe UI", 27, FontStyle.Regular, Muted, new RectangleF(254, 134, 64, 46));

            using (Brush track = new SolidBrush(Color.FromArgb(45, 52, 59))) g.FillRectangle(track, 24, 194, 304, 5);
            if (valid && reading.Percentage.Value > 0)
                using (Brush fill = new SolidBrush(accent)) g.FillRectangle(fill, 24, 194, 304F * reading.Percentage.Value / 100F, 5);

            string timestamp = reading == null ? "" : "更新 " + reading.Timestamp.ToLocalTime().ToString("HH:mm:ss") + "  ·  " + pollSeconds + " 秒間隔";
            Label(g, timestamp, "Yu Gothic UI", 8, FontStyle.Regular, Muted, new RectangleF(24, 210, 310, 22));
        }

        private static void Label(Graphics graphics, string text, string family, float size, FontStyle style, Color color, RectangleF bounds)
        {
            using (Font font = new Font(family, size * 96F / 72F, style, GraphicsUnit.Pixel))
            using (Brush brush = new SolidBrush(color))
            using (StringFormat format = new StringFormat(StringFormat.GenericDefault))
            {
                format.FormatFlags = StringFormatFlags.NoWrap;
                format.Trimming = StringTrimming.EllipsisCharacter;
                graphics.DrawString(text, font, brush, bounds, format);
            }
        }
    }
}
