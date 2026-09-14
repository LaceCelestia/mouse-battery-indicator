using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ViperBattery
{
    // Windows supplies cached percentages; this provider never opens a GATT session,
    // pairs devices, changes drivers, or guesses a percentage from an unknown value.
    public sealed class WindowsBatteryReader
    {
        private readonly object gate = new object();
        private Task<BatteryScan> pending;

        public BatteryScan Scan()
        {
            Task<BatteryScan> operation;
            lock (gate)
            {
                // Only the call that starts a scan may use its result. A worker
                // surviving an earlier timeout must never supply a later refresh.
                if (pending != null && !pending.IsCompleted)
                    return new BatteryScan { Diagnostic = "Windows: 前回のデバイス情報取得の終了を待っています。" };
                pending = Task.Factory.StartNew<BatteryScan>(ScanCore, CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
                operation = pending;
            }
            // At most one worker survives a stalled Windows device enumeration.
            if (!operation.Wait(3000))
                return new BatteryScan { Diagnostic = "Windows: デバイス情報の取得がタイムアウトしました。" };
            return operation.Result;
        }

        private static BatteryScan ScanCore()
        {
            try
            {
                string diagnostic;
                List<WindowsMouseNode> nodes = WindowsBatteryNative.Enumerate(out diagnostic);
                BatteryScan result = WindowsMouseBatterySelection.Select(nodes);
                result.Diagnostic = diagnostic + "\r\n" + result.Diagnostic;
                return result;
            }
            catch (Exception ex)
            {
                return new BatteryScan { Diagnostic = "Windows: " + ex.Message };
            }
        }
    }

    internal sealed class WindowsMouseNode
    {
        internal string Id;
        internal string ParentId;
        internal string ContainerId;
        internal string FriendlyName;
        internal string BusName;
        internal string[] Categories;
        internal bool IsMouse;
        internal bool Present;
        internal bool StatusKnown;
        internal uint Status;
        internal bool? ContainerConnected;
        internal int? Percentage;

        internal bool IsBluetooth
        {
            get
            {
                return !String.IsNullOrEmpty(Id) &&
                    (Id.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase) ||
                     Id.StartsWith("BTHLE\\", StringComparison.OrdinalIgnoreCase) ||
                     Id.StartsWith("BTHLEDEVICE\\", StringComparison.OrdinalIgnoreCase));
            }
        }

        internal bool IsBluetoothDeviceRoot
        {
            get
            {
                return !String.IsNullOrEmpty(Id) &&
                    (Id.StartsWith("BTHENUM\\DEV_", StringComparison.OrdinalIgnoreCase) ||
                     Id.StartsWith("BTHLE\\DEV_", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    internal static class WindowsMouseBatterySelection
    {
        // cfg.h: DN_STARTED, DN_HAS_PROBLEM, DN_DEVICE_DISCONNECTED. A remembered
        // Bluetooth devnode can be PRESENT while its wireless link is disconnected.
        private const uint UnusableFlags = 0x02000400;

        internal static BatteryScan Select(IList<WindowsMouseNode> nodes)
        {
            BatteryScan scan = new BatteryScan();
            Dictionary<string, WindowsMouseNode> byId = new Dictionary<string, WindowsMouseNode>(StringComparer.OrdinalIgnoreCase);
            foreach (WindowsMouseNode node in nodes)
                if (!String.IsNullOrEmpty(node.Id)) byId[node.Id] = node;
            HashSet<string> emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int mice = 0;
            int disconnected = 0;
            foreach (WindowsMouseNode mouse in nodes)
            {
                if (!mouse.Present || !mouse.IsMouse) continue;
                WindowsMouseNode root = FindRoot(mouse, byId);
                if (root == null) continue;
                mice++;
                if (!IsUsable(mouse) || !IsUsable(root) || HasDisconnectedAncestor(mouse, root, byId))
                {
                    disconnected++;
                    continue;
                }
                // A game controller can expose an auxiliary mouse collection. Do not
                // claim that its battery belongs to a mouse. Audio/keyboard-only
                // devices never enter here because they have no Mouse class node.
                if (HasCategory(root, "Input.Gaming") || HasCategory(root, "Audio.") ||
                    (HasCategory(root, "Input.Keyboard") && !HasCategory(root, "Input.Mouse"))) continue;
                if (!emitted.Add(root.Id)) continue;

                int? percentage = root.Percentage;
                if (!percentage.HasValue) percentage = mouse.Percentage;
                if (!percentage.HasValue)
                {
                    foreach (WindowsMouseNode sibling in nodes)
                    {
                        if (!sibling.Present || !sibling.Percentage.HasValue || !IsUsable(sibling)) continue;
                        WindowsMouseNode siblingRoot = FindRoot(sibling, byId);
                        bool sameRoot = siblingRoot != null && String.Equals(siblingRoot.Id, root.Id, StringComparison.OrdinalIgnoreCase);
                        if (sameRoot && !HasDisconnectedAncestor(sibling, root, byId))
                        {
                            percentage = sibling.Percentage;
                            break;
                        }
                    }
                }
                string container = !String.IsNullOrEmpty(root.ContainerId) ? root.ContainerId : mouse.ContainerId;
                scan.Readings.Add(new BatteryReading
                {
                    DeviceId = "windows:" + root.Id.ToLowerInvariant(),
                    ContainerId = container,
                    Provider = "Windows",
                    Connected = true,
                    DeviceName = FirstName(root.FriendlyName, root.BusName, mouse.FriendlyName, mouse.BusName),
                    Connection = "Bluetooth",
                    Percentage = percentage,
                    IsCharging = null,
                    Timestamp = DateTime.Now,
                    Detail = percentage.HasValue ? "Windows が最後に受信した残量です。充電状態は取得できません。" :
                        "このマウスの残量を Windows から取得できません。",
                    IsApproximate = false
                });
            }
            scan.Diagnostic = "Windows: Bluetooth mouse collections=" + mice + ", disconnected/unavailable=" + disconnected +
                ", devices=" + scan.Readings.Count + ". Windows-reported cached battery; charging state unavailable.";
            return scan;
        }

        private static bool HasCategory(WindowsMouseNode node, string prefix)
        {
            if (node.Categories == null) return false;
            foreach (string category in node.Categories)
                if (category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool IsUsable(WindowsMouseNode node)
        {
            return node.Present && node.StatusKnown && (node.Status & 8) != 0 &&
                (node.Status & UnusableFlags) == 0 && node.ContainerConnected != false;
        }

        private static bool HasDisconnectedAncestor(WindowsMouseNode node, WindowsMouseNode root,
            Dictionary<string, WindowsMouseNode> byId)
        {
            for (int depth = 0; depth < 16; depth++)
            {
                if (node.ContainerConnected == false || (node.StatusKnown && (node.Status & UnusableFlags) != 0)) return true;
                if (String.Equals(node.Id, root.Id, StringComparison.OrdinalIgnoreCase)) return false;
                // Classic Bluetooth services are siblings of their DEV_* node
                // beneath the radio. Their shared peripheral container is the link.
                if (node.IsBluetooth && SameContainer(node, root)) return false;
                if (String.IsNullOrEmpty(node.ParentId) || !byId.TryGetValue(node.ParentId, out node)) return true;
            }
            return true;
        }

        private static WindowsMouseNode FindRoot(WindowsMouseNode node, Dictionary<string, WindowsMouseNode> byId)
        {
            WindowsMouseNode bluetoothService = null;
            for (int depth = 0; depth < 16; depth++)
            {
                if (node.IsBluetoothDeviceRoot) return node;
                if (node.IsBluetooth) bluetoothService = node;
                else if (bluetoothService != null) break;
                if (String.IsNullOrEmpty(node.ParentId) || !byId.TryGetValue(node.ParentId, out node)) break;
            }
            if (bluetoothService == null) return null;
            WindowsMouseNode root = null;
            foreach (WindowsMouseNode candidate in byId.Values)
            {
                if (!candidate.IsBluetoothDeviceRoot || !SameContainer(candidate, bluetoothService)) continue;
                if (root != null) return null; // Ambiguous container: never cross devices.
                root = candidate;
            }
            return root;
        }

        private static bool SameContainer(WindowsMouseNode left, WindowsMouseNode right)
        {
            return !String.IsNullOrEmpty(left.ContainerId) && !String.IsNullOrEmpty(right.ContainerId) &&
                String.Equals(left.ContainerId, right.ContainerId, StringComparison.OrdinalIgnoreCase);
        }

        private static string FirstName(params string[] names)
        {
            foreach (string name in names)
                if (!String.IsNullOrWhiteSpace(name)) return name;
            return "Bluetooth マウス";
        }

        // DEVPROP_TYPE_BYTE only: other integers, text, arrays, empty properties and
        // the Windows unknown sentinel 101 must not turn into invented percentages.
        internal static int? ParsePercentage(uint type, byte[] data)
        {
            if (type != 3 || data == null || data.Length != 1 || data[0] > 100) return null;
            return data[0];
        }
    }

    internal static class WindowsBatteryNative
    {
        private static readonly Guid MouseClass = new Guid("4D36E96F-E325-11CE-BFC1-08002BE10318");
        private static readonly PropertyKey Parent = new PropertyKey("4340A6C5-93FA-4706-972C-7B648008A5A7", 8);
        private static readonly PropertyKey Container = new PropertyKey("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2);
        private static readonly PropertyKey InComputerContainer = new PropertyKey("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 4);
        private static readonly PropertyKey FriendlyName = new PropertyKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
        private static readonly PropertyKey BusName = new PropertyKey("540B947E-8B40-45BC-A8A2-6A0B894CBDA2", 4);
        private static readonly PropertyKey Category = new PropertyKey("78C34FC8-104A-4ACA-9EA4-524D52996E57", 90);
        private static readonly PropertyKey IsConnected = new PropertyKey("78C34FC8-104A-4ACA-9EA4-524D52996E57", 55);
        // Documented System.Devices.BatteryLife (propkey.h).
        // https://learn.microsoft.com/windows/win32/properties/props-system-devices-batterylife
        private static readonly PropertyKey BatteryLife = new PropertyKey("49CD1F76-5626-4B17-A4E8-18B4AA1A2213", 10);
        // Compatibility property exposed by the Windows Bluetooth stack. Its GUID
        // is not a published SDK contract. Original working implementation:
        // https://github.com/muedsa/BluetoothLEBatteryMonitor
        private static readonly PropertyKey BluetoothBattery = new PropertyKey("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);

        internal static List<WindowsMouseNode> Enumerate(out string diagnostic)
        {
            List<WindowsMouseNode> result = new List<WindowsMouseNode>();
            Stopwatch timer = Stopwatch.StartNew();
            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, 6); // PRESENT | ALLCLASSES
            if (set == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                for (uint index = 0; index < 4096 && timer.ElapsedMilliseconds < 2500; index++)
                {
                    DeviceInfo info = new DeviceInfo();
                    info.Size = Marshal.SizeOf(typeof(DeviceInfo));
                    if (!SetupDiEnumDeviceInfo(set, index, ref info))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 259) throw new System.ComponentModel.Win32Exception(error);
                        break;
                    }
                    StringBuilder id = new StringBuilder(1024);
                    uint required;
                    if (!SetupDiGetDeviceInstanceId(set, ref info, id, id.Capacity, out required)) continue;
                    WindowsMouseNode node = new WindowsMouseNode { Id = id.ToString(), IsMouse = info.ClassGuid == MouseClass, Present = true };
                    node.ParentId = ReadString(info.DevInst, Parent, 18);
                    uint status, problem;
                    node.StatusKnown = CM_Get_DevNode_Status(out status, out problem, info.DevInst, 0) == 0;
                    node.Status = status;
                    if (node.IsMouse || node.IsBluetooth || node.Id.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase))
                    {
                        node.ContainerId = ReadContainer(info.DevInst);
                        node.FriendlyName = ReadString(info.DevInst, FriendlyName, 18);
                        node.BusName = ReadString(info.DevInst, BusName, 18);
                        string categories = ReadString(info.DevInst, Category, 0x2012);
                        node.Categories = categories == null ? null : categories.Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
                        node.ContainerConnected = ReadBoolean(info.DevInst, IsConnected);
                        uint type;
                        byte[] data = ReadProperty(info.DevInst, BatteryLife, out type);
                        node.Percentage = WindowsMouseBatterySelection.ParsePercentage(type, data);
                        if (!node.Percentage.HasValue)
                        {
                            data = ReadProperty(info.DevInst, BluetoothBattery, out type);
                            node.Percentage = WindowsMouseBatterySelection.ParsePercentage(type, data);
                        }
                    }
                    result.Add(node);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            diagnostic = "Windows PnP: " + result.Count + " present nodes, " + timer.ElapsedMilliseconds + " ms.";
            if (timer.ElapsedMilliseconds >= 2500 || result.Count >= 4096) diagnostic += " Enumeration limit reached.";
            return result;
        }

        private static string ReadContainer(uint devInst)
        {
            if (ReadBoolean(devInst, InComputerContainer) == true) return null;
            uint type;
            byte[] bytes = ReadProperty(devInst, Container, out type);
            if (type != 13 || bytes == null || bytes.Length != 16) return null;
            Guid guid = new Guid(bytes);
            if (guid == Guid.Empty || guid == new Guid("00000000-0000-0000-FFFF-FFFFFFFFFFFF")) return null;
            return guid.ToString("D").ToLowerInvariant();
        }

        private static bool? ReadBoolean(uint devInst, PropertyKey key)
        {
            uint type;
            byte[] bytes = ReadProperty(devInst, key, out type);
            if (type != 17 || bytes == null || bytes.Length != 1) return null;
            return bytes[0] != 0;
        }

        private static string ReadString(uint devInst, PropertyKey key, uint expectedType)
        {
            uint type;
            byte[] bytes = ReadProperty(devInst, key, out type);
            if (type != expectedType || bytes == null || bytes.Length < 2 || bytes.Length % 2 != 0) return null;
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }

        private static byte[] ReadProperty(uint devInst, PropertyKey key, out uint type)
        {
            type = 0;
            uint length = 0;
            uint code = CM_Get_DevNode_Property(devInst, ref key, out type, null, ref length, 0);
            if (code != 0x1A || length == 0 || length > 16384) return null; // CR_BUFFER_SMALL
            byte[] bytes = new byte[length];
            if (CM_Get_DevNode_Property(devInst, ref key, out type, bytes, ref length, 0) != 0 || length != bytes.Length) return null;
            return bytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInfo
        {
            internal int Size;
            internal Guid ClassGuid;
            internal uint DevInst;
            internal IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            internal Guid Format;
            internal uint Id;
            internal PropertyKey(string format, uint id) { Format = new Guid(format); Id = id; }
        }

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(IntPtr guid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfo info, StringBuilder id, int length, out uint required);
        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_DevNode_Property(uint devInst, ref PropertyKey key, out uint type, byte[] buffer, ref uint size, uint flags);
    }
}
