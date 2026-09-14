using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ViperBattery
{
    // Protocol facts: OpenRazer driver/razercommon.h and razermouse_driver.c;
    // Windows mouse-collection access: github.com/damianyoko/viper-tray.
    // All command construction is restricted to the two battery GET commands.
    internal static class BatteryProtocol
    {
        internal const int ReportLength = 91;
        internal const byte BatteryCommand = 0x80;
        internal const byte ChargingCommand = 0x84;

        internal static byte[] CreateRequest(byte command, int length)
        {
            return CreateRequest(command, length, 0x1F);
        }

        internal static byte[] CreateRequest(byte command, int length, byte transaction)
        {
            if (command != BatteryCommand && command != ChargingCommand)
                throw new ArgumentOutOfRangeException("command");
            if (length < ReportLength || length > 4096)
                throw new ArgumentOutOfRangeException("length");
            if (!ValidTransaction(transaction)) throw new ArgumentOutOfRangeException("transaction");
            byte[] report = new byte[length];
            report[2] = transaction;
            report[6] = 2;
            report[7] = 7;
            report[8] = command;
            report[89] = Checksum(report);
            return report;
        }

        internal static byte Checksum(byte[] report)
        {
            byte result = 0;
            for (int i = 3; i <= 88; i++) result ^= report[i];
            return result;
        }

        internal static bool TryParse(byte[] report, byte command, out byte value, out string error)
        {
            return TryParse(report, command, 0x1F, out value, out error);
        }

        internal static bool TryParse(byte[] report, byte command, byte transaction, out byte value, out string error)
        {
            value = 0;
            error = null;
            if (command != BatteryCommand && command != ChargingCommand)
                error = "許可されていないコマンドです。";
            else if (!ValidTransaction(transaction))
                error = "未対応のトランザクションです。";
            else if (report == null || report.Length < ReportLength)
                error = "応答が短すぎます。";
            else if (report[0] != 0 || report[2] != transaction || report[3] != 0 ||
                     report[4] != 0 || report[5] != 0 || report[6] != 2 ||
                     report[7] != 7 || report[8] != command || report[90] != 0)
                error = "応答のヘッダーが問い合わせと一致しません。";
            else if (Checksum(report) != report[89])
                error = "応答のチェックサムが一致しません。";
            else if (report[1] != 2)
                error = StatusDescription(report[1]);
            else if (command == ChargingCommand && report[10] > 1)
                error = "充電状態の値が不正です。";
            if (error != null) return false;
            value = report[10];
            return true;
        }

        internal static int ToPercentage(byte value)
        {
            return (value * 100 + 127) / 255;
        }

        private static bool ValidTransaction(byte transaction)
        {
            return transaction == 0x1F || transaction == 0x3F || transaction == 0xFF;
        }

        private static string StatusDescription(byte status)
        {
            switch (status)
            {
                case 0: return "マウスの応答がまだ準備されていません。";
                case 1: return "マウスが別の処理を実行中です。";
                case 3: return "マウスが問い合わせに失敗しました。";
                case 4: return "マウスから応答がありません。スリープや接続を確認してください。";
                case 5: return "マウスがこの問い合わせに対応していません。";
                default: return "マウスが不明な状態を返しました (0x" + status.ToString("X2") + ")。";
            }
        }
    }

    internal sealed class RazerProduct
    {
        internal readonly string Name;
        internal readonly byte Transaction;
        internal readonly bool Wired;
        internal readonly bool Rechargeable;
        internal readonly int ResponseDelay;

        internal RazerProduct(string name, byte transaction, bool wired, bool rechargeable, int responseDelay = 80)
        {
            Name = "Razer " + name;
            Transaction = transaction;
            Wired = wired;
            Rechargeable = rechargeable;
            ResponseDelay = responseDelay;
        }
    }

    public sealed class RazerBatteryReader
    {
        private readonly object gate = new object();
        private Task<BatteryScan> pending;
        private const int QueryBudget = 4000;

        public BatteryReading Read()
        {
            BatteryScan scan = Scan();
            foreach (BatteryReading reading in scan.Readings)
                if (reading.Percentage.HasValue) return reading;
            return scan.Readings.Count > 0 ? scan.Readings[0] : Unavailable(scan.Diagnostic);
        }

        public BatteryScan Scan()
        {
            Task<BatteryScan> operation;
            lock (gate)
            {
                // Keep at most one driver worker. A timed-out operation is discarded,
                // even if it later completes; never publish values from an earlier scan.
                if (pending != null && !pending.IsCompleted)
                    return EmptyScan("前回の USB 問い合わせの終了を待っています。");
                pending = Task.Factory.StartNew<BatteryScan>(ScanCore, CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
                operation = pending;
            }
            if (!operation.Wait(5000))
                return EmptyScan("USB からの応答が遅れているため、今回の残量は表示できません。");
            return operation.Result;
        }

        public string Diagnose()
        {
            BatteryScan scan = Scan();
            StringBuilder report = new StringBuilder("Razer Battery Indicator\r\n");
            foreach (BatteryReading reading in scan.Readings)
            {
                report.AppendLine("取得日時: " + reading.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
                report.AppendLine("デバイス: " + reading.DeviceName);
                report.AppendLine("接続: " + reading.Connection);
                report.AppendLine("バッテリー: " + (reading.Percentage.HasValue ? reading.Percentage.Value + "%" : "取得できません"));
                report.AppendLine("充電: " + (reading.IsCharging.HasValue ? (reading.IsCharging.Value ? "充電中" : "充電していません") : "不明"));
                report.AppendLine("状態: " + reading.Detail);
                report.AppendLine();
            }
            report.Append(scan.Diagnostic);
            return report.ToString();
        }

        private BatteryScan ScanCore()
        {
            BatteryScan result = new BatteryScan();
            StringBuilder log = new StringBuilder();
            Stopwatch deadline = Stopwatch.StartNew();
            Mutex queryMutex = null;
            bool ownsMutex = false;
            try
            {
                // Preserve the mutex name used by previous versions and diagnostics.
                queryMutex = new Mutex(false, "Local\\RazerViperV3BatteryIndicator.HidQuery");
                try { ownsMutex = queryMutex.WaitOne(800); }
                catch (AbandonedMutexException) { ownsMutex = true; }
                if (!ownsMutex)
                {
                    log.AppendLine("別の残量取得が実行中です。少し待って更新してください。");
                    return result;
                }
                List<HidPath> paths = EnumeratePaths(log);
                Dictionary<string, BatteryReading> devices = new Dictionary<string, BatteryReading>(StringComparer.OrdinalIgnoreCase);
                int candidates = 0;
                foreach (HidPath endpoint in paths)
                {
                    BatteryReading existing;
                    string identity = DeviceIdentity(endpoint.Path, endpoint.ContainerId);
                    if (devices.TryGetValue(identity, out existing) && existing.Percentage.HasValue) continue;
                    using (SafeFileHandle handle = Native.CreateFile(endpoint.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                    {
                        if (handle.IsInvalid)
                        {
                            log.AppendLine(Win32Error("デバイスを開けません", Marshal.GetLastWin32Error()));
                            continue;
                        }
                        Native.HidAttributes attributes = new Native.HidAttributes();
                        attributes.Size = Marshal.SizeOf(typeof(Native.HidAttributes));
                        if (!Native.HidD_GetAttributes(handle, ref attributes) || attributes.VendorID != 0x1532) continue;
                        RazerProduct product = ProductFor(attributes.ProductID);
                        if (product == null) continue;
                        Native.HidCaps caps;
                        if (!GetCaps(handle, out caps)) continue;
                        log.AppendLine("VID 1532 / PID " + attributes.ProductID.ToString("X4") +
                            " (" + product.Name + "): UsagePage=" + caps.UsagePage + ", Usage=" + caps.Usage +
                            ", FeatureReport=" + caps.FeatureReportByteLength);
                        // Battery commands are sent only to known models' mouse collections.
                        if (caps.UsagePage != 1 || caps.Usage != 2 ||
                            caps.FeatureReportByteLength < BatteryProtocol.ReportLength ||
                            caps.FeatureReportByteLength > 4096) continue;
                        if (existing == null)
                        {
                            existing = new BatteryReading
                            {
                                DeviceId = identity, Provider = "Razer", ContainerId = endpoint.ContainerId,
                                DeviceName = product.Name, Connection = product.Wired ? "USB 有線" : "USB ワイヤレス",
                                Detail = "残量を取得できません。マウスの電源やスリープ状態を確認してください。",
                                Timestamp = DateTime.Now
                            };
                            devices.Add(identity, existing);
                            result.Readings.Add(existing);
                        }
                        if (++candidates > 32 || deadline.ElapsedMilliseconds >= QueryBudget)
                        {
                            existing.Detail = "今回の問い合わせ時間内に残量を取得できませんでした。";
                            continue;
                        }
                        byte level;
                        string error;
                        if (!Query(handle, caps.FeatureReportByteLength, BatteryProtocol.BatteryCommand,
                            product, deadline, log, out level, out error))
                        {
                            existing.Detail = error;
                            continue;
                        }
                        bool? charging = product.Rechargeable ? (bool?)null : false;
                        string chargingError = null;
                        if (product.Rechargeable)
                        {
                            byte chargingValue;
                            if (Query(handle, caps.FeatureReportByteLength, BatteryProtocol.ChargingCommand,
                                product, deadline, log, out chargingValue, out chargingError)) charging = chargingValue == 1;
                        }
                        existing.Connected = true;
                        existing.Percentage = BatteryProtocol.ToPercentage(level);
                        existing.IsCharging = charging;
                        existing.Detail = chargingError == null ? "マウスから取得しました。" : "残量を取得しました。充電状態は取得できません。";
                        existing.Timestamp = DateTime.Now;
                    }
                }
                if (result.Readings.Count == 0)
                    log.AppendLine("対応する Razer マウスが見つかりません。USB 接続を確認してください。");
                return result;
            }
            catch (Exception exception)
            {
                log.AppendLine(exception.GetType().Name + ": " + exception.Message);
                return result;
            }
            finally
            {
                if (ownsMutex) queryMutex.ReleaseMutex();
                if (queryMutex != null) queryMutex.Dispose();
                log.AppendLine("問い合わせ時間: " + deadline.ElapsedMilliseconds + " ms");
                result.Diagnostic = log.ToString();
            }
        }

        private static BatteryScan EmptyScan(string message)
        {
            return new BatteryScan { Diagnostic = message };
        }

        internal static string DeviceIdentity(string path, string containerId)
        {
            // Containers identify physical devices, whereas model names do not.
            // A complete interface path remains unique if Windows has no container ID.
            return "razer:" + (String.IsNullOrEmpty(containerId) ? path : containerId).ToLowerInvariant();
        }

        // Model IDs and the two battery command transaction IDs are facts from:
        // https://github.com/openrazer/openrazer/blob/master/driver/razermouse_driver.h
        // https://github.com/openrazer/openrazer/blob/master/driver/razermouse_driver.c
        // (razer_attr_read_charge_level / razer_attr_read_charge_status).
        internal static RazerProduct ProductFor(ushort product)
        {
            switch (product)
            {
                case 0x007A: return new RazerProduct("Viper Ultimate", 0xFF, true, true);
                case 0x007B: return new RazerProduct("Viper Ultimate", 0xFF, false, true);
                case 0x007C: return new RazerProduct("DeathAdder V2 Pro", 0x3F, true, true);
                case 0x007D: return new RazerProduct("DeathAdder V2 Pro", 0x3F, false, true);
                case 0x0083: return new RazerProduct("Basilisk X HyperSpeed", 0xFF, false, false);
                case 0x0086: return new RazerProduct("Basilisk Ultimate", 0x1F, true, true);
                case 0x0088: return new RazerProduct("Basilisk Ultimate", 0x1F, false, true);
                case 0x008F: return new RazerProduct("Naga Pro", 0x1F, true, true);
                case 0x0090: return new RazerProduct("Naga Pro", 0x1F, false, true);
                case 0x0094: return new RazerProduct("Orochi V2", 0x1F, false, false, 400);
                case 0x009C: return new RazerProduct("DeathAdder V2 X HyperSpeed", 0x1F, false, false);
                case 0x009E: return new RazerProduct("Viper Mini Signature Edition", 0x1F, true, true);
                case 0x009F: return new RazerProduct("Viper Mini Signature Edition", 0x1F, false, true);
                case 0x00A5: return new RazerProduct("Viper V2 Pro", 0x1F, true, true);
                case 0x00A6: return new RazerProduct("Viper V2 Pro", 0x1F, false, true);
                case 0x00A7: return new RazerProduct("Naga V2 Pro", 0x1F, true, true);
                case 0x00A8: return new RazerProduct("Naga V2 Pro", 0x1F, false, true);
                case 0x00AA: return new RazerProduct("Basilisk V3 Pro", 0x1F, true, true);
                case 0x00AB: return new RazerProduct("Basilisk V3 Pro", 0x1F, false, true);
                case 0x00AF: return new RazerProduct("Cobra Pro", 0x1F, true, true);
                case 0x00B0: return new RazerProduct("Cobra Pro", 0x1F, false, true);
                case 0x00B4: return new RazerProduct("Naga V2 HyperSpeed", 0x1F, false, false);
                case 0x00B6: case 0x00C2: return new RazerProduct("DeathAdder V3 Pro", 0x1F, true, true);
                case 0x00B7: case 0x00C3: return new RazerProduct("DeathAdder V3 Pro", 0x1F, false, true);
                case 0x00B8: return new RazerProduct("Viper V3 HyperSpeed", 0x1F, false, false);
                case 0x00B9: return new RazerProduct("Basilisk V3 X HyperSpeed", 0x1F, false, false);
                case 0x00C0: return new RazerProduct("Viper V3 Pro", 0x1F, true, true);
                case 0x00C1: return new RazerProduct("Viper V3 Pro", 0x1F, false, true);
                case 0x00C4: return new RazerProduct("DeathAdder V3 HyperSpeed", 0x1F, true, true);
                case 0x00C5: return new RazerProduct("DeathAdder V3 HyperSpeed", 0x1F, false, true);
                case 0x00CC: return new RazerProduct("Basilisk V3 Pro 35K", 0x1F, true, true);
                case 0x00CD: return new RazerProduct("Basilisk V3 Pro 35K", 0x1F, false, true);
                default: return null;
            }
        }
        private static bool Query(SafeFileHandle handle, int length, byte command,
            RazerProduct product, Stopwatch deadline, StringBuilder log, out byte value, out string error)
        {
            value = 0;
            error = "問い合わせの制限時間を超えました。";
            for (int attempt = 0; attempt < 2 && deadline.ElapsedMilliseconds < QueryBudget; attempt++)
            {
                byte[] request = BatteryProtocol.CreateRequest(command, length, product.Transaction);
                if (!Native.HidD_SetFeature(handle, request, request.Length))
                {
                    error = Win32Error("問い合わせを送信できません", Marshal.GetLastWin32Error());
                    log.AppendLine(error);
                    break;
                }
                for (int poll = 0; poll < 3 && deadline.ElapsedMilliseconds + product.ResponseDelay < QueryBudget; poll++)
                {
                    Thread.Sleep(product.ResponseDelay);
                    byte[] response = new byte[length];
                    if (!Native.HidD_GetFeature(handle, response, response.Length))
                    {
                        error = Win32Error("応答を取得できません", Marshal.GetLastWin32Error());
                        log.AppendLine(error);
                        break;
                    }
                    bool valid = BatteryProtocol.TryParse(response, command, product.Transaction, out value, out error);
                    log.AppendLine("07/" + command.ToString("X2") + " 応答: " +
                        BitConverter.ToString(response, 0, 11) + ", CRC=" + response[89].ToString("X2") +
                        (valid ? " / OK" : " / " + error));
                    if (valid) return true;
                    // Busy/unprepared can finish without resending. Other replies require a fresh request.
                    if (response[1] != 0 && response[1] != 1) break;
                }
                if (attempt == 0) Thread.Sleep(60);
            }
            return false;
        }

        private static BatteryReading Unavailable(string detail)
        {
            return new BatteryReading
            {
                Connected = false, DeviceName = "Razer マウス", Connection = "未接続 / 取得待ち",
                Provider = "Razer", Detail = detail, Timestamp = DateTime.Now
            };
        }

        private static string Win32Error(string operation, int code)
        {
            return operation + " (Windows " + code + ": " + new Win32Exception(code).Message + ")。";
        }

        private static bool GetCaps(SafeFileHandle handle, out Native.HidCaps caps)
        {
            caps = new Native.HidCaps();
            IntPtr preparsed;
            if (!Native.HidD_GetPreparsedData(handle, out preparsed)) return false;
            try { return Native.HidP_GetCaps(preparsed, out caps) == 0x00110000; }
            finally { Native.HidD_FreePreparsedData(preparsed); }
        }

        private sealed class HidPath
        {
            internal string Path;
            internal string ContainerId;
            internal RazerProduct Product;
        }

        private static List<HidPath> EnumeratePaths(StringBuilder log)
        {
            List<HidPath> paths = new List<HidPath>();
            Guid guid;
            Native.HidD_GetHidGuid(out guid);
            IntPtr devices = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
            if (devices == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                for (uint index = 0; index < 512; index++)
                {
                    Native.DeviceInterface data = new Native.DeviceInterface();
                    data.Size = Marshal.SizeOf(typeof(Native.DeviceInterface));
                    if (!Native.SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref guid, index, ref data))
                    {
                        int code = Marshal.GetLastWin32Error();
                        if (code != 259) log.AppendLine(Win32Error("HID の列挙に失敗しました", code));
                        break;
                    }
                    uint required;
                    Native.SetupDiGetDeviceInterfaceDetail(devices, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required < 6 || required > 65536) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        Native.DeviceInfo device = new Native.DeviceInfo();
                        device.Size = Marshal.SizeOf(typeof(Native.DeviceInfo));
                        if (!Native.SetupDiGetDeviceInterfaceDetailWithInfo(devices, ref data, detail, required, out required, ref device)) continue;
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (path == null) continue;
                        string lower = path.ToLowerInvariant();
                        if (!lower.Contains("vid_1532&")) continue;
                        int pidPosition = lower.IndexOf("pid_", StringComparison.Ordinal);
                        ushort pid;
                        if (pidPosition < 0 || pidPosition + 8 > lower.Length ||
                            !UInt16.TryParse(lower.Substring(pidPosition + 4, 4),
                                System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out pid)) continue;
                        RazerProduct product = ProductFor(pid);
                        if (product == null) continue;
                        paths.Add(new HidPath { Path = path, ContainerId = ReadContainerId(devices, ref device), Product = product });
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(devices); }
            // Prefer a directly connected mouse if the receiver is also plugged in.
            paths.Sort(delegate(HidPath a, HidPath b)
            {
                int rank = (a.Product.Wired ? 0 : 1).CompareTo(b.Product.Wired ? 0 : 1);
                return rank != 0 ? rank : StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path);
            });
            log.AppendLine("対応する HID インターフェース: " + paths.Count);
            return paths;
        }

        private static string ReadContainerId(IntPtr devices, ref Native.DeviceInfo device)
        {
            Native.PropertyKey key = new Native.PropertyKey
            {
                Format = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), Id = 2
            };
            byte[] buffer = new byte[16];
            uint type, required;
            if (!Native.SetupDiGetDeviceProperty(devices, ref device, ref key, out type,
                buffer, (uint)buffer.Length, out required, 0) || type != 0x0D || required != 16) return null;
            Guid value = new Guid(buffer);
            return value == Guid.Empty ? null : value.ToString("D");
        }

        private static class Native
        {
            [StructLayout(LayoutKind.Sequential)]
            internal struct DeviceInfo
            {
                internal int Size;
                internal Guid ClassGuid;
                internal uint DeviceInstance;
                internal IntPtr Reserved;
            }
            [StructLayout(LayoutKind.Sequential)]
            internal struct PropertyKey
            {
                internal Guid Format;
                internal uint Id;
            }
            [StructLayout(LayoutKind.Sequential)]
            internal struct DeviceInterface
            {
                internal int Size;
                internal Guid ClassGuid;
                internal uint Flags;
                internal IntPtr Reserved;
            }
            [StructLayout(LayoutKind.Sequential)]
            internal struct HidAttributes
            {
                internal int Size;
                internal ushort VendorID, ProductID, VersionNumber;
            }
            [StructLayout(LayoutKind.Sequential)]
            internal struct HidCaps
            {
                internal ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] internal ushort[] Reserved;
                internal ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
                internal ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
                internal ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
            [DllImport("hid.dll")]
            internal static extern void HidD_GetHidGuid(out Guid guid);
            [DllImport("hid.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HidAttributes attributes);
            [DllImport("hid.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
            [DllImport("hid.dll")]
            [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool HidD_FreePreparsedData(IntPtr data);
            [DllImport("hid.dll")]
            internal static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
            [DllImport("hid.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool HidD_SetFeature(SafeFileHandle handle, [In] byte[] report, int length);
            [DllImport("hid.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool HidD_GetFeature(SafeFileHandle handle, [In, Out] byte[] report, int length);
            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
            [DllImport("setupapi.dll", SetLastError = true)]
            internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices, IntPtr device, ref Guid guid, uint index, ref DeviceInterface data);
            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devices, ref DeviceInterface data, IntPtr detail, uint size, out uint required, IntPtr device);
            [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern bool SetupDiGetDeviceInterfaceDetailWithInfo(IntPtr devices, ref DeviceInterface data, IntPtr detail, uint size, out uint required, ref DeviceInfo device);
            [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern bool SetupDiGetDeviceProperty(IntPtr devices, ref DeviceInfo device, ref PropertyKey key,
                out uint type, [Out] byte[] buffer, uint size, out uint required, uint flags);
            [DllImport("setupapi.dll", SetLastError = true)]
            internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
        }
    }
}
