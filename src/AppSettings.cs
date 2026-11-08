using System;
using System.IO;
using System.Security;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace ViperBattery
{
    [XmlRoot("ViperV3BatteryIndicatorSettings")]
    public sealed class AppSettings
    {
        public int PollSeconds { get; set; }
        public string IconColor { get; set; }
        public string IconWeight { get; set; }
        public string Theme { get; set; }
        public bool LowBatteryNotification { get; set; }
        public string PreferredDeviceId { get; set; }
        public string PreferredDeviceName { get; set; }
        public string PreferredDeviceContainerId { get; set; }

        public AppSettings()
        {
            PollSeconds = 60;
            IconColor = "auto";
            IconWeight = "normal";
            Theme = "auto";
            LowBatteryNotification = true;
            PreferredDeviceId = "";
            PreferredDeviceName = "";
            PreferredDeviceContainerId = "";
        }

        private static string SettingsDirectory
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MouseBatteryIndicator");
            }
        }

        private static string SettingsPath
        {
            get { return Path.Combine(SettingsDirectory, "settings.xml"); }
        }

        public static AppSettings Load()
        {
            return LoadFromDirectories(SettingsDirectory, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ViperV3BatteryIndicator"));
        }

        internal static AppSettings LoadFromDirectories(string directory, string legacyDirectory)
        {
            string path = Path.Combine(directory, "settings.xml");
            bool migrate = !File.Exists(path);
            if (migrate) path = Path.Combine(legacyDirectory, "settings.xml");
            try
            {
                if (!File.Exists(path))
                    return new AppSettings();

                XmlReaderSettings readerSettings = new XmlReaderSettings();
                readerSettings.DtdProcessing = DtdProcessing.Prohibit;
                readerSettings.XmlResolver = null;
                readerSettings.MaxCharactersInDocument = 65536;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (XmlReader reader = XmlReader.Create(stream, readerSettings))
                {
                    AppSettings result = (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(reader);
                    if (result == null)
                        return new AppSettings();
                    result.Sanitize();
                    if (migrate)
                    {
                        try { result.SaveToDirectory(directory); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                        catch (SecurityException) { }
                    }
                    return result;
                }
            }
            catch (InvalidOperationException) { PreserveCorruptSettings(path); }
            catch (XmlException) { PreserveCorruptSettings(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
            return new AppSettings();
        }

        public void Save()
        {
            SaveToDirectory(SettingsDirectory);
        }

        internal void SaveToDirectory(string directory)
        {
            Sanitize();
            string path = Path.Combine(directory, "settings.xml");
            Directory.CreateDirectory(directory);
            string temporaryPath = Path.Combine(directory, "settings." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new XmlSerializer(typeof(AppSettings)).Serialize(stream, this);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                    File.Replace(temporaryPath, path, null);
                else
                    File.Move(temporaryPath, path);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (SecurityException) { }
            }
        }

        private void Sanitize()
        {
            PreferredDeviceId = (PreferredDeviceId ?? "").Trim();
            PreferredDeviceName = (PreferredDeviceName ?? "").Trim();
            Guid containerId;
            PreferredDeviceContainerId = Guid.TryParse(PreferredDeviceContainerId, out containerId) && containerId != Guid.Empty
                ? containerId.ToString("D") : "";
            if (PreferredDeviceId.Length > 2048) PreferredDeviceId = "";
            if (PreferredDeviceName.Length > 128) PreferredDeviceName = PreferredDeviceName.Substring(0, 128);
            if (PollSeconds != 15 && PollSeconds != 30 && PollSeconds != 60 && PollSeconds != 120)
                PollSeconds = 60;
            string color = (IconColor ?? "auto").Trim().ToLowerInvariant();
            IconColor = color == "green" || color == "white" || color == "black" ? color : "auto";
            string weight = (IconWeight ?? "normal").Trim().ToLowerInvariant();
            IconWeight = weight == "light" || weight == "bold" ? weight : "normal";
            string theme = (Theme ?? "auto").Trim().ToLowerInvariant();
            Theme = theme == "light" || theme == "dark" ? theme : "auto";
        }

        private static void PreserveCorruptSettings(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Move(path, Path.Combine(Path.GetDirectoryName(path),
                        "settings.corrupt." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".xml"));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
        }
    }

    public static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MouseBatteryIndicator";
        private const string LegacyValueName = "ViperV3BatteryIndicator";

        private static string StartupCommand
        {
            get { return "\"" + Application.ExecutablePath + "\" --background"; }
        }

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    {
                        return key != null && (String.Equals(key.GetValue(ValueName) as string, StartupCommand, StringComparison.OrdinalIgnoreCase)
                            || String.Equals(key.GetValue(LegacyValueName) as string, StartupCommand, StringComparison.OrdinalIgnoreCase));
                    }
                }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
                catch (SecurityException) { return false; }
            }
        }

        public static void SetEnabled(bool enabled)
        {
            if (enabled)
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null)
                        throw new IOException("Windows startup registry key is unavailable.");
                    key.SetValue(ValueName, StartupCommand, RegistryValueKind.String);
                    RemoveLegacyEntry(key);
                }
            }
            else
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key != null)
                    {
                        if (String.Equals(key.GetValue(ValueName) as string, StartupCommand, StringComparison.OrdinalIgnoreCase))
                            key.DeleteValue(ValueName, false);
                        RemoveLegacyEntry(key);
                    }
                }
            }
        }

        private static void RemoveLegacyEntry(RegistryKey key)
        {
            string value = key.GetValue(LegacyValueName) as string;
            string legacyCommand = "\"" + Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "ViperV3BatteryIndicator.exe") + "\" --background";
            if (String.Equals(value, StartupCommand, StringComparison.OrdinalIgnoreCase)
                || String.Equals(value, legacyCommand, StringComparison.OrdinalIgnoreCase))
                key.DeleteValue(LegacyValueName, false);
        }
    }
}
