using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using ViperBattery;

internal static class WindowsBatteryTests
{
    private static int assertions;
    private static void Assert(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    private static WindowsMouseNode Node(string id, string parent, string container)
    {
        return new WindowsMouseNode { Id = id, ParentId = parent, ContainerId = container,
            Present = true, StatusKnown = true, Status = 8 };
    }

    private static List<WindowsMouseNode> BluetoothFixture(bool lowEnergy)
    {
        // Real Windows topology: classic profile and DEV_* nodes are siblings
        // under the radio; LE GATT services are children of the peripheral node.
        WindowsMouseNode radio = Node("BTH\\MS_BTHBRB\\RADIO", null, "radio");
        WindowsMouseNode root = Node((lowEnergy ? "BTHLE" : "BTHENUM") + "\\DEV_001122334455\\DEVICE", radio.Id, "mouse-a");
        root.FriendlyName = "Example Bluetooth Mouse";
        root.Categories = new string[] { "Input.Mouse" };
        WindowsMouseNode service = Node((lowEnergy ? "BTHLEDEVICE" : "BTHENUM") + "\\{HID-SERVICE}\\MOUSE-A",
            lowEnergy ? root.Id : radio.Id, "mouse-a");
        WindowsMouseNode mouse = Node("HID\\MOUSE-A\\COL01", service.Id, "mouse-a");
        mouse.IsMouse = true;
        mouse.FriendlyName = "HID-compliant mouse";
        WindowsMouseNode battery = Node((lowEnergy ? "BTHLEDEVICE" : "BTHENUM") + "\\{BATTERY-SERVICE}\\MOUSE-A",
            lowEnergy ? root.Id : radio.Id, "mouse-a");
        battery.Percentage = 37;
        return new List<WindowsMouseNode> { radio, root, service, mouse, battery };
    }

    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--hardware")
        {
            BatteryScan live = new WindowsBatteryReader().Scan();
            Console.WriteLine(live.Diagnostic);
            foreach (BatteryReading reading in live.Readings)
                Console.WriteLine(reading.DeviceName + ": " + (reading.Percentage.HasValue ? reading.Percentage + "%" : "unavailable"));
            return 0;
        }
        try
        {
            TestPreviousScanIsNotReused();
            Assert(WindowsMouseBatterySelection.ParsePercentage(3, new byte[] { 0 }) == 0, "real 0% must survive");
            Assert(WindowsMouseBatterySelection.ParsePercentage(3, new byte[] { 100 }) == 100, "real 100%");
            Assert(!WindowsMouseBatterySelection.ParsePercentage(3, new byte[] { 101 }).HasValue, "Windows unknown sentinel");
            Assert(!WindowsMouseBatterySelection.ParsePercentage(3, new byte[] { 255 }).HasValue, "unknown byte");
            Assert(!WindowsMouseBatterySelection.ParsePercentage(3, null).HasValue, "missing property");
            Assert(!WindowsMouseBatterySelection.ParsePercentage(3, new byte[0]).HasValue, "empty property");
            Assert(!WindowsMouseBatterySelection.ParsePercentage(7, new byte[] { 37, 0, 0, 0 }).HasValue, "wrong DEVPROP type");
            Assert(!WindowsMouseBatterySelection.ParsePercentage(0x1003, new byte[] { 37 }).HasValue, "array modifier");
            foreach (bool le in new bool[] { false, true })
            {
                List<WindowsMouseNode> nodes = BluetoothFixture(le);
                BatteryScan scan = WindowsMouseBatterySelection.Select(nodes);
                Assert(scan.Readings.Count == 1 && scan.Readings[0].Percentage == 37, "related battery service " + le);
                Assert(scan.Readings[0].DeviceName == "Example Bluetooth Mouse", "physical device name " + le);
                Assert(scan.Readings[0].IsCharging == null, "cached battery does not prove charging " + le);
                WindowsMouseNode second = Node("HID\\MOUSE-A\\COL02", nodes[2].Id, "mouse-a");
                second.IsMouse = true;
                nodes.Add(second);
                Assert(WindowsMouseBatterySelection.Select(nodes).Readings.Count == 1, "dedup mouse collections " + le);
                nodes[1].Status |= 0x02000000;
                Assert(WindowsMouseBatterySelection.Select(nodes).Readings.Count == 0, "present but disconnected physical mouse " + le);
                nodes[1].Status = 8;
                nodes[2].Status |= 0x02000000;
                Assert(WindowsMouseBatterySelection.Select(nodes).Readings.Count == 0, "disconnected HID service " + le);
                nodes[2].Status = 8;
                nodes[1].ContainerConnected = false;
                Assert(WindowsMouseBatterySelection.Select(nodes).Readings.Count == 0, "explicit disconnected metadata " + le);
                nodes[1].ContainerConnected = null;
                nodes[4].ContainerId = "headset-b";
                nodes[4].ParentId = nodes[0].Id;
                scan = WindowsMouseBatterySelection.Select(nodes);
                Assert(scan.Readings.Count == 1 && !scan.Readings[0].Percentage.HasValue, "same radio cannot donate headset battery " + le);
                nodes[1].Categories = new string[] { "Input.Gaming" };
                Assert(WindowsMouseBatterySelection.Select(nodes).Readings.Count == 0, "controller auxiliary mouse excluded " + le);
                nodes[1].Categories = new string[] { "Input.Keyboard" };
                Assert(WindowsMouseBatterySelection.Select(nodes).Readings.Count == 0, "keyboard auxiliary mouse excluded " + le);
                nodes[1].Categories = new string[] { "Audio.Headphones" };
                Assert(WindowsMouseBatterySelection.Select(nodes).Readings.Count == 0, "audio auxiliary mouse excluded " + le);
            }
            List<WindowsMouseNode> keyboard = BluetoothFixture(true);
            keyboard[3].IsMouse = false;
            Assert(WindowsMouseBatterySelection.Select(keyboard).Readings.Count == 0, "keyboard/headset without a mouse function");
            List<WindowsMouseNode> absent = BluetoothFixture(true);
            absent[3].Present = false;
            Assert(WindowsMouseBatterySelection.Select(absent).Readings.Count == 0, "non-present mouse collection");
            List<WindowsMouseNode> unknown = BluetoothFixture(true);
            unknown[1].StatusKnown = false;
            Assert(WindowsMouseBatterySelection.Select(unknown).Readings.Count == 0, "unknown root status cannot prove connectivity");
            List<WindowsMouseNode> two = BluetoothFixture(true);
            List<WindowsMouseNode> other = BluetoothFixture(true);
            foreach (WindowsMouseNode node in other)
            {
                node.Id = node.Id.Replace("MOUSE-A", "MOUSE-B").Replace("001122334455", "AABBCCDDEEFF");
                if (node.ParentId != null) node.ParentId = node.ParentId.Replace("MOUSE-A", "MOUSE-B").Replace("001122334455", "AABBCCDDEEFF");
                node.ContainerId = node.ContainerId.Replace("mouse-a", "mouse-b");
            }
            other[4].Percentage = 82;
            two.AddRange(other.GetRange(1, 4));
            BatteryScan multiple = WindowsMouseBatterySelection.Select(two);
            Assert(multiple.Readings.Count == 2 && multiple.Readings[0].Percentage == 37 && multiple.Readings[1].Percentage == 82,
                "two mice on one radio retain their own battery values");
            Assert(multiple.Readings[0].DeviceId != multiple.Readings[1].DeviceId, "stable separate device identities");
            Console.WriteLine("Windows battery tests passed: " + assertions);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void TestPreviousScanIsNotReused()
    {
        // Inject the state left by a timed-out native driver, without making a
        // real driver stall or adding test-only behavior to the production reader.
        WindowsBatteryReader reader = new WindowsBatteryReader();
        TaskCompletionSource<BatteryScan> previous = new TaskCompletionSource<BatteryScan>();
        FieldInfo pending = typeof(WindowsBatteryReader).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic);
        pending.SetValue(reader, previous.Task);
        BatteryScan stale = new BatteryScan();
        stale.Readings.Add(new BatteryReading { DeviceId = "stale-test-device", Percentage = 53, Connected = true });
        Task<BatteryScan> refresh = Task.Factory.StartNew<BatteryScan>(reader.Scan);
        try
        {
            Assert(refresh.Wait(1000), "refresh must not wait for the preceding scan");
            Assert(refresh.Result.Readings.Count == 0 && !String.IsNullOrEmpty(refresh.Result.Diagnostic),
                "pending scan returns unavailable rather than an old percentage");
            Assert(Object.ReferenceEquals(pending.GetValue(reader), previous.Task), "pending scan does not spawn another native worker");
        }
        finally
        {
            previous.SetResult(stale);
            refresh.Wait(4000);
        }
        BatteryScan fresh = reader.Scan();
        Assert(!Object.ReferenceEquals(pending.GetValue(reader), previous.Task), "completed old scan is replaced on the next refresh");
        foreach (BatteryReading reading in fresh.Readings)
            Assert(reading.DeviceId != "stale-test-device", "a late result cannot reappear as the current battery");
        Assert(!Object.ReferenceEquals(fresh, stale), "late scan result object is discarded");
    }
}
