using System;
using System.Collections.Generic;
using System.Globalization;

namespace Teamprojekt.Ftms
{
    public static class FtmsParsers
    {
        public static ResistanceScale DefaultResistance()
        {
            return new ResistanceScale(0, 255, 10, true, "resistance");
        }

        public static ResistanceScale DefaultPower()
        {
            return new ResistanceScale(0, 1000, 10, false, "power");
        }

        public static ResistanceScale DefaultGrade()
        {
            return new ResistanceScale(-1000, 2000, 50, false, "grade");
        }

        public static BikeReading ParseIndoorBike(byte[] data)
        {
            var reading = new BikeReading();
            if (data == null || data.Length < 2)
                return reading;

            int flags = ReadU16(data, 0);
            int resistanceOctets = ResistanceOctets(flags, data.Length);
            int index = 2;

            bool Take(int size, out byte[] chunk)
            {
                if (index + size > data.Length)
                {
                    chunk = null;
                    return false;
                }
                chunk = new byte[size];
                Buffer.BlockCopy(data, index, chunk, 0, size);
                index += size;
                return true;
            }

            // Flag bit 0 is inverted: clear means instantaneous speed is present.
            if ((flags & 0x0001) == 0)
            {
                if (!Take(2, out byte[] raw)) return reading;
                reading.SpeedKmh = ReadU16(raw, 0) / 100f;
            }
            if ((flags & 0x0002) != 0)
            {
                if (!Take(2, out byte[] raw)) return reading;
                reading.AvgSpeedKmh = ReadU16(raw, 0) / 100f;
            }
            if ((flags & 0x0004) != 0)
            {
                if (!Take(2, out byte[] raw)) return reading;
                reading.CadenceRpm = ReadU16(raw, 0) / 2f;
            }
            if ((flags & 0x0008) != 0)
            {
                if (!Take(2, out byte[] raw)) return reading;
                reading.AvgCadenceRpm = ReadU16(raw, 0) / 2f;
            }
            if ((flags & 0x0010) != 0)
            {
                if (!Take(3, out byte[] raw)) return reading;
                reading.DistanceM = ReadU24(raw, 0);
            }
            if ((flags & 0x0020) != 0)
            {
                if (!Take(resistanceOctets, out byte[] raw)) return reading;
                reading.Resistance = resistanceOctets == 2 ? ReadS16(raw, 0) : raw[0];
            }
            if ((flags & 0x0040) != 0)
            {
                if (!Take(2, out byte[] raw)) return reading;
                reading.PowerW = ReadS16(raw, 0);
            }
            if ((flags & 0x0080) != 0)
            {
                if (!Take(2, out byte[] raw)) return reading;
                reading.AvgPowerW = ReadS16(raw, 0);
            }
            if ((flags & 0x0100) != 0)
            {
                if (!Take(2, out byte[] total) || !Take(2, out _) || !Take(1, out _))
                    return reading;
                int kcal = ReadU16(total, 0);
                if (kcal != 0xFFFF)
                    reading.EnergyKcal = kcal;
            }
            if ((flags & 0x0200) != 0)
            {
                if (!Take(1, out byte[] raw)) return reading;
                reading.TrainerHr = raw[0];
            }
            if ((flags & 0x0400) != 0)
            {
                if (!Take(1, out byte[] raw)) return reading;
                reading.Met = raw[0] / 10f;
            }
            if ((flags & 0x0800) != 0)
            {
                if (!Take(2, out byte[] raw)) return reading;
                reading.ElapsedS = ReadU16(raw, 0);
            }
            if ((flags & 0x1000) != 0)
            {
                if (!Take(2, out byte[] raw)) return reading;
                reading.RemainingS = ReadU16(raw, 0);
            }
            return reading;
        }

        public static HeartReading ParseHeartRate(byte[] data)
        {
            var reading = new HeartReading();
            if (data == null || data.Length == 0)
                return reading;
            int flags = data[0];
            int index = 1;
            int bpm;
            if ((flags & 0x01) != 0)
            {
                if (index + 2 > data.Length)
                    return reading;
                bpm = ReadU16(data, index);
                index += 2;
            }
            else
            {
                if (index + 1 > data.Length)
                    return reading;
                bpm = data[index];
                index += 1;
            }

            int contactBits = (flags >> 1) & 0x03;
            string contact = null;
            if (contactBits == 3)
                contact = "contact";
            else if (contactBits == 2)
                contact = "no contact";

            if ((flags & 0x08) != 0)
            {
                if (index + 2 > data.Length)
                {
                    reading.Bpm = bpm;
                    reading.Contact = contact;
                    return reading;
                }
                index += 2;
            }

            float? rr = null;
            if ((flags & 0x10) != 0)
            {
                while (index + 2 <= data.Length)
                {
                    rr = ReadU16(data, index) / 1024f * 1000f;
                    index += 2;
                }
            }

            reading.Bpm = bpm;
            reading.Contact = contact;
            reading.RrMs = rr;
            return reading;
        }

        public static List<string> MachineTypes(byte[] payload)
        {
            var names = new List<string>();
            if (payload == null || payload.Length < 2)
                return names;
            int typeField = payload.Length >= 3 ? ReadU16(payload, 1) : payload[1];
            if ((typeField & (1 << 0)) != 0) names.Add("treadmill");
            if ((typeField & (1 << 1)) != 0) names.Add("cross trainer");
            if ((typeField & (1 << 2)) != 0) names.Add("step climber");
            if ((typeField & (1 << 3)) != 0) names.Add("stair climber");
            if ((typeField & (1 << 4)) != 0) names.Add("rower");
            if ((typeField & (1 << 5)) != 0) names.Add("indoor bike");
            return names;
        }

        public static bool? MachineAvailable(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                return null;
            return (payload[0] & 0x01) != 0;
        }

        public static string DescribeFeatures(byte[] value)
        {
            if (value == null || value.Length < 4)
                return "";
            int bits = ReadU32(value, 0);
            var names = new List<string>();
            if ((bits & (1 << 0)) != 0) names.Add("average speed");
            if ((bits & (1 << 1)) != 0) names.Add("cadence");
            if ((bits & (1 << 2)) != 0) names.Add("distance");
            if ((bits & (1 << 7)) != 0) names.Add("resistance");
            if ((bits & (1 << 9)) != 0) names.Add("energy");
            if ((bits & (1 << 10)) != 0) names.Add("heart rate");
            if ((bits & (1 << 12)) != 0) names.Add("elapsed time");
            if ((bits & (1 << 14)) != 0) names.Add("power");
            return string.Join(", ", names);
        }

        public static List<string> SupportedModes(byte[] feature)
        {
            var fallback = new List<string> { "resistance", "grade", "power" };
            if (feature == null || feature.Length < 8)
                return fallback;
            int bits = ReadU32(feature, 4);
            var modes = new List<string>();
            if ((bits & (1 << 2)) != 0) modes.Add("resistance");
            if ((bits & (1 << 13)) != 0) modes.Add("grade");
            if ((bits & (1 << 3)) != 0) modes.Add("power");
            return modes.Count > 0 ? modes : fallback;
        }

        public static ResistanceScale ParseResistanceRange(byte[] data)
        {
            if (data == null || data.Length < 6)
                return null;
            int minimum = ReadS16(data, 0);
            int maximum = ReadS16(data, 2);
            int increment = ReadU16(data, 4);
            if (maximum < minimum)
                return null;
            if (increment <= 0)
                increment = 1;
            minimum = Math.Max(0, minimum);
            maximum = Math.Min(255, maximum);
            if (maximum < minimum)
                return null;
            if (increment > maximum - minimum && maximum != minimum)
                increment = Math.Max(1, maximum - minimum);
            return new ResistanceScale(minimum, maximum, increment, true, "resistance");
        }

        public static ResistanceScale ParsePowerRange(byte[] data)
        {
            if (data == null || data.Length < 6)
                return null;
            int minimum = ReadS16(data, 0);
            int maximum = ReadS16(data, 2);
            int increment = ReadU16(data, 4);
            if (maximum < minimum)
                return null;
            if (increment <= 0)
                increment = 10;
            return new ResistanceScale(Math.Max(0, minimum), maximum, increment, false, "power");
        }

        public static int SnapGrid(int value, ResistanceScale scale)
        {
            if (scale == null)
                return value;
            if (value <= scale.Minimum)
                return scale.Minimum;
            if (value >= scale.Maximum)
                return scale.Maximum;
            if (scale.Increment <= 0)
                return value;
            int offset = value - scale.Minimum;
            int steps = (offset + scale.Increment / 2) / scale.Increment;
            int snapped = scale.Minimum + steps * scale.Increment;
            if (snapped > scale.Maximum)
                return scale.Maximum;
            return snapped;
        }

        public static ResistanceStep Move(int? current, int? reported, int direction, ResistanceScale scale)
        {
            int baseValue;
            if (!current.HasValue)
                baseValue = reported.HasValue ? SnapReported(reported.Value, scale) : scale.Minimum;
            else
                baseValue = SnapGrid(current.Value, scale);
            int updated = baseValue + direction * scale.Increment;
            if (updated < scale.Minimum)
                updated = scale.Minimum;
            else if (updated > scale.Maximum)
                updated = scale.Maximum;
            return new ResistanceStep { Value = updated, Changed = updated != baseValue };
        }

        public static TargetCommand TargetPayload(ResistanceScale scale, int value)
        {
            if (scale.Kind == "power")
            {
                return new TargetCommand
                {
                    Opcode = FtmsUuids.OpSetPower,
                    Payload = S16(value)
                };
            }
            if (scale.Kind == "grade")
            {
                byte[] payload = new byte[6];
                byte[] grade = S16(value);
                payload[2] = grade[0];
                payload[3] = grade[1];
                // Rolling resistance 0.004 and wind resistance 0.51 kg/m.
                payload[4] = 40;
                payload[5] = 51;
                return new TargetCommand
                {
                    Opcode = FtmsUuids.OpSetSimulation,
                    Payload = payload
                };
            }
            return new TargetCommand
            {
                Opcode = FtmsUuids.OpSetResistance,
                Payload = new[] { (byte)(value & 0xFF) }
            };
        }

        public static bool TryControlResponse(byte[] payload, out int opcode, out int result)
        {
            opcode = 0;
            result = 0;
            if (payload == null || payload.Length < 3 || payload[0] != FtmsUuids.OpResponse)
                return false;
            opcode = payload[1];
            result = payload[2];
            return true;
        }

        public static string ResultName(int result)
        {
            switch (result)
            {
                case 0x01: return "success";
                case 0x02: return "op code not supported";
                case 0x03: return "invalid parameter";
                case 0x04: return "operation failed";
                case 0x05: return "control not permitted";
                default: return "result 0x" + result.ToString("X2");
            }
        }

        public static string StatusName(int code)
        {
            switch (code)
            {
                case 0x01: return "reset";
                case 0x02: return "stopped or paused by user";
                case 0x03: return "stopped by safety key";
                case 0x04: return "started or resumed by user";
                case 0x05: return "target speed changed";
                case 0x06: return "target incline changed";
                case 0x07: return "target resistance changed";
                case 0x08: return "target power changed";
                case 0x09: return "target heart rate changed";
                case 0x12: return "simulation parameters changed";
                case 0x14: return "spin down status";
                case 0x15: return "target cadence changed";
                case 0xFF: return "control permission lost";
                default: return "status 0x" + code.ToString("X2");
            }
        }

        public static string ModeLabel(string kind)
        {
            if (kind == "grade") return "grade";
            if (kind == "power") return "target power";
            return "resistance";
        }

        public static string FormatLevel(int value, bool tenths)
        {
            if (!tenths)
                return value.ToString(CultureInfo.InvariantCulture);
            if (value % 10 == 0)
                return (value / 10).ToString(CultureInfo.InvariantCulture);
            return (value / 10.0).ToString("0.0", CultureInfo.InvariantCulture);
        }

        public static string FormatTarget(int value, ResistanceScale scale)
        {
            if (scale == null)
                return value.ToString(CultureInfo.InvariantCulture);
            if (scale.Kind == "power")
                return value.ToString(CultureInfo.InvariantCulture) + " W";
            if (scale.Kind == "grade")
            {
                string text = (value / 100.0).ToString("0.0", CultureInfo.InvariantCulture);
                if (text.EndsWith(".0", StringComparison.Ordinal))
                    text = text.Substring(0, text.Length - 2);
                return text + " %";
            }
            return FormatLevel(value, scale.Tenths);
        }

        public static string FormatDuration(int seconds)
        {
            if (seconds < 0)
                seconds = 0;
            int minutes = seconds / 60;
            int secs = seconds % 60;
            int hours = minutes / 60;
            minutes %= 60;
            if (hours > 0)
                return hours + ":" + minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + secs.ToString("00", CultureInfo.InvariantCulture);
            return minutes + ":" + secs.ToString("00", CultureInfo.InvariantCulture);
        }

        public static string FormatDistance(int meters)
        {
            if (meters < 1000)
                return meters.ToString(CultureInfo.InvariantCulture) + " m";
            return (meters / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " km";
        }

        static int SnapReported(int reported, ResistanceScale scale)
        {
            if (scale.Tenths)
            {
                int scaled = reported * 10;
                if (scale.Minimum <= scaled && scaled <= scale.Maximum)
                    return SnapGrid(scaled, scale);
            }
            if (scale.Minimum <= reported && reported <= scale.Maximum)
                return SnapGrid(reported, scale);
            return scale.Minimum;
        }

        static int PayloadSize(int flags, int resistanceOctets)
        {
            int size = 2;
            if ((flags & 0x0001) == 0) size += 2;
            if ((flags & 0x0002) != 0) size += 2;
            if ((flags & 0x0004) != 0) size += 2;
            if ((flags & 0x0008) != 0) size += 2;
            if ((flags & 0x0010) != 0) size += 3;
            if ((flags & 0x0020) != 0) size += resistanceOctets;
            if ((flags & 0x0040) != 0) size += 2;
            if ((flags & 0x0080) != 0) size += 2;
            if ((flags & 0x0100) != 0) size += 5;
            if ((flags & 0x0200) != 0) size += 1;
            if ((flags & 0x0400) != 0) size += 1;
            if ((flags & 0x0800) != 0) size += 2;
            if ((flags & 0x1000) != 0) size += 2;
            return size;
        }

        static int ResistanceOctets(int flags, int packetLength)
        {
            if ((flags & 0x0020) == 0)
                return 0;
            if (packetLength == PayloadSize(flags, 1))
                return 1;
            return 2;
        }

        static int ReadU16(byte[] data, int offset)
        {
            return data[offset] | (data[offset + 1] << 8);
        }

        static int ReadS16(byte[] data, int offset)
        {
            int value = ReadU16(data, offset);
            return value >= 0x8000 ? value - 0x10000 : value;
        }

        static int ReadU24(byte[] data, int offset)
        {
            return data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
        }

        static int ReadU32(byte[] data, int offset)
        {
            return data[offset]
                | (data[offset + 1] << 8)
                | (data[offset + 2] << 16)
                | (data[offset + 3] << 24);
        }

        static byte[] S16(int value)
        {
            short signed = (short)value;
            return new[] { (byte)(signed & 0xFF), (byte)((signed >> 8) & 0xFF) };
        }
    }
}
