using System;
using System.Diagnostics;
using System.Threading;
using ViperBattery;

internal static class HidInventoryTests
{
    private static int assertions;
    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    private static int Main()
    {
        try
        {
            HidInventoryEntry item = new HidInventoryEntry { Group = 2, VendorId = 0x1532, ProductId = 0x00C1,
                Manufacturer = "Razer", Product = "Razer Viper V3 Pro", HasCaps = true, UsagePage = 1, Usage = 2,
                InputLength = 17, OutputLength = 0, FeatureLength = 91 };
            string line = HidInventory.FormatEntry(item);
            Check(line.Contains("Group 02") && line.Contains("VID:PID 1532:00C1"), "Model identity is formatted as USB IDs with an anonymous group");
            Check(line.Contains("Manufacturer: Razer") && line.Contains("Product: Razer Viper V3 Pro"), "Manufacturer and product are preserved");
            Check(line.Contains("Usage 0001/0002") && line.Contains("Input 17, Output 0, Feature 91 bytes"), "HID capabilities are available for protocol identification");
            item.HasCaps = false;
            Check(HidInventory.FormatEntry(item).Contains("HID capabilities unavailable") && !HidInventory.FormatEntry(item).Contains("Feature 91"), "Failed capabilities do not appear as valid lengths");
            Check(HidInventory.CleanLabel(null) == "(unavailable)" && HidInventory.CleanLabel(" \r\n ") == "(unavailable)", "Missing USB strings are explicit");
            Check(HidInventory.CleanLabel("Mouse\r\nInjected | Field\u2028Next") == "Mouse  Injected   Field Next", "Descriptors cannot inject report lines or columns");
            Check(HidInventory.CleanLabel("Mouse\0trailing-buffer") == "Mouse", "NUL terminates the descriptor");
            Check(HidInventory.CleanLabel(new String('A', 1024)).Length == 128, "Malformed labels cannot grow diagnostics without bound");

            int calls = 0;
            using (ManualResetEvent release = new ManualResetEvent(false))
            using (ManualResetEvent entered = new ManualResetEvent(false))
            using (ManualResetEvent finished = new ManualResetEvent(false))
            {
                HidInventoryWorker worker = new HidInventoryWorker(delegate(int timeout)
                {
                    Interlocked.Increment(ref calls);
                    entered.Set();
                    release.WaitOne();
                    finished.Set();
                    return "complete";
                }, 80);
                Stopwatch clock = Stopwatch.StartNew();
                Check(worker.Run().Contains("timed out"), "A stuck metadata query cannot hold up diagnostics");
                Check(clock.ElapsedMilliseconds < 2000 && entered.WaitOne(1000), "Metadata timeout is bounded and a worker was started");
                for (int i = 0; i < 5; i++) Check(worker.Run().Contains("still pending"), "Retries never launch more workers while the driver is stuck");
                Check(calls == 1, "Only one outstanding metadata query exists");
                release.Set();
                Check(finished.WaitOne(1000), "The blocked worker can finish normally");
                string result = null;
                for (int i = 0; i < 100; i++)
                {
                    result = worker.Run();
                    if (result == "complete") break;
                    Thread.Sleep(10);
                }
                Check(result == "complete" && calls == 2, "Later diagnostics can run after the earlier worker finishes");
            }
            HidInventoryWorker errorWorker = new HidInventoryWorker(delegate(int timeout) { throw new Exception(@"C:\Users\PrivateName\secret"); }, 500);
            string failure = errorWorker.Run();
            Check(failure.Contains("Exception") && !failure.Contains("PrivateName") && !failure.Contains("secret"), "Exception messages cannot expose local paths or user names");
            Console.WriteLine("PASS: " + assertions + " HID inventory assertions.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
