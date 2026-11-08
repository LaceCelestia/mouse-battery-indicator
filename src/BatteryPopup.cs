using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ViperBattery
{
    internal sealed class BatteryPopup : Form
    {
        // Layout in 96 DPI pixels; S() scales to the system DPI.
        private const int LogicalWidth = 376;
        private const int LogicalHeight = 412;
        private const int Pad = 20;
        private const int FooterTop = 348;
        private static readonly Rectangle CardBounds = new Rectangle(16, 140, LogicalWidth - 32, 168);
        private static readonly Rectangle SegmentBounds = new Rectangle(LogicalWidth - 28 - 108, 148, 108, 28);
        private static readonly Rectangle PlotBounds = new Rectangle(66, 192, LogicalWidth - 66 - 32, 82);
        private static readonly TimeSpan Day = TimeSpan.FromDays(1);
        private static readonly TimeSpan Week = TimeSpan.FromDays(7);

        private readonly FlyoutButton refreshButton;
        private readonly FlyoutButton settingsButton;
        private readonly FlyoutButton closeButton;
        private readonly FlyoutButton dayButton;
        private readonly FlyoutButton weekButton;
        private readonly float scale = 1F;
        private Font nameFont;
        private Font nameCjkFont;
        private Font textFont;
        private Font smallFont;
        private Font titleFont;
        private Font heroTextFont;
        private Font statusGlyphFont;
        private FontFamily displayFamily;
        private BatteryReading reading;
        private bool refreshing;
        private int pollSeconds = 60;
        private TimeSpan range = Day;
        private int? hoverX;
        private bool roundedByDwm;
        private int autoHiddenTick;

        internal BatteryPopup(Action refresh, Action settings)
        {
            Text = Program.AppName;
            AccessibleName = "マウスのバッテリー残量";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            KeyPreview = true;
            Clock = delegate { return DateTime.UtcNow; };
            using (Graphics graphics = CreateGraphics()) scale = graphics.DpiX / 96F;
            CreateFonts();
            ClientSize = new Size(S(LogicalWidth), S(LogicalHeight));

            dayButton = MakeButton(FlyoutButtonKind.Segment, "24時間", null, new Rectangle(SegmentBounds.X + 2, SegmentBounds.Y + 2, 52, 24));
            dayButton.AccessibleName = "24時間の推移を表示";
            dayButton.Click += delegate { SetRange(Day); };
            weekButton = MakeButton(FlyoutButtonKind.Segment, "7日間", null, new Rectangle(SegmentBounds.X + 54, SegmentBounds.Y + 2, 52, 24));
            weekButton.AccessibleName = "7日間の推移を表示";
            weekButton.Click += delegate { SetRange(Week); };
            dayButton.Selected = true;

            refreshButton = MakeButton(FlyoutButtonKind.Accent, "今すぐ更新", FluentGlyphs.Refresh, new Rectangle(Pad, FooterTop + 16, 148, 32));
            refreshButton.ShowText = true;
            refreshButton.Click += delegate { refresh(); };
            settingsButton = MakeButton(FlyoutButtonKind.Subtle, "設定", FluentGlyphs.Settings, new Rectangle(LogicalWidth - Pad - 76, FooterTop + 16, 36, 32));
            settingsButton.AccessibleName = "設定メニュー";
            settingsButton.Click += delegate { settings(); };
            closeButton = MakeButton(FlyoutButtonKind.Subtle, "閉じる", FluentGlyphs.Close, new Rectangle(LogicalWidth - Pad - 36, FooterTop + 16, 36, 32));
            closeButton.AccessibleName = "閉じる（通知領域に常駐）";
            closeButton.Click += delegate { Hide(); };
            ToolTip tips = new ToolTip();
            tips.SetToolTip(settingsButton, "設定");
            tips.SetToolTip(closeButton, "閉じる");
            Disposed += delegate { tips.Dispose(); };

            ApplyTheme();
            Deactivate += delegate
            {
                // Clicking the tray icon deactivates the popup before the click arrives; remember it for toggling.
                if (Visible) autoHiddenTick = Environment.TickCount;
                Hide();
            };
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Hide(); };
        }

        internal BatteryHistory History { get; set; }
        internal Func<DateTime> Clock { get; set; }
        internal bool WeekRange { get { return range == Week; } }

        protected override CreateParams CreateParams
        {
            get
            {
                const int CsDropShadow = 0x00020000;
                CreateParams parameters = base.CreateParams;
                parameters.ClassStyle |= CsDropShadow;
                return parameters;
            }
        }

        private int S(float value) { return (int)Math.Round(value * scale); }
        private Rectangle R(int x, int y, int width, int height) { return new Rectangle(S(x), S(y), S(width), S(height)); }
        private Rectangle R(Rectangle logical) { return R(logical.X, logical.Y, logical.Width, logical.Height); }

        private void CreateFonts()
        {
            nameFont = new Font(FluentFonts.Title, 15F * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            nameCjkFont = new Font(FluentFonts.TextSemibold, 15F * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            textFont = new Font(FluentFonts.Text, 12F * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            smallFont = new Font(FluentFonts.Text, 11F * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            titleFont = new Font(FluentFonts.TextSemibold, 13F * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            heroTextFont = new Font(FluentFonts.TextSemibold, 24F * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            if (FluentFonts.Icons != null) statusGlyphFont = new Font(FluentFonts.Icons, 12F * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            try { displayFamily = new FontFamily(FluentFonts.Display); }
            catch (ArgumentException) { displayFamily = new FontFamily(GenericFontFamilies.SansSerif); }
            Font = new Font(FluentFonts.Text, 13F * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private FlyoutButton MakeButton(FlyoutButtonKind kind, string text, string glyph, Rectangle bounds)
        {
            FlyoutButton button = new FlyoutButton(kind);
            button.Text = text;
            button.Glyph = glyph;
            button.UiScale = scale;
            button.Font = kind == FlyoutButtonKind.Segment ? smallFont : Font;
            button.SetBounds(S(bounds.X), S(bounds.Y), S(bounds.Width), S(bounds.Height));
            Controls.Add(button);
            return button;
        }

        private void SetRange(TimeSpan value)
        {
            range = value;
            dayButton.Selected = value == Day;
            weekButton.Selected = value == Week;
            hoverX = null;
            UpdateAccessibleText();
            Invalidate();
        }

        internal void ShowWeek(bool week) { SetRange(week ? Week : Day); }

        // True right after a click outside (such as on the tray icon) closed the popup.
        internal bool JustAutoHidden { get { return autoHiddenTick != 0 && unchecked(Environment.TickCount - autoHiddenTick) < 500; } }

        // Renders the hover tooltip at a position across the chart, for previews and tests.
        internal void PreviewHover(double fraction)
        {
            Rectangle plot = R(PlotBounds);
            hoverX = plot.Left + (int)Math.Round(plot.Width * Math.Max(0, Math.Min(1, fraction)));
            Invalidate();
        }

        // Follows the current Windows mode and accent; called on show and when Windows settings change.
        internal void ApplyTheme()
        {
            FluentTheme theme = FluentTheme.Current;
            BackColor = theme.Background;
            ForeColor = theme.TextPrimary;
            refreshButton.Surface = theme.Footer;
            settingsButton.Surface = theme.Footer;
            closeButton.Surface = theme.Footer;
            dayButton.Surface = theme.ControlFill;
            weekButton.Surface = theme.ControlFill;
            if (IsHandleCreated && roundedByDwm) FluentWindow.TrySetBorder(Handle, theme.WindowStroke);
            Invalidate(true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            roundedByDwm = FluentWindow.TryRound(Handle, false);
            if (roundedByDwm) FluentWindow.TrySetBorder(Handle, FluentTheme.Current.WindowStroke);
        }

        internal void UpdateReading(BatteryReading value, bool busy, int interval)
        {
            reading = value;
            refreshing = busy;
            pollSeconds = interval;
            refreshButton.Enabled = !busy;
            refreshButton.Text = busy ? "更新中…" : "今すぐ更新";
            UpdateAccessibleText();
            Invalidate();
        }

        private void UpdateAccessibleText()
        {
            string text = "バッテリー残量 " + MouseBatteryService.LevelText(reading)
                + (reading != null && reading.Connected && reading.IsCharging == true ? "、充電中" : "");
            IList<BatterySample> samples = VisibleSamples();
            if (samples.Count > 1)
                text += "。" + (range == Week ? "過去7日間" : "過去24時間") + "の最高 " + samples.Max(s => s.Percent)
                    + "%、最低 " + samples.Min(s => s.Percent) + "%";
            AccessibleDescription = text;
        }

        internal void ShowNearTray()
        {
            ApplyTheme();
            Screen screen = Screen.FromPoint(Cursor.Position);
            Rectangle area = screen.WorkingArea;
            int margin = S(12);
            // Open next to the taskbar wherever it is docked (other app bars do not count).
            int edge = TaskbarEdge(screen);
            int x = edge == 0 ? area.Left + margin : area.Right - Width - margin;
            int y = edge == 1 ? area.Top + margin : area.Bottom - Height - margin;
            Location = new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
            hoverX = null;
            Show();
            Activate();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct AppBarData
        {
            public int Size;
            public IntPtr Window;
            public uint CallbackMessage;
            public uint Edge;
            public NativeRect Bounds;
            public IntPtr Param;
        }

        [DllImport("shell32.dll")]
        private static extern IntPtr SHAppBarMessage(uint message, ref AppBarData data);

        // 0 = left, 1 = top, 2 = right, 3 = bottom.
        private static int TaskbarEdge(Screen screen)
        {
            try
            {
                AppBarData data = new AppBarData();
                data.Size = Marshal.SizeOf(typeof(AppBarData));
                if (SHAppBarMessage(5 /* ABM_GETTASKBARPOS */, ref data) != IntPtr.Zero)
                {
                    Rectangle bar = Rectangle.FromLTRB(data.Bounds.Left, data.Bounds.Top, data.Bounds.Right, data.Bounds.Bottom);
                    if (data.Edge <= 3 && screen.Bounds.IntersectsWith(bar)) return (int)data.Edge;
                }
            }
            catch { }
            return 3;
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

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Rectangle plot = R(PlotBounds);
            plot.Inflate(S(6), S(10));
            int? next = plot.Contains(e.Location) ? (int?)Math.Max(R(PlotBounds).Left, Math.Min(R(PlotBounds).Right, e.X)) : null;
            if (next != hoverX)
            {
                hoverX = next;
                Invalidate(R(CardBounds));
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hoverX.HasValue)
            {
                hoverX = null;
                Invalidate(R(CardBounds));
            }
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            FluentTheme theme = FluentTheme.Current;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            using (Brush footer = new SolidBrush(theme.Footer)) g.FillRectangle(footer, 0, S(FooterTop), Width, Height - S(FooterTop));
            using (Pen divider = new Pen(theme.Divider)) g.DrawLine(divider, 0, S(FooterTop), Width, S(FooterTop));
            if (!roundedByDwm)
                using (Pen border = new Pen(theme.WindowStroke)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

            DrawHeader(g, theme);
            DrawHero(g, theme);
            DrawChart(g, theme);

            string meta = reading == null ? "" : reading.Timestamp.ToLocalTime().ToString("H:mm:ss", CultureInfo.InvariantCulture) + " に更新 · " + pollSeconds + " 秒ごとに確認";
            FluentDraw.Text(g, meta, textFont, theme.TextTertiary, R(Pad, 318, LogicalWidth - Pad * 2, 20), TextFormatFlags.VerticalCenter);
        }

        private bool Connected { get { return reading != null && reading.Connected; } }
        private bool Valid { get { return Connected && reading.Percentage.HasValue; } }
        private bool Charging { get { return Connected && reading.IsCharging == true; } }
        private bool Low { get { return Valid && reading.Percentage.Value <= 20 && !Charging; } }
        private bool Coarse { get { return Connected && !reading.Percentage.HasValue && !String.IsNullOrEmpty(reading.BatteryLevelText); } }

        private void DrawHeader(Graphics g, FluentTheme theme)
        {
            string device = reading != null && !String.IsNullOrWhiteSpace(reading.DeviceName) ? reading.DeviceName : "マウス";
            // Segoe UI links Japanese to Meiryo UI Regular; names with Japanese use the Japanese semibold face.
            Font font = device.Any(c => c >= '\u3000') ? nameCjkFont : nameFont;
            FluentDraw.Text(g, device, font, theme.TextPrimary, R(Pad, 14, LogicalWidth - Pad * 2, 26), TextFormatFlags.VerticalCenter);

            Rectangle line = R(Pad, 42, LogicalWidth - Pad * 2, 20);
            int x = line.Left;
            if (reading == null)
            {
                Run(g, ref x, line, "確認中…", theme.TextSecondary);
                return;
            }
            if (!Connected)
            {
                Run(g, ref x, line, String.IsNullOrEmpty(reading.Connection) ? "未接続" : reading.Connection, theme.TextSecondary);
                return;
            }
            if (!String.IsNullOrEmpty(reading.Connection)) Run(g, ref x, line, reading.Connection, theme.TextSecondary);
            if (Charging)
            {
                if (x > line.Left) Run(g, ref x, line, "  ·  ", theme.TextTertiary);
                GlyphRun(g, ref x, line, FluentGlyphs.Bolt, theme.Charging);
                Run(g, ref x, line, "充電中", theme.TextSecondary);
            }
            else if (Low)
            {
                if (x > line.Left) Run(g, ref x, line, "  ·  ", theme.TextTertiary);
                GlyphRun(g, ref x, line, FluentGlyphs.Warning, theme.Low);
                Run(g, ref x, line, "残量わずか", theme.TextSecondary);
            }
            else if (x == line.Left) Run(g, ref x, line, "接続中", theme.TextSecondary);
            if (reading.IsApproximate) Run(g, ref x, line, "  ·  推定値", theme.TextTertiary);
        }

        private void Run(Graphics g, ref int x, Rectangle line, string text, Color color)
        {
            if (x >= line.Right) return;
            Size size = TextRenderer.MeasureText(g, text, textFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            FluentDraw.Text(g, text, textFont, color, new Rectangle(x, line.Top, line.Right - x, line.Height), TextFormatFlags.VerticalCenter);
            x += size.Width;
        }

        private void GlyphRun(Graphics g, ref int x, Rectangle line, string glyph, Color color)
        {
            if (statusGlyphFont == null || x >= line.Right) return;
            int size = S(14);
            FluentDraw.Glyph(g, glyph, statusGlyphFont, color, new Rectangle(x, line.Top + (line.Height - size) / 2, size, size));
            x += size + S(4);
        }

        private void DrawHero(Graphics g, FluentTheme theme)
        {
            int top = 74;
            if (Valid)
            {
                using (GraphicsPath digits = new GraphicsPath())
                using (GraphicsPath percent = new GraphicsPath())
                using (Brush primary = new SolidBrush(theme.TextPrimary))
                using (Brush secondary = new SolidBrush(theme.TextSecondary))
                {
                    digits.AddString(reading.Percentage.Value.ToString(CultureInfo.InvariantCulture), displayFamily, (int)FontStyle.Regular, 100F, Point.Empty, StringFormat.GenericTypographic);
                    RectangleF bounds = digits.GetBounds();
                    float height = S(50);
                    float k = height / bounds.Height;
                    Place(digits, bounds, k, S(Pad), S(top));
                    g.FillPath(primary, digits);

                    percent.AddString("%", displayFamily, (int)FontStyle.Regular, 100F, Point.Empty, StringFormat.GenericTypographic);
                    RectangleF percentBounds = percent.GetBounds();
                    float percentHeight = S(22);
                    Place(percent, percentBounds, percentHeight / percentBounds.Height, S(Pad) + bounds.Width * k + S(6), S(top) + height - percentHeight);
                    g.FillPath(secondary, percent);
                }
            }
            else if (Connected && !String.IsNullOrEmpty(reading.BatteryLevelText))
                FluentDraw.Text(g, reading.BatteryLevelText, heroTextFont, theme.TextPrimary, R(Pad, top + 6, 250, 40), TextFormatFlags.VerticalCenter);
            else if (reading == null)
                FluentDraw.Text(g, "確認中…", heroTextFont, theme.TextSecondary, R(Pad, top + 6, 250, 40), TextFormatFlags.VerticalCenter);
            else
            {
                FluentDraw.Text(g, "取得できません", heroTextFont, theme.TextSecondary, R(Pad, top, 250, 36), TextFormatFlags.VerticalCenter);
                FluentDraw.Text(g, "マウスを動かしてから「今すぐ更新」を押してください", textFont, theme.TextTertiary, R(Pad, top + 36, 260, 20), TextFormatFlags.VerticalCenter);
            }
            DrawBattery(g, theme, new RectangleF(S(LogicalWidth - Pad - 62), S(top + 12), S(58), S(28)));
        }

        private static void Place(GraphicsPath path, RectangleF bounds, float k, float left, float top)
        {
            using (Matrix transform = new Matrix())
            {
                transform.Translate(-bounds.X, -bounds.Y, MatrixOrder.Append);
                transform.Scale(k, k, MatrixOrder.Append);
                transform.Translate(left, top, MatrixOrder.Append);
                path.Transform(transform);
            }
        }

        private void DrawBattery(Graphics g, FluentTheme theme, RectangleF body)
        {
            float stroke = Math.Max(1F, 1.5F * scale);
            Color outline = Valid || Coarse ? theme.TextSecondary : theme.TextDisabled;
            FluentDraw.StrokeRounded(g, outline, stroke, body, S(6));
            FluentDraw.FillRounded(g, outline, new RectangleF(body.Right + S(2), body.Top + body.Height / 2 - S(5), S(3), S(10)), S(1.5F));
            if (Valid && reading.Percentage.Value > 0)
            {
                float inset = S(4);
                RectangleF inner = new RectangleF(body.X + inset, body.Y + inset, body.Width - inset * 2, body.Height - inset * 2);
                float width = Math.Max(S(3), inner.Width * reading.Percentage.Value / 100F);
                Color fill = Charging ? theme.Charging : (Low ? theme.Low : theme.Battery);
                FluentDraw.FillRounded(g, fill, new RectangleF(inner.X, inner.Y, width, inner.Height), S(3));
            }
            if (Charging)
            {
                // Bolt with a ring in the surface color so it reads over both the fill and the empty part.
                float cx = body.X + body.Width / 2, cy = body.Y + body.Height / 2, u = S(1);
                PointF[] bolt = {
                    new PointF(cx + 1.5F * u, cy - 9 * u), new PointF(cx - 5 * u, cy + 1.5F * u), new PointF(cx - 0.5F * u, cy + 1.5F * u),
                    new PointF(cx - 1.5F * u, cy + 9 * u), new PointF(cx + 5 * u, cy - 1.5F * u), new PointF(cx + 0.5F * u, cy - 1.5F * u)
                };
                using (GraphicsPath path = new GraphicsPath())
                using (Pen ring = new Pen(theme.Background, 3F * scale) { LineJoin = LineJoin.Round })
                using (Brush brush = new SolidBrush(theme.TextPrimary))
                {
                    path.AddPolygon(bolt);
                    g.DrawPath(ring, path);
                    g.FillPath(brush, path);
                }
            }
        }

        private IList<BatterySample> VisibleSamples()
        {
            List<BatterySample> samples = new List<BatterySample>();
            if (reading == null) return samples;
            DateTime now = Clock();
            string key = History == null ? null : History.ResolveKey(reading);
            if (key != null) samples.AddRange(History.Query(key, now - range, now));
            // Extend the line to "now" with the live reading, unless the log already ended long ago.
            if (Valid && key != null && String.Equals(key, BatteryHistory.KeyFor(reading), StringComparison.OrdinalIgnoreCase))
            {
                BatterySample last = samples.Count > 0 ? samples[samples.Count - 1] : null;
                if (last == null || (now - last.Utc <= BatteryHistory.GapLimit && last.Utc < now))
                    samples.Add(new BatterySample { Utc = now, Percent = reading.Percentage.Value, Charging = reading.IsCharging });
            }
            return samples;
        }

        private void DrawChart(Graphics g, FluentTheme theme)
        {
            Rectangle card = R(CardBounds);
            FluentDraw.FillRounded(g, theme.Card, new RectangleF(card.X, card.Y, card.Width, card.Height), S(8));
            FluentDraw.StrokeRounded(g, theme.CardStroke, 1F, new RectangleF(card.X + 0.5F, card.Y + 0.5F, card.Width - 1, card.Height - 1), S(8));
            Rectangle segments = R(SegmentBounds);
            FluentDraw.FillRounded(g, theme.ControlFill, new RectangleF(segments.X, segments.Y, segments.Width, segments.Height), S(5));

            FluentDraw.Text(g, "バッテリー推移", titleFont, theme.TextPrimary, R(28, 148, 120, 28), TextFormatFlags.VerticalCenter);

            Rectangle plot = R(PlotBounds);
            DateTime now = Clock();
            DateTime from = now - range;
            IList<BatterySample> samples = VisibleSamples();

            for (int level = 0; level <= 100; level += 50)
            {
                int y = Y(plot, level);
                g.SmoothingMode = SmoothingMode.None;
                using (Pen pen = new Pen(level == 0 ? theme.AxisLine : theme.GridLine)) g.DrawLine(pen, plot.Left, y, plot.Right, y);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                FluentDraw.Text(g, level + "%", smallFont, theme.TextTertiary,
                    new Rectangle(card.Left + S(8), y - S(8), plot.Left - S(8) - (card.Left + S(8)), S(16)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
            DrawTimeAxis(g, theme, plot, from, now);

            bool anyCharging = false;
            if (samples.Count > 0)
            {
                GraphicsState state = g.Save();
                Rectangle clip = plot;
                clip.Inflate(S(12), S(12));
                g.SetClip(clip);
                anyCharging = DrawChargingBands(g, theme, plot, samples, from);
                DrawSeries(g, theme, plot, samples, from);
                BatterySample last = samples[samples.Count - 1];
                // The glowing "now" dot marks a live reading; a disconnected mouse has none.
                if (Valid && now - last.Utc <= BatteryHistory.GapLimit)
                    DrawDot(g, theme, new PointF(X(plot, from, last.Utc), Y(plot, last.Percent)), true);
                g.Restore(state);
            }
            if (anyCharging) DrawChargingKey(g, theme);
            // A line needs at least a few minutes of data; the first scan alone is only a dot.
            bool sparse = samples.Count < 2 || samples[samples.Count - 1].Utc - samples[0].Utc < TimeSpan.FromMinutes(5);
            if (sparse)
            {
                string title = "まだ記録がありません", detail = "使い続けると、ここに残量の推移が表示されます";
                if (reading == null) { title = "確認中…"; detail = null; }
                else if (Coarse) { title = "推移を表示できません"; detail = "このマウスは残量を段階でのみ返します"; }
                // Mask the gridlines behind the message so the text stays readable.
                FluentDraw.FillRounded(g, theme.Card, new RectangleF(plot.Left - S(2), plot.Top + S(12), plot.Width + S(4), S(44)), S(4));
                Rectangle message = new Rectangle(plot.Left, plot.Top + S(14), plot.Width, S(20));
                FluentDraw.Text(g, title, textFont, theme.TextSecondary, message, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                message.Offset(0, S(20));
                FluentDraw.Text(g, detail, smallFont, theme.TextTertiary, message, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else if (hoverX.HasValue) DrawHover(g, theme, plot, samples, from);
        }

        private static int Y(Rectangle plot, int percent)
        {
            return plot.Bottom - (int)Math.Round(plot.Height * percent / 100.0);
        }

        private float X(Rectangle plot, DateTime from, DateTime utc)
        {
            return plot.Left + (float)((utc - from).Ticks / (double)range.Ticks * plot.Width);
        }

        private bool DrawChargingBands(Graphics g, FluentTheme theme, Rectangle plot, IList<BatterySample> samples, DateTime from)
        {
            bool any = false;
            DateTime? start = null;
            using (Brush band = new SolidBrush(FluentTheme.WithAlpha(theme.TextPrimary, ChargingBandAlpha(theme))))
            {
                for (int i = 0; i < samples.Count; i++)
                {
                    if (samples[i].Charging != true) continue;
                    any = true;
                    bool connected = i + 1 < samples.Count && samples[i + 1].Utc - samples[i].Utc <= BatteryHistory.GapLimit;
                    if (!start.HasValue) start = samples[i].Utc;
                    if (connected && samples[i + 1].Charging == true) continue;
                    // One rectangle per charging period, so overlapping edges do not darken the band.
                    DateTime end = connected ? samples[i + 1].Utc : samples[i].Utc;
                    float left = X(plot, from, start.Value), right = Math.Max(left + S(2), X(plot, from, end));
                    g.FillRectangle(band, left, plot.Top, right - left, plot.Height);
                    start = null;
                }
            }
            return any;
        }

        private static int ChargingBandAlpha(FluentTheme theme) { return theme.IsLight ? 22 : 26; }

        private void DrawSeries(Graphics g, FluentTheme theme, Rectangle plot, IList<BatterySample> samples, DateTime from)
        {
            List<List<PointF>> runs = new List<List<PointF>>();
            List<PointF> run = null;
            for (int i = 0; i < samples.Count; i++)
            {
                if (run == null || samples[i].Utc - samples[i - 1].Utc > BatteryHistory.GapLimit)
                {
                    run = new List<PointF>();
                    runs.Add(run);
                }
                PointF point = new PointF(X(plot, from, samples[i].Utc), Y(plot, samples[i].Percent));
                // A week holds thousands of samples; keep about two points per pixel column.
                if (run.Count >= 2 && i < samples.Count - 1 && point.X - run[run.Count - 2].X < 1F) run[run.Count - 1] = point;
                else run.Add(point);
            }
            Color line = theme.Battery;
            foreach (List<PointF> points in runs)
            {
                if (points.Count == 1)
                {
                    using (Brush dot = new SolidBrush(line)) g.FillEllipse(dot, points[0].X - S(2.5F), points[0].Y - S(2.5F), S(5), S(5));
                    continue;
                }
                using (GraphicsPath area = new GraphicsPath())
                {
                    area.AddLines(points.ToArray());
                    area.AddLine(points[points.Count - 1], new PointF(points[points.Count - 1].X, plot.Bottom));
                    area.AddLine(new PointF(points[points.Count - 1].X, plot.Bottom), new PointF(points[0].X, plot.Bottom));
                    area.CloseFigure();
                    using (LinearGradientBrush fill = new LinearGradientBrush(new PointF(0, plot.Top - 1), new PointF(0, plot.Bottom + 1),
                        FluentTheme.WithAlpha(line, theme.IsLight ? 60 : 92), FluentTheme.WithAlpha(line, theme.IsLight ? 6 : 10)))
                        g.FillPath(fill, area);
                }
                PointF[] array = points.ToArray();
                if (!theme.IsLight)
                    using (Pen glow = new Pen(FluentTheme.WithAlpha(line, 46), 6F * scale) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLines(glow, array);
                using (Pen pen = new Pen(line, 2F * scale) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(pen, array);
            }
        }

        private void DrawDot(Graphics g, FluentTheme theme, PointF center, bool glow)
        {
            float r = 4.5F * scale;
            if (glow && !theme.IsLight)
                using (Brush halo = new SolidBrush(FluentTheme.WithAlpha(theme.Battery, 60)))
                    g.FillEllipse(halo, center.X - r * 1.9F, center.Y - r * 1.9F, r * 3.8F, r * 3.8F);
            using (Brush ring = new SolidBrush(theme.Battery)) g.FillEllipse(ring, center.X - r, center.Y - r, r * 2, r * 2);
            float inner = r * 0.5F;
            using (Brush core = new SolidBrush(Color.White)) g.FillEllipse(core, center.X - inner, center.Y - inner, inner * 2, inner * 2);
        }

        private void DrawChargingKey(Graphics g, FluentTheme theme)
        {
            Size title = TextRenderer.MeasureText(g, "バッテリー推移", titleFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            int x = S(28) + title.Width + S(10);
            Rectangle swatch = new Rectangle(x, S(162) - S(5), S(10), S(10));
            FluentDraw.FillRounded(g, FluentTheme.Blend(theme.TextPrimary, theme.Card, ChargingBandAlpha(theme) / 255F), swatch, S(2));
            FluentDraw.StrokeRounded(g, theme.AxisLine, 1F, new RectangleF(swatch.X + 0.5F, swatch.Y + 0.5F, swatch.Width - 1, swatch.Height - 1), S(2));
            FluentDraw.Text(g, "充電中", smallFont, theme.TextTertiary, new Rectangle(swatch.Right + S(4), S(152), S(60), S(20)), TextFormatFlags.VerticalCenter);
        }

        private void DrawTimeAxis(Graphics g, FluentTheme theme, Rectangle plot, DateTime fromUtc, DateTime nowUtc)
        {
            DateTime fromLocal = fromUtc.ToLocalTime(), nowLocal = nowUtc.ToLocalTime();
            List<KeyValuePair<DateTime, bool>> ticks = new List<KeyValuePair<DateTime, bool>>();
            if (range == Week)
            {
                for (DateTime t = fromLocal.Date.AddHours(12); t < nowLocal; t = t.AddHours(12))
                    if (t > fromLocal) ticks.Add(new KeyValuePair<DateTime, bool>(t, t.Hour == 0));
            }
            else
            {
                // Labels every 6 hours from the first full hour, with a minor tick in between.
                DateTime first = new DateTime(fromLocal.Year, fromLocal.Month, fromLocal.Day, fromLocal.Hour, 0, 0, DateTimeKind.Local).AddHours(1);
                for (int i = 0; ; i++)
                {
                    DateTime t = first.AddHours(3 * i);
                    if (t >= nowLocal) break;
                    ticks.Add(new KeyValuePair<DateTime, bool>(t, i % 2 == 0));
                }
            }

            int axisY = Y(plot, 0);
            int labelTop = axisY + S(8);
            Size nowSize = TextRenderer.MeasureText(g, "現在", smallFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            int limit = plot.Right - nowSize.Width - S(10);
            FluentDraw.Text(g, "現在", smallFont, theme.TextTertiary, new Rectangle(plot.Right - nowSize.Width, labelTop, nowSize.Width + S(2), S(16)), TextFormatFlags.Left);
            g.SmoothingMode = SmoothingMode.None;
            using (Pen tick = new Pen(theme.AxisLine))
            {
                g.DrawLine(tick, plot.Right, axisY, plot.Right, axisY + S(4));
                int lastRight = Int32.MinValue;
                DateTime? lastLabelDate = null;
                foreach (KeyValuePair<DateTime, bool> pair in ticks)
                {
                    int x = (int)Math.Round(X(plot, fromUtc, pair.Key.ToUniversalTime()));
                    g.DrawLine(tick, x, axisY, x, axisY + S(pair.Value ? 4 : 2));
                    if (!pair.Value) continue;
                    string label = TickLabel(pair.Key, nowLocal, lastLabelDate);
                    Size size = TextRenderer.MeasureText(g, label, smallFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    int left = Math.Max(S(CardBounds.Left + 8), x - size.Width / 2);
                    if (left < lastRight + S(8) || left + size.Width > limit) continue;
                    FluentDraw.Text(g, label, smallFont, theme.TextTertiary, new Rectangle(left, labelTop, size.Width + S(2), S(16)), TextFormatFlags.Left);
                    lastRight = left + size.Width;
                    lastLabelDate = pair.Key.Date;
                }
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
        }

        private string TickLabel(DateTime local, DateTime nowLocal, DateTime? previousDate)
        {
            string day = local.Date == nowLocal.Date ? "本日" : (local.Date == nowLocal.Date.AddDays(-1) ? "昨日"
                : local.ToString("M/d", CultureInfo.InvariantCulture));
            if (range == Week) return day == "本日" || day == "昨日" ? day : local.ToString("M/d", CultureInfo.InvariantCulture);
            string time = local.ToString("H:mm", CultureInfo.InvariantCulture);
            return previousDate.HasValue && previousDate.Value == local.Date ? time : day + " " + time;
        }

        private void DrawHover(Graphics g, FluentTheme theme, Rectangle plot, IList<BatterySample> samples, DateTime from)
        {
            BatterySample nearest = null;
            float nearestX = 0, best = Single.MaxValue;
            foreach (BatterySample sample in samples)
            {
                float x = X(plot, from, sample.Utc);
                if (x < plot.Left - 1 || x > plot.Right + 1) continue;
                float distance = Math.Abs(x - hoverX.Value);
                if (distance < best) { best = distance; nearest = sample; nearestX = x; }
            }
            if (nearest == null) return;
            PointF point = new PointF(nearestX, Y(plot, nearest.Percent));
            g.SmoothingMode = SmoothingMode.None;
            using (Pen guide = new Pen(theme.AxisLine)) g.DrawLine(guide, (int)Math.Round(nearestX), plot.Top, (int)Math.Round(nearestX), plot.Bottom);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawDot(g, theme, point, false);

            DateTime local = nearest.Utc.ToLocalTime();
            string when = range == Week ? local.ToString("M/d H:mm", CultureInfo.InvariantCulture) : local.ToString("H:mm", CultureInfo.InvariantCulture);
            string text = when + "   " + nearest.Percent + "%" + (nearest.Charging == true ? "  充電中" : "");
            Size size = TextRenderer.MeasureText(g, text, smallFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            Rectangle card = R(CardBounds);
            int width = size.Width + S(16), height = size.Height + S(8);
            int left = Math.Max(card.Left + S(6), Math.Min(card.Right - S(6) - width, (int)nearestX - width / 2));
            int top = (int)point.Y - S(12) - height;
            // Above a high point the tooltip would slide under the 24時間/7日間 buttons; show it below instead.
            if (top < R(SegmentBounds).Bottom + S(4)) top = (int)point.Y + S(12);
            RectangleF box = new RectangleF(left, top, width, height);
            FluentDraw.FillRounded(g, theme.Tooltip, box, S(4));
            FluentDraw.StrokeRounded(g, theme.TooltipStroke, 1F, new RectangleF(left + 0.5F, top + 0.5F, width - 1, height - 1), S(4));
            FluentDraw.Text(g, text, smallFont, theme.TextPrimary, new Rectangle(left, top, width, height), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void Dispose(bool disposing)
        {
            Font controlFont = Font;
            base.Dispose(disposing);
            if (disposing)
            {
                // Child buttons share these fonts, so release them after the controls are gone.
                foreach (IDisposable item in new IDisposable[] { nameFont, nameCjkFont, textFont, smallFont, titleFont, heroTextFont, statusGlyphFont, displayFamily, controlFont })
                    if (item != null) item.Dispose();
            }
        }
    }
}
