using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ViperBattery
{
    internal sealed class BatterySample
    {
        internal DateTime Utc;
        internal int Percent;
        internal bool? Charging;
    }

    // Keeps a small local log of battery levels so the popup can chart the last day or week.
    // Each line is "UTC time<TAB>percent<TAB>charging (1/0/empty)<TAB>device key".
    internal sealed class BatteryHistory
    {
        internal static readonly TimeSpan Retention = TimeSpan.FromDays(8);
        internal static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(10);
        // A longer silence means the app, PC, or mouse was off, so the chart breaks the line.
        internal static readonly TimeSpan GapLimit = TimeSpan.FromMinutes(25);
        private const string TimeFormat = "yyyy-MM-ddTHH:mm:ssZ";
        private const long MaxFileBytes = 4L * 1024 * 1024;

        private readonly Dictionary<string, List<BatterySample>> series = new Dictionary<string, List<BatterySample>>(StringComparer.OrdinalIgnoreCase);
        private readonly string path;
        private DateTime lastCompactUtc;
        private bool reportedWriteFailure;

        internal Action<Exception> ReportError = AppLog.Write;

        internal BatteryHistory(string path)
        {
            this.path = path;
        }

        internal static string DefaultPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MouseBatteryIndicator", "history.tsv");
            }
        }

        // A stable key across USB ports and reconnections: the HID path changes, the product name does not.
        internal static string KeyFor(BatteryReading reading)
        {
            if (reading == null || String.IsNullOrWhiteSpace(reading.DeviceName)) return null;
            return Clean(reading.Provider) + "|" + Clean(reading.DeviceName);
        }

        // A disconnected mouse is reported without its provider; match its log by product name.
        internal string ResolveKey(BatteryReading reading)
        {
            string key = KeyFor(reading);
            if (key == null || reading.Connected || series.ContainsKey(key)) return key;
            string suffix = "|" + Clean(reading.DeviceName);
            foreach (string candidate in series.Keys)
                if (candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return candidate;
            return key;
        }

        private static string Clean(string value)
        {
            return (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        internal static BatteryHistory Load(string path, DateTime nowUtc)
        {
            BatteryHistory history = new BatteryHistory(path);
            history.lastCompactUtc = nowUtc;
            if (String.IsNullOrEmpty(path)) return history;
            bool dirty = false;
            try
            {
                FileInfo file = new FileInfo(path);
                if (!file.Exists) return history;
                if (file.Length > MaxFileBytes)
                {
                    // An oversized log is not worth parsing at startup; start a fresh one.
                    history.Rewrite();
                    return history;
                }
                foreach (string line in File.ReadLines(path, Encoding.UTF8))
                {
                    string key;
                    BatterySample sample = Parse(line, out key);
                    if (sample == null || nowUtc - sample.Utc > Retention) { dirty = true; continue; }
                    history.Insert(key, sample);
                }
            }
            catch (Exception error)
            {
                AppLog.Write(error);
                return history;
            }
            if (dirty) history.Rewrite();
            return history;
        }

        internal static BatterySample Parse(string line, out string key)
        {
            key = null;
            if (String.IsNullOrEmpty(line)) return null;
            string[] parts = line.Split('\t');
            if (parts.Length != 4 || parts[3].Trim().Length == 0) return null;
            DateTime utc;
            int percent;
            if (!DateTime.TryParseExact(parts[0], TimeFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc)) return null;
            if (!Int32.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out percent) || percent < 0 || percent > 100) return null;
            bool? charging;
            if (parts[2] == "1") charging = true;
            else if (parts[2] == "0") charging = false;
            else if (parts[2].Length == 0) charging = null;
            else return null;
            key = parts[3];
            return new BatterySample { Utc = utc, Percent = percent, Charging = charging };
        }

        private static string Format(string key, BatterySample sample)
        {
            return sample.Utc.ToString(TimeFormat, CultureInfo.InvariantCulture) + "\t"
                + sample.Percent.ToString(CultureInfo.InvariantCulture) + "\t"
                + (sample.Charging.HasValue ? (sample.Charging.Value ? "1" : "0") : "") + "\t" + key;
        }

        // Records changes immediately and unchanged levels at most every Heartbeat.
        internal bool Record(BatteryReading reading, DateTime nowUtc)
        {
            if (reading == null || !reading.Connected || !reading.Percentage.HasValue) return false;
            int percent = reading.Percentage.Value;
            if (percent < 0 || percent > 100) return false;
            string key = KeyFor(reading);
            if (key == null) return false;
            BatterySample sample = new BatterySample { Utc = TrimToSecond(nowUtc), Percent = percent, Charging = reading.IsCharging };
            List<BatterySample> samples;
            if (series.TryGetValue(key, out samples) && samples.Count > 0)
            {
                BatterySample last = samples[samples.Count - 1];
                TimeSpan elapsed = sample.Utc - last.Utc;
                if (last.Percent == percent && last.Charging == sample.Charging && elapsed >= TimeSpan.Zero && elapsed < Heartbeat)
                    return false;
            }
            Insert(key, sample);
            if (nowUtc - lastCompactUtc > TimeSpan.FromDays(1))
            {
                lastCompactUtc = nowUtc;
                Prune(nowUtc);
                Rewrite();
            }
            else Append(Format(key, sample) + Environment.NewLine);
            return true;
        }

        internal IList<BatterySample> Query(string key, DateTime fromUtc, DateTime toUtc)
        {
            List<BatterySample> samples;
            if (key == null || !series.TryGetValue(key, out samples)) return new List<BatterySample>();
            List<BatterySample> result = new List<BatterySample>();
            BatterySample before = null;
            foreach (BatterySample sample in samples)
            {
                if (sample.Utc < fromUtc) { before = sample; continue; }
                if (sample.Utc > toUtc) break;
                result.Add(sample);
            }
            // Carry the line in from the left edge when the previous sample is close enough to connect.
            if (before != null && (result.Count == 0 ? toUtc : result[0].Utc) - before.Utc <= GapLimit)
                result.Insert(0, before);
            return result;
        }

        internal int Count(string key)
        {
            List<BatterySample> samples;
            return key != null && series.TryGetValue(key, out samples) ? samples.Count : 0;
        }

        private static DateTime TrimToSecond(DateTime utc)
        {
            return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        }

        private void Insert(string key, BatterySample sample)
        {
            List<BatterySample> samples;
            if (!series.TryGetValue(key, out samples))
            {
                samples = new List<BatterySample>();
                series[key] = samples;
            }
            // The clock can move backwards (manual change, time sync); keep the list ordered.
            int index = samples.Count;
            while (index > 0 && samples[index - 1].Utc > sample.Utc) index--;
            samples.Insert(index, sample);
        }

        private void Prune(DateTime nowUtc)
        {
            foreach (string key in series.Keys.ToList())
            {
                series[key].RemoveAll(sample => nowUtc - sample.Utc > Retention);
                if (series[key].Count == 0) series.Remove(key);
            }
        }

        private void Append(string text)
        {
            if (String.IsNullOrEmpty(path)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, text, new UTF8Encoding(false));
            }
            catch (Exception error) { ReportWriteFailure(error); }
        }

        private void Rewrite()
        {
            if (String.IsNullOrEmpty(path)) return;
            string temporary = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                StringBuilder text = new StringBuilder();
                foreach (KeyValuePair<string, List<BatterySample>> pair in series)
                    foreach (BatterySample sample in pair.Value)
                        text.Append(Format(pair.Key, sample)).Append(Environment.NewLine);
                File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch (Exception error)
            {
                ReportWriteFailure(error);
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }
        }

        private void ReportWriteFailure(Exception error)
        {
            // History is optional; log the first failure instead of every poll.
            if (reportedWriteFailure) return;
            reportedWriteFailure = true;
            ReportError(error);
        }
    }
}
