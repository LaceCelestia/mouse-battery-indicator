using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;
using ViperBattery;

internal static class ServiceTests
{
    private static int assertions;
    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    private static BatteryReading Mouse(string id, string provider, int? percentage, string container)
    {
        return new BatteryReading { DeviceId = id, DeviceName = id, Provider = provider,
            Connected = true, Percentage = percentage, ContainerId = container, Timestamp = DateTime.Now };
    }

    private static int Main()
    {
        try
        {
            string container = "3d0a6ad9-7c5d-4c26-b2c0-15a40ce74294";
            BatteryReading razer = Mouse("razer:a", "Razer", 53, container);
            BatteryReading logitech = Mouse("logitech:b:1", "Logitech", 76, null);
            List<BatteryReading> devices = new List<BatteryReading> { logitech, razer };
            Check(MouseBatteryService.Select(devices, "LOGITECH:B:1", "razer:a", "") == logitech, "Explicit selection overrides automatic history, ignoring ID case");
            BatteryReading missing = MouseBatteryService.Select(devices, "logitech:missing", "razer:a", "Saved Mouse");
            Check(!missing.Connected && missing.Percentage == null && missing.DeviceName == "Saved Mouse", "Missing selected mouse does not show a different mouse's charge");
            Check(missing.DeviceId == "logitech:missing", "Missing selection keeps its identity for reconnection");
            Check(MouseBatteryService.Select(devices, "", "logitech:b:1", "") == logitech, "Automatic mode keeps the current available mouse");
            logitech.Connected = false;
            Check(MouseBatteryService.Select(devices, "", "logitech:b:1", "") == razer, "Automatic mode switches away from a disconnected mouse");
            Check(MouseBatteryService.Select(devices, "logitech:b:1", null, "") == logitech, "Explicit mode keeps a disconnected selection");
            logitech.Connected = true;
            logitech.Percentage = 0;
            Check(MouseBatteryService.Select(devices, "", "logitech:b:1", "") == logitech, "A genuine zero-percent reading is available");
            Check(MouseBatteryService.LevelText(logitech) == "0%", "Zero is not an unavailable marker");
            Check(MouseBatteryService.Select(new List<BatteryReading>(), "", null, "").Percentage == null, "No devices does not invent a percentage");

            BatteryReading windows = Mouse("windows:a", "Windows", 45, "{" + container.ToUpperInvariant() + "}");
            List<BatteryReading> merged = MouseBatteryService.Merge(new[] { razer, windows, logitech });
            Check(merged.Count == 2 && !merged.Contains(windows), "Native battery supersedes a Windows snapshot of the same physical mouse");
            Check(MouseBatteryService.Select(merged, windows.DeviceId, null, windows.DeviceName, windows.ContainerId) == razer, "Saved Windows selection follows the same mouse when the native reader recovers");
            Check(!MouseBatteryService.Select(merged, windows.DeviceId, null, windows.DeviceName, Guid.NewGuid().ToString()).Connected, "Fallback never changes to a different physical mouse");
            razer.Percentage = null;
            merged = MouseBatteryService.Merge(new[] { razer, windows });
            Check(merged.Count == 2 && MouseBatteryService.Select(merged, "", null, "") == windows, "Windows remains usable if the native percentage is unavailable");
            Check(MouseBatteryService.Select(merged, razer.DeviceId, null, razer.DeviceName, container) == windows, "Explicit native selection uses Windows battery for the same physical mouse when needed");
            razer.Percentage = 53;
            windows.ContainerId = "bad-container";
            Check(MouseBatteryService.Merge(new[] { razer, windows }).Count == 2, "Unproven identities are never combined");
            windows.ContainerId = Guid.Empty.ToString();
            razer.ContainerId = Guid.Empty.ToString();
            Check(MouseBatteryService.Merge(new[] { razer, windows }).Count == 2, "Empty container GUID does not match unrelated devices");
            BatteryReading secondLogitech = Mouse("logitech:b:2", "Logitech", 12, container);
            logitech.ContainerId = container;
            Check(MouseBatteryService.Merge(new[] { logitech, secondLogitech }).Count == 2, "Different receiver slots stay independently selectable");
            BatteryReading duplicate = Mouse("LOGITECH:B:1", "Logitech", null, container);
            Check(MouseBatteryService.Merge(new[] { logitech, duplicate }).Single() == logitech, "Duplicate collection uses its available percentage");

            BatteryReading coarse = Mouse("logitech:c", "Logitech", null, null);
            coarse.BatteryLevelText = "残量: 高";
            Check(MouseBatteryService.LevelText(coarse) == "残量: 高" && !coarse.Percentage.HasValue, "A coarse level is not presented as an exact percentage");
            coarse.Percentage = 68;
            coarse.IsApproximate = true;
            Check(MouseBatteryService.LevelText(coarse) == "約 68%", "Voltage estimates are labeled approximate");
            coarse.Connected = false;
            Check(!MouseBatteryService.LevelText(coarse).Contains("68"), "Disconnected data never appears in the tooltip");

            XmlSerializer serializer = new XmlSerializer(typeof(AppSettings));
            AppSettings oldSettings;
            using (StringReader input = new StringReader("<ViperV3BatteryIndicatorSettings><PollSeconds>30</PollSeconds><IconColor>white</IconColor><IconWeight>bold</IconWeight><LowBatteryNotification>false</LowBatteryNotification></ViperV3BatteryIndicatorSettings>"))
                oldSettings = (AppSettings)serializer.Deserialize(input);
            Check(oldSettings.PreferredDeviceId == "" && oldSettings.PreferredDeviceName == "", "Existing settings default to automatic device selection");
            Check(oldSettings.PollSeconds == 30 && oldSettings.IconColor == "white" && oldSettings.IconWeight == "bold" && !oldSettings.LowBatteryNotification, "Existing appearance and polling preferences survive");
            oldSettings.PreferredDeviceId = "logitech:device&slot1";
            oldSettings.PreferredDeviceName = "Mouse & Trackball";
            string xml;
            using (StringWriter output = new StringWriter()) { serializer.Serialize(output, oldSettings); xml = output.ToString(); }
            using (StringReader input = new StringReader(xml))
            {
                AppSettings saved = (AppSettings)serializer.Deserialize(input);
                Check(saved.PreferredDeviceId == oldSettings.PreferredDeviceId && saved.PreferredDeviceName == oldSettings.PreferredDeviceName, "Selected device survives the XML settings round trip");
            }
            TestScanFailures();
            TestSettingsMigration();
            Console.WriteLine("PASS: " + assertions + " service assertions.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void TestScanFailures()
    {
        int calls = 0;
        using (ManualResetEvent release = new ManualResetEvent(false))
        using (ManualResetEvent finished = new ManualResetEvent(false))
        {
            MouseBatteryService service = new MouseBatteryService(100, new Dictionary<string, Func<BatteryScan>> {
                { "Test", delegate {
                    int call = Interlocked.Increment(ref calls);
                    if (call == 1) return new BatteryScan { Readings = new List<BatteryReading> { Mouse("test:mouse", "Test", 55, null) } };
                    if (call == 2) { release.WaitOne(); finished.Set(); return new BatteryScan { Readings = new List<BatteryReading> { Mouse("test:mouse", "Test", 54, null) } }; }
                    if (call == 3) throw new IOException("Driver unavailable");
                    return new BatteryScan { Readings = new List<BatteryReading> { Mouse("test:mouse", "Test", 101, null) } };
                } }
            });
            Check(service.Scan().Readings.Single().Percentage == 55, "Successful provider reading reaches the selection layer");
            Check(service.Scan().Readings.Single().Percentage == null, "Timed-out provider clears previously known charge");
            Check(service.Scan().Readings.Single().Percentage == null && calls == 2, "Pending timed-out scan is not adopted or duplicated on the next poll");
            release.Set();
            Check(finished.WaitOne(2000), "Delayed test provider finishes");
            // The worker has signaled immediately before returning. A short join window lets the Task publish completion.
            Thread.Sleep(20);
            BatteryScan failed = service.Scan();
            Check(calls == 3 && failed.Readings.Single().Percentage == null && failed.Diagnostic.Contains("Driver unavailable"), "Late charge is discarded and a new provider error does not restore it");
            Check(service.Scan().Readings.Single().Percentage == null, "Out-of-range provider percentages are rejected");
        }
    }

    private static void TestSettingsMigration()
    {
        string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string temporary = Path.Combine(parent, "MouseBatterySettingsTest-" + Guid.NewGuid().ToString("N"));
        string legacy = Path.Combine(temporary, "old");
        string current = Path.Combine(temporary, "new");
        try
        {
            Directory.CreateDirectory(legacy);
            string oldFile = Path.Combine(legacy, "settings.xml");
            string xml = "<ViperV3BatteryIndicatorSettings><PollSeconds>30</PollSeconds><IconColor>white</IconColor><IconWeight>bold</IconWeight><LowBatteryNotification>false</LowBatteryNotification><PreferredDeviceId>razer:one</PreferredDeviceId><PreferredDeviceName>Saved Mouse</PreferredDeviceName></ViperV3BatteryIndicatorSettings>";
            File.WriteAllText(oldFile, xml);
            AppSettings migrated = AppSettings.LoadFromDirectories(current, legacy);
            Check(migrated.PollSeconds == 30 && migrated.IconColor == "white" && migrated.IconWeight == "bold", "Legacy appearance settings migrate to the generic directory");
            Check(migrated.Theme == "auto", "Settings saved before the theme option follow Windows");
            Check(!migrated.LowBatteryNotification && migrated.PreferredDeviceId == "razer:one", "Migration preserves notifications and selected mouse");
            Check(File.Exists(Path.Combine(current, "settings.xml")), "Migration saves settings in the new directory");
            Check(File.ReadAllText(oldFile) == xml, "Migration leaves the original settings intact");
            migrated.PollSeconds = 120;
            migrated.Theme = "dark";
            migrated.SaveToDirectory(current);
            AppSettings reloaded = AppSettings.LoadFromDirectories(current, legacy);
            Check(reloaded.PollSeconds == 120, "Existing new settings take priority over the legacy copy");
            Check(reloaded.Theme == "dark", "The chosen theme survives a restart");
            File.WriteAllText(Path.Combine(current, "settings.xml"), "<ViperV3BatteryIndicatorSettings><Theme>neon</Theme></ViperV3BatteryIndicatorSettings>");
            Check(AppSettings.LoadFromDirectories(current, legacy).Theme == "auto", "An unknown theme falls back to following Windows");
            Check(AppSettings.LoadFromDirectories(Path.Combine(temporary, "absent"), Path.Combine(temporary, "also-absent")).PollSeconds == 60, "Fresh installations retain defaults");
        }
        finally
        {
            string resolved = Path.GetFullPath(temporary);
            if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe settings test cleanup path");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
