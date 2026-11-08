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
        // When the log could not be read at startup, never rewrite it: that would drop the unread lines.
        private bool appendOnly;

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

        // A disconnected mouse is reported without its provider; use the most recent log with its product name.
        internal string ResolveKey(BatteryReading reading)
        {
            string key = KeyFor(reading);
            if (key == null || reading.Connected || series.ContainsKey(key)) return key;
            string suffix = "|" + Clean(reading.DeviceName);
            string best = null;
            DateTime newest = DateTime.MinValue;
            foreach (KeyValuePair<string, List<BatterySample>> pair in series)
            {
                if (!pair.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || pair.Value.Count == 0) continue;
                DateTime last = pair.Value[pair.Value.Count - 1].Utc;
                if (best == null || last > newest) { best = pair.Key; newest = last; }
            }
            return best ?? key;
        }

        private static string Clean(string value)
        {
            return (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        internal static BatteryHistory Load(string path, DateTime nowUtc, Action<Exception> reportError = null)
        {
            BatteryHistory history = new BatteryHistory(path);
            if (reportError != null) history.ReportError = reportError;
            history.lastCompactUtc = nowUtc;
            if (String.IsNullOrEmpty(path)) return history;
            bool dirty = false;
            try
            {
                FileInfo file = new FileInfo(path);
                if (!file.Exists) return history;
                IEnumerable<string> lines;
                if (file.Length > MaxFileBytes)
                {
                    // Keep the newest part of an oversized log instead of discarding all of it.
                    lines = ReadTail(path, MaxFileBytes * 3 / 4);
                    dirty = true;
                }
                else lines = File.ReadLines(path, Encoding.UTF8);
                foreach (string line in lines)
                {
                    string key;
                    BatterySample sample = Parse(line, out key);
                    if (sample == null || nowUtc - sample.Utc > Retention) { dirty = true; continue; }
                    history.Insert(key, sample);
                }
            }
            catch (Exception error)
            {
                history.appendOnly = true;
                history.ReportError(error);
                return history;
            }
            if (dirty) history.Rewrite();
            return history;
        }

        private static List<string> ReadTail(string path, long bytes)
        {
            List<string> lines = new List<string>();
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(-bytes, SeekOrigin.End);
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    reader.ReadLine(); // The first line is usually cut in the middle.
                    string line;
                    while ((line = reader.ReadLine()) != null) lines.Add(line);
                }
            }
            return lines;
        }

        // Records every mouse from one scan; two mice with the same product name keep the first reading only.
        internal int RecordScan(IEnumerable<BatteryReading> readings, DateTime nowUtc)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int recorded = 0;
            if (readings == null) return 0;
            foreach (BatteryReading reading in readings)
            {
                string key = KeyFor(reading);
                if (key == null || reading == null || !reading.Connected || !reading.Percentage.HasValue || !seen.Add(key)) continue;
                if (Record(reading, nowUtc)) recorded++;
            }
            return recorded;
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
        // Voltage estimates jitter by 1%, so for those a 1% move also waits for the heartbeat.
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
                // Compare with the sample just before this time: the last one, unless the clock moved back.
                BatterySample previous = null;
                for (int i = samples.Count - 1; i >= 0; i--)
                    if (samples[i].Utc <= sample.Utc) { previous = samples[i]; break; }
                if (previous != null && sample.Utc - previous.Utc < Heartbeat && previous.Charging == sample.Charging
                    && (previous.Percent == percent || (reading.IsApproximate && Math.Abs(previous.Percent - percent) <= 1)))
                    return false;
            }
            Insert(key, sample);
            if (!appendOnly && nowUtc - lastCompactUtc > TimeSpan.FromDays(1))
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
