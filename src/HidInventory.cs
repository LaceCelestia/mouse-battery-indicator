using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ViperBattery
{
    // Diagnostic metadata only. No input reports are consumed and no output or
    // feature report is sent, even for devices whose protocol is unknown.
    internal static class HidInventory
    {
        private const int BudgetMilliseconds = 2000;
        private static readonly HidInventoryWorker worker = new HidInventoryWorker(Collect, BudgetMilliseconds);

        internal static string Diagnose() { return worker.Run(); }

        private static string Collect(int budget)
        {
            Stopwatch clock = Stopwatch.StartNew();
            StringBuilder report = new StringBuilder("Present HID metadata (read-only; anonymous physical groups):");
            report.AppendLine();
            Dictionary<string, int> groups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int count = 0, unavailable = 0;
            Guid hidGuid;
            Native.HidD_GetHidGuid(out hidGuid);
            IntPtr list = Native.SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, 0x12);
            if (list == new IntPtr(-1)) return "HID inventory: SetupAPI unavailable.";
            try
            {
                for (uint index = 0; index < 512 && clock.ElapsedMilliseconds < budget; index++)
                {
                    Native.DeviceInterface entry = new Native.DeviceInterface();
                    entry.Size = Marshal.SizeOf(typeof(Native.DeviceInterface));
                    if (!Native.SetupDiEnumDeviceInterfaces(list, IntPtr.Zero, ref hidGuid, index, ref entry)) break;
                    Native.DeviceInfo info = new Native.DeviceInfo();
                    info.Size = Marshal.SizeOf(typeof(Native.DeviceInfo));
                    uint needed;
                    Native.SetupDiGetDeviceInterfaceDetail(list, ref entry, IntPtr.Zero, 0, out needed, ref info);
                    if (needed < 6 || needed > 65536) { unavailable++; continue; }
                    IntPtr detail = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetail(list, ref entry, detail, needed, out needed, ref info))
                        { unavailable++; continue; }
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (String.IsNullOrEmpty(path)) { unavailable++; continue; }
                        // Zero desired access permits descriptor queries only.
                        using (SafeFileHandle handle = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                        {
                            if (handle.IsInvalid) { unavailable++; continue; }
                            Native.Attributes attributes = new Native.Attributes();
                            attributes.Size = Marshal.SizeOf(typeof(Native.Attributes));
                            if (!Native.HidD_GetAttributes(handle, ref attributes)) { unavailable++; continue; }
                            StringBuilder product = new StringBuilder(256), manufacturer = new StringBuilder(256);
                            Native.HidD_GetProductString(handle, product, 512);
                            Native.HidD_GetManufacturerString(handle, manufacturer, 512);
                            // Container GUIDs are used only to associate collections;
                            // serial numbers, instance paths and GUIDs are not logged.
                            string groupKey = ReadContainer(list, ref info) ?? "interface:" + index.ToString(CultureInfo.InvariantCulture);
                            int group;
                            if (!groups.TryGetValue(groupKey, out group)) { group = groups.Count + 1; groups.Add(groupKey, group); }
                            HidInventoryEntry item = new HidInventoryEntry { Group = group, VendorId = attributes.VendorID,
                                ProductId = attributes.ProductID, Manufacturer = manufacturer.ToString(), Product = product.ToString() };
                            IntPtr preparsed;
                            if (Native.HidD_GetPreparsedData(handle, out preparsed))
                            {
                                try
                                {
                                    Native.Caps caps;
                                    if (Native.HidP_GetCaps(preparsed, out caps) == 0x00110000)
                                    {
                                        item.HasCaps = true; item.UsagePage = caps.UsagePage; item.Usage = caps.Usage;
                                        item.InputLength = caps.InputLength; item.OutputLength = caps.OutputLength; item.FeatureLength = caps.FeatureLength;
                                    }
                                }
                                finally { Native.HidD_FreePreparsedData(preparsed); }
                            }
                            report.AppendLine(FormatEntry(item));
                            count++;
                        }
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(list); }
            report.AppendFormat(CultureInfo.InvariantCulture, "HID collections: {0}; unavailable metadata: {1}.", count, unavailable);
            if (clock.ElapsedMilliseconds >= budget) report.Append(" Enumeration time limit reached; inventory may be incomplete.");
            report.AppendLine();
            return report.ToString();
        }

        internal static string FormatEntry(HidInventoryEntry entry)
        {
            string line = String.Format(CultureInfo.InvariantCulture, "Group {0:D2} | VID:PID {1:X4}:{2:X4} | Manufacturer: {3} | Product: {4}",
                entry.Group, entry.VendorId, entry.ProductId, CleanLabel(entry.Manufacturer), CleanLabel(entry.Product));
            if (!entry.HasCaps) return line + " | HID capabilities unavailable";
            return line + String.Format(CultureInfo.InvariantCulture, " | Usage {0:X4}/{1:X4} | Input {2}, Output {3}, Feature {4} bytes",
                entry.UsagePage, entry.Usage, entry.InputLength, entry.OutputLength, entry.FeatureLength);
        }

        internal static string CleanLabel(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return "(unavailable)";
            StringBuilder clean = new StringBuilder();
            foreach (char c in text)
            {
                if (c == '\0') break;
                // Keep each USB descriptor on one line and prevent delimiter injection.
                clean.Append(Char.IsControl(c) || c == '|' || c == '\u2028' || c == '\u2029' ? ' ' : c);
                if (clean.Length >= 128) break;
            }
            string result = clean.ToString().Trim();
            return result.Length == 0 ? "(unavailable)" : result;
        }

        private static string ReadContainer(IntPtr list, ref Native.DeviceInfo info)
        {
            Native.PropertyKey key = new Native.PropertyKey { Format = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), Id = 2 };
            byte[] buffer = new byte[16];
            uint type, needed;
            if (!Native.SetupDiGetDeviceProperty(list, ref info, ref key, out type, buffer, 16, out needed, 0) || type != 13 || needed != 16)
                return null;
            Guid container = new Guid(buffer);
            return container == Guid.Empty ? null : container.ToString("D");
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
            [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
            [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
            [DllImport("hid.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool HidD_GetProductString(SafeFileHandle handle, StringBuilder buffer, int length);
            [DllImport("hid.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool HidD_GetManufacturerString(SafeFileHandle handle, StringBuilder buffer, int length);
            [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsed);
            [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool HidD_FreePreparsedData(IntPtr preparsed);
            [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr preparsed, out Caps caps);
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

    internal sealed class HidInventoryEntry
    {
        internal int Group;
        internal ushort VendorId, ProductId, UsagePage, Usage, InputLength, OutputLength, FeatureLength;
        internal string Manufacturer, Product;
        internal bool HasCaps;
    }

    // A driver can hang even during metadata queries. Leave at most one worker
    // alive and never wait beyond the diagnostic's caller budget.
    internal sealed class HidInventoryWorker
    {
        private readonly object gate = new object();
        private readonly Func<int, string> collect;
        private readonly int timeoutMilliseconds;
        private Task<string> pending;

        internal HidInventoryWorker(Func<int, string> collect, int timeoutMilliseconds)
        {
            if (collect == null) throw new ArgumentNullException("collect");
            if (timeoutMilliseconds < 1) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            this.collect = collect; this.timeoutMilliseconds = timeoutMilliseconds;
        }

        internal string Run()
        {
            Task<string> current;
            lock (gate)
            {
                if (pending != null && !pending.IsCompleted) return "HID inventory: previous metadata query is still pending.";
                current = pending = Task.Factory.StartNew(delegate
                {
                    try { return collect(timeoutMilliseconds); }
                    catch (Exception error) { return "HID inventory failed (" + error.GetType().Name + ")."; }
                }, System.Threading.CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            return current.Wait(timeoutMilliseconds) ? current.Result : "HID inventory: metadata query timed out; retry after it finishes.";
        }
    }
}
