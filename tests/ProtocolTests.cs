using System;
using ViperBattery;

internal static class ProtocolTests
{
    private static int assertions;
    private static void Assert(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    // Fixtures are intentionally independent of the production request builder/checksum routine.
    private static byte[] Response(byte command, byte value)
    {
        return Response(command, value, 0x1F);
    }

    private static byte[] Response(byte command, byte value, byte transaction)
    {
        byte[] bytes = new byte[91];
        bytes[1] = 2; bytes[2] = transaction; bytes[6] = 2; bytes[7] = 7;
        bytes[8] = command; bytes[10] = value;
        bytes[89] = (byte)(2 ^ 7 ^ command ^ value);
        return bytes;
    }

    private static void Reject(byte[] bytes, byte command, string name)
    {
        byte value;
        string error;
        Assert(!BatteryProtocol.TryParse(bytes, command, out value, out error), name + " was accepted");
        Assert(value == 0 && !String.IsNullOrEmpty(error), name + " leaked a result");
    }

    private static void Profile(ushort pid, string name, byte transaction, bool wired, bool rechargeable)
    {
        RazerProduct product = RazerBatteryReader.ProductFor(pid);
        Assert(product != null, "missing supported PID " + pid.ToString("X4"));
        Assert(product.Name == "Razer " + name, "wrong device name for " + pid.ToString("X4"));
        Assert(product.Transaction == transaction, "wrong battery transaction for " + pid.ToString("X4"));
        Assert(product.Wired == wired && product.Rechargeable == rechargeable, "wrong connection/charging capability");
    }

    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--hardware")
        {
            Console.WriteLine(new RazerBatteryReader().Diagnose());
            return 0;
        }
        try
        {
            byte[] request = BatteryProtocol.CreateRequest(0x80, 91);
            Assert(BitConverter.ToString(request, 0, 11) == "00-00-1F-00-00-00-02-07-80-00-00", "battery request header");
            Assert(request[89] == 0x85 && request[90] == 0, "known battery checksum");
            Assert(BatteryProtocol.CreateRequest(0x84, 91)[89] == 0x81, "known charging checksum");
            bool rejected = false;
            try { BatteryProtocol.CreateRequest(0x01, 91); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected, "non-read command must be refused");
            rejected = false;
            try { BatteryProtocol.CreateRequest(0x80, 90); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected, "short request must be refused");
            byte value;
            string error;
            // Replies from another model's transaction must not satisfy a pending query,
            // even though transaction IDs are intentionally outside the XOR checksum.
            foreach (byte transaction in new byte[] { 0x1F, 0x3F, 0xFF })
            {
                foreach (byte command in new byte[] { 0x80, 0x84 })
                {
                    byte[] modelRequest = BatteryProtocol.CreateRequest(command, 91, transaction);
                    Assert(modelRequest[2] == transaction && modelRequest[7] == 7 && modelRequest[8] == command,
                        "model request header");
                    Assert(modelRequest[89] == (command == 0x80 ? 0x85 : 0x81), "transaction independent checksum");
                    Assert(BatteryProtocol.TryParse(Response(command, 1, transaction), command, transaction, out value, out error)
                        && value == 1 && error == null, "model response rejected");
                    foreach (byte wrongTransaction in new byte[] { 0x1F, 0x3F, 0xFF })
                    {
                        if (wrongTransaction == transaction) continue;
                        Assert(!BatteryProtocol.TryParse(Response(command, 1, wrongTransaction), command, transaction, out value, out error)
                            && value == 0 && !String.IsNullOrEmpty(error), "foreign transaction accepted");
                    }
                    byte[] badChecksum = Response(command, 1, transaction);
                    badChecksum[89] ^= 0x40;
                    Assert(!BatteryProtocol.TryParse(badChecksum, command, transaction, out value, out error), "model checksum not enforced");
                    byte[] busy = Response(command, 1, transaction);
                    busy[1] = 1;
                    Assert(!BatteryProtocol.TryParse(busy, command, transaction, out value, out error), "busy response became fresh reading");
                }
            }
            rejected = false;
            try { BatteryProtocol.CreateRequest(0x80, 91, 0x00); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected, "unsupported transaction request must be refused");
            Assert(!BatteryProtocol.TryParse(Response(0x80, 1, 0x00), 0x80, 0x00, out value, out error), "unsupported transaction response accepted");
            Profile(0x007A, "Viper Ultimate", 0xFF, true, true);
            Profile(0x007B, "Viper Ultimate", 0xFF, false, true);
            Profile(0x007C, "DeathAdder V2 Pro", 0x3F, true, true);
            Profile(0x007D, "DeathAdder V2 Pro", 0x3F, false, true);
            Profile(0x0083, "Basilisk X HyperSpeed", 0xFF, false, false);
            Profile(0x0086, "Basilisk Ultimate", 0x1F, true, true);
            Profile(0x0088, "Basilisk Ultimate", 0x1F, false, true);
            Profile(0x008F, "Naga Pro", 0x1F, true, true);
            Profile(0x0090, "Naga Pro", 0x1F, false, true);
            Profile(0x0094, "Orochi V2", 0x1F, false, false);
            Assert(RazerBatteryReader.ProductFor(0x0094).ResponseDelay >= 400, "Orochi receiver needs the documented longer wait");
            Profile(0x009C, "DeathAdder V2 X HyperSpeed", 0x1F, false, false);
            Profile(0x00A5, "Viper V2 Pro", 0x1F, true, true);
            Profile(0x00A6, "Viper V2 Pro", 0x1F, false, true);
            Profile(0x00AA, "Basilisk V3 Pro", 0x1F, true, true);
            Profile(0x00AB, "Basilisk V3 Pro", 0x1F, false, true);
            Profile(0x00B6, "DeathAdder V3 Pro", 0x1F, true, true);
            Profile(0x00B7, "DeathAdder V3 Pro", 0x1F, false, true);
            Profile(0x00C2, "DeathAdder V3 Pro", 0x1F, true, true);
            Profile(0x00C3, "DeathAdder V3 Pro", 0x1F, false, true);
            Profile(0x00B8, "Viper V3 HyperSpeed", 0x1F, false, false);
            Profile(0x00C0, "Viper V3 Pro", 0x1F, true, true);
            Profile(0x00C1, "Viper V3 Pro", 0x1F, false, true);
            foreach (ushort pid in new ushort[] { 0x0000, 0x0078, 0x0084, 0x0099, 0x00B2, 0xFFFF })
                Assert(RazerBatteryReader.ProductFor(pid) == null, "unknown or wired-only mouse must not get battery commands");
            string firstMouse = RazerBatteryReader.DeviceIdentity("HID#first", "AAAAAAAA-0000-0000-0000-000000000001");
            string secondMouse = RazerBatteryReader.DeviceIdentity("HID#second", "AAAAAAAA-0000-0000-0000-000000000002");
            Assert(firstMouse != secondMouse, "same-model physical devices must remain separate");
            Assert(firstMouse == RazerBatteryReader.DeviceIdentity("HID#anothercollection", "aaaaaaaa-0000-0000-0000-000000000001"),
                "same physical container must retain identity across collections");
            Assert(RazerBatteryReader.DeviceIdentity("HID#first", null) != RazerBatteryReader.DeviceIdentity("HID#second", null),
                "missing containers must preserve distinct interface paths");
            Assert(RazerBatteryReader.DeviceIdentity("HID#first", null) == RazerBatteryReader.DeviceIdentity("hid#FIRST", ""),
                "Windows path identity must be case insensitive");
            Assert(BatteryProtocol.TryParse(Response(0x80, 128), 0x80, out value, out error) && value == 128, "valid battery response");
            Assert(BatteryProtocol.TryParse(Response(0x80, 0), 0x80, out value, out error) && value == 0, "valid depleted battery is not offline");
            Assert(BatteryProtocol.TryParse(Response(0x80, 255), 0x80, out value, out error) && value == 255, "valid full battery");
            Assert(BatteryProtocol.TryParse(Response(0x84, 1), 0x84, out value, out error) && value == 1, "charging");
            Assert(BatteryProtocol.TryParse(Response(0x84, 0), 0x84, out value, out error) && value == 0, "not charging");
            Reject(Response(0x84, 2), 0x84, "invalid charging state");
            Reject(Response(0x80, 128), 0x84, "battery reply during charging query");
            Reject(null, 0x80, "null response");
            Reject(new byte[90], 0x80, "truncated response");
            foreach (int position in new int[] { 0, 2, 3, 4, 5, 6, 7, 8, 89, 90 })
            {
                byte[] wrong = Response(0x80, 128);
                wrong[position] ^= 1;
                Reject(wrong, 0x80, "invalid header/checksum at " + position);
            }
            foreach (byte status in new byte[] { 0, 1, 3, 4, 5, 255 })
            {
                byte[] wrong = Response(0x80, 128); wrong[1] = status;
                Reject(wrong, 0x80, "non-success status " + status);
            }
            byte[] corrupt = Response(0x80, 128); corrupt[10] = 255;
            Reject(corrupt, 0x80, "corrupt battery data");
            Assert(BatteryProtocol.ToPercentage(0) == 0, "0 percent");
            Assert(BatteryProtocol.ToPercentage(255) == 100, "100 percent");
            Assert(BatteryProtocol.ToPercentage(128) == 50, "50 percent");
            int previous = -1;
            for (int raw = 0; raw <= 255; raw++)
            {
                int percentage = BatteryProtocol.ToPercentage((byte)raw);
                Assert(percentage >= previous && percentage >= 0 && percentage <= 100, "conversion range/monotonicity");
                previous = percentage;
            }
            Console.WriteLine("PASS: " + assertions + " protocol assertions");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception.Message);
            return 1;
        }
    }
}
