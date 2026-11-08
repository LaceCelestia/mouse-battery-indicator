using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ViperBattery
{
    internal sealed class TrayApplication : ApplicationContext
    {
        private readonly Control dispatcher = new Control();
        private readonly MouseBatteryService reader = new MouseBatteryService();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly AppSettings settings = AppSettings.Load();
        private readonly BatteryHistory history = BatteryHistory.Load(BatteryHistory.DefaultPath, DateTime.UtcNow);
        private readonly EventWaitHandle showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        private RegisteredWaitHandle showWait;
        private ContextMenuStrip menu;
        private BatteryPopup popup;
        private BatteryReading reading;
        private List<BatteryReading> devices = new List<BatteryReading>();
        private string notificationDeviceId;
        private Icon currentIcon;
        private bool refreshing;
        private bool savingDiagnostics;
        private bool lowBatteryNotified;
        private bool popupOpenAtPress;
        private volatile bool stopping;

        internal TrayApplication(bool show)
        {
            IntPtr unused = dispatcher.Handle;
            FluentTheme.SetPreference(settings.Theme);
            popup = new BatteryPopup(Refresh, ShowMenu);
            popup.History = history;
            tray.Text = "残量を確認中";
            UpdateIcon();
            tray.Visible = true;
            // Decide at press time: pressing on the taskbar already deactivates and hides the popup.
            tray.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) popupOpenAtPress = popup.Visible || popup.JustAutoHidden; };
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) TogglePopup(); };
            tray.BalloonTipClicked += delegate { ShowPopup(); };
            BuildMenu();
            timer.Interval = settings.PollSeconds * 1000;
            timer.Tick += delegate { Refresh(); };
            timer.Start();
            showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, delegate { Dispatch(ShowPopup); }, null, Timeout.Infinite, false);
            SystemEvents.UserPreferenceChanged += OnPreferencesChanged;
            SystemEvents.PowerModeChanged += OnPowerChanged;
            Refresh();
            if (show) dispatcher.BeginInvoke(new Action(ShowPopup));
        }

        private void Dispatch(Action action)
        {
            if (stopping) return;
            try { dispatcher.BeginInvoke(new Action(delegate { if (!stopping) action(); })); }
            catch (InvalidOperationException) { }
        }

        private void OnPreferencesChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            Dispatch(delegate
            {
                // Light/dark mode and accent color changes arrive here; the menu reads the theme when it paints.
                FluentTheme.Refresh();
                UpdateIcon();
                popup.ApplyTheme();
            });
        }
        private void OnPowerChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) Dispatch(Refresh);
        }

        private void Refresh()
        {
            if (refreshing || stopping) return;
            refreshing = true;
            popup.UpdateReading(reading, true, settings.PollSeconds);
            Task.Factory.StartNew(delegate
            {
                try { return reader.Scan(); }
                catch (Exception error)
                {
                    AppLog.Write(error);
                    return new BatteryScan { Diagnostic = error.Message };
                }
            }).ContinueWith(delegate(Task<BatteryScan> task)
            {
                Dispatch(delegate
                {
                    refreshing = false;
                    devices = task.Result.Readings ?? new List<BatteryReading>();
                    DateTime now = DateTime.UtcNow;
                    history.RecordScan(devices, now);
                    ApplySelection();
                    timer.Stop();
                    timer.Start();
                    if (reading.Percentage.HasValue && reading.Connected)
                    {
                        if (reading.IsCharging == true || reading.Percentage.Value > 25) lowBatteryNotified = false;
                        if (settings.LowBatteryNotification && reading.IsCharging != true && reading.Percentage.Value <= 20 && !lowBatteryNotified)
                        {
                            lowBatteryNotified = true;
                            tray.ShowBalloonTip(6000, "バッテリー残量低下", "残量 " + reading.Percentage.Value + "%", ToolTipIcon.Warning);
                        }
                    }
                });
            });
        }

        private void ApplySelection()
        {
            reading = MouseBatteryService.Select(devices, settings.PreferredDeviceId,
                reading == null ? null : reading.DeviceId, settings.PreferredDeviceName, settings.PreferredDeviceContainerId);
            // A sleeping or briefly missing mouse becomes a placeholder without an ID; only a different
            // connected mouse resets the low-battery notice, so waking the same mouse does not notify again.
            if (reading.Connected && reading.DeviceId != null
                && !String.Equals(notificationDeviceId, reading.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                notificationDeviceId = reading.DeviceId;
                lowBatteryNotified = false;
            }
            UpdateIcon();
            popup.UpdateReading(reading, refreshing, settings.PollSeconds);
        }

        private void UpdateIcon()
        {
            int? percent = reading != null && reading.Connected ? reading.Percentage : null;
            Icon next = NumericIcon.Create(percent, settings.IconColor, reading != null && reading.IsCharging == true, settings.IconWeight);
            tray.Icon = next;
            Icon previous = currentIcon;
            currentIcon = next;
            if (previous != null) previous.Dispose();
            string state = MouseBatteryService.LevelText(reading) + (reading != null && reading.Connected && reading.IsCharging == true ? "（充電中）" : "");
            string device = reading != null && !String.IsNullOrWhiteSpace(reading.DeviceName) ? reading.DeviceName : "マウス";
            string tooltip = device + "\n" + state;
            tray.Text = tooltip.Length <= 63 ? tooltip : tooltip.Substring(0, 62) + "…";
        }

        private void TogglePopup()
        {
            bool wasOpen = popupOpenAtPress || popup.Visible;
            popupOpenAtPress = false;
            if (popup.Visible) popup.Hide();
            else if (!wasOpen) ShowPopup();
        }

        private void ShowPopup()
        {
            popup.UpdateReading(reading, refreshing, settings.PollSeconds);
            popup.ShowNearTray();
        }

        private void ShowMenu()
        {
            menu.Show(Cursor.Position);
        }

        private void BuildMenu()
        {
            ContextMenuStrip previous = menu;
            menu = new ContextMenuStrip();
            menu.Font = new Font("Yu Gothic UI", 9F);
            menu.Items.Add("詳細を表示", null, delegate { ShowPopup(); });
            menu.Items.Add("今すぐ更新", null, delegate { Refresh(); });
            ToolStripMenuItem deviceMenu = new ToolStripMenuItem("表示するマウス");
            menu.Items.Add(deviceMenu);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem interval = new ToolStripMenuItem("更新間隔");
            foreach (int value in new[] { 15, 30, 60, 120 })
            {
                int seconds = value;
                ToolStripMenuItem item = new ToolStripMenuItem(seconds + " 秒") { Checked = settings.PollSeconds == seconds };
                item.Click += delegate
                {
                    settings.PollSeconds = seconds;
                    SaveSettings();
                    timer.Interval = seconds * 1000;
                    popup.UpdateReading(reading, refreshing, seconds);
                };
                interval.DropDownItems.Add(item);
            }
            menu.Items.Add(interval);
            ToolStripMenuItem color = new ToolStripMenuItem("数字の色");
            string[] values = { "auto", "green", "white", "black" };
            string[] labels = { "自動（Windows のテーマに合わせる）", "グリーン", "白", "黒" };
            for (int i = 0; i < values.Length; i++)
            {
                string value = values[i];
                ToolStripMenuItem item = new ToolStripMenuItem(labels[i]) { Checked = settings.IconColor == value };
                item.Click += delegate { settings.IconColor = value; SaveSettings(); UpdateIcon(); };
                color.DropDownItems.Add(item);
            }
            menu.Items.Add(color);
            ToolStripMenuItem weight = new ToolStripMenuItem("数字の太さ");
            string[] weightValues = { "light", "normal", "bold" };
            string[] weightLabels = { "細め", "標準", "太め" };
            for (int i = 0; i < weightValues.Length; i++)
            {
                string value = weightValues[i];
                ToolStripMenuItem item = new ToolStripMenuItem(weightLabels[i]) { Checked = settings.IconWeight == value };
                item.Click += delegate { settings.IconWeight = value; SaveSettings(); UpdateIcon(); };
                weight.DropDownItems.Add(item);
            }
            menu.Items.Add(weight);
            ToolStripMenuItem themeMenu = new ToolStripMenuItem("テーマ");
            string[] themeValues = { "auto", "light", "dark" };
            string[] themeLabels = { "自動（Windows に合わせる）", "ライト", "ダーク" };
            for (int i = 0; i < themeValues.Length; i++)
            {
                string value = themeValues[i];
                ToolStripMenuItem item = new ToolStripMenuItem(themeLabels[i]) { Checked = settings.Theme == value };
                item.Click += delegate
                {
                    settings.Theme = value;
                    SaveSettings();
                    FluentTheme.SetPreference(value);
                    popup.ApplyTheme();
                };
                themeMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(themeMenu);
            ToolStripMenuItem notifications = new ToolStripMenuItem("残量 20% 以下で通知") { Checked = settings.LowBatteryNotification };
            notifications.Click += delegate { settings.LowBatteryNotification = !settings.LowBatteryNotification; SaveSettings(); };
            menu.Items.Add(notifications);
            ToolStripMenuItem startup = new ToolStripMenuItem("Windows 起動時に実行") { Checked = StartupRegistration.IsEnabled };
            startup.Click += delegate
            {
                try { StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled); }
                catch (Exception error)
                {
                    AppLog.Write(error);
                    MessageBox.Show("自動起動の設定を変更できませんでした。\n" + error.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            menu.Items.Add(startup);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem diagnostics = new ToolStripMenuItem("診断結果を保存");
            diagnostics.Click += delegate { SaveDiagnostics(); };
            menu.Items.Add(diagnostics);
            menu.Items.Add("使い方", null, delegate
            {
                MessageBox.Show("タスクバー右側の数字が、マウスのバッテリー残量（%）です。\n\n数字が見つからない場合は、通知領域の「隠れているインジケーターを表示」（∧）を開き、数字をタスクバーへドラッグしてください。\n\n左クリック：機種名・残量・充電状態\n右クリック → 表示するマウス：表示対象を選択\n\n数字の下の緑の線は充電中の表示です。\n「--」は数値を取得できない状態です。残量を段階で返す機種は、詳細画面にその段階を表示します。", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            menu.Items.Add("終了", null, delegate { ExitThread(); });
            // Refresh check marks when the menu opens, without replacing a menu inside its click handler.
            menu.Opening += delegate
            {
                PopulateDevices(deviceMenu);
                foreach (ToolStripMenuItem item in interval.DropDownItems) item.Checked = item.Text == settings.PollSeconds + " 秒";
                for (int i = 0; i < values.Length; i++) ((ToolStripMenuItem)color.DropDownItems[i]).Checked = values[i] == settings.IconColor;
                for (int i = 0; i < weightValues.Length; i++) ((ToolStripMenuItem)weight.DropDownItems[i]).Checked = weightValues[i] == settings.IconWeight;
                for (int i = 0; i < themeValues.Length; i++) ((ToolStripMenuItem)themeMenu.DropDownItems[i]).Checked = themeValues[i] == settings.Theme;
                notifications.Checked = settings.LowBatteryNotification;
                startup.Checked = StartupRegistration.IsEnabled;
                diagnostics.Enabled = !savingDiagnostics;
            };
            FluentMenu.Apply(menu);
            FluentMenu.Apply(deviceMenu.DropDown);
            tray.ContextMenuStrip = menu;
            if (previous != null) previous.Dispose();
        }

        private void PopulateDevices(ToolStripMenuItem parent)
        {
            foreach (ToolStripItem old in parent.DropDownItems.Cast<ToolStripItem>().ToArray()) old.Dispose();
            parent.DropDownItems.Clear();
            ToolStripMenuItem automatic = new ToolStripMenuItem("自動") { Checked = String.IsNullOrEmpty(settings.PreferredDeviceId) };
            automatic.Click += delegate { SelectDevice(null, null, null); };
            parent.DropDownItems.Add(automatic);
            parent.DropDownItems.Add(new ToolStripSeparator());
            foreach (BatteryReading device in devices)
            {
                string id = device.DeviceId;
                string name = device.DeviceName;
                string container = device.ContainerId;
                string label = name + "  " + MouseBatteryService.LevelText(device);
                if (!String.IsNullOrEmpty(device.Connection)) label += "  (" + device.Connection + ")";
                ToolStripMenuItem item = new ToolStripMenuItem(label.Replace("&", "&&")) {
                    Checked = !String.IsNullOrEmpty(settings.PreferredDeviceId) && reading != null
                        && String.Equals(id, reading.DeviceId, StringComparison.OrdinalIgnoreCase)
                };
                item.Click += delegate { SelectDevice(id, name, container); };
                parent.DropDownItems.Add(item);
            }
            if (!String.IsNullOrEmpty(settings.PreferredDeviceId) && !devices.Any(device =>
                reading != null && String.Equals(device.DeviceId, reading.DeviceId, StringComparison.OrdinalIgnoreCase)))
                parent.DropDownItems.Add(new ToolStripMenuItem((settings.PreferredDeviceName + "  (未接続)").Replace("&", "&&")) { Checked = true, Enabled = false });
            else if (devices.Count == 0)
                parent.DropDownItems.Add(new ToolStripMenuItem(refreshing ? "確認中…" : "対応するマウスが見つかりません") { Enabled = false });
        }

        private void SelectDevice(string id, string name, string container)
        {
            settings.PreferredDeviceId = id ?? "";
            settings.PreferredDeviceName = name ?? "";
            settings.PreferredDeviceContainerId = container ?? "";
            SaveSettings();
            ApplySelection();
            Refresh();
        }

        private void SaveDiagnostics()
        {
            if (savingDiagnostics || stopping) return;
            string destination;
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "診断結果を保存";
                dialog.Filter = "テキストファイル (*.txt)|*.txt";
                dialog.DefaultExt = "txt";
                dialog.FileName = "mouse-battery-diagnostics.txt";
                if (dialog.ShowDialog() != DialogResult.OK) return;
                destination = dialog.FileName;
            }
            savingDiagnostics = true;
            Task.Factory.StartNew(delegate
            {
                string errorMessage = null;
                try
                {
                    string report = "Mouse Battery Indicator " + Application.ProductVersion + " - " + DateTime.Now.ToString("s")
                        + Environment.NewLine + "Last battery scan:" + Environment.NewLine + reader.Diagnose()
                        + Environment.NewLine + HidInventory.Diagnose();
                    File.WriteAllText(destination, report, new UTF8Encoding(true));
                }
                catch (Exception error) { AppLog.Write(error); errorMessage = error.Message; }
                Dispatch(delegate
                {
                    savingDiagnostics = false;
                    if (errorMessage != null)
                        MessageBox.Show("診断結果を保存できませんでした。\n" + errorMessage, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            });
        }

        private void SaveSettings()
        {
            try { settings.Save(); }
            catch (Exception error)
            {
                AppLog.Write(error);
                MessageBox.Show("設定を保存できませんでした。\n" + error.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !stopping)
            {
                stopping = true;
                SystemEvents.UserPreferenceChanged -= OnPreferencesChanged;
                SystemEvents.PowerModeChanged -= OnPowerChanged;
                timer.Stop();
                timer.Dispose();
                if (showWait != null) showWait.Unregister(null);
                showSignal.Dispose();
                tray.Visible = false;
                tray.Dispose();
                if (currentIcon != null) currentIcon.Dispose();
                menu.Dispose();
                popup.Dispose();
                dispatcher.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
