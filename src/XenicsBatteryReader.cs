using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ViperBattery
{
    // Only the battery query found in the manufacturer's GE AIR software is
    // implemented. No pairing, authentication or configuration command is sent.
    public sealed class XenicsBatteryReader
    {
        private const int ScanBudget = 3500;
        private static readonly XenicsScanWorker worker = new XenicsScanWorker(ScanCore, ScanBudget);

        public BatteryScan Scan() { return worker.Run(); }

        private static BatteryScan ScanCore(int budget)
        {
            BatteryScan scan = new BatteryScan();
            StringBuilder log = new StringBuilder("Xenics / OEM battery (experimental; command 04 only):\r\n");
            Stopwatch clock = Stopwatch.StartNew();
            using (Mutex mutex = new Mutex(false, @"Local\MouseBatteryIndicator.XenicsBatteryProbe"))
            {
                bool owned = false;
                try
                {
                    try { owned = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned) { scan.Diagnostic = log + "Another battery query is in progress."; return scan; }
                    if (!XenicsHidTransport.ReapCancelledIo())
                    { scan.Diagnostic = log + "Previous HID cancellation is still pending."; return scan; }
                    List<XenicsEndpoint> endpoints = XenicsHidTransport.Enumerate(log, clock, budget);
                    List<XenicsRoute> routes = XenicsProtocol.CreateRoutes(endpoints);
                    foreach (IGrouping<string, XenicsRoute> group in routes.GroupBy(r => r.DeviceId, StringComparer.OrdinalIgnoreCase))
                    {
                        if (clock.ElapsedMilliseconds >= budget) { log.AppendLine("Scan time limit reached."); break; }
                        XenicsRoute first = group.First();
                        BatteryReading reading = new BatteryReading { DeviceId = first.DeviceId,
                            Provider = "Xenics", ContainerId = XenicsProtocol.Container(first.Feature.ContainerId),
                            DeviceName = XenicsProtocol.DeviceName(first.Feature.Product, first.Input.Product),
                            Connection = first.Feature.ProductId == 0xFA7C ? "USB レシーバー" : "USB",
                            Timestamp = DateTime.Now, Detail = "残量を取得できません。" };
                        string error = "対応する入力レポートがありません。";
                        foreach (XenicsRoute route in group.Take(4))
                        {
                            int remaining = Math.Min(900, budget - (int)clock.ElapsedMilliseconds);
                            if (remaining <= 0 || !XenicsHidTransport.ReapCancelledIo()) break;
                            try
                            {
                                using (XenicsHidTransport transport = new XenicsHidTransport(route))
                                {
                                    byte[] reply = transport.ReadBattery(remaining, out error);
                                    int percentage; bool charging;
                                    if (reply != null && XenicsProtocol.ParseResponse(reply, out percentage, out charging, out error))
                                    {
                                        reading.Connected = true; reading.Percentage = percentage; reading.IsCharging = charging;
                                        reading.Timestamp = DateTime.Now; reading.Detail = null;
                                        break;
                                    }
                                }
                            }
                            catch (Exception ex) { error = "HID を開けません (" + ex.GetType().Name + ")."; }
                        }
                        if (!reading.Connected) reading.Detail = error;
                        scan.Readings.Add(reading);
                        log.AppendLine("25A7:" + first.Feature.ProductId.ToString("X4") + " " + reading.DeviceName + ": " +
                            (reading.Percentage.HasValue ? reading.Percentage.Value + "%" + (reading.IsCharging == true ? " charging" : "") : error));
                    }
                    if (routes.Count == 0) log.AppendLine("No supported vendor report 08/09 route. Exact GE AIR model identification is not inferred from shared USB IDs.");
                    if (scan.Readings.Any(r => !r.Connected))
                        log.AppendLine("Check that the mouse is awake. Firmware may require the manufacturer's initialization; no authentication or pairing command was attempted.");
                }
                catch (Exception ex) { log.AppendLine("Scan failed (" + ex.GetType().Name + ")."); }
                finally { if (owned) mutex.ReleaseMutex(); }
            }
            scan.Diagnostic = log.ToString();
            return scan;
        }
    }

    internal sealed class XenicsEndpoint
    {
        internal string Path, ContainerId, Product;
        internal ushort VendorId, ProductId, UsagePage, InputLength, FeatureLength;
        internal bool HasInput09, HasFeature08;
    }

    internal sealed class XenicsRoute
    {
        internal XenicsEndpoint Feature, Input;
        internal string DeviceId;
    }

    internal static class XenicsProtocol
    {
        internal static bool Supported(ushort vendor, ushort product)
        { return vendor == 0x25A7 && (product == 0xFA7B || product == 0xFA7C); }

        internal static byte[] BatteryRequest()
        {
            byte[] request = new byte[17];
            request[0] = 0x08; request[1] = 0x04; request[16] = 0x49;
            return request;
        }

        internal static bool IsBatteryResponse(byte[] packet)
        { return packet != null && packet.Length == 17 && packet[0] == 0x09 && packet[1] == 0x04; }

        internal static bool ParseResponse(byte[] packet, out int percentage, out bool charging, out string error)
        {
            percentage = 0; charging = false; error = null;
            if (!IsBatteryResponse(packet)) { error = "残量の応答形式が一致しません。"; return false; }
            if (packet[2] != 0) { error = "マウスが残量を返しません (status " + packet[2].ToString("X2") + ")."; return false; }
            if (packet[6] > 100) { error = "残量が有効な範囲外です。"; return false; }
            percentage = packet[6]; charging = packet[7] != 0;
            return true;
        }

        internal static string Container(string value)
        {
            Guid id;
            return Guid.TryParse(value, out id) && id != Guid.Empty ? id.ToString("D") : null;
        }

        internal static List<XenicsRoute> CreateRoutes(IEnumerable<XenicsEndpoint> source)
        {
            List<XenicsEndpoint> endpoints = source.Where(e => e != null && !String.IsNullOrWhiteSpace(e.Path) &&
                Supported(e.VendorId, e.ProductId) && e.UsagePage >= 0xFF00).ToList();
            List<XenicsRoute> routes = new List<XenicsRoute>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XenicsEndpoint feature in endpoints.Where(e => e.FeatureLength == 17 && e.HasFeature08))
            {
                string container = Container(feature.ContainerId);
                foreach (XenicsEndpoint input in endpoints.Where(e => e.InputLength == 17 && e.HasInput09))
                {
                    if (feature.ProductId != input.ProductId) continue;
                    bool samePath = String.Equals(feature.Path, input.Path, StringComparison.OrdinalIgnoreCase);
                    if (!samePath && (container == null || container != Container(input.ContainerId))) continue;
                    if (!seen.Add(feature.Path + "\n" + input.Path)) continue;
                    routes.Add(new XenicsRoute { Feature = feature, Input = input,
                        DeviceId = "xenics:" + (container ?? feature.Path.ToLowerInvariant()) + ":" + feature.ProductId.ToString("x4") });
                }
            }
            return routes;
        }

        internal static string DeviceName(string featureProduct, string inputProduct)
        {
            foreach (string product in new[] { featureProduct, inputProduct })
            {
                if (String.IsNullOrWhiteSpace(product)) continue;
                StringBuilder clean = new StringBuilder();
                foreach (char c in product)
                {
                    if (c == '\0') break;
                    clean.Append(Char.IsControl(c) || c == '\u2028' || c == '\u2029' ? ' ' : c);
                    if (clean.Length >= 128) break;
                }
                if (clean.ToString().Trim().Length > 0) return clean.ToString().Trim();
            }
            return "Xenics / OEM マウス";
        }
    }

    internal sealed class XenicsScanWorker
    {
        private readonly object gate = new object();
        private readonly Func<int, BatteryScan> collect;
        private readonly int budget;
        private Task<BatteryScan> pending;
        internal XenicsScanWorker(Func<int, BatteryScan> collect, int budget)
        { this.collect = collect; this.budget = budget; }
        internal BatteryScan Run()
        {
            Task<BatteryScan> current;
            lock (gate)
            {
                if (pending != null && !pending.IsCompleted)
                    return new BatteryScan { Diagnostic = "Xenics: previous HID query is still pending." };
                current = pending = Task.Factory.StartNew(delegate
                {
                    try { return collect(budget); }
                    catch (Exception ex) { return new BatteryScan { Diagnostic = "Xenics: " + ex.GetType().Name }; }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            return current.Wait(budget) ? current.Result : new BatteryScan { Diagnostic = "Xenics: HID query timed out; retry after it finishes." };
        }
    }

    internal sealed class XenicsHidTransport : IDisposable
    {
        private readonly SafeFileHandle feature, input;
        private static readonly object retiredGate = new object();
        private static readonly List<PendingIo> retired = new List<PendingIo>();
        internal const uint SetFeatureIoctl = 0x000B0191; // HID_IN_CTL_CODE(100), Microsoft hidclass.h.

        internal XenicsHidTransport(XenicsRoute route)
        {
            if (!ReapCancelledIo()) throw new InvalidOperationException("HID cancellation is pending.");
            input = Native.CreateFile(route.Input.Path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (input.IsInvalid) { input.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            try
            {
                feature = Native.CreateFile(route.Feature.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (feature.IsInvalid) { feature.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            }
            catch { input.Dispose(); throw; }
        }

        internal static bool ReapCancelledIo()
        {
            lock (retiredGate)
            {
                for (int i = retired.Count - 1; i >= 0; i--)
                {
                    if (!retired[i].IsComplete()) continue;
                    retired[i].Dispose(); retired.RemoveAt(i);
                }
                return retired.Count == 0;
            }
        }

        internal byte[] ReadBattery(int timeout, out string error)
        {
            error = null;
            Stopwatch clock = Stopwatch.StartNew();
            PendingIo receive = null, send = null;
            try
            {
                // Arm the dedicated vendor input first so a fast response cannot
                // be lost between a feature send and opening the receive handle.
                receive = ArmFreshRead(input, clock, timeout, out error);
                if (receive == null) return null;
                send = new PendingIo(feature, XenicsProtocol.BatteryRequest());
                if (!send.Begin(true, out error) || !send.Wait(Remaining(clock, timeout), out error)) return null;
                send.Dispose(); send = null;
                for (int packets = 0; packets < 32 && clock.ElapsedMilliseconds < timeout; packets++)
                {
                    if (!receive.Wait(Remaining(clock, timeout), out error)) return null;
                    byte[] response = receive.ReadBytes();
                    receive.Dispose(); receive = null;
                    if (XenicsProtocol.IsBatteryResponse(response)) return response;
                    receive = new PendingIo(input, null);
                    if (!receive.Begin(false, out error)) return null;
                }
                error = "残量の応答がありません。";
                return null;
            }
            finally { Retire(receive); Retire(send); }
        }

        private static int Remaining(Stopwatch clock, int timeout)
        { return Math.Max(0, timeout - (int)clock.ElapsedMilliseconds); }

        internal static PendingIo ArmFreshRead(SafeFileHandle handle, Stopwatch clock, int timeout, out string error)
        {
            error = null;
            // This protocol has no transaction counter. Drop packets already
            // queued before our query instead of presenting them as a new read.
            for (int stale = 0; stale < 32 && clock.ElapsedMilliseconds < timeout; stale++)
            {
                PendingIo read = new PendingIo(handle, null);
                if (!read.Begin(false, out error)) { Retire(read); return null; }
                if (!read.IsComplete()) return read;
                read.Dispose();
            }
            error = "照会前の入力が続いているため、残量を確認できません。";
            return null;
        }

        private static void Retire(PendingIo operation)
        {
            if (operation == null) return;
            if (!operation.IsComplete()) operation.Cancel();
            if (operation.IsComplete()) operation.Dispose();
            else
            {
                // At most two operations can be retired. New transports/scans
                // are blocked until both complete. Retain buffers AND handles.
                lock (retiredGate) retired.Add(operation);
            }
        }

        public void Dispose() { feature.Dispose(); input.Dispose(); }

        internal sealed class PendingIo : IDisposable
        {
            private readonly SafeFileHandle owner;
            private readonly IntPtr file;
            private readonly EventWaitHandle done;
            private readonly IntPtr buffer, overlapped;
            private bool reference, started, completed, disposed;
            private uint transferred;
            private int failure;
            internal PendingIo(SafeFileHandle owner, byte[] outgoing)
            {
                this.owner = owner;
                owner.DangerousAddRef(ref reference);
                file = owner.DangerousGetHandle();
                try
                {
                    done = new EventWaitHandle(false, EventResetMode.ManualReset);
                    buffer = Marshal.AllocHGlobal(17);
                    Marshal.Copy(outgoing ?? new byte[17], 0, buffer, 17);
                    Native.OverlappedData data = new Native.OverlappedData { Event = done.SafeWaitHandle.DangerousGetHandle() };
                    overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.OverlappedData)));
                    Marshal.StructureToPtr(data, overlapped, false);
                }
                catch { Dispose(); throw; }
            }
            internal bool Begin(bool featureWrite, out string error)
            {
                started = true;
                bool success = featureWrite
                    ? Native.DeviceIoControl(file, SetFeatureIoctl, buffer, 17, IntPtr.Zero, 0, out transferred, overlapped)
                    : Native.ReadFile(file, buffer, 17, out transferred, overlapped);
                failure = success ? 0 : Marshal.GetLastWin32Error();
                completed = success || failure != 997;
                if (success || failure == 997) { error = null; return true; }
                error = "HID I/O (Windows " + failure + ")."; return false;
            }
            internal bool IsComplete()
            {
                if (!started || completed) return true;
                bool success = Native.GetOverlappedResult(file, overlapped, out transferred, false);
                int code = success ? 0 : Marshal.GetLastWin32Error();
                if (!success && code == 996) return false;
                completed = true; failure = code;
                return true;
            }
            internal bool Wait(int timeout, out string error)
            {
                if (!IsComplete() && (!done.WaitOne(timeout) || !IsComplete()))
                { error = "残量の応答が時間内に届きませんでした。"; return false; }
                error = failure == 0 ? null : "HID I/O (Windows " + failure + ").";
                return failure == 0;
            }
            internal byte[] ReadBytes()
            {
                if (transferred != 17) return null;
                byte[] data = new byte[17]; Marshal.Copy(buffer, data, 0, 17); return data;
            }
            internal void Cancel() { Native.CancelIoEx(file, overlapped); done.WaitOne(40); }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (overlapped != IntPtr.Zero) Marshal.FreeHGlobal(overlapped);
                if (done != null) done.Dispose();
                if (reference) { owner.DangerousRelease(); reference = false; }
            }
        }

        internal static List<XenicsEndpoint> Enumerate(StringBuilder log, Stopwatch clock, int budget)
        {
            List<XenicsEndpoint> endpoints = new List<XenicsEndpoint>();
            Guid guid; Native.HidD_GetHidGuid(out guid);
            IntPtr list = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
            if (list == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                for (uint index = 0; index < 512 && clock.ElapsedMilliseconds < budget; index++)
                {
                    Native.DeviceInterface entry = new Native.DeviceInterface { Size = Marshal.SizeOf(typeof(Native.DeviceInterface)) };
                    if (!Native.SetupDiEnumDeviceInterfaces(list, IntPtr.Zero, ref guid, index, ref entry)) break;
                    Native.DeviceInfo info = new Native.DeviceInfo { Size = Marshal.SizeOf(typeof(Native.DeviceInfo)) };
                    uint needed;
                    Native.SetupDiGetDeviceInterfaceDetail(list, ref entry, IntPtr.Zero, 0, out needed, ref info);
                    if (needed < 6 || needed > 65536) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetail(list, ref entry, detail, needed, out needed, ref info)) continue;
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (path == null || path.IndexOf("vid_25a7", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        using (SafeFileHandle handle = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                        {
                            if (handle.IsInvalid) continue;
                            Native.Attributes attributes = new Native.Attributes { Size = Marshal.SizeOf(typeof(Native.Attributes)) };
                            if (!Native.HidD_GetAttributes(handle, ref attributes) || !XenicsProtocol.Supported(attributes.VendorID, attributes.ProductID)) continue;
                            IntPtr preparsed;
                            if (!Native.HidD_GetPreparsedData(handle, out preparsed)) continue;
                            try
                            {
                                Native.Caps caps;
                                if (Native.HidP_GetCaps(preparsed, out caps) != 0x00110000) continue;
                                log.AppendLine("25A7:" + attributes.ProductID.ToString("X4") + " usage " + caps.UsagePage.ToString("X4") + "/" +
                                    caps.Usage.ToString("X4") + " input " + caps.InputLength + " feature " + caps.FeatureLength);
                                if (caps.UsagePage < 0xFF00) continue;
                                bool receive = caps.InputLength == 17 && HasReport(preparsed, 0, caps.InputValueCaps, caps.InputButtonCaps, 9);
                                bool send = caps.FeatureLength == 17 && HasReport(preparsed, 2, caps.FeatureValueCaps, caps.FeatureButtonCaps, 8);
                                if (!receive && !send) continue;
                                StringBuilder product = new StringBuilder(126);
                                Native.HidD_GetProductString(handle, product, 252);
                                endpoints.Add(new XenicsEndpoint { Path = path, VendorId = attributes.VendorID, ProductId = attributes.ProductID,
                                    UsagePage = caps.UsagePage, InputLength = caps.InputLength, FeatureLength = caps.FeatureLength,
                                    HasInput09 = receive, HasFeature08 = send, ContainerId = ReadContainer(list, ref info), Product = product.ToString() });
                            }
                            finally { Native.HidD_FreePreparsedData(preparsed); }
                        }
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(list); }
            return endpoints;
        }

        private static bool HasReport(IntPtr preparsed, int type, ushort values, ushort buttons, byte report)
        { return HasReportCaps(preparsed, type, values, false, report) || HasReportCaps(preparsed, type, buttons, true, report); }

        private static bool HasReportCaps(IntPtr preparsed, int type, ushort count, bool buttons, byte report)
        {
            if (count == 0 || count > 512) return false;
            IntPtr memory = Marshal.AllocHGlobal(count * 72);
            try
            {
                ushort used = count;
                int status = buttons ? Native.HidP_GetButtonCaps(type, memory, ref used, preparsed) : Native.HidP_GetValueCaps(type, memory, ref used, preparsed);
                if (status != 0x00110000 || used > count) return false;
                for (int i = 0; i < used; i++) if (Marshal.ReadByte(memory, i * 72 + 2) == report) return true;
                return false;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        private static string ReadContainer(IntPtr list, ref Native.DeviceInfo info)
        {
            Native.PropertyKey key = new Native.PropertyKey { Format = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), Id = 2 };
            byte[] buffer = new byte[16]; uint type, needed;
            if (!Native.SetupDiGetDeviceProperty(list, ref info, ref key, out type, buffer, 16, out needed, 0) || type != 13 || needed != 16) return null;
            return XenicsProtocol.Container(new Guid(buffer).ToString("D"));
        }

        private static class Native
        {
            [StructLayout(LayoutKind.Sequential)] internal struct DeviceInterface
            { internal int Size; internal Guid Class; internal uint Flags; internal IntPtr Reserved; }
            [StructLayout(LayoutKind.Sequential)] internal struct DeviceInfo
            { internal int Size; internal Guid Class; internal uint DevInst; internal IntPtr Reserved; }
            [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey
            { internal Guid Format; internal uint Id; }
            [StructLayout(LayoutKind.Sequential)] internal struct Attributes
            { internal int Size; internal ushort VendorID, ProductID, Version; }
            [StructLayout(LayoutKind.Sequential)] internal struct OverlappedData
            { internal IntPtr Internal, InternalHigh; internal uint Offset, OffsetHigh; internal IntPtr Event; }
            [StructLayout(LayoutKind.Sequential)] internal struct Caps
            {
                internal ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] internal ushort[] Reserved;
                internal ushort LinkNodes, InputButtonCaps, InputValueCaps, InputData;
                internal ushort OutputButtonCaps, OutputValueCaps, OutputData;
                internal ushort FeatureButtonCaps, FeatureValueCaps, FeatureData;
            }
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
            [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool ReadFile(IntPtr file, IntPtr buffer, uint count, out uint read, IntPtr overlapped);
            [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool DeviceIoControl(IntPtr file, uint code, IntPtr input, uint inputLength, IntPtr output, uint outputLength, out uint count, IntPtr overlapped);
            [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetOverlappedResult(IntPtr file, IntPtr overlapped, out uint count, [MarshalAs(UnmanagedType.Bool)] bool wait);
            [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);
            [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
            [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
            [DllImport("hid.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool HidD_GetProductString(SafeFileHandle handle, StringBuilder buffer, int length);
            [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsed);
            [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool HidD_FreePreparsedData(IntPtr preparsed);
            [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr preparsed, out Caps caps);
            [DllImport("hid.dll")] internal static extern int HidP_GetValueCaps(int type, IntPtr caps, ref ushort count, IntPtr preparsed);
            [DllImport("hid.dll")] internal static extern int HidP_GetButtonCaps(int type, IntPtr caps, ref ushort count, IntPtr preparsed);
            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr owner, uint flags);
            [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr list, IntPtr info, ref Guid guid, uint index, ref DeviceInterface entry);
            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr list, ref DeviceInterface entry, IntPtr detail, uint size, out uint needed, ref DeviceInfo info);
            [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetupDiGetDeviceProperty(IntPtr list, ref DeviceInfo info, ref PropertyKey key, out uint type, byte[] buffer, uint size, out uint needed, uint flags);
            [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr list);
        }
    }
}
