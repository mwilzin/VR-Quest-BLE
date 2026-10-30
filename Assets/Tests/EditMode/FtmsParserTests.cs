using System.Collections.Generic;
using NUnit.Framework;
using Teamprojekt.Ftms;

namespace Teamprojekt.Ftms.Tests
{
    public class FtmsParserTests
    {
        [Test]
        public void IndoorBikeData_SpeedCadencePower()
        {
            byte[] packet = Packet(0x0044, U16(2550), U16(180), S16(250));
            BikeReading parsed = FtmsParsers.ParseIndoorBike(packet);
            Assert.AreEqual(25.5f, parsed.SpeedKmh.Value, 0.001f);
            Assert.AreEqual(90f, parsed.CadenceRpm.Value, 0.001f);
            Assert.AreEqual(250, parsed.PowerW.Value);
            Assert.IsFalse(parsed.Resistance.HasValue);
        }

        [Test]
        public void IndoorBikeData_MoreDataOmitsSpeedAndKeepsNegativePower()
        {
            byte[] packet = Packet(0x0041, S16(-5));
            BikeReading parsed = FtmsParsers.ParseIndoorBike(packet);
            Assert.IsFalse(parsed.SpeedKmh.HasValue);
            Assert.AreEqual(-5, parsed.PowerW.Value);
        }

        [Test]
        public void IndoorBikeData_SignedResistanceStaysAligned()
        {
            byte[] packet = Packet(0x0060, U16(1000), S16(20), S16(100));
            BikeReading parsed = FtmsParsers.ParseIndoorBike(packet);
            Assert.AreEqual(10f, parsed.SpeedKmh.Value, 0.001f);
            Assert.AreEqual(20, parsed.Resistance.Value);
            Assert.AreEqual(100, parsed.PowerW.Value);
        }

        [Test]
        public void IndoorBikeData_UnsignedResistanceDoesNotShiftPower()
        {
            byte[] packet = Packet(0x0060, U16(1000), new byte[] { 20 }, S16(100));
            BikeReading parsed = FtmsParsers.ParseIndoorBike(packet);
            Assert.AreEqual(20, parsed.Resistance.Value);
            Assert.AreEqual(100, parsed.PowerW.Value);
        }

        [Test]
        public void IndoorBikeData_EnergySentinelThenHeartRate()
        {
            byte[] packet = Packet(0x0300, U16(0), U16(0xFFFF), U16(0xFFFF), new byte[] { 0xFF, 130 });
            BikeReading parsed = FtmsParsers.ParseIndoorBike(packet);
            Assert.IsFalse(parsed.EnergyKcal.HasValue);
            Assert.AreEqual(130, parsed.TrainerHr.Value);
        }

        [Test]
        public void IndoorBikeData_TruncatedAndEmpty()
        {
            Assert.IsFalse(FtmsParsers.ParseIndoorBike(new byte[] { 0x40 }).HasAny());
            Assert.IsFalse(FtmsParsers.ParseIndoorBike(new byte[0]).HasAny());
        }

        [Test]
        public void IndoorBikeData_Averages()
        {
            byte[] packet = Packet(0x000A, U16(2000), U16(1900), U16(170));
            BikeReading parsed = FtmsParsers.ParseIndoorBike(packet);
            Assert.AreEqual(20f, parsed.SpeedKmh.Value, 0.001f);
            Assert.AreEqual(19f, parsed.AvgSpeedKmh.Value, 0.001f);
            Assert.IsFalse(parsed.CadenceRpm.HasValue);
            Assert.AreEqual(85f, parsed.AvgCadenceRpm.Value, 0.001f);
        }

        [Test]
        public void HeartRate_ContactAndRr()
        {
            HeartReading hr = FtmsParsers.ParseHeartRate(new byte[] { 0x16, 72, 0x00, 0x02 });
            Assert.AreEqual(72, hr.Bpm.Value);
            Assert.AreEqual("contact", hr.Contact);
            Assert.AreEqual(500f, hr.RrMs.Value, 0.01f);
        }

        [Test]
        public void HeartRate_Uint16WithoutContact()
        {
            HeartReading hr = FtmsParsers.ParseHeartRate(new byte[] { 0x01, 0xB4, 0x00 });
            Assert.AreEqual(180, hr.Bpm.Value);
            Assert.IsNull(hr.Contact);
        }

        [Test]
        public void HeartRate_SkipsEnergyBeforeRr()
        {
            HeartReading hr = FtmsParsers.ParseHeartRate(new byte[] { 0x18, 80, 10, 0, 0x00, 0x04 });
            Assert.AreEqual(80, hr.Bpm.Value);
            Assert.AreEqual(1000f, hr.RrMs.Value, 0.01f);
        }

        [Test]
        public void HeartRate_NoContact()
        {
            HeartReading hr = FtmsParsers.ParseHeartRate(new byte[] { 0x04, 60 });
            Assert.AreEqual("no contact", hr.Contact);
        }

        [Test]
        public void MachineTypeAndAvailability()
        {
            byte[] payload = { 0x01, 0x20, 0x00 };
            CollectionAssert.AreEqual(new[] { "indoor bike" }, FtmsParsers.MachineTypes(payload));
            Assert.IsTrue(FtmsParsers.MachineAvailable(payload).Value);
            Assert.IsFalse(FtmsParsers.MachineAvailable(new byte[] { 0x00, 0x20, 0x00 }).Value);
            Assert.AreEqual(0, FtmsParsers.MachineTypes(new byte[0]).Count);
            Assert.IsNull(FtmsParsers.MachineAvailable(new byte[0]));
        }

        [Test]
        public void FeaturesAndTargetModes()
        {
            int bits = (1 << 1) | (1 << 14);
            Assert.AreEqual("cadence, power", FtmsParsers.DescribeFeatures(U32(bits)));

            byte[] powerOnly = Join(U32(0), U32(1 << 3));
            CollectionAssert.AreEqual(new[] { "power" }, FtmsParsers.SupportedModes(powerOnly));
            byte[] both = Join(U32(0), U32((1 << 2) | (1 << 3)));
            CollectionAssert.AreEqual(new[] { "resistance", "power" }, FtmsParsers.SupportedModes(both));
            CollectionAssert.AreEqual(
                new[] { "resistance", "grade", "power" },
                FtmsParsers.SupportedModes(new byte[0]));
        }

        [Test]
        public void DurationAndDistance()
        {
            Assert.AreEqual("12:34", FtmsParsers.FormatDuration(754));
            Assert.AreEqual("4.82 km", FtmsParsers.FormatDistance(4820));
        }

        [Test]
        public void ResistanceRangeAndSteps()
        {
            var span = new ResistanceScale(10, 200, 10, true, "resistance");
            byte[] encoded = Join(S16(10), S16(200), U16(10));
            Assert.IsTrue(FtmsParsers.ParseResistanceRange(encoded).Same(span));
            Assert.AreEqual("1", FtmsParsers.FormatLevel(10, true));
            Assert.AreEqual("1.5", FtmsParsers.FormatLevel(15, true));

            ResistanceStep up = FtmsParsers.Move(null, 8, 1, span);
            Assert.AreEqual(90, up.Value);
            Assert.IsTrue(up.Changed);
            ResistanceStep max = FtmsParsers.Move(200, null, 1, span);
            Assert.AreEqual(200, max.Value);
            Assert.IsFalse(max.Changed);
            ResistanceStep min = FtmsParsers.Move(10, null, -1, span);
            Assert.AreEqual(10, min.Value);
            Assert.IsFalse(min.Changed);
        }

        [Test]
        public void TargetPayloads()
        {
            TargetCommand power = FtmsParsers.TargetPayload(FtmsParsers.DefaultPower(), 150);
            Assert.AreEqual(FtmsUuids.OpSetPower, power.Opcode);
            CollectionAssert.AreEqual(S16(150), power.Payload);

            TargetCommand grade = FtmsParsers.TargetPayload(FtmsParsers.DefaultGrade(), 50);
            Assert.AreEqual(FtmsUuids.OpSetSimulation, grade.Opcode);
            CollectionAssert.AreEqual(S16(50), new[] { grade.Payload[2], grade.Payload[3] });
            Assert.AreEqual(40, grade.Payload[4]);
            Assert.AreEqual(51, grade.Payload[5]);
        }

        [Test]
        public void PlanResistanceThenRevert()
        {
            var span = new ResistanceScale(10, 200, 10, true, "resistance");
            var controller = new FtmsTargetController();
            controller.SetCommandReady(true);
            controller.SetControlGranted(true);
            controller.SetScale(span);
            PlanResult plan = controller.Plan(1, 8, null);
            Assert.IsTrue(plan.HasUpdate);
            Assert.AreEqual(90, plan.Updated);
            Assert.IsFalse(plan.HasPrevious);
            Assert.AreEqual("", plan.Note);
            controller.Revert(plan.HasPrevious, plan.Previous);
            Assert.IsFalse(controller.Target.HasValue);
        }

        [Test]
        public void ChooseStrongestIndoorBikeAndSeparateStrap()
        {
            var found = new List<FtmsDevice>
            {
                new FtmsDevice
                {
                    Name = "KICKR",
                    Address = "bike",
                    Rssi = -40,
                    IsFtms = true,
                    IsHr = true,
                    MachineTypes = new List<string> { "indoor bike" }
                },
                new FtmsDevice { Name = "Other", Address = "other", Rssi = -30, IsFtms = true },
                new FtmsDevice { Name = "Polar", Address = "strap", Rssi = -55, IsHr = true }
            };
            FtmsDevice bike = FtmsSelection.ChooseBike(found, null);
            Assert.AreEqual("bike", bike.Address);
            FtmsDevice hr = FtmsSelection.ChooseHeart(found, null, bike.Address);
            Assert.AreEqual("strap", hr.Address);
        }

        static byte[] Packet(int flags, params byte[][] parts)
        {
            int length = 2;
            for (int i = 0; i < parts.Length; i++)
                length += parts[i].Length;
            var data = new byte[length];
            data[0] = (byte)(flags & 0xFF);
            data[1] = (byte)((flags >> 8) & 0xFF);
            int index = 2;
            for (int i = 0; i < parts.Length; i++)
            {
                System.Buffer.BlockCopy(parts[i], 0, data, index, parts[i].Length);
                index += parts[i].Length;
            }
            return data;
        }

        static byte[] U16(int value)
        {
            return new[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };
        }

        static byte[] S16(int value)
        {
            short signed = (short)value;
            return new[] { (byte)(signed & 0xFF), (byte)((signed >> 8) & 0xFF) };
        }

        static byte[] U32(int value)
        {
            return new[]
            {
                (byte)(value & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 24) & 0xFF)
            };
        }

        static byte[] Join(params byte[][] parts)
        {
            int length = 0;
            for (int i = 0; i < parts.Length; i++)
                length += parts[i].Length;
            var data = new byte[length];
            int index = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                System.Buffer.BlockCopy(parts[i], 0, data, index, parts[i].Length);
                index += parts[i].Length;
            }
            return data;
        }
    }
}
