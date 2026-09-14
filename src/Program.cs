using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ViperBattery
{
    internal static class Program
    {
        internal const string AppName = "マウスのバッテリー";
        private const string InstanceName = @"Local\ViperV3BatteryIndicator.Instance";
        internal const string ShowEventName = @"Local\ViperV3BatteryIndicator.Show";

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (args.Length > 0 && args[0] == "--diagnose")
                {
                    string destination = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostics.txt");
                    MouseBatteryService reader = new MouseBatteryService();
                    BatteryScan scan = reader.Scan();
                    AppSettings settings = AppSettings.Load();
                    BatteryReading reading = MouseBatteryService.Select(scan.Readings, settings.PreferredDeviceId, null, settings.PreferredDeviceName, settings.PreferredDeviceContainerId);
                    string report = "Mouse Battery - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine
                        + "Connected: " + reading.Connected + Environment.NewLine
                        + "Device: " + reading.DeviceName + Environment.NewLine
                        + "Battery: " + MouseBatteryService.LevelText(reading) + Environment.NewLine
                        + "Charging: " + (reading.IsCharging.HasValue ? reading.IsCharging.ToString() : "unknown") + Environment.NewLine
                        + "Connection: " + reading.Connection + Environment.NewLine
                        + "Detail: " + reading.Detail + Environment.NewLine
                        + "Detected devices: " + scan.Readings.Count + Environment.NewLine + Environment.NewLine;
                    foreach (BatteryReading device in scan.Readings)
                        report += device.DeviceName + " | " + MouseBatteryService.LevelText(device) + " | " + device.Connection + Environment.NewLine;
                    report += Environment.NewLine + reader.Diagnose();
                    File.WriteAllText(destination, report, new UTF8Encoding(true));
                    return reading.Connected && reading.Percentage.HasValue ? 0 : 2;
                }
                if (args.Length == 2 && args[0] == "--capture")
                {
                    using (BatteryPopup popup = new BatteryPopup(delegate { }, delegate { }))
                    {
                        BatteryScan scan = new MouseBatteryService().Scan();
                        AppSettings settings = AppSettings.Load();
                        popup.UpdateReading(MouseBatteryService.Select(scan.Readings, settings.PreferredDeviceId, null, settings.PreferredDeviceName, settings.PreferredDeviceContainerId), false, settings.PollSeconds);
                        popup.SavePreview(Path.GetFullPath(args[1]));
                    }
                    return 0;
                }

                bool isFirst;
                using (Mutex instance = new Mutex(true, InstanceName, out isFirst))
                {
                    if (!isFirst)
                    {
                        try { using (EventWaitHandle signal = EventWaitHandle.OpenExisting(ShowEventName)) signal.Set(); }
                        catch (WaitHandleCannotBeOpenedException) { }
                        return 0;
                    }
                    try
                    {
                        Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { AppLog.Write(e.Exception); };
                        using (TrayApplication app = new TrayApplication(Array.IndexOf(args, "--show") >= 0))
                            Application.Run(app);
                    }
                    finally { instance.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception error)
            {
                AppLog.Write(error);
                if (args.Length == 0 || args[0] == "--show")
                    MessageBox.Show("起動できませんでした。\n" + error.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }

    internal static class AppLog
    {
        private static readonly object Sync = new object();
        internal static void Write(Exception error)
        {
            try
            {
                lock (Sync)
                {
                    string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ViperV3BatteryIndicator");
                    Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, "app.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 262144) File.Delete(path);
                    File.AppendAllText(path, DateTime.Now.ToString("s") + " " + error + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
