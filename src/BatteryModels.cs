using System;
using System.Collections.Generic;

namespace ViperBattery
{
    public sealed class BatteryReading
    {
        public bool Connected { get; set; }
        public int? Percentage { get; set; }
        public bool? IsCharging { get; set; }
        public string DeviceName { get; set; }
        public string Connection { get; set; }
        public string Detail { get; set; }
        public DateTime Timestamp { get; set; }
        public string DeviceId { get; set; }
        public string Provider { get; set; }
        public string ContainerId { get; set; }
        public string BatteryLevelText { get; set; }
        public bool IsApproximate { get; set; }
    }

    public sealed class BatteryScan
    {
        public BatteryScan() { Readings = new List<BatteryReading>(); }
        public List<BatteryReading> Readings { get; set; }
        public string Diagnostic { get; set; }
    }
}
