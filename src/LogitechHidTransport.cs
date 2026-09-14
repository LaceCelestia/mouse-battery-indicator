using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ViperBattery
{
    internal sealed class LogitechEndpoint
    {
        internal string Path, ContainerId;
        internal ushort ProductId, InputLength, OutputLength;
    }

    internal interface ILogitechTransport : IDisposable
    {
        byte[] Exchange(byte[] request, int timeoutMilliseconds, out string error);
    }

    // Only a vendor command collection is opened. Windows' mouse and keyboard
    // input collections are never opened for read/write or consumed here.
    internal sealed class LogitechHidTransport : ILogitechTransport
    {
        private readonly SafeFileHandle handle;
        private readonly int inputLength, outputLength;
        private bool faulted;
        private static readonly object retiredGate = new object();
        private static IoOperation retiredOperation;

        internal LogitechHidTransport(LogitechEndpoint endpoint)
        {
            if (!ReapCancelledIo()) throw new InvalidOperationException("HID のキャンセル完了を待っています。");
            handle = Native.CreateFile(endpoint.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            inputLength = endpoint.InputLength;
            outputLength = endpoint.OutputLength;
        }

        internal static bool ReapCancelledIo()
        {
            lock (retiredGate)
            {
                if (retiredOperation == null) return true;
                if (!retiredOperation.Done.WaitOne(0)) return false;
                retiredOperation.Dispose();
                retiredOperation = null;
                return true;
            }
        }

        public byte[] Exchange(byte[] request, int timeoutMilliseconds, out string error)
        {
            error = null;
            if (faulted || timeoutMilliseconds <= 0) { error = "HID の応答待ちが終了しました。"; return null; }
            if (request == null || request.Length != 20 || request[0] != 0x11)
                throw new ArgumentException("HID++ の long GET report が必要です。", "request");
            Stopwatch clock = Stopwatch.StartNew();
            byte[] outgoing = new byte[outputLength];
            Array.Copy(request, outgoing, request.Length);
            int written;
            if (!Transfer(outgoing, true, Remaining(clock, timeoutMilliseconds), out written, out error) || written != outputLength)
            {
                faulted = true;
                if (error == null) error = "HID の送信長が一致しません。";
                return null;
            }
            // A bounded number of unrelated HID++ notifications can arrive while
            // G HUB / Options+ is also using its own shared command handle.
            for (int packet = 0; packet < 128 && clock.ElapsedMilliseconds < timeoutMilliseconds; packet++)
            {
                byte[] incoming = new byte[inputLength];
                int received;
                if (!Transfer(incoming, false, Remaining(clock, timeoutMilliseconds), out received, out error))
                { faulted = true; return null; }
                if (received < 7) continue;
                if (received != incoming.Length) Array.Resize(ref incoming, received);
                int match = LogitechProtocol.MatchResponse(request, incoming);
                if (match == 1) return incoming;
                if (match == -1)
                {
                    error = "HID++ error 0x" + incoming[5].ToString("X2");
                    return null;
                }
            }
            faulted = true;
            error = "HID++ の応答がありません。";
            return null;
        }

        private static int Remaining(Stopwatch clock, int timeout)
        {
            return Math.Max(1, timeout - (int)clock.ElapsedMilliseconds);
        }

        private bool Transfer(byte[] data, bool write, int timeout, out int count, out string error)
        {
            count = 0;
            error = null;
            IoOperation operation = new IoOperation(data.Length);
            bool keepAlive = false;
            try
            {
                if (write) Marshal.Copy(data, 0, operation.Buffer, data.Length);
                uint transferred;
                bool complete = write
                    ? Native.WriteFile(handle, operation.Buffer, (uint)data.Length, out transferred, operation.Overlapped)
                    : Native.ReadFile(handle, operation.Buffer, (uint)data.Length, out transferred, operation.Overlapped);
                int code = complete ? 0 : Marshal.GetLastWin32Error();
                if (!complete && code != 997)
                { error = "HID I/O (Windows " + code + ")"; return false; }
                if (!complete && !operation.Done.WaitOne(timeout))
                {
                    Native.CancelIoEx(handle, operation.Overlapped);
                    // A broken driver must neither retain a pointer to freed memory
                    // nor cause unlimited background workers / cancellation buffers.
                    if (!operation.Done.WaitOne(100))
                    {
                        lock (retiredGate) retiredOperation = operation;
                        keepAlive = true;
                    }
                    error = "HID の応答が時間内に届きませんでした。";
                    return false;
                }
                if (!Native.GetOverlappedResult(handle, operation.Overlapped, out transferred, false))
                { error = "HID I/O (Windows " + Marshal.GetLastWin32Error() + ")"; return false; }
                if (transferred > data.Length) { error = "HID の応答長が不正です。"; return false; }
                count = (int)transferred;
                if (!write) Marshal.Copy(operation.Buffer, data, 0, count);
                return true;
            }
            finally { if (!keepAlive) operation.Dispose(); }
        }

        public void Dispose()
        {
            if (!handle.IsClosed) handle.Dispose();
        }

        private sealed class IoOperation : IDisposable
        {
            internal readonly EventWaitHandle Done;
            internal IntPtr Buffer, Overlapped;
            internal IoOperation(int length)
            {
                Done = new EventWaitHandle(false, EventResetMode.ManualReset);
                Buffer = Marshal.AllocHGlobal(length);
                Native.OverlappedData data = new Native.OverlappedData();
                data.Event = Done.SafeWaitHandle.DangerousGetHandle();
                Overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.OverlappedData)));
                Marshal.StructureToPtr(data, Overlapped, false);
            }
            public void Dispose()
            {
                if (Buffer != IntPtr.Zero) Marshal.FreeHGlobal(Buffer);
                if (Overlapped != IntPtr.Zero) Marshal.FreeHGlobal(Overlapped);
                Buffer = Overlapped = IntPtr.Zero;
                Done.Dispose();
            }
        }

        internal static List<LogitechEndpoint> Enumerate(StringBuilder log, Stopwatch clock, int budget)
        {
            List<LogitechEndpoint> endpoints = new List<LogitechEndpoint>();
            Guid hidGuid;
            Native.HidD_GetHidGuid(out hidGuid);
            IntPtr devices = Native.SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, 0x12);
            if (devices == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                for (uint index = 0; index < 512 && clock.ElapsedMilliseconds < budget; index++)
                {
                    Native.DeviceInterface entry = new Native.DeviceInterface();
                    entry.Size = Marshal.SizeOf(typeof(Native.DeviceInterface));
                    if (!Native.SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref hidGuid, index, ref entry)) break;
                    uint needed;
                    Native.DeviceInfo info = new Native.DeviceInfo();
                    info.Size = Marshal.SizeOf(typeof(Native.DeviceInfo));
                    Native.SetupDiGetDeviceInterfaceDetail(devices, ref entry, IntPtr.Zero, 0, out needed, ref info);
                    if (needed < 6 || needed > 65536) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetail(devices, ref entry, detail, needed, out needed, ref info)) continue;
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (path == null || path.IndexOf("vid_046d", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        using (SafeFileHandle metadata = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                        {
                            if (metadata.IsInvalid) continue;
                            Native.Attributes attributes = new Native.Attributes();
                            attributes.Size = Marshal.SizeOf(typeof(Native.Attributes));
                            if (!Native.HidD_GetAttributes(metadata, ref attributes) || attributes.VendorID != 0x046D) continue;
                            IntPtr preparsed;
                            if (!Native.HidD_GetPreparsedData(metadata, out preparsed)) continue;
                            try
                            {
                                Native.Caps caps;
                                if (Native.HidP_GetCaps(preparsed, out caps) != 0x00110000 || caps.UsagePage < 0xFF00 ||
                                    caps.InputLength < 20 || caps.InputLength > 512 || caps.OutputLength < 20 || caps.OutputLength > 512)
                                    continue;
                                // Verify report 0x11 actually belongs to both directions
                                // of this collection; size alone also matches audio/DFU HID.
                                if (!HasLongReport(preparsed, 0, caps.InputValueCaps, caps.InputButtonCaps) ||
                                    !HasLongReport(preparsed, 1, caps.OutputValueCaps, caps.OutputButtonCaps)) continue;
                                endpoints.Add(new LogitechEndpoint { Path = path, ProductId = attributes.ProductID,
                                    InputLength = caps.InputLength, OutputLength = caps.OutputLength,
                                    ContainerId = ReadContainer(devices, ref info) });
                                log.AppendLine("Logitech PID " + attributes.ProductID.ToString("X4") + " usage " +
                                    caps.UsagePage.ToString("X4") + "/" + caps.Usage.ToString("X4") + " HID++ long collection");
                            }
                            finally { Native.HidD_FreePreparsedData(preparsed); }
                        }
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(devices); }
            return endpoints;
        }

        private static bool HasLongReport(IntPtr preparsed, int reportType, ushort values, ushort buttons)
        {
            // HIDP_VALUE_CAPS and HIDP_BUTTON_CAPS are both 72 bytes; ReportID is
            // byte 2 in their common fixed prefix (Windows hidpi.h).
            return HasReportCaps(preparsed, reportType, values, false) || HasReportCaps(preparsed, reportType, buttons, true);
        }

        private static bool HasReportCaps(IntPtr preparsed, int reportType, ushort count, bool buttons)
        {
            if (count == 0 || count > 512) return false;
            IntPtr memory = Marshal.AllocHGlobal(count * 72);
            try
            {
                ushort used = count;
                int status = buttons ? Native.HidP_GetButtonCaps(reportType, memory, ref used, preparsed)
                    : Native.HidP_GetValueCaps(reportType, memory, ref used, preparsed);
                if (status != 0x00110000 || used > count) return false;
                for (int i = 0; i < used; i++) if (Marshal.ReadByte(memory, i * 72 + 2) == 0x11) return true;
                return false;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        private static string ReadContainer(IntPtr devices, ref Native.DeviceInfo info)
        {
            Native.PropertyKey key = new Native.PropertyKey();
            key.Format = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"); key.Id = 2;
            byte[] buffer = new byte[16];
            uint type, needed;
            if (!Native.SetupDiGetDeviceProperty(devices, ref info, ref key, out type, buffer, 16, out needed, 0) ||
                type != 0x0D || needed != 16) return null;
            Guid value = new Guid(buffer);
            return value == Guid.Empty ? null : value.ToString("D");
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
            internal static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, uint count, out uint read, IntPtr overlapped);
            [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool WriteFile(SafeFileHandle file, IntPtr buffer, uint count, out uint written, IntPtr overlapped);
            [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr overlapped, out uint count, [MarshalAs(UnmanagedType.Bool)] bool wait);
            [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);
            [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
            [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool HidD_GetAttributes(SafeFileHandle file, ref Attributes attributes);
            [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool HidD_GetPreparsedData(SafeFileHandle file, out IntPtr preparsed);
            [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool HidD_FreePreparsedData(IntPtr preparsed);
            [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr preparsed, out Caps caps);
            [DllImport("hid.dll")] internal static extern int HidP_GetValueCaps(int reportType, IntPtr caps, ref ushort length, IntPtr preparsed);
            [DllImport("hid.dll")] internal static extern int HidP_GetButtonCaps(int reportType, IntPtr caps, ref ushort length, IntPtr preparsed);
            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr owner, uint flags);
            [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices, IntPtr info, ref Guid guid, uint index, ref DeviceInterface entry);
            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devices, ref DeviceInterface entry, IntPtr detail, uint size, out uint needed, ref DeviceInfo info);
            [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetupDiGetDeviceProperty(IntPtr devices, ref DeviceInfo info, ref PropertyKey key, out uint type, byte[] buffer, uint size, out uint needed, uint flags);
            [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
        }
    }
}
