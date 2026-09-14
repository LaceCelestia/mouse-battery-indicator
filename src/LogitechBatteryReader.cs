using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ViperBattery
{
    // Protocol facts, independently implemented:
    // https://github.com/Logitech/cpg-docs/tree/master/hidpp20
    // https://github.com/torvalds/linux/blob/master/drivers/hid/hid-logitech-hidpp.c
    // https://github.com/pwr-Solaar/Solaar/blob/master/lib/logitech_receiver/hidpp20.py
    internal static class LogitechProtocol
    {
        internal static byte[] Request(byte device, byte feature, byte function, byte software, params byte[] parameters)
        {
            if ((device != 0xFF && (device < 1 || device > 6)) || function > 15 || software == 0 || software > 15 ||
                parameters == null || parameters.Length > 16)
                throw new ArgumentException("HID++ の問い合わせが不正です。");
            byte[] request = new byte[20];
            request[0] = 0x11; request[1] = device; request[2] = feature;
            request[3] = (byte)((function << 4) | software);
            Array.Copy(parameters, 0, request, 4, parameters.Length);
            return request;
        }

        // 1 = exact reply, -1 = exact error, 0 = other client's/event/malformed packet.
        internal static int MatchResponse(byte[] request, byte[] response)
        {
            if (request == null || request.Length < 7 || response == null || response.Length < 7 ||
                (response[0] != 0x10 && response[0] != 0x11) || (response[0] == 0x11 && response.Length < 20) ||
                response[1] != request[1]) return 0;
            if ((response[2] == 0x8F && response[0] == 0x10) || (response[2] == 0xFF && response[0] == 0x11))
                return response[3] == request[2] && response[4] == request[3] ? -1 : 0;
            return response[2] == request[2] && response[3] == request[3] ? 1 : 0;
        }

        internal static bool IsMouse(byte kind) { return kind == 3 || kind == 5; }

        internal static byte[] Parameters(byte[] report)
        {
            if (report == null || report.Length < 7) return null;
            int length = report[0] == 0x10 ? 3 : report[0] == 0x11 && report.Length >= 20 ? 16 : 0;
            if (length == 0) return null;
            byte[] result = new byte[length];
            Array.Copy(report, 4, result, 0, length);
            return result;
        }

        internal static bool ParseUnified(byte[] capabilities, byte[] data, BatteryReading reading)
        {
            if (capabilities == null || capabilities.Length < 2 || data == null || data.Length < 4 || data[2] > 4)
                return false;
            bool percentage = (capabilities[1] & 2) != 0;
            if (percentage && data[0] > 100) return false;
            reading.Percentage = percentage ? (int?)data[0] : null;
            reading.IsCharging = data[2] == 4 ? (bool?)null : data[2] == 1 || data[2] == 2;
            reading.IsApproximate = false;
            reading.BatteryLevelText = percentage ? null : UnifiedLevel((byte)(data[1] & capabilities[0]));
            reading.Detail = data[2] == 4 ? "マウスが充電エラーを報告しています。"
                : percentage ? "" : "このマウスは残量を段階で通知します。";
            return true;
        }

        private static string UnifiedLevel(byte value)
        {
            if ((value & 8) != 0) return "満充電";
            if ((value & 4) != 0) return "残量良好";
            if ((value & 2) != 0) return "残量少";
            if ((value & 1) != 0) return "残量わずか";
            return "残量不明";
        }

        internal static bool ParseLevelStatus(byte[] capabilities, byte[] data, BatteryReading reading)
        {
            if (capabilities == null || capabilities.Length < 2 || data == null || data.Length < 3 ||
                data[0] > 100 || data[1] > 100 || data[2] > 7) return false;
            bool percentage = capabilities[0] >= 10 && (capabilities[1] & 2) != 0;
            reading.Percentage = percentage && data[0] > 0 ? (int?)data[0] : null;
            reading.IsCharging = data[2] > 4 ? (bool?)null : data[2] == 1 || data[2] == 2 || data[2] == 4;
            reading.IsApproximate = false;
            reading.BatteryLevelText = null;
            if (!reading.Percentage.HasValue)
            {
                if (data[2] == 3) reading.BatteryLevelText = "満充電";
                else if (data[0] == 0) reading.BatteryLevelText = "残量不明";
                else if (data[0] < 11) reading.BatteryLevelText = "残量わずか";
                else if (data[0] < 30) reading.BatteryLevelText = "残量少";
                else if (data[0] < 81) reading.BatteryLevelText = "残量良好";
                else reading.BatteryLevelText = "満充電";
            }
            reading.Detail = data[2] > 4 ? "マウスが電池または充電のエラーを報告しています。"
                : !percentage ? "このマウスは残量を段階で通知します。"
                : data[0] == 0 ? "マウスが残量の数値を通知していません。" : "";
            return true;
        }

        internal static bool ParseVoltage(byte[] data, bool adc, BatteryReading reading)
        {
            if (data == null || data.Length < 3) return false;
            int millivolts = (data[0] << 8) | data[1];
            if (millivolts < 500 || millivolts > 5000) return false;
            byte flags = data[2];
            if (adc)
            {
                if (flags != 1 && flags != 3 && flags != 7) return false;
                reading.IsCharging = flags == 3;
                // 1F20 covers different battery chemistries. Do not apply a
                // headset curve or the 1001 curve to arbitrary mice.
                reading.Percentage = null;
                reading.IsApproximate = false;
                reading.BatteryLevelText = flags == 7 ? "満充電" : "電圧のみ";
                reading.Detail = "電池電圧 " + millivolts + " mV（残量の数値は取得できません）。";
                return true;
            }
            int charge = flags & 7;
            reading.IsCharging = (flags & 0x80) == 0 ? (bool?)false : charge == 0 ? (bool?)true : charge == 1 ? (bool?)false : null;
            // Publicly observed 1001 Li-ion curve; outside its range we only
            // show voltage. The taskbar and popup must label this as estimated.
            reading.Percentage = EstimatePercentage(millivolts);
            reading.IsApproximate = reading.Percentage.HasValue;
            reading.BatteryLevelText = reading.Percentage.HasValue ? null : "電圧のみ";
            reading.Detail = "電池電圧 " + millivolts + " mV" +
                (reading.Percentage.HasValue ? " からの推定値です。" : "（残量の数値は取得できません）。") +
                ((flags & 0x80) != 0 && charge >= 2 ? "充電状態は不明です。" : "");
            return true;
        }

        internal static int? EstimatePercentage(int millivolts)
        {
            if (millivolts < 3500 || millivolts > 4400) return null;
            // Measurement points, not a claim of model-specific calibration.
            int[] voltage = { 3500, 3579, 3646, 3671, 3717, 3751, 3778, 3811, 3859, 3922, 3989, 4067, 4186 };
            int[] percent = { 0, 2, 5, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
            for (int i = 1; i < voltage.Length; i++)
            {
                if (millivolts > voltage[i]) continue;
                return percent[i - 1] + (int)Math.Round((millivolts - voltage[i - 1]) *
                    (double)(percent[i] - percent[i - 1]) / (voltage[i] - voltage[i - 1]));
            }
            return 100;
        }
    }

    public sealed class LogitechBatteryReader
    {
        private const int ScanBudget = 7000;
        private readonly object gate = new object();
        private readonly Func<BatteryScan> scanOperation;
        private Task<BatteryScan> pending;
        private static int sequence = Environment.TickCount & 0x7FFF;

        public LogitechBatteryReader() { scanOperation = ScanCore; }

        internal LogitechBatteryReader(Func<BatteryScan> operation)
        {
            if (operation == null) throw new ArgumentNullException("operation");
            scanOperation = operation;
        }

        public BatteryScan Scan()
        {
            Task<BatteryScan> operation;
            lock (gate)
            {
                // Only the call starting an operation can return its result.
                // Reusing a prior timed-out operation would present old data as a
                // fresh reading, even if that operation finishes during this call.
                if (pending != null && !pending.IsCompleted)
                    return new BatteryScan { Diagnostic = "Logitech: 前回の問い合わせの終了を待っています。" };
                pending = Task.Factory.StartNew<BatteryScan>(scanOperation, CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
                operation = pending;
            }
            if (!operation.Wait(8000)) return new BatteryScan { Diagnostic = "Logitech: デバイスの応答待ちが時間内に完了しませんでした。" };
            return operation.Result;
        }

        private BatteryScan ScanCore()
        {
            BatteryScan result = new BatteryScan();
            Stopwatch clock = Stopwatch.StartNew();
            StringBuilder log = new StringBuilder();
            Mutex mutex = null;
            bool owns = false;
            try
            {
                mutex = new Mutex(false, "Local\\MouseBatteryIndicator.LogitechQuery");
                try { owns = mutex.WaitOne(250); } catch (AbandonedMutexException) { owns = true; }
                if (!owns) { log.AppendLine("別の Logitech 問い合わせが実行中です。"); return result; }
                if (!LogitechHidTransport.ReapCancelledIo())
                { log.AppendLine("HID のキャンセル完了を待っています。"); return result; }
                List<LogitechEndpoint> endpoints = LogitechHidTransport.Enumerate(log, clock, ScanBudget);
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (LogitechEndpoint endpoint in endpoints)
                {
                    if (clock.ElapsedMilliseconds >= ScanBudget) break;
                    foreach (byte index in new byte[] { 0xFF, 1, 2, 3, 4, 5, 6 })
                    {
                        if (clock.ElapsedMilliseconds >= ScanBudget) break;
                        string identity = (String.IsNullOrEmpty(endpoint.ContainerId) ? endpoint.Path : endpoint.ContainerId) + ":" + index;
                        if (seen.Contains(identity)) continue;
                        try
                        {
                            // A timeout discards the handle and its per-handle input
                            // queue before probing the next receiver slot.
                            using (ILogitechTransport transport = new LogitechHidTransport(endpoint))
                            {
                                BatteryReading reading = ReadDevice(transport, endpoint, index, clock, log);
                                if (reading != null)
                                {
                                    seen.Add(identity);
                                    result.Readings.Add(reading);
                                    // FF successfully identifying as a mouse denotes a direct
                                    // device, not a receiver whose child slots need probing.
                                    if (index == 0xFF) break;
                                }
                            }
                        }
                        catch (Exception exception) { log.AppendLine(exception.GetType().Name + ": " + exception.Message); }
                    }
                }
                if (endpoints.Count == 0) log.AppendLine("Logitech HID++ の command collection は見つかりませんでした。");
                if (clock.ElapsedMilliseconds >= ScanBudget) log.AppendLine("探索の制限時間に達しました。");
            }
            catch (Exception exception) { log.AppendLine(exception.GetType().Name + ": " + exception.Message); }
            finally
            {
                if (owns) mutex.ReleaseMutex();
                if (mutex != null) mutex.Dispose();
                log.AppendLine("Logitech: " + result.Readings.Count + " device(s), " + clock.ElapsedMilliseconds + " ms");
                result.Diagnostic = log.ToString();
            }
            return result;
        }

        internal static BatteryReading ReadDevice(ILogitechTransport transport, LogitechEndpoint endpoint, byte device,
            Stopwatch clock, StringBuilder log)
        {
            QuerySession session = new QuerySession(transport, device, clock, log);
            byte challenge = (byte)(NextSoftwareId() + 0x60);
            byte[] ping = session.Get(0x0000, 0, 1, 0, 0, challenge);
            if (ping == null || ping.Length < 3 || ping[0] < 2 || ping[2] != challenge) return null;
            byte nameFeature = session.Find(0x0005);
            if (nameFeature == 0) return null;
            byte[] kind = session.Get(0x0005, nameFeature, 2);
            if (kind == null || kind.Length < 1 || !LogitechProtocol.IsMouse(kind[0])) return null;
            string name = ReadName(session, nameFeature);
            BatteryReading reading = new BatteryReading
            {
                DeviceId = "logitech:" + endpoint.Path.ToLowerInvariant() + ":" + device.ToString("X2"),
                Provider = "Logitech", Connected = true,
                // A receiver's container belongs to all its paired slots, so it
                // cannot identify an individual mouse for cross-provider merging.
                ContainerId = device == 0xFF ? endpoint.ContainerId : null,
                DeviceName = String.IsNullOrEmpty(name) ? "Logitech マウス" : name,
                Connection = device == 0xFF ? "USB" : "USB レシーバー",
                Detail = "このマウスの残量は取得できません。", Timestamp = DateTime.Now
            };
            BatteryReading fallback = null;
            foreach (ushort featureId in new ushort[] { 0x1004, 0x1000, 0x1001, 0x1F20 })
            {
                byte feature = session.Find(featureId);
                if (feature == 0) continue;
                byte[] capability = null, data;
                if (featureId == 0x1004)
                {
                    capability = session.Get(featureId, feature, 0);
                    data = session.Get(featureId, feature, 1);
                }
                else
                {
                    if (featureId == 0x1000) capability = session.Get(featureId, feature, 1);
                    data = session.Get(featureId, feature, 0);
                }
                BatteryReading value = new BatteryReading();
                bool valid = featureId == 0x1004 ? LogitechProtocol.ParseUnified(capability, data, value)
                    : featureId == 0x1000 ? LogitechProtocol.ParseLevelStatus(capability, data, value)
                    : LogitechProtocol.ParseVoltage(data, featureId == 0x1F20, value);
                if (!valid) continue;
                log.AppendLine("slot " + device.ToString("X2") + " battery feature " + featureId.ToString("X4") + " OK");
                if (fallback == null || (!fallback.Percentage.HasValue && value.Percentage.HasValue)) fallback = value;
                if (value.Percentage.HasValue && !value.IsApproximate) break;
            }
            if (fallback != null)
            {
                reading.Percentage = fallback.Percentage; reading.IsCharging = fallback.IsCharging;
                reading.BatteryLevelText = fallback.BatteryLevelText; reading.IsApproximate = fallback.IsApproximate;
                reading.Detail = fallback.Detail;
            }
            return reading;
        }

        private static string ReadName(QuerySession session, byte feature)
        {
            byte[] lengthReply = session.Get(0x0005, feature, 0);
            if (lengthReply == null || lengthReply[0] == 0 || lengthReply[0] > 96) return null;
            byte[] name = new byte[lengthReply[0]];
            int position = 0;
            while (position < name.Length)
            {
                byte[] part = session.Get(0x0005, feature, 1, (byte)position);
                if (part == null || part.Length == 0) return null;
                int copy = Math.Min(part.Length, name.Length - position);
                Array.Copy(part, 0, name, position, copy);
                position += copy;
            }
            string text;
            try { text = new UTF8Encoding(false, true).GetString(name).TrimEnd('\0').Trim(); }
            catch (DecoderFallbackException) { return null; }
            foreach (char character in text) if (Char.IsControl(character)) return null;
            return text;
        }

        private static byte NextSoftwareId()
        {
            return (byte)((Interlocked.Increment(ref sequence) & 0x7FFFFFFF) % 15 + 1);
        }

        private sealed class QuerySession
        {
            private readonly ILogitechTransport transport;
            private readonly byte device;
            private readonly Stopwatch clock;
            private readonly StringBuilder log;
            internal QuerySession(ILogitechTransport channel, byte index, Stopwatch timer, StringBuilder diagnostic)
            { transport = channel; device = index; clock = timer; log = diagnostic; }
            internal byte Find(ushort feature)
            {
                byte[] reply = Get(0, 0, 0, (byte)(feature >> 8), (byte)feature);
                return reply != null && reply.Length >= 3 && reply[0] != 0xFF ? reply[0] : (byte)0;
            }
            internal byte[] Get(ushort featureId, byte featureIndex, byte function, params byte[] parameters)
            {
                // Allowlist every command dispatched to the device. Merely using
                // WriteFile is required for GET request transport, never for settings.
                bool allowed = featureId == 0 && featureIndex == 0 && (function == 0 || function == 1) ||
                    featureId == 0x0005 && function <= 2 ||
                    (featureId == 0x1004 || featureId == 0x1000) && function <= 1 ||
                    (featureId == 0x1001 || featureId == 0x1F20) && function == 0;
                if (!allowed) throw new InvalidOperationException("読み取り専用コマンドではありません。");
                int remaining = ScanBudget - (int)clock.ElapsedMilliseconds;
                if (remaining <= 0) return null;
                byte[] request = LogitechProtocol.Request(device, featureIndex, function, NextSoftwareId(), parameters);
                string error;
                byte[] response = transport.Exchange(request, Math.Min(350, remaining), out error);
                if (response == null || LogitechProtocol.MatchResponse(request, response) != 1)
                {
                    log.AppendLine("slot " + device.ToString("X2") + ", feature " + featureId.ToString("X4") +
                        "/" + function + ": " + (error ?? "応答が一致しません。"));
                    return null;
                }
                return LogitechProtocol.Parameters(response);
            }
        }
    }
}
