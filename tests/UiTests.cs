using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ViperBattery;

internal static class UiTests
{
    private static int assertions;

    [DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr process, int flags);

    [STAThread]
    private static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string outputDirectory = AppDomain.CurrentDomain.BaseDirectory;
            TestPopup(outputDirectory);
            TestIcons();
            TestChargingDigitConsistency();
            SaveDesignPreviews(outputDirectory);
            SaveIconSheet(Path.Combine(outputDirectory, "icons.png"));
            Console.WriteLine("UI tests: " + assertions + " assertions passed. No hardware access.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("UI tests failed: " + error);
            return 1;
        }
    }

    private static void TestPopup(string outputDirectory)
    {
        using (BatteryPopup popup = new BatteryPopup(delegate { }, delegate { }))
        {
            popup.Location = new Point(-32000, -32000);
            Assert(!popup.ShowInTaskbar, "Details must not add a taskbar window.");
            popup.Show();
            Assert(popup.Visible, "Details can be shown.");
            Assert(popup.Location.X < 0 && popup.Location.Y < 0, "UI tests stay offscreen.");

            // Close() follows the same UserClosing path as Alt+F4.
            popup.Close();
            Assert(!popup.IsDisposed, "Closing details must preserve the reusable popup.");
            Assert(!popup.Visible, "Closing details hides the popup.");
            popup.Show();
            Assert(popup.Visible, "Details can reopen after closing.");

            Button refresh = popup.Controls.OfType<Button>().Single(button => button.Text == "今すぐ更新");
            BatteryReading full = Sample(100, false);
            popup.UpdateReading(full, true, 15);
            Assert(!refresh.Enabled, "Refresh must be disabled while an update is active.");
            popup.UpdateReading(full, false, 60);
            Assert(refresh.Enabled, "Refresh becomes available after an update.");
            foreach (Control control in popup.Controls)
            {
                Assert(popup.ClientRectangle.Contains(control.Bounds), "Every control fits inside the popup.");
                Assert(control.Width > 0 && control.Height > 0, "Every control has a usable size.");
            }

            BatteryReading[] states = { full, Sample(0, false), Sample(null, false), Sample(17, true) };
            string[] names = { "popup-100.png", "popup-0.png", "popup-offline.png", "popup-charging.png" };
            List<long> signatures = new List<long>();
            for (int i = 0; i < states.Length; i++)
            {
                popup.UpdateReading(states[i], false, 60);
                string path = Path.Combine(outputDirectory, names[i]);
                popup.SavePreview(path);
                using (Bitmap preview = new Bitmap(path))
                {
                    Assert(preview.Size == popup.Size, "Preview dimensions match the popup.");
                    HashSet<int> colors = new HashSet<int>();
                    long signature = 17;
                    for (int y = 0; y < preview.Height; y += 3)
                    for (int x = 0; x < preview.Width; x += 3)
                    {
                        int pixel = preview.GetPixel(x, y).ToArgb();
                        colors.Add(pixel);
                        signature = unchecked(signature * 31 + pixel);
                    }
                    Assert(colors.Count > 20, "Preview contains rendered text and controls.");
                    signatures.Add(signature);
                }
            }
            Assert(signatures.Distinct().Count() == states.Length, "Full, empty, unavailable, and charging previews differ.");
            popup.Hide();
        }
    }

    private static BatteryReading Sample(int? percent, bool charging)
    {
        return new BatteryReading
        {
            Connected = percent.HasValue,
            Percentage = percent,
            IsCharging = percent.HasValue ? (bool?)charging : null,
            DeviceName = "Razer Viper V3 Pro",
            Connection = "USB wireless",
            Detail = "Synthetic UI test data",
            Timestamp = new DateTime(2026, 1, 1, 12, 34, 56)
        };
    }

    private static void TestIcons()
    {
        int?[] values = { null, 0, 17, 100 };
        string[] preferences = { "auto", "white", "black", "green" };
        foreach (int? value in values)
        foreach (string preference in preferences)
        foreach (bool charging in new[] { false, true })
        {
            using (Icon icon = NumericIcon.Create(value, preference, charging))
            using (Bitmap bitmap = icon.ToBitmap())
            {
                Assert(icon.Width >= 16 && icon.Height >= 16, "Tray icons have usable dimensions.");
                int visiblePixels = 0;
                for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                    if (bitmap.GetPixel(x, y).A > 0) visiblePixels++;
                Assert(visiblePixels > 3, "Every battery state produces a nonblank icon.");
                Assert(visiblePixels < bitmap.Width * bitmap.Height, "Tray icons preserve a transparent background.");
            }
        }

        CollectFinalizers();
        using (Process process = Process.GetCurrentProcess())
        {
            int baselineGdi = GetGuiResources(process.Handle, 0);
            int baselineUser = GetGuiResources(process.Handle, 1);
            for (int i = 0; i < 500; i++)
            {
                using (Icon icon = NumericIcon.Create(values[i % values.Length], preferences[i % preferences.Length], i % 2 == 0))
                    Assert(icon.Handle != IntPtr.Zero, "Repeated icon replacement returns a valid handle.");
            }
            CollectFinalizers();
            int gdiDelta = GetGuiResources(process.Handle, 0) - baselineGdi;
            int userDelta = GetGuiResources(process.Handle, 1) - baselineUser;
            Assert(gdiDelta <= 10, "Repeated icon replacement must not leak GDI resources (delta " + gdiDelta + ").");
            Assert(userDelta <= 10, "Repeated icon replacement must not leak HICON/USER resources (delta " + userDelta + ").");
            Console.WriteLine("500 icon replacements: GDI delta " + gdiDelta + ", USER delta " + userDelta + ".");
        }
    }

    private static void TestChargingDigitConsistency()
    {
        int[] sizes = { 16, 20, 24, 32 };
        foreach (int size in sizes)
        {
            using (Icon idleIcon = NumericIcon.CreateAtSize(39, "auto", false, "normal", size, true))
            using (Icon chargingIcon = NumericIcon.CreateAtSize(39, "auto", true, "normal", size, true))
            using (Bitmap idle = idleIcon.ToBitmap())
            using (Bitmap charging = chargingIcon.ToBitmap())
            {
                // Reproduce the reported 39% mismatch at common taskbar icon sizes.
                int digitBottom = -1;
                int digitTop = idle.Height;
                for (int y = 0; y < idle.Height; y++)
                for (int x = 0; x < idle.Width; x++)
                    if (idle.GetPixel(x, y).A > 0)
                    {
                        digitTop = Math.Min(digitTop, y);
                        digitBottom = y;
                    }
                Assert(digitBottom >= 0, "The reference digits must be visible at " + size + "px.");
                Assert(Math.Abs((digitTop + digitBottom + 1) / 2F - size / 2F) <= 0.5F,
                    "Digits must be vertically centered in the icon at " + size + "px.");

                // Derive the digit region from rendered ink, independently of layout constants.
                bool sameDigits = true;
                for (int y = 0; y <= digitBottom; y++)
                for (int x = 0; x < idle.Width; x++)
                    if (idle.GetPixel(x, y).ToArgb() != charging.GetPixel(x, y).ToArgb()) sameDigits = false;
                Assert(sameDigits, "Charging must preserve every digit pixel and its position at 39%, " + size + "px.");

                int markerTop = charging.Height;
                for (int y = digitBottom + 1; y < charging.Height; y++)
                for (int x = 0; x < charging.Width; x++)
                    if (charging.GetPixel(x, y).A > 0) markerTop = Math.Min(markerTop, y);
                Assert(markerTop < charging.Height, "Charging must add a visible marker below the digits at " + size + "px.");
                Assert(markerTop > digitBottom + 1, "A fully transparent row must separate the digits and charging marker at " + size + "px.");
            }
        }
    }

    private static BatteryHistory SampleHistory(DateTime nowUtc, out int current)
    {
        // A week of synthetic use: discharge during the day, PC off at night, and a recharge when low.
        BatteryHistory history = new BatteryHistory(null);
        BatteryReading reading = Sample(55, false);
        reading.Provider = "Razer";
        double level = 55;
        bool charging = false;
        for (DateTime t = nowUtc.AddDays(-7.5); t <= nowUtc; t = t.AddMinutes(10))
        {
            int hour = t.ToLocalTime().Hour;
            if (hour >= 2 && hour < 8) continue;
            if (charging) { level += 5; if (level >= 100) { level = 100; charging = false; } }
            else { level -= 0.22; if (level <= 16) charging = true; }
            reading.Percentage = (int)Math.Round(level);
            reading.IsCharging = charging;
            history.Record(reading, t);
        }
        current = (int)Math.Round(level);
        return history;
    }

    private static void SaveDesignPreviews(string outputDirectory)
    {
        DateTime now = new DateTime(2026, 11, 8, 7, 20, 0, DateTimeKind.Utc);
        int current;
        BatteryHistory history = SampleHistory(now, out current);
        try
        {
            foreach (bool light in new[] { false, true })
            {
                FluentTheme.Override(light, light ? Color.FromArgb(0, 95, 184) : Color.FromArgb(96, 205, 255));
                string theme = light ? "light" : "dark";
                using (BatteryPopup popup = new BatteryPopup(delegate { }, delegate { }))
                {
                    popup.History = history;
                    popup.Clock = delegate { return now; };
                    popup.Location = new Point(-32000, -32000);
                    popup.Show();
                    popup.ApplyTheme();
                    BatteryReading live = Sample(current, false);
                    live.Provider = "Razer";
                    live.Timestamp = now.ToLocalTime();
                    popup.UpdateReading(live, false, 60);
                    popup.SavePreview(Path.Combine(outputDirectory, "design-" + theme + "-day.png"));
                    popup.PreviewHover(0.62);
                    popup.SavePreview(Path.Combine(outputDirectory, "design-" + theme + "-hover.png"));
                    popup.ShowWeek(true);
                    popup.SavePreview(Path.Combine(outputDirectory, "design-" + theme + "-week.png"));
                    Assert(popup.WeekRange, "The week view can be selected.");
                    popup.ShowWeek(false);

                    BatteryReading charging = Sample(17, true);
                    charging.Provider = "Razer";
                    charging.Timestamp = now.ToLocalTime();
                    popup.UpdateReading(charging, false, 60);
                    popup.SavePreview(Path.Combine(outputDirectory, "design-" + theme + "-charging.png"));

                    BatteryReading low = Sample(12, false);
                    low.DeviceName = "MX Master 3S";
                    low.Provider = "Logitech";
                    low.Connection = "USB レシーバー";
                    low.Timestamp = now.ToLocalTime();
                    popup.UpdateReading(low, false, 60);
                    popup.SavePreview(Path.Combine(outputDirectory, "design-" + theme + "-low-empty.png"));

                    popup.UpdateReading(MouseBatteryService.Unavailable("test", "Razer Viper V3 Pro", "offline"), false, 60);
                    popup.SavePreview(Path.Combine(outputDirectory, "design-" + theme + "-offline.png"));
                    popup.Hide();
                }
                SaveMenuPreview(Path.Combine(outputDirectory, "design-" + theme + "-menu.png"));
            }
        }
        finally { FluentTheme.Override(null, null); }
    }

    private static void SaveMenuPreview(string path)
    {
        using (ContextMenuStrip menu = new ContextMenuStrip())
        {
            menu.Font = new Font("Yu Gothic UI", 9F);
            menu.Items.Add("詳細を表示");
            menu.Items.Add("今すぐ更新");
            ToolStripMenuItem devices = new ToolStripMenuItem("表示するマウス");
            devices.DropDownItems.Add(new ToolStripMenuItem("自動") { Checked = true });
            menu.Items.Add(devices);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem interval = new ToolStripMenuItem("更新間隔");
            interval.DropDownItems.Add(new ToolStripMenuItem("60 秒") { Checked = true });
            menu.Items.Add(interval);
            menu.Items.Add(new ToolStripMenuItem("残量 20% 以下で通知") { Checked = true });
            menu.Items.Add(new ToolStripMenuItem("Windows 起動時に実行"));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("診断結果を保存") { Enabled = false });
            menu.Items.Add("終了");
            FluentMenu.Apply(menu);
            IntPtr unused = menu.Handle;
            menu.Size = menu.GetPreferredSize(Size.Empty);
            menu.PerformLayout();
            Assert(menu.Items[1].Height >= 24, "Menu rows have touch-friendly height (" + menu.Items[1].Height + ").");
            using (Bitmap bitmap = new Bitmap(menu.Width, menu.Height))
            {
                menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, menu.Size));
                bitmap.Save(path, ImageFormat.Png);
            }
        }
    }

    private static void CollectFinalizers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void SaveIconSheet(string path)
    {
        int?[] values = { null, 0, 17, 100, 17 };
        string[] labels = { "Unavailable", "0%", "17%", "100%", "17% charging" };
        using (Bitmap sheet = new Bitmap(740, 230, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(sheet))
        using (Font font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            for (int row = 0; row < 2; row++)
            {
                Color background = row == 0 ? Color.FromArgb(25, 28, 33) : Color.FromArgb(245, 245, 247);
                Color foreground = row == 0 ? Color.White : Color.FromArgb(24, 28, 32);
                int top = row * 115;
                using (Brush brush = new SolidBrush(background)) graphics.FillRectangle(brush, 0, top, sheet.Width, 115);
                using (Brush brush = new SolidBrush(foreground))
                {
                    graphics.DrawString(row == 0 ? "Dark taskbar - native icon size" : "Light taskbar - native icon size", font, brush, 16, top + 10);
                    for (int column = 0; column < values.Length; column++)
                    {
                        int left = 22 + column * 145;
                        graphics.DrawString(labels[column], font, brush, left, top + 38);
                        using (Icon icon = NumericIcon.Create(values[column], row == 0 ? "white" : "black", column == 4))
                            graphics.DrawIconUnstretched(icon, new Rectangle(left + 25, top + 68, icon.Width, icon.Height));
                    }
                }
            }
            sheet.Save(path, ImageFormat.Png);
        }
    }

    private static void Assert(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
