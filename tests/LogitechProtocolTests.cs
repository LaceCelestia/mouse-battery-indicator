using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ViperBattery;

internal static class LogitechProtocolTests
{
    private static int assertions;

    private static void Check(bool condition, string name)
    {
        assertions++;
        if (!condition) throw new Exception(name);
    }

    private static byte[] Packet(string hex)
    {
        string[] parts = hex.Split(' ');
        byte[] result = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++) result[i] = Convert.ToByte(parts[i], 16);
        return result;
    }

    private static int Main(string[] args)
    {
        try
        {
            TestPackets();
            TestUnifiedBattery();
            TestLevelBattery();
            TestVoltage();
            TestDeviceDiscovery();
            TestOverlappedTransport();
            TestOverlappingScans();
            if (args.Length == 1 && args[0] == "--scan")
            {
                Stopwatch timer = Stopwatch.StartNew();
                BatteryScan scan = new LogitechBatteryReader().Scan();
                Check(timer.ElapsedMilliseconds < 9000, "Native scan has a bounded wait");
                Console.WriteLine(scan.Diagnostic);
                foreach (BatteryReading reading in scan.Readings)
                    Console.WriteLine(reading.DeviceName + " " + reading.Percentage + " " + reading.BatteryLevelText);
            }
            Console.WriteLine("Logitech protocol tests passed: " + assertions + " assertions (synthetic HID++ fixtures; no Logitech hardware claim).");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void TestPackets()
    {
        byte[] request = LogitechProtocol.Request(2, 9, 1, 0xA);
        Check(BitConverter.ToString(request).StartsWith("11-02-09-1A"), "Long request header includes receiver slot, feature, function and software ID");
        byte[] report = Packet("11 02 09 1A 49 04 01 01 00 00 00 00 00 00 00 00 00 00 00 00");
        Check(LogitechProtocol.MatchResponse(request, report) == 1, "Matching long reply");
        byte[] wrong = (byte[])report.Clone(); wrong[1] = 1;
        Check(LogitechProtocol.MatchResponse(request, wrong) == 0, "Other paired mouse cannot supply battery");
        wrong = (byte[])report.Clone(); wrong[2] = 8;
        Check(LogitechProtocol.MatchResponse(request, wrong) == 0, "Wrong feature rejected");
        wrong = (byte[])report.Clone(); wrong[3] = 0x0A;
        Check(LogitechProtocol.MatchResponse(request, wrong) == 0, "Wrong function rejected");
        wrong = (byte[])report.Clone(); wrong[3] = 0x1B;
        Check(LogitechProtocol.MatchResponse(request, wrong) == 0, "Other software reply rejected");
        wrong = (byte[])report.Clone(); wrong[3] = 0x10;
        Check(LogitechProtocol.MatchResponse(request, wrong) == 0, "Broadcast notification rejected");
        Check(LogitechProtocol.MatchResponse(request, Packet("11 02 09 1A 49 04 01")) == 0, "Truncated long report rejected");
        Check(LogitechProtocol.MatchResponse(request, Packet("10 02 09 1A 49 04 01")) == 1, "Short reply payload is three bytes");
        Check(LogitechProtocol.Parameters(Packet("10 02 09 1A 49 04 01")).Length == 3, "Short packets are not padded into invented status bytes");
        Check(LogitechProtocol.MatchResponse(request, Packet("10 02 8F 09 1A 08 00")) == -1, "HID++1 unknown device error is recognized");
        Check(LogitechProtocol.MatchResponse(request, Packet("11 02 FF 09 1A 08 00 00 00 00 00 00 00 00 00 00 00 00 00 00")) == -1, "HID++2 busy error is recognized");
        Check(LogitechProtocol.MatchResponse(request, Packet("11 02 FF 09 1B 08 00 00 00 00 00 00 00 00 00 00 00 00 00 00")) == 0, "Error addressed to another software ID rejected");
        Check(LogitechProtocol.MatchResponse(request, null) == 0, "Missing response rejected");
        bool rejected = false;
        try { LogitechProtocol.Request(2, 9, 1, 0); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Software ID zero is reserved for events");
    }

    private static void TestUnifiedBattery()
    {
        byte[] caps = Packet("0F 03");
        BatteryReading value = new BatteryReading();
        Check(LogitechProtocol.ParseUnified(caps, Packet("49 04 01 01"), value) && value.Percentage == 73 && value.IsCharging == true,
            "1004 state of charge with charging");
        Check(LogitechProtocol.ParseUnified(caps, Packet("00 01 00 00"), value) && value.Percentage == 0 && value.IsCharging == false,
            "1004 capability distinguishes real zero percent");
        Check(LogitechProtocol.ParseUnified(caps, Packet("64 08 03 01"), value) && value.Percentage == 100 && value.IsCharging == false,
            "Full battery does not remain charging");
        Check(LogitechProtocol.ParseUnified(caps, Packet("32 04 02 01"), value) && value.IsCharging == true,
            "1004 slow charge");
        Check(LogitechProtocol.ParseUnified(caps, Packet("32 04 04 01"), value) && value.IsCharging == null && value.Detail.Contains("エラー"),
            "1004 charging error is not advertised as charging");
        Check(LogitechProtocol.ParseUnified(Packet("0F 00"), Packet("00 04 00 00"), value) && value.Percentage == null && value.BatteryLevelText == "残量良好",
            "Discrete-only battery never invents fifty percent");
        Check(LogitechProtocol.ParseUnified(Packet("03 00"), Packet("00 08 00 00"), value) && value.Percentage == null && value.BatteryLevelText == "残量不明",
            "Unsupported discrete level bits ignored");
        Check(LogitechProtocol.ParseUnified(Packet("0F 00"), Packet("00 03 00 00"), value) && value.BatteryLevelText == "残量少",
            "1004 level is a bitmask: low takes precedence over critical");
        Check(LogitechProtocol.ParseUnified(Packet("0F 00"), Packet("00 0C 00 00"), value) && value.BatteryLevelText == "満充電",
            "1004 full takes precedence over good");
        Check(LogitechProtocol.ParseUnified(Packet("07 00"), Packet("00 0F 00 00"), value) && value.BatteryLevelText == "残量良好",
            "1004 priority is applied only after masking unsupported levels");
        Check(!LogitechProtocol.ParseUnified(caps, Packet("FF 04 00 00"), value), "Invalid percentage rejected");
        Check(!LogitechProtocol.ParseUnified(caps, Packet("32 04 05 00"), value), "Unknown 1004 charging status rejected");
        Check(!LogitechProtocol.ParseUnified(null, Packet("32 04 00 00"), value), "Missing capabilities cannot establish percent support");
        Check(!LogitechProtocol.ParseUnified(caps, Packet("32 04 00"), value), "Truncated 1004 status rejected");
    }

    private static void TestLevelBattery()
    {
        BatteryReading value = new BatteryReading();
        Check(LogitechProtocol.ParseLevelStatus(Packet("64 06"), Packet("48 47 00"), value) && value.Percentage == 72 && value.IsCharging == false,
            "1000 mileage percentage");
        Check(LogitechProtocol.ParseLevelStatus(Packet("04 04"), Packet("32 14 00"), value) && value.Percentage == null && value.BatteryLevelText == "残量良好",
            "1000 discrete report does not masquerade as 50 percent");
        Check(LogitechProtocol.ParseLevelStatus(Packet("64 06"), Packet("00 00 01"), value) && value.Percentage == null && value.IsCharging == true,
            "1000 zero during charge is unknown, not an empty battery");
        Check(LogitechProtocol.ParseLevelStatus(Packet("64 06"), Packet("00 00 03"), value) && value.Percentage == null && value.BatteryLevelText == "満充電" && value.IsCharging == false,
            "1000 full state without a numeric value");
        Check(LogitechProtocol.ParseLevelStatus(Packet("64 06"), Packet("20 00 04"), value) && value.IsCharging == true,
            "1000 status 4 means slow charge, unlike 1004");
        Check(LogitechProtocol.ParseLevelStatus(Packet("64 06"), Packet("00 00 06"), value) && value.IsCharging == null && value.Detail.Contains("エラー"),
            "Thermal error does not show charging");
        Check(!LogitechProtocol.ParseLevelStatus(Packet("64 06"), Packet("65 00 00"), value), "Out of range 1000 percentage rejected");
    }

    private static void TestVoltage()
    {
        BatteryReading value = new BatteryReading();
        Check(LogitechProtocol.ParseVoltage(Packet("0E E3 00"), false, value) && value.Percentage == 50 && value.IsApproximate && value.IsCharging == false,
            "1001 voltage estimation is identified as approximate");
        Check(LogitechProtocol.ParseVoltage(Packet("10 5A 80"), false, value) && value.IsCharging == true, "1001 charging flag");
        Check(LogitechProtocol.ParseVoltage(Packet("10 5A 81"), false, value) && value.IsCharging == false, "1001 full flag");
        Check(LogitechProtocol.ParseVoltage(Packet("10 5A 82"), false, value) && value.IsCharging == null, "1001 charge failure");
        Check(LogitechProtocol.ParseVoltage(Packet("05 DC 00"), false, value) && value.Percentage == null && !value.IsApproximate,
            "AA cell voltage is not forced through a Li-ion curve");
        Check(!LogitechProtocol.ParseVoltage(Packet("00 00 00"), false, value), "Zero voltage rejected");
        Check(!LogitechProtocol.ParseVoltage(Packet("FF FF 00"), false, value), "Sentinel voltage rejected");
        Check(LogitechProtocol.EstimatePercentage(3499) == null && LogitechProtocol.EstimatePercentage(4401) == null,
            "Out of calibrated range yields no percent");
        Check(LogitechProtocol.ParseVoltage(Packet("0E E3 03"), true, value) && value.IsCharging == true && value.Percentage == null,
            "ADC charge reports voltage without invented curve");
        Check(LogitechProtocol.ParseVoltage(Packet("0E E3 07"), true, value) && value.IsCharging == false && value.BatteryLevelText == "満充電",
            "ADC full state");
        Check(!LogitechProtocol.ParseVoltage(Packet("0E E3 00"), true, value), "ADC disconnected flag rejected");
        Check(!LogitechProtocol.ParseVoltage(Packet("0E E3 0F"), true, value), "ADC unknown flags rejected");
    }

    private static void TestDeviceDiscovery()
    {
        LogitechEndpoint endpoint = new LogitechEndpoint { Path = "fixture-device", ContainerId = "fixture-container", ProductId = 0xC548 };
        FixtureTransport mouse = new FixtureTransport();
        BatteryReading value = LogitechBatteryReader.ReadDevice(mouse, endpoint, 2, Stopwatch.StartNew(), new StringBuilder());
        Check(value != null && value.Percentage == 73 && value.IsCharging == true && value.Provider == "Logitech", "Paired mouse battery discovered through root feature lookup");
        Check(value.DeviceName == mouse.Name && mouse.NameReads == 3, "Full UTF-8 device name assembled across three reports");
        Check(value.DeviceId == "logitech:fixture-device:02" && value.ContainerId == null, "Receiver slot has independent identity, not receiver container");
        Check(mouse.BadCommands == 0, "Only name, root and battery GET functions dispatched");
        FixtureTransport direct = new FixtureTransport();
        value = LogitechBatteryReader.ReadDevice(direct, endpoint, 0xFF, Stopwatch.StartNew(), new StringBuilder());
        Check(value != null && value.ContainerId == "fixture-container" && value.DeviceId.EndsWith(":FF"), "Direct device preserves container and FF address");
        FixtureTransport keyboard = new FixtureTransport { Kind = 0 };
        value = LogitechBatteryReader.ReadDevice(keyboard, endpoint, 1, Stopwatch.StartNew(), new StringBuilder());
        Check(value == null && keyboard.BatteryReads == 0, "Keyboard is excluded before battery query");
        FixtureTransport receiver = new FixtureTransport { Kind = 7 };
        Check(LogitechBatteryReader.ReadDevice(receiver, endpoint, 0xFF, Stopwatch.StartNew(), new StringBuilder()) == null,
            "Receiver itself is not displayed as a mouse");
        FixtureTransport offline = new FixtureTransport { Offline = true };
        Check(LogitechBatteryReader.ReadDevice(offline, endpoint, 6, Stopwatch.StartNew(), new StringBuilder()) == null && offline.BatteryReads == 0,
            "Offline receiver slot never produces a battery");
        FixtureTransport stale = new FixtureTransport { WrongPing = true };
        Check(LogitechBatteryReader.ReadDevice(stale, endpoint, 1, Stopwatch.StartNew(), new StringBuilder()) == null,
            "Stale ping challenge cannot identify a live device");
        Check(LogitechProtocol.IsMouse(3) && LogitechProtocol.IsMouse(5) && !LogitechProtocol.IsMouse(0) && !LogitechProtocol.IsMouse(8),
            "Only mouse and trackball device types are accepted");
    }

    private static void TestOverlappedTransport()
    {
        string pipeName = "MouseBattery.Logitech.Test." + Guid.NewGuid().ToString("N");
        using (NamedPipeServerStream server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        using (ManualResetEvent releaseServer = new ManualResetEvent(false))
        {
            Task peer = Task.Factory.StartNew(delegate
            {
                server.WaitForConnection();
                byte[] first = ReadPacket(server);
                first[4] = 73; first[5] = 4; first[6] = 1;
                server.Write(first, 0, first.Length);
                byte[] second = ReadPacket(server), otherClient = (byte[])second.Clone();
                otherClient[3] ^= 1;
                server.Write(otherClient, 0, otherClient.Length);
                second[4] = 61;
                server.Write(second, 0, second.Length);
                ReadPacket(server);
                releaseServer.WaitOne(1500);
            });
            try
            {
                LogitechEndpoint endpoint = new LogitechEndpoint { Path = @"\\.\pipe\" + pipeName, InputLength = 20, OutputLength = 20 };
                using (LogitechHidTransport transport = new LogitechHidTransport(endpoint))
                {
                    string error;
                    byte[] request = LogitechProtocol.Request(2, 9, 1, 0xA);
                    byte[] reply = transport.Exchange(request, 350, out error);
                    Check(reply != null && reply[4] == 73 && error == null, "Real overlapped WriteFile/ReadFile round trip through test pipe");
                    reply = transport.Exchange(LogitechProtocol.Request(2, 9, 1, 0xB), 350, out error);
                    Check(reply != null && reply[4] == 61, "Native transport skips another client's packet then accepts exact response");
                    Stopwatch timer = Stopwatch.StartNew();
                    reply = transport.Exchange(LogitechProtocol.Request(2, 9, 1, 0xC), 40, out error);
                    Check(reply == null && error != null && timer.ElapsedMilliseconds < 1000, "Real pending ReadFile is cancelled within its deadline");
                    timer.Restart();
                    reply = transport.Exchange(request, 350, out error);
                    Check(reply == null && timer.ElapsedMilliseconds < 100, "Timed-out channel rejects further requests until reopened");
                }
                Check(LogitechHidTransport.ReapCancelledIo(), "Normal Windows cancellation leaves no retained I/O buffer");
            }
            finally
            {
                releaseServer.Set();
                if (!peer.Wait(3000)) throw new Exception("Test pipe worker failed to finish");
            }
        }
    }

    private static byte[] ReadPacket(Stream stream)
    {
        byte[] result = new byte[20];
        int offset = 0;
        while (offset < result.Length)
        {
            int received = stream.Read(result, offset, result.Length - offset);
            if (received == 0) throw new EndOfStreamException();
            offset += received;
        }
        return result;
    }

    private static void TestOverlappingScans()
    {
        using (ManualResetEvent entered = new ManualResetEvent(false))
        using (ManualResetEvent release = new ManualResetEvent(false))
        {
            int starts = 0;
            LogitechBatteryReader reader = new LogitechBatteryReader(delegate
            {
                int generation = Interlocked.Increment(ref starts);
                if (generation == 1)
                {
                    entered.Set();
                    if (!release.WaitOne(3000)) throw new Exception("Delayed scan fixture was not released");
                }
                BatteryScan result = new BatteryScan();
                result.Readings.Add(new BatteryReading { Percentage = generation == 1 ? 72 : 31 });
                return result;
            });
            Task<BatteryScan> first = Task.Factory.StartNew<BatteryScan>(reader.Scan);
            try
            {
                Check(entered.WaitOne(1000), "First scan has an operation in flight");
                Stopwatch timer = Stopwatch.StartNew();
                BatteryScan overlapping = reader.Scan();
                Check(overlapping.Readings.Count == 0 && !String.IsNullOrEmpty(overlapping.Diagnostic),
                    "A call with an earlier operation pending cannot acquire its stale result");
                Check(timer.ElapsedMilliseconds < 500 && starts == 1,
                    "Overlapping scan returns immediately without starting another worker");
                release.Set();
                Check(first.Wait(1000) && first.Result.Readings[0].Percentage == 72,
                    "Original operation is consumed only by the initiating call");
                BatteryScan fresh = reader.Scan();
                Check(starts == 2 && fresh.Readings[0].Percentage == 31,
                    "Next scan starts a new operation and does not reuse a completed result");
            }
            finally
            {
                release.Set();
                if (!first.Wait(3000)) throw new Exception("Delayed scan worker failed to finish");
            }
        }
    }

    // Synthetic protocol packets exercise the real request/parser path. They do
    // not represent a claim that a particular Logitech model was hardware-tested.
    private sealed class FixtureTransport : ILogitechTransport
    {
        internal readonly string Name = "Logitech Wireless Mouse Protocol Fixture";
        internal byte Kind = 3;
        internal bool Offline, WrongPing;
        internal int BatteryReads, NameReads, BadCommands;
        public byte[] Exchange(byte[] request, int timeoutMilliseconds, out string error)
        {
            error = null;
            Check(timeoutMilliseconds > 0 && timeoutMilliseconds <= 350, "Every protocol operation receives a finite timeout");
            if (Offline) { error = "offline fixture"; return null; }
            int feature = request[2], function = request[3] >> 4;
            if (feature == 0 && function == 1)
                return Reply(request, 4, 5, WrongPing ? (byte)0 : request[6]);
            if (feature == 0 && function == 0)
            {
                int id = (request[4] << 8) | request[5];
                return Reply(request, id == 5 ? (byte)3 : id == 0x1004 ? (byte)9 : (byte)0, 0, 0);
            }
            if (feature == 3 && function == 2) return Reply(request, Kind);
            if (feature == 3 && function == 0) return Reply(request, (byte)Encoding.UTF8.GetByteCount(Name));
            if (feature == 3 && function == 1)
            {
                NameReads++;
                byte[] name = Encoding.UTF8.GetBytes(Name), part = new byte[Math.Min(16, name.Length - request[4])];
                Array.Copy(name, request[4], part, 0, part.Length);
                return Reply(request, part);
            }
            if (feature == 9 && function == 0) { BatteryReads++; return Reply(request, 0x0F, 3); }
            if (feature == 9 && function == 1) { BatteryReads++; return Reply(request, 73, 4, 1, 1); }
            BadCommands++;
            throw new Exception("Unexpected command: " + BitConverter.ToString(request));
        }
        private static byte[] Reply(byte[] request, params byte[] parameters)
        {
            byte[] reply = new byte[20];
            Array.Copy(request, reply, 4);
            Array.Copy(parameters, 0, reply, 4, parameters.Length);
            return reply;
        }
        public void Dispose() { }
    }
}
