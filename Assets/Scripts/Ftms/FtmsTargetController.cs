using System.Collections.Generic;

namespace Teamprojekt.Ftms
{
    /// <summary>
    /// Arrow-key target state from the Python monitor: snap, clamp, and fall through modes.
    /// </summary>
    public sealed class FtmsTargetController
    {
        public bool CommandReady { get; private set; }
        public bool ControlGranted { get; private set; }
        public bool Rejected { get; private set; }
        public string Note = "Waiting for the bike";
        public int? Target { get; private set; }
        public ResistanceScale Scale { get; private set; }

        readonly List<string> _modes = new List<string>();
        readonly Dictionary<string, ResistanceScale> _scales = new Dictionary<string, ResistanceScale>();
        int _modeIndex;

        public void SetCommandReady(bool ready)
        {
            CommandReady = ready;
            if (!ready)
            {
                ControlGranted = false;
                Rejected = false;
            }
        }

        public void SetControlGranted(bool granted)
        {
            ControlGranted = granted;
        }

        public void SetScale(ResistanceScale scale)
        {
            Scale = scale;
            if (scale != null && Target.HasValue)
                Target = FtmsParsers.SnapGrid(Target.Value, scale);
        }

        public void Configure(IList<string> modes, IDictionary<string, ResistanceScale> scales)
        {
            _modes.Clear();
            _scales.Clear();
            if (modes != null)
            {
                for (int i = 0; i < modes.Count; i++)
                    _modes.Add(modes[i]);
            }
            if (scales != null)
            {
                foreach (var pair in scales)
                    _scales[pair.Key] = pair.Value;
            }
            _modeIndex = 0;
            Rejected = false;
            Target = null;
            Scale = _modes.Count > 0 && _scales.ContainsKey(_modes[0]) ? _scales[_modes[0]] : null;
        }

        public string AdvanceMode()
        {
            if (_modeIndex + 1 >= _modes.Count)
            {
                Rejected = true;
                return null;
            }
            _modeIndex++;
            string kind = _modes[_modeIndex];
            Scale = _scales.ContainsKey(kind) ? _scales[kind] : null;
            Target = null;
            return kind;
        }

        public PlanResult Plan(int direction, int? reportedResistance, int? reportedPower)
        {
            if (Rejected)
                return NoteOnly("");
            if (!CommandReady)
                return NoteOnly(Note ?? "");
            if (!ControlGranted)
                return NoteOnly("FTMS control is not granted");

            ResistanceScale scale = Scale ?? FtmsParsers.DefaultResistance();
            int? reported;
            if (scale.Kind == "power")
                reported = reportedPower;
            else if (scale.Kind == "grade")
                reported = Target.HasValue ? (int?)null : 0;
            else
                reported = reportedResistance;

            int? previous = Target;
            ResistanceStep step = FtmsParsers.Move(previous, reported, direction, scale);
            Target = step.Value;
            Scale = scale;
            if (!step.Changed)
            {
                string end = direction > 0 ? "maximum" : "minimum";
                return new PlanResult
                {
                    Scale = scale,
                    HasPrevious = previous.HasValue,
                    Previous = previous ?? 0,
                    Note = "Already at the " + end
                };
            }
            return new PlanResult
            {
                HasUpdate = true,
                Updated = step.Value,
                HasPrevious = previous.HasValue,
                Previous = previous ?? 0,
                Scale = scale,
                Note = ""
            };
        }

        public PlanResult PlanAbsolute(int wireValue)
        {
            if (Rejected)
                return NoteOnly("");
            if (!CommandReady)
                return NoteOnly(Note ?? "");
            if (!ControlGranted)
                return NoteOnly("FTMS control is not granted");

            ResistanceScale scale = Scale ?? FtmsParsers.DefaultResistance();
            int clamped = wireValue;
            if (clamped < scale.Minimum) clamped = scale.Minimum;
            if (clamped > scale.Maximum) clamped = scale.Maximum;
            int snapped = FtmsParsers.SnapGrid(clamped, scale);
            int? previous = Target;
            if (previous.HasValue && previous.Value == snapped)
            {
                return new PlanResult
                {
                    Scale = scale,
                    HasPrevious = true,
                    Previous = previous.Value,
                    Note = "Already at that target"
                };
            }
            Target = snapped;
            Scale = scale;
            return new PlanResult
            {
                HasUpdate = true,
                Updated = snapped,
                HasPrevious = previous.HasValue,
                Previous = previous ?? 0,
                Scale = scale,
                Note = ""
            };
        }

        public void Revert(bool hasPrevious, int previous)
        {
            Target = hasPrevious ? (int?)previous : null;
        }

        static PlanResult NoteOnly(string note)
        {
            return new PlanResult { Note = note ?? "" };
        }
    }
}
