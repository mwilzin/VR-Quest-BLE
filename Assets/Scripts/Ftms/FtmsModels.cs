using System;
using System.Collections.Generic;

namespace Teamprojekt.Ftms
{
    public sealed class ResistanceScale
    {
        public readonly int Minimum;
        public readonly int Maximum;
        public readonly int Increment;
        public readonly bool Tenths;
        public readonly string Kind;

        public ResistanceScale(int minimum, int maximum, int increment, bool tenths, string kind)
        {
            Minimum = minimum;
            Maximum = maximum;
            Increment = increment;
            Tenths = tenths;
            Kind = string.IsNullOrEmpty(kind) ? "resistance" : kind;
        }

        public bool Same(ResistanceScale other)
        {
            return other != null
                && Minimum == other.Minimum
                && Maximum == other.Maximum
                && Increment == other.Increment
                && Tenths == other.Tenths
                && Kind == other.Kind;
        }
    }

    public sealed class BikeReading
    {
        public float? SpeedKmh;
        public float? AvgSpeedKmh;
        public float? CadenceRpm;
        public float? AvgCadenceRpm;
        public int? DistanceM;
        public int? Resistance;
        public int? PowerW;
        public int? AvgPowerW;
        public int? EnergyKcal;
        public int? TrainerHr;
        public float? Met;
        public int? ElapsedS;
        public int? RemainingS;
        public bool HasUpdate;
        public float UpdatedAt;

        public bool HasAny()
        {
            return SpeedKmh.HasValue || AvgSpeedKmh.HasValue || CadenceRpm.HasValue || AvgCadenceRpm.HasValue
                || DistanceM.HasValue || Resistance.HasValue || PowerW.HasValue || AvgPowerW.HasValue
                || EnergyKcal.HasValue || TrainerHr.HasValue || Met.HasValue || ElapsedS.HasValue
                || RemainingS.HasValue;
        }

        public void Apply(BikeReading incoming, float now)
        {
            if (incoming.SpeedKmh.HasValue) SpeedKmh = incoming.SpeedKmh;
            if (incoming.AvgSpeedKmh.HasValue) AvgSpeedKmh = incoming.AvgSpeedKmh;
            if (incoming.CadenceRpm.HasValue) CadenceRpm = incoming.CadenceRpm;
            if (incoming.AvgCadenceRpm.HasValue) AvgCadenceRpm = incoming.AvgCadenceRpm;
            if (incoming.DistanceM.HasValue) DistanceM = incoming.DistanceM;
            if (incoming.Resistance.HasValue) Resistance = incoming.Resistance;
            if (incoming.PowerW.HasValue) PowerW = incoming.PowerW;
            if (incoming.AvgPowerW.HasValue) AvgPowerW = incoming.AvgPowerW;
            if (incoming.EnergyKcal.HasValue) EnergyKcal = incoming.EnergyKcal;
            if (incoming.TrainerHr.HasValue) TrainerHr = incoming.TrainerHr;
            if (incoming.Met.HasValue) Met = incoming.Met;
            if (incoming.ElapsedS.HasValue) ElapsedS = incoming.ElapsedS;
            if (incoming.RemainingS.HasValue) RemainingS = incoming.RemainingS;
            HasUpdate = true;
            UpdatedAt = now;
        }

        public BikeReading Clone()
        {
            return (BikeReading)MemberwiseClone();
        }
    }

    public sealed class HeartReading
    {
        public int? Bpm;
        public string Contact;
        public float? RrMs;
        public bool HasUpdate;
        public float UpdatedAt;

        public void Apply(HeartReading incoming, float now)
        {
            if (!incoming.Bpm.HasValue)
                return;
            Bpm = incoming.Bpm;
            Contact = incoming.Contact;
            if (incoming.RrMs.HasValue)
                RrMs = incoming.RrMs;
            HasUpdate = true;
            UpdatedAt = now;
        }

        public HeartReading Clone()
        {
            return (HeartReading)MemberwiseClone();
        }
    }

    public sealed class FtmsDevice
    {
        public string Name;
        public string Address;
        public int Rssi;
        public bool IsFtms;
        public bool IsHr;
        public List<string> MachineTypes = new List<string>();
        public bool? Available;
    }

    public sealed class FtmsGattChar
    {
        public string Service;
        public string Uuid;
        public int Properties;
    }

    public sealed class FtmsSnapshot
    {
        public string BikeName = "—";
        public string BikeAddress = "—";
        public string HrName = "—";
        public string HrAddress = "—";
        public string BikeLink = "idle";
        public string HrLink = "idle";
        public string BikeDetail = "";
        public string HrDetail = "";
        public BikeReading Bike = new BikeReading();
        public HeartReading Heart = new HeartReading();
        public bool HasTarget;
        public int Target;
        public bool HasScale;
        public int ScaleMinimum;
        public int ScaleMaximum;
        public int ScaleIncrement;
        public bool ScaleTenths;
        public string TargetKind = "resistance";
        public bool ControlGranted;
        public bool CommandReady;
        public bool TargetRejected;
        public string TargetNote = "";
        public List<string> Logs = new List<string>();
    }

    public struct ResistanceStep
    {
        public int Value;
        public bool Changed;
    }

    public struct TargetCommand
    {
        public int Opcode;
        public byte[] Payload;
    }

    public struct PlanResult
    {
        public bool HasUpdate;
        public int Updated;
        public bool HasPrevious;
        public int Previous;
        public ResistanceScale Scale;
        public string Note;
    }
}
