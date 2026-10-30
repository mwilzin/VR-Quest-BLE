using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;
using UnityEngine.UI;

namespace Teamprojekt.Ftms
{
    /// <summary>
    /// Head-locked readout. Quest A raises the target and B lowers it. Keyboard arrows do the same in the Editor.
    /// </summary>
    public sealed class FtmsDebugPanel : MonoBehaviour
    {
        FtmsMonitor _monitor;
        Text _text;
        Transform _panel;
        float _nextRefresh;

        void Start()
        {
            _monitor = GetComponent<FtmsMonitor>();
            BuildCanvas();
            if (_monitor != null)
            {
                _monitor.Updated += OnUpdated;
                Refresh();
                if (_monitor.autoStart)
                    _monitor.StartSession();
            }
        }

        void OnDestroy()
        {
            if (_monitor != null)
                _monitor.Updated -= OnUpdated;
        }

        void Update()
        {
            if (_monitor == null)
                return;
            AttachToCamera();
            if (Pressed(XRController.rightHand, "primaryButton") || KeyPressed(Key.RightArrow))
                _monitor.NudgeTarget(1);
            if (Pressed(XRController.rightHand, "secondaryButton") || KeyPressed(Key.LeftArrow))
                _monitor.NudgeTarget(-1);
            if (Time.unscaledTime >= _nextRefresh)
                Refresh();
        }

        void OnUpdated(FtmsSnapshot _)
        {
            Refresh();
        }

        void BuildCanvas()
        {
            var root = new GameObject("FtmsPanel");
            _panel = root.transform;
            AttachToCamera();

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rect = root.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(1100f, 980f);
            var background = root.AddComponent<Image>();
            background.color = new Color(0f, 0f, 0f, 0.78f);
            background.raycastTarget = false;

            var textObject = new GameObject("Readout");
            textObject.transform.SetParent(root.transform, false);
            var textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(36f, 28f);
            textRect.offsetMax = new Vector2(-36f, -28f);
            _text = textObject.AddComponent<Text>();
            _text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_text.font == null)
                _text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _text.fontSize = 32;
            _text.color = Color.white;
            _text.alignment = TextAnchor.UpperLeft;
            _text.horizontalOverflow = HorizontalWrapMode.Wrap;
            _text.verticalOverflow = VerticalWrapMode.Overflow;
            _text.raycastTarget = false;
            _text.text = "FTMS";
        }

        void AttachToCamera()
        {
            if (_panel == null || _panel.parent != null)
                return;
            Camera camera = Camera.main;
            if (camera == null)
                return;
            _panel.SetParent(camera.transform, false);
            _panel.localPosition = new Vector3(0f, -0.05f, 1.3f);
            _panel.localRotation = Quaternion.identity;
            _panel.localScale = Vector3.one * 0.0009f;
        }

        void Refresh()
        {
            _nextRefresh = Time.unscaledTime + 0.25f;
            if (_text == null || _monitor == null || _monitor.Current == null)
                return;
            _text.text = Render(_monitor.Current, Time.realtimeSinceStartup);
        }

        static string Render(FtmsSnapshot snap, float now)
        {
            BikeReading bike = snap.Bike;
            HeartReading hr = snap.Heart;
            var lines = new StringBuilder();
            lines.AppendLine("FTMS indoor bike");
            lines.AppendLine();
            lines.AppendLine("Bike   " + snap.BikeName + "   " + snap.BikeLink);
            if (!string.IsNullOrEmpty(snap.BikeDetail))
                lines.AppendLine("       " + snap.BikeDetail);
            lines.AppendLine("       " + snap.BikeAddress);
            lines.AppendLine("Heart  " + snap.HrName + "   " + snap.HrLink);
            if (!string.IsNullOrEmpty(snap.HrDetail))
                lines.AppendLine("       " + snap.HrDetail);
            lines.AppendLine("       " + snap.HrAddress);
            lines.AppendLine();
            lines.AppendLine(Row("Speed", Num(bike.SpeedKmh, "0.00", "km/h"),
                bike.AvgSpeedKmh.HasValue ? "avg " + bike.AvgSpeedKmh.Value.ToString("0.00", CultureInfo.InvariantCulture) : ""));
            lines.AppendLine(Row("Cadence", Num(bike.CadenceRpm, "0.0", "rpm"),
                bike.AvgCadenceRpm.HasValue ? "avg " + bike.AvgCadenceRpm.Value.ToString("0.0", CultureInfo.InvariantCulture) : ""));
            lines.AppendLine(Row("Power", bike.PowerW.HasValue ? bike.PowerW.Value + " W" : "—",
                bike.AvgPowerW.HasValue ? "avg " + bike.AvgPowerW.Value + " W" : ""));
            if (bike.DistanceM.HasValue)
                lines.AppendLine(Row("Distance", FtmsParsers.FormatDistance(bike.DistanceM.Value), ""));
            lines.AppendLine(Row("Resistance", bike.Resistance.HasValue ? bike.Resistance.Value.ToString(CultureInfo.InvariantCulture) : "—", ""));
            lines.AppendLine(Row("Target", TargetText(snap), TargetExtra(snap)));
            if (bike.EnergyKcal.HasValue)
                lines.AppendLine(Row("Energy", bike.EnergyKcal.Value + " kcal", ""));
            if (bike.ElapsedS.HasValue)
                lines.AppendLine(Row("Elapsed", FtmsParsers.FormatDuration(bike.ElapsedS.Value), ""));
            if (bike.RemainingS.HasValue)
                lines.AppendLine(Row("Remaining", FtmsParsers.FormatDuration(bike.RemainingS.Value), ""));
            if (bike.Met.HasValue)
                lines.AppendLine(Row("MET", bike.Met.Value.ToString("0.0", CultureInfo.InvariantCulture), ""));
            lines.AppendLine(Row("Bike updated", Age(bike.HasUpdate, bike.UpdatedAt, now), ""));
            lines.AppendLine();
            lines.AppendLine(Row("Heart rate", hr.Bpm.HasValue ? hr.Bpm.Value + " bpm" : "—", ""));
            if (!string.IsNullOrEmpty(hr.Contact))
                lines.AppendLine(Row("Contact", hr.Contact, ""));
            if (hr.RrMs.HasValue)
                lines.AppendLine(Row("RR interval", hr.RrMs.Value.ToString("0", CultureInfo.InvariantCulture) + " ms", ""));
            if (bike.TrainerHr.HasValue)
                lines.AppendLine(Row("Trainer HR", bike.TrainerHr.Value + " bpm", ""));
            lines.AppendLine(Row("HR updated", Age(hr.HasUpdate, hr.UpdatedAt, now), ""));
            if (snap.Logs != null && snap.Logs.Count > 0)
            {
                lines.AppendLine();
                for (int i = 0; i < snap.Logs.Count; i++)
                    lines.AppendLine(snap.Logs[i]);
            }
            lines.AppendLine();
            string kind = snap.HasScale ? FtmsParsers.ModeLabel(snap.TargetKind) : "resistance";
            lines.Append("A / Right  raise " + kind + "     B / Left  lower");
            return lines.ToString();
        }

        static string TargetText(FtmsSnapshot snap)
        {
            if (snap.TargetRejected)
                return "—";
            if (!snap.HasTarget || !snap.HasScale)
                return "—";
            return FtmsParsers.FormatTarget(snap.Target, ScaleOf(snap));
        }

        static string TargetExtra(FtmsSnapshot snap)
        {
            if (snap.TargetRejected)
                return "not supported";
            if (!snap.HasTarget || !snap.HasScale)
                return "A / B";
            ResistanceScale scale = ScaleOf(snap);
            return FtmsParsers.FormatTarget(scale.Minimum, scale) + " to " + FtmsParsers.FormatTarget(scale.Maximum, scale);
        }

        static ResistanceScale ScaleOf(FtmsSnapshot snap)
        {
            return new ResistanceScale(
                snap.ScaleMinimum, snap.ScaleMaximum, snap.ScaleIncrement, snap.ScaleTenths, snap.TargetKind);
        }

        static string Row(string label, string value, string extra)
        {
            if (string.IsNullOrEmpty(extra))
                return label + "  " + value;
            return label + "  " + value + "   " + extra;
        }

        static string Num(float? value, string format, string unit)
        {
            if (!value.HasValue)
                return "—";
            return value.Value.ToString(format, CultureInfo.InvariantCulture) + " " + unit;
        }

        static string Age(bool hasUpdate, float updatedAt, float now)
        {
            if (!hasUpdate)
                return "waiting";
            float delta = now - updatedAt;
            if (delta < 1.5f)
                return "live";
            if (delta >= 3f)
                return "stale " + delta.ToString("0", CultureInfo.InvariantCulture) + "s";
            return delta.ToString("0", CultureInfo.InvariantCulture) + "s ago";
        }

        static bool Pressed(XRController controller, string controlName)
        {
            if (controller == null)
                return false;
            ButtonControl control = controller.TryGetChildControl<ButtonControl>(controlName);
            return control != null && control.wasPressedThisFrame;
        }

        static bool KeyPressed(Key key)
        {
            Keyboard keyboard = Keyboard.current;
            return keyboard != null && keyboard[key].wasPressedThisFrame;
        }
    }
}
