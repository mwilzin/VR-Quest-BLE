using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace Teamprojekt.Ftms
{
    /// <summary>
    /// Unity endpoint for an FTMS indoor bike and a heart-rate strap.
    /// NudgeTarget(-1/+1) is the left/right arrow. SetTarget writes one absolute value.
    /// </summary>
    public sealed class FtmsMonitor : MonoBehaviour
    {
        public string bikeNameContains;
        public string hrNameContains;
        public bool listenOnly;
        public bool autoStart = true;

        public FtmsSnapshot Current { get; private set; }
        public event Action<FtmsSnapshot> Updated;

        FtmsBleClient _ble;
        FtmsTargetController _targets;
        BikeReading _bike;
        HeartReading _heart;
        readonly Dictionary<string, FtmsDevice> _devices = new Dictionary<string, FtmsDevice>();
        readonly Queue<string> _logs = new Queue<string>();
        readonly Queue<TargetRequest> _requests = new Queue<TargetRequest>();
        List<FtmsGattChar> _bikeChars = new List<FtmsGattChar>();
        List<FtmsGattChar> _hrChars = new List<FtmsGattChar>();

        string _bikeName = "—";
        string _bikeAddress = "—";
        string _hrName = "—";
        string _hrAddress = "—";
        string _bikeLink = "idle";
        string _hrLink = "idle";
        string _bikeDetail = "";
        string _hrDetail = "";
        string _forcedBike;
        string _forcedHr;
        bool _stop;
        bool _pumping;
        bool _scanDone;
        bool _bikeAutoReconnect;
        bool _hrAutoReconnect;
        bool _bikeUnsupported;
        bool _hrUnsupported;
        int _bikeEpoch;
        int _hrEpoch;
        int _bikeSetupId;
        int _hrSetupId;
        int _waitOpcode = -1;
        int _waitResult;
        bool _waitDone;
#if UNITY_ANDROID && !UNITY_EDITOR
        PermissionCallbacks _permissionCallbacks;
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindAnyObjectByType<FtmsMonitor>() != null)
                return;
            var go = new GameObject("Ftms");
            DontDestroyOnLoad(go);
            go.AddComponent<FtmsMonitor>();
            go.AddComponent<FtmsDebugPanel>();
        }

        void Awake()
        {
            _ble = new FtmsBleClient();
            _ble.Connection += OnConnection;
            _ble.ServicesReady += OnServices;
            _ble.Notification += OnNotification;
            _ble.Error += OnError;
            _ble.ScanResult += OnScan;
            _ble.ScanComplete += OnScanComplete;
            _targets = new FtmsTargetController();
            _bike = new BikeReading();
            _heart = new HeartReading();
            Publish();
        }

        void OnDestroy()
        {
            StopSession();
            if (_ble != null)
            {
                _ble.ScanResult -= OnScan;
                _ble.ScanComplete -= OnScanComplete;
                _ble.Dispose();
                _ble = null;
            }
        }

        public void StartSession()
        {
            StopSession();
            _stop = false;
            _bikeUnsupported = false;
            _hrUnsupported = false;
            StartCoroutine(RunSession());
        }

        public void StopSession()
        {
            _stop = true;
            _bikeAutoReconnect = false;
            _hrAutoReconnect = false;
            _bikeEpoch++;
            _hrEpoch++;
            _bikeSetupId++;
            _hrSetupId++;
            _pumping = false;
            _requests.Clear();
            StopAllCoroutines();
            if (_ble != null && _ble.IsAvailable)
                _ble.Shutdown();
        }

        public void ConnectBike(string address)
        {
            if (string.IsNullOrEmpty(address))
                return;
            _forcedBike = address;
            _bikeUnsupported = false;
            _bikeEpoch++;
            _bikeAddress = address;
            _bikeName = LookupName(address) ?? address;
            _bikeAutoReconnect = _ble != null && _ble.IsAvailable;
            Publish();
            if (_bikeAutoReconnect)
                _ble.Connect("bike", address);
        }

        public void ConnectHeartRate(string address)
        {
            if (string.IsNullOrEmpty(address))
                return;
            _forcedHr = address;
            _hrUnsupported = false;
            _hrEpoch++;
            _hrAddress = address;
            _hrName = LookupName(address) ?? address;
            _hrAutoReconnect = _ble != null && _ble.IsAvailable;
            Publish();
            if (_hrAutoReconnect)
                _ble.Connect("hr", address);
        }

        public void NudgeTarget(int direction)
        {
            if (direction == 0)
                return;
            _requests.Enqueue(new NudgeRequest { Direction = direction });
            Pump();
        }

        public void SetTarget(int wireValue)
        {
            _requests.Enqueue(new AbsoluteRequest { Value = wireValue });
            Pump();
        }

        IEnumerator RunSession()
        {
            _devices.Clear();
            _scanDone = false;
            if (_ble == null || !_ble.IsAvailable)
            {
                SetBikeLink("unavailable", "BLE runs on the Quest");
                SetHrLink("unavailable", "BLE runs on the Quest");
                Log("BLE only runs on an Android device.");
                yield break;
            }

            SetBikeLink("scanning", "");
            SetHrLink("scanning", "");
            Log("Scanning until the indoor bike and heart rate sensor are found…");

            bool granted = false;
            yield return StartCoroutine(EnsurePermissions(ok => granted = ok));
            if (!granted || _stop)
            {
                Log("Bluetooth permission was denied.");
                SetBikeLink("permission", "Bluetooth permission denied");
                SetHrLink("permission", "Bluetooth permission denied");
                yield break;
            }

            bool bikeSettled = false;
            bool hrSettled = false;
            bool loggedNearby = false;
            bool loggedBikeHr = false;
            _scanDone = false;
            _ble.StartScan(0);
            float restartAt = Time.realtimeSinceStartup + 30f;

            while (!_stop && (!bikeSettled || !hrSettled))
            {
                List<FtmsDevice> found = RankedDevices();
                if (!loggedNearby && found.Count > 0)
                {
                    loggedNearby = true;
                    LogNearby(found);
                }

                FtmsDevice bike = null;
                FtmsDevice hr = null;
                if (!bikeSettled)
                {
                    bike = string.IsNullOrEmpty(_forcedBike)
                        ? FtmsSelection.ChooseBike(found, bikeNameContains)
                        : Resolve(_forcedBike, found);
                }

                string bikeAddress = bikeSettled ? _bikeAddress : (bike == null ? null : bike.Address);
                if (!hrSettled)
                {
                    hr = string.IsNullOrEmpty(_forcedHr)
                        ? FtmsSelection.ChooseHeart(found, hrNameContains, bikeAddress)
                        : Resolve(_forcedHr, found);
                    if (hr != null && FtmsSelection.SameAddress(hr.Address, bikeAddress))
                    {
                        if (!loggedBikeHr)
                        {
                            loggedBikeHr = true;
                            Log("The heart-rate selection is the bike itself. Still scanning for a separate heart rate sensor.");
                        }
                        hr = null;
                    }
                }

                if (bike != null || hr != null)
                {
                    yield return StartCoroutine(WaitUntilScanStopped());
                    if (_stop)
                        yield break;
                    if (bike != null)
                    {
                        SettleBike(bike);
                        bikeSettled = true;
                    }
                    if (hr != null)
                    {
                        SettleHeart(hr);
                        hrSettled = true;
                    }
                    if (bikeSettled && hrSettled)
                        break;
                    yield return new WaitForSecondsRealtime(0.3f);
                    if (_stop)
                        yield break;
                    _scanDone = false;
                    _ble.StartScan(0);
                    restartAt = Time.realtimeSinceStartup + 30f;
                }
                else if (_scanDone || Time.realtimeSinceStartup >= restartAt)
                {
                    bool failed = _scanDone;
                    yield return StartCoroutine(WaitUntilScanStopped());
                    if (_stop)
                        yield break;
                    yield return new WaitForSecondsRealtime(failed ? 6f : 0.3f);
                    if (_stop)
                        yield break;
                    _scanDone = false;
                    _ble.StartScan(0);
                    restartAt = Time.realtimeSinceStartup + 30f;
                }

                yield return null;
            }

            if (!_stop && _ble != null)
                _ble.StopScan();
        }

        IEnumerator EnsurePermissions(Action<bool> done)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            const string scan = "android.permission.BLUETOOTH_SCAN";
            const string connect = "android.permission.BLUETOOTH_CONNECT";
            if (Permission.HasUserAuthorizedPermission(scan) && Permission.HasUserAuthorizedPermission(connect))
            {
                done(true);
                yield break;
            }
            bool denied = false;
            _permissionCallbacks = new PermissionCallbacks();
            _permissionCallbacks.PermissionDenied += _ => denied = true;
            _permissionCallbacks.PermissionDeniedAndDontAskAgain += _ => denied = true;
            Permission.RequestUserPermissions(new[] { scan, connect }, _permissionCallbacks);
            float end = Time.realtimeSinceStartup + 45f;
            while (Time.realtimeSinceStartup < end && !_stop)
            {
                if (Permission.HasUserAuthorizedPermission(scan) && Permission.HasUserAuthorizedPermission(connect))
                {
                    done(true);
                    yield break;
                }
                if (denied)
                {
                    done(false);
                    yield break;
                }
                yield return null;
            }
            done(Permission.HasUserAuthorizedPermission(scan) && Permission.HasUserAuthorizedPermission(connect));
#else
            done(false);
            yield break;
#endif
        }

        void OnScan(FtmsDevice device)
        {
            if (device == null || string.IsNullOrEmpty(device.Address))
                return;
            _devices[device.Address] = device;
        }

        void OnScanComplete()
        {
            _scanDone = true;
        }

        void OnConnection(string role, string state, string detail)
        {
            if (role == "bike")
            {
                if (state == "connecting")
                    SetBikeLink("connecting", "");
                else if (state == "connected")
                    SetBikeLink("connected", "enabling FTMS");
                else if (state == "disconnected")
                {
                    _bikeSetupId++;
                    _targets.SetCommandReady(false);
                    if (_ble != null)
                        _ble.SetStopOnQuit(false);
                    SetBikeLink("disconnected", string.IsNullOrEmpty(detail) ? "link lost" : detail);
                    Log("Bike disconnected");
                    if (_bikeAutoReconnect && !_bikeUnsupported && !_stop)
                        StartCoroutine(Reconnect("bike", _bikeEpoch, _bikeAddress));
                }
            }
            else if (role == "hr")
            {
                if (state == "connecting")
                    SetHrLink("connecting", "");
                else if (state == "connected")
                    SetHrLink("connected", "");
                else if (state == "disconnected")
                {
                    _hrSetupId++;
                    SetHrLink("disconnected", string.IsNullOrEmpty(detail) ? "link lost" : detail);
                    Log("Heart sensor disconnected");
                    if (_hrAutoReconnect && !_hrUnsupported && !_stop)
                        StartCoroutine(Reconnect("hr", _hrEpoch, _hrAddress));
                }
            }
        }

        void OnServices(string role, List<FtmsGattChar> chars)
        {
            if (role == "bike")
            {
                _bikeChars = chars ?? new List<FtmsGattChar>();
                int id = ++_bikeSetupId;
                StartCoroutine(EnableBike(id));
            }
            else if (role == "hr")
            {
                _hrChars = chars ?? new List<FtmsGattChar>();
                int id = ++_hrSetupId;
                StartCoroutine(EnableHr(id));
            }
        }

        void OnNotification(string role, string uuid, byte[] data)
        {
            if (data == null)
                data = Array.Empty<byte>();
            try
            {
                if (role == "bike" && uuid == FtmsUuids.IndoorBikeData)
                {
                    BikeReading parsed = FtmsParsers.ParseIndoorBike(data);
                    if (!parsed.HasAny())
                        return;
                    _bike.Apply(parsed, Time.realtimeSinceStartup);
                    Publish();
                }
                else if (role == "bike" && uuid == FtmsUuids.FtmsStatus)
                {
                    HandleStatus(data);
                }
                else if (role == "bike" && uuid == FtmsUuids.ControlPoint)
                {
                    if (FtmsParsers.TryControlResponse(data, out int opcode, out int result) && opcode == _waitOpcode)
                    {
                        _waitResult = result;
                        _waitDone = true;
                    }
                }
                else if (role == "hr" && uuid == FtmsUuids.HrMeasurement)
                {
                    HeartReading parsed = FtmsParsers.ParseHeartRate(data);
                    if (!parsed.Bpm.HasValue)
                        return;
                    _heart.Apply(parsed, Time.realtimeSinceStartup);
                    Publish();
                }
            }
            catch (Exception ex)
            {
                Log((role == "hr" ? "Heart rate" : "Bike data") + " parse error: " + ex.Message);
            }
        }

        void OnError(string message)
        {
            if (!string.IsNullOrEmpty(message))
                Log(message);
        }

        void HandleStatus(byte[] data)
        {
            if (data.Length == 0)
                return;
            int code = data[0];
            if (code == 0x06 || code == 0x12)
                return;
            Log("Bike status: " + FtmsParsers.StatusName(code));
            if (code == 0xFF)
            {
                _targets.SetControlGranted(false);
                SetBikeLink("connected", "control permission lost");
            }
        }

        IEnumerator EnableBike(int setupId)
        {
            if (!HasService(_bikeChars, FtmsUuids.FtmsService))
            {
                FailBike("Fitness Machine Service (0x1826) not found");
                yield break;
            }
            if (!HasChar(_bikeChars, FtmsUuids.IndoorBikeData))
            {
                FailBike("Indoor Bike Data (0x2AD2) not found");
                yield break;
            }

            byte[] feature = Array.Empty<byte>();
            if (HasChar(_bikeChars, FtmsUuids.FtmsFeature))
            {
                int status = -1;
                byte[] value = null;
                yield return StartCoroutine(Read("bike", FtmsUuids.FtmsFeature, (s, d) => { status = s; value = d; }));
                if (setupId != _bikeSetupId || _stop) yield break;
                if (status == 0 && value != null)
                {
                    feature = value;
                    string summary = FtmsParsers.DescribeFeatures(feature);
                    if (!string.IsNullOrEmpty(summary))
                        Log("Bike features: " + summary);
                }
                else
                {
                    Log("Could not read bike features");
                }
            }

            int bikeStatus = -1;
            yield return StartCoroutine(Subscribe("bike", FtmsUuids.IndoorBikeData, s => bikeStatus = s));
            if (setupId != _bikeSetupId || _stop) yield break;
            if (bikeStatus != 0)
            {
                Log("Could not subscribe to indoor bike data");
                SetBikeLink("connected", "bike data subscribe failed");
                yield break;
            }
            Log("Subscribed to indoor bike data");

            if (HasChar(_bikeChars, FtmsUuids.FtmsStatus))
            {
                int statusStatus = -1;
                yield return StartCoroutine(Subscribe("bike", FtmsUuids.FtmsStatus, s => statusStatus = s));
                if (setupId != _bikeSetupId || _stop) yield break;
                if (statusStatus != 0)
                    Log("Bike status notifications unavailable");
            }

            if (listenOnly)
            {
                _targets.Note = "Listening only; resistance keys are off";
                SetBikeLink("connected", "listening for data");
                Publish();
                yield break;
            }

            if (!HasChar(_bikeChars, FtmsUuids.ControlPoint))
            {
                _targets.Note = "This bike has no FTMS control point";
                SetBikeLink("connected", "no control point, listening for data");
                Log("This bike has no FTMS control point");
                yield break;
            }

            int controlStatus = -1;
            yield return StartCoroutine(Subscribe("bike", FtmsUuids.ControlPoint, s => controlStatus = s));
            if (setupId != _bikeSetupId || _stop) yield break;
            if (controlStatus != 0)
            {
                _targets.Note = "Control point indications unavailable";
                Log("Control point indications unavailable");
                Publish();
                yield break;
            }

            yield return StartCoroutine(LoadTargets(feature, setupId));
            if (setupId != _bikeSetupId || _stop) yield break;
            _targets.SetCommandReady(true);

            bool ok = false;
            string reason = "";
            yield return StartCoroutine(Command(FtmsUuids.OpRequestControl, Array.Empty<byte>(), (success, why) => { ok = success; reason = why; }));
            if (setupId != _bikeSetupId || _stop) yield break;
            if (!ok)
            {
                _targets.SetControlGranted(false);
                SetBikeLink("connected", "control not taken (" + reason + ")");
                Log("Request control failed: " + reason);
                yield break;
            }

            _targets.SetControlGranted(true);
            Log("FTMS control granted");
            yield return StartCoroutine(Command(FtmsUuids.OpStart, Array.Empty<byte>(), (success, why) => { ok = success; reason = why; }));
            if (setupId != _bikeSetupId || _stop) yield break;
            if (ok)
            {
                _ble.SetStopOnQuit(true);
                SetBikeLink("connected", "control granted, session started");
                Log("FTMS session started");
                yield break;
            }
            if (reason == "operation failed")
                SetBikeLink("connected", "control granted, bike already running");
            else
                SetBikeLink("connected", "control granted, start failed (" + reason + ")");
            Log("Start or resume: " + reason);
        }

        IEnumerator LoadTargets(byte[] feature, int setupId)
        {
            List<string> modes = FtmsParsers.SupportedModes(feature);
            ResistanceScale resistance = FtmsParsers.DefaultResistance();
            ResistanceScale power = FtmsParsers.DefaultPower();
            bool rangeFailed = false;
            if (HasChar(_bikeChars, FtmsUuids.ResistanceRange))
            {
                int status = -1;
                byte[] value = null;
                yield return StartCoroutine(Read("bike", FtmsUuids.ResistanceRange, (s, d) => { status = s; value = d; }));
                if (setupId != _bikeSetupId || _stop) yield break;
                if (status == 0)
                {
                    ResistanceScale parsed = FtmsParsers.ParseResistanceRange(value);
                    if (parsed != null)
                        resistance = parsed;
                }
                else
                {
                    rangeFailed = true;
                }
            }
            if (HasChar(_bikeChars, FtmsUuids.PowerRange))
            {
                int status = -1;
                byte[] value = null;
                yield return StartCoroutine(Read("bike", FtmsUuids.PowerRange, (s, d) => { status = s; value = d; }));
                if (setupId != _bikeSetupId || _stop) yield break;
                if (status == 0)
                {
                    ResistanceScale parsed = FtmsParsers.ParsePowerRange(value);
                    if (parsed != null)
                        power = parsed;
                }
                else
                {
                    rangeFailed = true;
                }
            }
            if (rangeFailed)
                Log("Could not read target ranges");
            var scales = new Dictionary<string, ResistanceScale>
            {
                { "resistance", resistance },
                { "grade", FtmsParsers.DefaultGrade() },
                { "power", power }
            };
            _targets.Configure(modes, scales);
            ResistanceScale current = scales[modes[0]];
            Log("A/B change " + FtmsParsers.ModeLabel(modes[0]) + " ("
                + FtmsParsers.FormatTarget(current.Minimum, current) + " to "
                + FtmsParsers.FormatTarget(current.Maximum, current) + ")");
        }

        IEnumerator EnableHr(int setupId)
        {
            if (!HasChar(_hrChars, FtmsUuids.HrMeasurement))
            {
                _hrUnsupported = true;
                _hrAutoReconnect = false;
                SetHrLink("unsupported", "Heart Rate Measurement (0x2A37) not found");
                Log("Heart Rate Measurement (0x2A37) not found");
                yield break;
            }
            int status = -1;
            yield return StartCoroutine(Subscribe("hr", FtmsUuids.HrMeasurement, s => status = s));
            if (setupId != _hrSetupId || _stop) yield break;
            if (status != 0)
            {
                Log("Heart rate notifications failed");
                yield break;
            }
            SetHrLink("connected", "heart rate notifications on");
            Log("Subscribed to heart rate");
        }

        IEnumerator Reconnect(string role, int epoch, string address)
        {
            yield return new WaitForSecondsRealtime(3f);
            if (_stop || string.IsNullOrEmpty(address) || address == "—")
                yield break;
            if (role == "bike")
            {
                if (epoch != _bikeEpoch || !_bikeAutoReconnect || _bikeUnsupported)
                    yield break;
                SetBikeLink("reconnecting", "");
                Log("Reconnecting to the bike…");
                _ble.Connect("bike", address);
            }
            else
            {
                if (epoch != _hrEpoch || !_hrAutoReconnect || _hrUnsupported)
                    yield break;
                SetHrLink("reconnecting", "");
                Log("Reconnecting to the heart sensor…");
                _ble.Connect("hr", address);
            }
        }

        void Pump()
        {
            if (_pumping || _stop)
                return;
            _pumping = true;
            StartCoroutine(PumpRequests());
        }

        IEnumerator PumpRequests()
        {
            while (_requests.Count > 0 && !_stop)
            {
                TargetRequest next = _requests.Dequeue();
                if (next is NudgeRequest nudge)
                {
                    int direction = nudge.Direction;
                    while (_requests.Count > 0 && _requests.Peek() is NudgeRequest)
                        direction += ((NudgeRequest)_requests.Dequeue()).Direction;
                    if (direction != 0)
                        yield return StartCoroutine(ApplyDirection(direction));
                }
                else if (next is AbsoluteRequest absolute)
                {
                    yield return StartCoroutine(ApplyAbsolute(absolute.Value));
                }
            }
            _pumping = false;
        }

        IEnumerator ApplyDirection(int direction)
        {
            if (_targets.CommandReady && !_targets.ControlGranted && !_targets.Rejected)
            {
                bool granted = false;
                string why = "";
                yield return StartCoroutine(Command(FtmsUuids.OpRequestControl, Array.Empty<byte>(), (ok, reason) => { granted = ok; why = reason; }));
                if (!granted)
                {
                    Log("Request control failed: " + why);
                    yield break;
                }
                _targets.SetControlGranted(true);
                Log("FTMS control granted");
            }

            while (!_stop)
            {
                PlanResult plan = _targets.Plan(direction, _bike.Resistance, _bike.PowerW);
                if (!plan.HasUpdate || plan.Scale == null)
                {
                    if (!string.IsNullOrEmpty(plan.Note))
                        Log(plan.Note);
                    else
                        Publish();
                    yield break;
                }
                Publish();
                TargetCommand command = FtmsParsers.TargetPayload(plan.Scale, plan.Updated);
                bool ok = false;
                string reason = "";
                yield return StartCoroutine(Command(command.Opcode, command.Payload, (success, why) => { ok = success; reason = why; }));
                if (ok)
                {
                    Log(Capitalize(FtmsParsers.ModeLabel(plan.Scale.Kind)) + " set to "
                        + FtmsParsers.FormatTarget(plan.Updated, plan.Scale));
                    yield break;
                }
                string failed = plan.Scale.Kind;
                _targets.Revert(plan.HasPrevious, plan.Previous);
                if (reason != "op code not supported")
                {
                    Log(reason == "write failed"
                        ? "Target command failed: " + reason
                        : "Target not changed: " + reason);
                    yield break;
                }
                string next = _targets.AdvanceMode();
                if (next == null)
                {
                    Log("This bike does not accept resistance, grade, or power targets");
                    yield break;
                }
                Log(Capitalize(FtmsParsers.ModeLabel(failed))
                    + " isn't supported. A/B now change " + FtmsParsers.ModeLabel(next) + ".");
            }
        }

        IEnumerator ApplyAbsolute(int wireValue)
        {
            PlanResult plan = _targets.PlanAbsolute(wireValue);
            if (!plan.HasUpdate || plan.Scale == null)
            {
                if (!string.IsNullOrEmpty(plan.Note))
                    Log(plan.Note);
                else
                    Publish();
                yield break;
            }
            Publish();
            TargetCommand command = FtmsParsers.TargetPayload(plan.Scale, plan.Updated);
            bool ok = false;
            string reason = "";
            yield return StartCoroutine(Command(command.Opcode, command.Payload, (success, why) => { ok = success; reason = why; }));
            if (ok)
            {
                Log(Capitalize(FtmsParsers.ModeLabel(plan.Scale.Kind)) + " set to "
                    + FtmsParsers.FormatTarget(plan.Updated, plan.Scale));
                yield break;
            }
            _targets.Revert(plan.HasPrevious, plan.Previous);
            Log(reason == "write failed" ? "Target command failed: " + reason : "Target not changed: " + reason);
        }

        IEnumerator Command(int opcode, byte[] payload, Action<bool, string> done)
        {
            _waitOpcode = opcode;
            _waitDone = false;
            var body = new byte[1 + (payload == null ? 0 : payload.Length)];
            body[0] = (byte)opcode;
            if (payload != null && payload.Length > 0)
                Buffer.BlockCopy(payload, 0, body, 1, payload.Length);
            int writeStatus = -1;
            int id = _ble.BeginWrite("bike", FtmsUuids.ControlPoint, body, true);
            yield return StartCoroutine(WaitOp(id, (status, _) => writeStatus = status));
            if (writeStatus != 0)
            {
                _waitOpcode = -1;
                done(false, "write failed");
                yield break;
            }
            float end = Time.realtimeSinceStartup + 5f;
            while (!_waitDone && Time.realtimeSinceStartup < end && !_stop)
                yield return null;
            bool got = _waitDone;
            int result = _waitResult;
            _waitOpcode = -1;
            if (!got)
            {
                done(false, "no response");
                yield break;
            }
            done(result == FtmsUuids.ResultSuccess, FtmsParsers.ResultName(result));
        }

        IEnumerator Read(string role, string uuid, Action<int, byte[]> done)
        {
            int id = _ble.BeginRead(role, uuid);
            yield return StartCoroutine(WaitOp(id, done));
        }

        IEnumerator Subscribe(string role, string uuid, Action<int> done)
        {
            int id = _ble.BeginSubscribe(role, uuid);
            yield return StartCoroutine(WaitOp(id, (status, _) => done(status)));
        }

        IEnumerator WaitOp(int id, Action<int, byte[]> done)
        {
            float end = Time.realtimeSinceStartup + 10f;
            int status;
            byte[] data;
            while (!_ble.TryComplete(id, out status, out data))
            {
                if (_stop || Time.realtimeSinceStartup >= end)
                {
                    _ble.Cancel(id);
                    done(-1, Array.Empty<byte>());
                    yield break;
                }
                yield return null;
            }
            done(status, data);
        }

        void FailBike(string message)
        {
            _bikeUnsupported = true;
            _bikeAutoReconnect = false;
            SetBikeLink("unsupported", message);
            Log(message);
        }

        IEnumerator WaitUntilScanStopped()
        {
            if (_scanDone || _ble == null)
                yield break;
            _ble.StopScan();
            float waitEnd = Time.realtimeSinceStartup + 2f;
            while (!_scanDone && Time.realtimeSinceStartup < waitEnd && !_stop)
                yield return null;
        }

        void SettleBike(FtmsDevice bike)
        {
            _bikeName = bike.Name;
            _bikeAddress = bike.Address;
            Log("Using bike: " + bike.Name + "  (" + bike.Address + ")");
            _bikeEpoch++;
            _bikeAutoReconnect = true;
            _ble.Connect("bike", bike.Address);
        }

        void SettleHeart(FtmsDevice hr)
        {
            _hrName = hr.Name;
            _hrAddress = hr.Address;
            Log("Using heart rate sensor: " + hr.Name + "  (" + hr.Address + ")");
            _hrEpoch++;
            _hrAutoReconnect = true;
            _ble.Connect("hr", hr.Address);
        }

        List<FtmsDevice> RankedDevices()
        {
            var found = new List<FtmsDevice>(_devices.Values);
            found.Sort((a, b) => b.Rssi.CompareTo(a.Rssi));
            return found;
        }

        void LogNearby(List<FtmsDevice> found)
        {
            int shown = Mathf.Min(8, found.Count);
            var names = new System.Text.StringBuilder();
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) names.Append(", ");
                names.Append(found[i].Name);
            }
            Log("Nearby: " + names);
        }

        FtmsDevice Resolve(string address, List<FtmsDevice> found)
        {
            for (int i = 0; i < found.Count; i++)
            {
                if (FtmsSelection.SameAddress(found[i].Address, address))
                    return found[i];
            }
            return new FtmsDevice
            {
                Name = address,
                Address = address,
                Rssi = -200,
                MachineTypes = new List<string>()
            };
        }

        string LookupName(string address)
        {
            foreach (var pair in _devices)
            {
                if (FtmsSelection.SameAddress(pair.Key, address) && pair.Value.Name != "(no name)")
                    return pair.Value.Name;
            }
            return null;
        }

        static bool HasService(List<FtmsGattChar> chars, string service)
        {
            for (int i = 0; i < chars.Count; i++)
            {
                if (chars[i].Service == service)
                    return true;
            }
            return false;
        }

        static bool HasChar(List<FtmsGattChar> chars, string uuid)
        {
            for (int i = 0; i < chars.Count; i++)
            {
                if (chars[i].Uuid == uuid)
                    return true;
            }
            return false;
        }

        void SetBikeLink(string status, string detail)
        {
            _bikeLink = status;
            _bikeDetail = detail ?? "";
            Publish();
        }

        void SetHrLink(string status, string detail)
        {
            _hrLink = status;
            _hrDetail = detail ?? "";
            Publish();
        }

        void Log(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message;
            _logs.Enqueue(line);
            while (_logs.Count > 8)
                _logs.Dequeue();
            Debug.Log("[FTMS] " + message);
            Publish();
        }

        void Publish()
        {
            var snap = new FtmsSnapshot
            {
                BikeName = _bikeName,
                BikeAddress = _bikeAddress,
                HrName = _hrName,
                HrAddress = _hrAddress,
                BikeLink = _bikeLink,
                HrLink = _hrLink,
                BikeDetail = _bikeDetail,
                HrDetail = _hrDetail,
                Bike = _bike.Clone(),
                Heart = _heart.Clone(),
                ControlGranted = _targets.ControlGranted,
                CommandReady = _targets.CommandReady,
                TargetRejected = _targets.Rejected,
                TargetNote = _targets.Note ?? "",
                Logs = new List<string>(_logs)
            };
            if (_targets.Target.HasValue)
            {
                snap.HasTarget = true;
                snap.Target = _targets.Target.Value;
            }
            if (_targets.Scale != null)
            {
                snap.HasScale = true;
                snap.ScaleMinimum = _targets.Scale.Minimum;
                snap.ScaleMaximum = _targets.Scale.Maximum;
                snap.ScaleIncrement = _targets.Scale.Increment;
                snap.ScaleTenths = _targets.Scale.Tenths;
                snap.TargetKind = _targets.Scale.Kind;
            }
            Current = snap;
            Updated?.Invoke(snap);
        }

        static string Capitalize(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            return char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        abstract class TargetRequest { }
        sealed class NudgeRequest : TargetRequest { public int Direction; }
        sealed class AbsoluteRequest : TargetRequest { public int Value; }
    }
}
