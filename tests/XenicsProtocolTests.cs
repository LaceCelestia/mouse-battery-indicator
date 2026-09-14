using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using ViperBattery;

internal static class XenicsProtocolTests
{
    private static int assertions;
    private static void Check(bool condition, string name)
    {
        assertions++;
        if (Environment.GetEnvironmentVariable("XENICS_TEST_TRACE") == "1") Console.WriteLine(name);
        if (!condition) throw new Exception(name);
    }

    private static int Main()
    {
        try
        {
            Packets(); Routes(); Names(); Worker(); PipeIo();
            Console.WriteLine("Xenics protocol tests: " + assertions + " assertions passed (no mouse I/O).");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Packets()
    {
        byte[] request = XenicsProtocol.BatteryRequest();
        Check(request.Length == 17 && request[0] == 8 && request[1] == 4 && request[16] == 0x49, "documented battery GET");
        Check(request.Skip(2).Take(14).All(b => b == 0), "battery GET has no settings payload");
        Check((request.Sum(b => (int)b) & 255) == 0x55, "manufacturer request checksum");
        request[1] = 0xFF;
        Check(XenicsProtocol.BatteryRequest()[1] == 4, "caller cannot mutate subsequent request");
        Check(XenicsHidTransport.SetFeatureIoctl == ((11u << 16) | (100u << 2) | 1u), "official HID SET FEATURE control code");
        int percentage; bool charging; string error;
        for (int pct = 0; pct <= 100; pct++)
        {
            byte[] reply = Reply(pct, 0);
            Check(XenicsProtocol.ParseResponse(reply, out percentage, out charging, out error) && percentage == pct && !charging && error == null,
                "exact percent including zero " + pct);
            reply[7] = 1;
            Check(XenicsProtocol.ParseResponse(reply, out percentage, out charging, out error) && percentage == pct && charging, "charging " + pct);
        }
        byte[] packet = Reply(67, 255);
        Check(XenicsProtocol.ParseResponse(packet, out percentage, out charging, out error) && charging, "official nonzero charging semantics");
        for (int pct = 101; pct < 256; pct++)
            Check(!XenicsProtocol.ParseResponse(Reply(pct, 0), out percentage, out charging, out error) && percentage == 0 && !charging && error != null, "invalid percentage " + pct);
        Check(!XenicsProtocol.ParseResponse(null, out percentage, out charging, out error), "null response");
        for (int size = 0; size <= 32; size++)
        {
            if (size == 17) continue;
            byte[] reply = new byte[size];
            Array.Copy(Reply(40, 0), reply, Math.Min(17, size));
            Check(!XenicsProtocol.ParseResponse(reply, out percentage, out charging, out error), "wrong report size " + size);
        }
        packet = Reply(40, 1); packet[0] = 8;
        Check(!XenicsProtocol.ParseResponse(packet, out percentage, out charging, out error), "request echo is not a response");
        packet = Reply(40, 1); packet[1] = 0x0A;
        Check(!XenicsProtocol.ParseResponse(packet, out percentage, out charging, out error), "notification is not battery response");
        packet = Reply(40, 1); packet[2] = 1;
        Check(!XenicsProtocol.ParseResponse(packet, out percentage, out charging, out error) && percentage == 0 && !charging, "failure must not expose payload as battery");
        packet = Reply(40, 1); packet[16] = 0xAE;
        Check(XenicsProtocol.ParseResponse(packet, out percentage, out charging, out error), "no undocumented response checksum constraint");
    }

    private static byte[] Reply(int percentage, int charging)
    { byte[] p = new byte[17]; p[0] = 9; p[1] = 4; p[6] = (byte)percentage; p[7] = (byte)charging; return p; }

    private static XenicsEndpoint Endpoint(string path, string container, bool send, bool receive)
    {
        return new XenicsEndpoint { Path = path, ContainerId = container, VendorId = 0x25A7, ProductId = 0xFA7C,
            UsagePage = 0xFF00, InputLength = receive ? (ushort)17 : (ushort)0, FeatureLength = send ? (ushort)17 : (ushort)0,
            HasFeature08 = send, HasInput09 = receive };
    }

    private static void Routes()
    {
        string a = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", b = "BBBBBBBB-BBBB-CCCC-DDDD-EEEEEEEEEEEE";
        XenicsEndpoint outputA = Endpoint("output-a", a, true, false), inputA = Endpoint("input-a", "{" + a.ToLowerInvariant() + "}", false, true);
        XenicsEndpoint outputB = Endpoint("output-b", b, true, false), inputB = Endpoint("input-b", b, false, true);
        List<XenicsRoute> routes = XenicsProtocol.CreateRoutes(new[] { outputA, inputA, outputB, inputB });
        Check(routes.Count == 2 && routes.All(r => r.Feature.Path.Substring(7) == r.Input.Path.Substring(6)), "same model on two receivers cannot cross routes");
        Check(routes.Select(r => r.DeviceId).Distinct().Count() == 2, "distinct physical device identifiers");
        Check(XenicsProtocol.CreateRoutes(new[] { outputA, inputB }).Count == 0, "different container rejected");
        inputA.ProductId = 0xFA7B;
        Check(XenicsProtocol.CreateRoutes(new[] { outputA, inputA }).Count == 0, "different product ID rejected even in same container");
        inputA.ProductId = 0xFA7C; inputA.ContainerId = null; outputA.ContainerId = null;
        Check(XenicsProtocol.CreateRoutes(new[] { outputA, inputA }).Count == 0, "unknown containers cannot be combined");
        inputA.ContainerId = Guid.Empty.ToString(); outputA.ContainerId = Guid.Empty.ToString();
        Check(XenicsProtocol.CreateRoutes(new[] { outputA, inputA }).Count == 0, "empty GUID is not proof of shared physical device");
        XenicsEndpoint combined = Endpoint("combined", null, true, true);
        Check(XenicsProtocol.CreateRoutes(new[] { combined }).Count == 1, "same collection is safe without container");
        Check(XenicsProtocol.CreateRoutes(new[] { combined, combined }).Count == 1, "duplicate enumeration produces one route");
        combined.UsagePage = 1;
        Check(XenicsProtocol.CreateRoutes(new[] { combined }).Count == 0, "never read mouse or keyboard collections");
        combined.UsagePage = 0xFF00; combined.HasInput09 = false;
        Check(XenicsProtocol.CreateRoutes(new[] { combined }).Count == 0, "length alone cannot identify vendor input report");
        combined.HasInput09 = true; combined.HasFeature08 = false;
        Check(XenicsProtocol.CreateRoutes(new[] { combined }).Count == 0, "require feature report 08 descriptor");
        combined.HasFeature08 = true; combined.FeatureLength = 65;
        Check(XenicsProtocol.CreateRoutes(new[] { combined }).Count == 0, "different report family rejected");
        combined.FeatureLength = 17; combined.VendorId = 0x046D;
        Check(XenicsProtocol.CreateRoutes(new[] { combined }).Count == 0, "other vendors rejected");
        Check(XenicsProtocol.Supported(0x25A7, 0xFA7B) && XenicsProtocol.Supported(0x25A7, 0xFA7C), "official config IDs");
        Check(!XenicsProtocol.Supported(0x33E4, 0x00AA) && !XenicsProtocol.Supported(0x3299, 0x4EB7) && !XenicsProtocol.Supported(0x25A7, 0xFA7D), "pairing and unknown IDs never probed");
    }

    private static void Names()
    {
        Check(XenicsProtocol.DeviceName("TITAN GE AIR", "receiver") == "TITAN GE AIR", "USB product name preserved");
        Check(XenicsProtocol.DeviceName("Redragon M914", null) == "Redragon M914", "shared IDs never force Xenics model");
        Check(XenicsProtocol.DeviceName(null, "Receiver") == "Receiver", "input product fallback");
        Check(!XenicsProtocol.DeviceName(null, null).Contains("GE AIR"), "unknown OEM is not labelled exact model");
        Check(XenicsProtocol.DeviceName("Mouse\r\nInjected\u2028text\0secret", null) == "Mouse  Injected text", "descriptor newline and NUL safety");
        Check(XenicsProtocol.DeviceName(new String('x', 200), null).Length == 128, "descriptor bound");
    }

    private static void Worker()
    {
        using (ManualResetEvent release = new ManualResetEvent(false))
        using (ManualResetEvent started = new ManualResetEvent(false))
        using (ManualResetEvent finished = new ManualResetEvent(false))
        {
            int calls = 0;
            XenicsScanWorker worker = new XenicsScanWorker(delegate(int budget)
            {
                Interlocked.Increment(ref calls); started.Set(); release.WaitOne(); finished.Set();
                return new BatteryScan { Diagnostic = "completed" };
            }, 40);
            Stopwatch clock = Stopwatch.StartNew();
            BatteryScan first = worker.Run();
            Check(started.WaitOne(1000), "worker started");
            Check(clock.ElapsedMilliseconds < 1000 && first.Diagnostic.Contains("timed out"), "bounded caller when a native call hangs");
            BatteryScan second = worker.Run();
            Check(calls == 1 && second.Diagnostic.Contains("pending"), "no accumulating workers during native hang");
            Check(first.Readings.Count == 0 && second.Readings.Count == 0, "timeouts do not invent zero or reuse percentage");
            release.Set(); Check(finished.WaitOne(1000), "worker safely finishes after timeout");
        }
        XenicsScanWorker throwing = new XenicsScanWorker(delegate(int budget) { throw new InvalidOperationException(); }, 1000);
        Check(throwing.Run().Diagnostic.Contains("InvalidOperationException"), "worker exception is reported");
    }

    // Exercise actual Windows overlapped reads/cancellation with a local pipe,
    // without opening a HID device or sending manufacturer commands.
    private static void PipeIo()
    {
        string name = "MouseBatteryXenicsTests_" + Guid.NewGuid().ToString("N");
        using (NamedPipeServerStream server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        using (NamedPipeClientStream client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            IAsyncResult wait = server.BeginWaitForConnection(null, null);
            client.Connect(1000); server.EndWaitForConnection(wait);
            SafeFileHandle handle;
            Check(DuplicateHandle(GetCurrentProcess(), server.SafePipeHandle.DangerousGetHandle(), GetCurrentProcess(), out handle, 0, false, 2), "duplicate local pipe handle");
            using (handle)
            {
                string error;
                using (XenicsHidTransport.PendingIo read = new XenicsHidTransport.PendingIo(handle, null))
                {
                    Check(read.Begin(false, out error), "start async local read");
                    Check(!read.Wait(1, out error), "read deadline returns before data");
                    byte[] data = Reply(42, 1); client.Write(data, 0, data.Length); client.Flush();
                    Check(read.Wait(1000, out error), "async completion after initial timeout");
                    Check(read.ReadBytes().SequenceEqual(data), "report is copied after completion");
                }
                using (XenicsHidTransport.PendingIo shortRead = new XenicsHidTransport.PendingIo(handle, null))
                {
                    Check(shortRead.Begin(false, out error), "start short read");
                    client.Write(new byte[8], 0, 8); client.Flush();
                    Check(shortRead.Wait(1000, out error) && shortRead.ReadBytes() == null, "partial read cannot parse as battery");
                }
                byte[] stale = Reply(10, 0);
                IAsyncResult staleWrite = client.BeginWrite(stale, 0, stale.Length, null, null);
                using (XenicsHidTransport.PendingIo fresh = XenicsHidTransport.ArmFreshRead(handle, Stopwatch.StartNew(), 1000, out error))
                {
                    Check(fresh != null && !fresh.IsComplete(), "queued response discarded before arming fresh query");
                    client.EndWrite(staleWrite);
                    byte[] current = Reply(62, 0); client.Write(current, 0, current.Length); client.Flush();
                    Check(fresh.Wait(1000, out error) && fresh.ReadBytes().SequenceEqual(current), "old percentage never replaces fresh response");
                }
                using (XenicsHidTransport.PendingIo cancelled = new XenicsHidTransport.PendingIo(handle, null))
                {
                    Check(cancelled.Begin(false, out error), "start read to cancel");
                    handle.Dispose();
                    cancelled.Cancel();
                    Check(!cancelled.Wait(1000, out error) && cancelled.IsComplete(), "pending operation retains native handle after managed owner disposal");
                }
            }
        }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out SafeFileHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}
