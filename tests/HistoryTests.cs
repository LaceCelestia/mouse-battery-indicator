using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ViperBattery;

internal static class HistoryTests
{
    private static int assertions;
    private static readonly DateTime Start = new DateTime(2026, 11, 8, 3, 0, 0, DateTimeKind.Utc);

    private static int Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mbi-history-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            TestRecordingPolicy();
            TestQueryWindow();
            TestKeys();
            TestPersistence(Path.Combine(directory, "history.tsv"));
            TestCorruptAndExpiredLines(Path.Combine(directory, "corrupt.tsv"));
            TestDailyCompaction(Path.Combine(directory, "compact.tsv"));
            TestUnwritableLocation(directory);
            Console.WriteLine("History tests passed: " + assertions + " assertions.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("History tests failed: " + error);
            return 1;
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch { }
        }
    }

    private static BatteryReading Reading(int? percent, bool? charging, string name = "Razer Viper V3 Pro", string provider = "Razer")
    {
        return new BatteryReading
        {
            Connected = percent.HasValue, Percentage = percent, IsCharging = charging,
            DeviceName = name, Provider = provider, DeviceId = "test:" + name, Timestamp = DateTime.Now
        };
    }

    private static void TestRecordingPolicy()
    {
        BatteryHistory history = new BatteryHistory(null);
        string key = BatteryHistory.KeyFor(Reading(80, false));
        Check(history.Record(Reading(80, false), Start), "The first sample is recorded.");
        Check(!history.Record(Reading(80, false), Start.AddMinutes(1)), "An unchanged level within the heartbeat is skipped.");
        Check(!history.Record(Reading(80, false), Start.AddMinutes(9)), "An unchanged level is skipped until the heartbeat.");
        Check(history.Record(Reading(80, false), Start.AddMinutes(10)), "An unchanged level is kept every heartbeat so gaps stay detectable.");
        Check(history.Record(Reading(79, false), Start.AddMinutes(11)), "A level change is recorded immediately.");
        Check(history.Record(Reading(79, true), Start.AddMinutes(12)), "A charging change is recorded immediately.");
        Check(history.Record(Reading(79, null), Start.AddMinutes(13)), "An unknown charging state is a distinct state.");
        Check(!history.Record(Reading(null, null), Start.AddMinutes(14)), "A disconnected mouse adds nothing.");
        BatteryReading coarse = Reading(50, false);
        coarse.Percentage = null;
        coarse.BatteryLevelText = "残量良好";
        Check(!history.Record(coarse, Start.AddMinutes(15)), "Coarse levels without a percentage are not charted as numbers.");
        Check(!history.Record(Reading(101, false), Start.AddMinutes(16)), "Out-of-range levels are rejected.");
        Check(!history.Record(null, Start.AddMinutes(17)), "A missing reading is ignored.");
        Check(history.Count(key) == 5, "Exactly the meaningful samples are kept (" + history.Count(key) + ").");

        // The clock can move backwards; the series stays ordered for drawing.
        Check(history.Record(Reading(60, false), Start.AddMinutes(5)), "A sample from an earlier clock time is recorded.");
        IList<BatterySample> all = history.Query(key, Start.AddDays(-1), Start.AddDays(1));
        Check(all.Select(s => s.Utc).SequenceEqual(all.Select(s => s.Utc).OrderBy(t => t)), "Samples stay ordered after a clock change.");

        BatterySample stored = history.Query(key, Start, Start).Single();
        Check(stored.Utc.Kind == DateTimeKind.Utc && stored.Utc == Start, "Times are stored in UTC.");
    }

    private static void TestQueryWindow()
    {
        BatteryHistory history = new BatteryHistory(null);
        string key = BatteryHistory.KeyFor(Reading(90, false));
        history.Record(Reading(90, false), Start);
        history.Record(Reading(89, false), Start.AddMinutes(20));
        history.Record(Reading(88, false), Start.AddMinutes(35));
        history.Record(Reading(70, false), Start.AddHours(5));

        IList<BatterySample> window = history.Query(key, Start.AddMinutes(30), Start.AddHours(6));
        Check(window.Count == 3 && window[0].Percent == 89, "A sample just before the window carries the line in from the left edge.");
        window = history.Query(key, Start.AddHours(1), Start.AddHours(6));
        Check(window.Count == 1 && window[0].Percent == 70, "A sample long before the window is not connected across the gap.");
        window = history.Query(key, Start.AddHours(6), Start.AddHours(7));
        Check(window.Count == 0, "A window after the last sample beyond the gap limit is empty.");
        Check(history.Query("Razer|Unknown", Start, Start.AddDays(1)).Count == 0, "Unknown devices have no history.");
        Check(history.Query(null, Start, Start.AddDays(1)).Count == 0, "A missing key has no history.");
    }

    private static void TestKeys()
    {
        Check(BatteryHistory.KeyFor(Reading(50, false, "Name\twith\r\nbreaks")) == "Razer|Name with  breaks", "Keys cannot break the tab-separated format.");
        Check(BatteryHistory.KeyFor(Reading(50, false, " ")) == null, "A nameless reading has no key.");
        Check(BatteryHistory.KeyFor(Reading(50, false, "MX Master 3S", "Logitech")) != BatteryHistory.KeyFor(Reading(50, false, "MX Master 3S", "Windows")),
            "The same product over Bluetooth and a receiver keeps separate logs.");

        BatteryHistory history = new BatteryHistory(null);
        history.Record(Reading(64, false), Start);
        BatteryReading offline = MouseBatteryService.Unavailable("test:Razer Viper V3 Pro", "Razer Viper V3 Pro", "offline");
        Check(history.ResolveKey(offline) == "Razer|Razer Viper V3 Pro", "A disconnected mouse still shows its own history.");
        Check(history.ResolveKey(Reading(64, false, "Razer Viper V3 Pro", "Windows")) == "Windows|Razer Viper V3 Pro",
            "A connected mouse never borrows another provider's log.");
    }

    private static void TestPersistence(string path)
    {
        BatteryHistory history = BatteryHistory.Load(path, Start);
        Check(!File.Exists(path), "Loading a missing log does not create a file.");
        history.Record(Reading(100, true), Start);
        history.Record(Reading(99, false), Start.AddMinutes(30));
        history.Record(Reading(45, false, "MX Master 3S", "Logitech"), Start.AddMinutes(31));
        Check(File.Exists(path), "Samples are appended to the log.");
        string text = File.ReadAllText(path, Encoding.UTF8);
        Check(File.ReadAllBytes(path)[0] == (byte)'2', "The log is written without a byte order mark.");
        Check(text.Contains("2026-11-08T03:00:00Z\t100\t1\tRazer|Razer Viper V3 Pro"), "Lines use invariant UTC time, percent, charging, and key.");

        BatteryHistory loaded = BatteryHistory.Load(path, Start.AddHours(1));
        IList<BatterySample> razer = loaded.Query("Razer|Razer Viper V3 Pro", Start.AddDays(-1), Start.AddDays(1));
        Check(razer.Count == 2 && razer[0].Percent == 100 && razer[0].Charging == true && razer[1].Charging == false, "Samples survive a restart.");
        Check(loaded.Count("Logitech|MX Master 3S") == 1, "Each mouse keeps its own series.");
        Check(!loaded.Record(Reading(99, false), Start.AddMinutes(35)), "Deduplication continues across restarts.");
    }

    private static void TestCorruptAndExpiredLines(string path)
    {
        File.WriteAllText(path, String.Join("\n", new[] {
            "2026-11-08T02:00:00Z\t55\t0\tRazer|Viper",
            "garbage",
            "2026-11-08T02:10:00Z\t101\t0\tRazer|Viper",
            "2026-11-08T02:20:00Z\t50\tmaybe\tRazer|Viper",
            "2026-11-08T02:30:00Z\t50\t0\t",
            "2026/11/08 02:40:00\t50\t0\tRazer|Viper",
            "2026-10-01T00:00:00Z\t90\t0\tRazer|Viper",
            "2026-11-08T02:50:00Z\t48\t\tRazer|Viper",
            ""
        }), new UTF8Encoding(false));
        BatteryHistory history = BatteryHistory.Load(path, Start);
        IList<BatterySample> samples = history.Query("Razer|Viper", Start.AddDays(-30), Start);
        Check(samples.Count == 2 && samples[0].Percent == 55 && samples[1].Percent == 48 && samples[1].Charging == null,
            "Invalid and expired lines are skipped (" + samples.Count + " kept).");
        string[] lines = File.ReadAllLines(path);
        Check(lines.Length == 2, "The log is compacted after dropping invalid or expired lines.");
        Check(!File.Exists(path + ".tmp"), "Compaction leaves no temporary file.");
    }

    private static void TestDailyCompaction(string path)
    {
        BatteryHistory history = BatteryHistory.Load(path, Start);
        history.Record(Reading(90, false), Start);
        for (int day = 1; day <= 10; day++) history.Record(Reading(90 - day, false), Start.AddDays(day).AddMinutes(1));
        DateTime now = Start.AddDays(10).AddMinutes(1);
        BatteryHistory loaded = BatteryHistory.Load(path, now);
        IList<BatterySample> samples = loaded.Query("Razer|Razer Viper V3 Pro", now.AddDays(-30), now);
        Check(samples.All(s => now - s.Utc <= BatteryHistory.Retention), "Samples older than the retention period are removed.");
        Check(File.ReadAllLines(path).Length <= 9, "The log file stays bounded by the retention period.");
    }

    private static void TestUnwritableLocation(string directory)
    {
        // A directory where the log file should be makes every write fail; history must keep working in memory.
        string blocked = Path.Combine(directory, "blocked.tsv");
        Directory.CreateDirectory(blocked);
        BatteryHistory history = BatteryHistory.Load(blocked, Start);
        int reported = 0;
        history.ReportError = delegate { reported++; };
        Check(history.Record(Reading(70, false), Start), "Recording continues when the log cannot be written.");
        Check(history.Count("Razer|Razer Viper V3 Pro") == 1, "Samples are still available in memory.");
        history.Record(Reading(69, false), Start.AddMinutes(1));
        Check(reported == 1, "A failing log is reported once, not on every poll (" + reported + ").");
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
