using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ViperBattery
{
    internal sealed class MouseBatteryService
    {
        private readonly object gate = new object();
        private readonly ProviderSlot[] providers;
        private readonly int waitMilliseconds = 9000;
        private string diagnostic = "未取得";

        internal MouseBatteryService()
        {
            RazerBatteryReader razer = new RazerBatteryReader();
            LogitechBatteryReader logitech = new LogitechBatteryReader();
            XenicsBatteryReader xenics = new XenicsBatteryReader();
            WindowsBatteryReader windows = new WindowsBatteryReader();
            providers = new[] {
                new ProviderSlot("Razer", razer.Scan),
                new ProviderSlot("Logitech", logitech.Scan),
                new ProviderSlot("Xenics", xenics.Scan),
                new ProviderSlot("Windows", windows.Scan)
            };
        }

        internal MouseBatteryService(int waitMilliseconds, IDictionary<string, Func<BatteryScan>> scanners)
        {
            if (waitMilliseconds < 0) throw new ArgumentOutOfRangeException("waitMilliseconds");
            this.waitMilliseconds = waitMilliseconds;
            providers = scanners.Select(pair => new ProviderSlot(pair.Key, pair.Value)).ToArray();
        }

        internal BatteryScan Scan()
        {
            lock (gate)
            {
                // Each provider has at most one worker, including when a driver stops responding.
                HashSet<ProviderSlot> started = new HashSet<ProviderSlot>();
                foreach (ProviderSlot provider in providers) if (provider.Begin()) started.Add(provider);
                Stopwatch deadline = Stopwatch.StartNew();
                List<BatteryReading> readings = new List<BatteryReading>();
                StringBuilder log = new StringBuilder();
                foreach (ProviderSlot provider in providers)
                {
                    int remaining = Math.Max(0, waitMilliseconds - (int)deadline.ElapsedMilliseconds);
                    bool completed = started.Contains(provider) && provider.Pending.Wait(remaining);
                    BatteryScan result = completed ? provider.Pending.Result : null;
                    log.AppendLine("[" + provider.Name + "]");
                    if (result != null)
                    {
                        provider.LastDevices = result.Readings ?? new List<BatteryReading>();
                        foreach (BatteryReading reading in provider.LastDevices)
                        {
                            if (reading == null || String.IsNullOrEmpty(reading.DeviceId)) continue;
                            if (String.IsNullOrEmpty(reading.Provider)) reading.Provider = provider.Name;
                            if (reading.Percentage.HasValue && (reading.Percentage.Value < 0 || reading.Percentage.Value > 100))
                            {
                                reading.Percentage = null;
                                reading.Detail = "残量の値が範囲外です。";
                            }
                            if (!reading.Connected) { reading.Percentage = null; reading.IsCharging = null; }
                            readings.Add(reading);
                        }
                        log.AppendLine(result.Diagnostic ?? "取得終了");
                    }
                    else
                    {
                        log.AppendLine("取得の制限時間を超えました。以前の残量は表示しません。");
                        foreach (BatteryReading previous in provider.LastDevices)
                            if (previous != null && !String.IsNullOrEmpty(previous.DeviceId))
                                readings.Add(Unavailable(previous.DeviceId, previous.DeviceName, "取得の制限時間を超えました。"));
                    }
                    log.AppendLine();
                }
                BatteryScan scan = new BatteryScan { Readings = Merge(readings), Diagnostic = log.ToString() };
                diagnostic = scan.Diagnostic;
                return scan;
            }
        }

        internal string Diagnose()
        {
            lock (gate) return diagnostic;
        }

        internal static List<BatteryReading> Merge(IEnumerable<BatteryReading> readings)
        {
            List<BatteryReading> unique = readings.Where(r => r != null && !String.IsNullOrEmpty(r.DeviceId))
                .GroupBy(r => r.DeviceId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(Quality).First()).ToList();
            // A receiver's container can contain several different mice. Only eliminate duplicate
            // Windows-property records; never merge two HID++ child slots or two native devices.
            return unique.Where(candidate => !IsWindows(candidate) || !unique.Any(other =>
                !IsWindows(other) && other.Connected && other.Percentage.HasValue &&
                SameContainer(other.ContainerId, candidate.ContainerId)))
                .OrderBy(r => r.DeviceName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(r => r.Connection).ThenBy(r => r.DeviceId, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsWindows(BatteryReading reading)
        {
            return String.Equals(reading.Provider, "Windows", StringComparison.OrdinalIgnoreCase)
                || reading.DeviceId.StartsWith("windows:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameContainer(string a, string b)
        {
            Guid first, second;
            return Guid.TryParse(a, out first) && Guid.TryParse(b, out second)
                && first != Guid.Empty && first == second;
        }

        private static int Quality(BatteryReading reading)
        {
            return (reading.Connected ? 100 : 0) + (reading.Percentage.HasValue ? 20 : 0)
                + (!String.IsNullOrEmpty(reading.BatteryLevelText) ? 5 : 0) + (!reading.IsApproximate ? 1 : 0);
        }

        internal static BatteryReading Select(IList<BatteryReading> readings, string preferredId, string previousId, string preferredName, string preferredContainerId = null)
        {
            if (!String.IsNullOrEmpty(preferredId))
            {
                BatteryReading selected = readings.FirstOrDefault(r => String.Equals(r.DeviceId, preferredId, StringComparison.OrdinalIgnoreCase));
                if (selected != null && selected.Connected && selected.Percentage.HasValue) return selected;
                // Windows and a native provider can describe the same physical mouse using different IDs.
                BatteryReading sameMouse = readings.Where(r => r.Connected && SameContainer(r.ContainerId, preferredContainerId))
                    .OrderByDescending(Quality).FirstOrDefault();
                return sameMouse ?? selected ?? Unavailable(preferredId, preferredName, "選択したマウスが見つかりません。");
            }
            BatteryReading previous = readings.FirstOrDefault(r => String.Equals(r.DeviceId, previousId, StringComparison.OrdinalIgnoreCase)
                && r.Connected && r.Percentage.HasValue);
            if (previous != null) return previous;
            return readings.OrderByDescending(Quality)
                .ThenBy(r => String.Equals(r.Provider, "Razer", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault()
                ?? Unavailable(null, "マウス", "残量を取得できるマウスが見つかりません。");
        }

        internal static BatteryReading Unavailable(string id, string name, string detail)
        {
            return new BatteryReading {
                DeviceId = id, DeviceName = String.IsNullOrEmpty(name) ? "マウス" : name,
                Connected = false, Detail = detail, Connection = "未接続 / 取得待ち", Timestamp = DateTime.Now
            };
        }

        internal static string LevelText(BatteryReading reading)
        {
            if (reading == null) return "確認中…";
            if (!reading.Connected) return "取得できません";
            if (reading.Percentage.HasValue) return (reading.IsApproximate ? "約 " : "") + reading.Percentage.Value + "%";
            return String.IsNullOrEmpty(reading.BatteryLevelText) ? "取得できません" : reading.BatteryLevelText;
        }

        private sealed class ProviderSlot
        {
            internal readonly string Name;
            private readonly Func<BatteryScan> scan;
            internal Task<BatteryScan> Pending;
            internal List<BatteryReading> LastDevices = new List<BatteryReading>();
            internal ProviderSlot(string name, Func<BatteryScan> scan) { Name = name; this.scan = scan; }

            internal bool Begin()
            {
                if (Pending != null && !Pending.IsCompleted) return false;
                Pending = Task.Factory.StartNew(delegate
                {
                    try { return scan() ?? new BatteryScan { Diagnostic = "結果なし" }; }
                    catch (Exception error)
                    {
                        return new BatteryScan {
                            Readings = LastDevices.Where(r => r != null).Select(r =>
                                Unavailable(r.DeviceId, r.DeviceName, "残量を取得できませんでした。")).ToList(),
                            Diagnostic = error.GetType().Name + ": " + error.Message
                        };
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                return true;
            }
        }
    }
}
