using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;

namespace Teamprojekt.Ftms
{
    /// <summary>
    /// Android BLE transport. Editor play mode reports that Bluetooth is device-only.
    /// </summary>
    public sealed class FtmsBleClient : IDisposable
    {
        public event Action<FtmsDevice> ScanResult;
        public event Action ScanComplete;
        public event Action<string, string, string> Connection;
        public event Action<string, List<FtmsGattChar>> ServicesReady;
        public event Action<string, string, byte[]> Notification;
        public event Action<string> Error;

        readonly FtmsBleReceiver _receiver;
        readonly Dictionary<int, Pending> _pending = new Dictionary<int, Pending>();
        int _nextId;

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaClass _bridge;
#endif

        public bool IsAvailable
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return _bridge != null;
#else
                return false;
#endif
            }
        }

        public FtmsBleClient()
        {
            var go = new GameObject("FtmsBleReceiver");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _receiver = go.AddComponent<FtmsBleReceiver>();
            _receiver.JsonReceived += Handle;
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                _bridge = new AndroidJavaClass("com.teamprojekt.ftms.FtmsBleBridge");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[FTMS] BLE bridge is missing: " + ex.Message);
            }
#endif
        }

        public void StartScan(int seconds)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _bridge?.CallStatic("startScan", seconds);
#else
            Debug.Log("[FTMS] BLE only runs on an Android device.");
#endif
        }

        public void StopScan()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _bridge?.CallStatic("stopScan");
#endif
        }

        public void Connect(string role, string address)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _bridge?.CallStatic("connect", role, address);
#endif
        }

        public void Disconnect(string role)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _bridge?.CallStatic("disconnect", role);
#endif
        }

        public void SetStopOnQuit(bool enabled)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_bridge == null)
                return;
            byte[] payload = enabled
                ? new byte[] { FtmsUuids.OpStop, FtmsUuids.StopParam }
                : Array.Empty<byte>();
            _bridge.CallStatic("setStopOnQuit", "bike", FtmsUuids.ControlPoint, payload);
#endif
        }

        public void Shutdown()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_bridge == null)
                return;
            try
            {
                _bridge.CallStatic("shutdown");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[FTMS] Shutdown failed: " + ex.Message);
            }
#endif
        }

        public int BeginRead(string role, string uuid)
        {
            return Begin(id =>
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                _bridge.CallStatic("read", role, uuid, id);
#else
                throw new InvalidOperationException("BLE only runs on an Android device.");
#endif
            });
        }

        public int BeginSubscribe(string role, string uuid)
        {
            return Begin(id =>
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                _bridge.CallStatic("subscribe", role, uuid, id);
#else
                throw new InvalidOperationException("BLE only runs on an Android device.");
#endif
            });
        }

        public int BeginWrite(string role, string uuid, byte[] data, bool withResponse)
        {
            return Begin(id =>
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                _bridge.CallStatic("write", role, uuid, data ?? Array.Empty<byte>(), withResponse, id);
#else
                throw new InvalidOperationException("BLE only runs on an Android device.");
#endif
            });
        }

        public bool TryComplete(int id, out int status, out byte[] data)
        {
            status = -1;
            data = Array.Empty<byte>();
            if (!_pending.TryGetValue(id, out Pending pending) || !pending.Done)
                return false;
            status = pending.Status;
            data = pending.Data ?? Array.Empty<byte>();
            _pending.Remove(id);
            return true;
        }

        public void Cancel(int id)
        {
            _pending.Remove(id);
        }

        public void Dispose()
        {
            if (_receiver != null)
            {
                _receiver.JsonReceived -= Handle;
                UnityEngine.Object.Destroy(_receiver.gameObject);
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            _bridge?.Dispose();
            _bridge = null;
#endif
        }

        int Begin(Action<int> call)
        {
            int id = ++_nextId;
            var pending = new Pending();
            _pending[id] = pending;
            if (!IsAvailable)
            {
                pending.Done = true;
                pending.Status = -1;
                return id;
            }
            try
            {
                call(id);
            }
            catch (Exception ex)
            {
                pending.Done = true;
                pending.Status = -1;
                Debug.LogWarning("[FTMS] BLE call failed: " + ex.Message);
            }
            return id;
        }

        void Handle(string json)
        {
            if (string.IsNullOrEmpty(json))
                return;
            BleMessage msg;
            try
            {
                msg = JsonUtility.FromJson<BleMessage>(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[FTMS] Bad BLE event: " + ex.Message);
                return;
            }
            if (msg == null || string.IsNullOrEmpty(msg.type))
                return;

            switch (msg.type)
            {
                case "scan":
                    ScanResult?.Invoke(ToDevice(msg));
                    break;
                case "scanComplete":
                    ScanComplete?.Invoke();
                    break;
                case "connection":
                    Connection?.Invoke(msg.role ?? "", msg.state ?? "", msg.message ?? "");
                    break;
                case "services":
                    ServicesReady?.Invoke(msg.role ?? "", ToChars(msg));
                    break;
                case "notify":
                    Notification?.Invoke(msg.role ?? "", FtmsUuids.Short(msg.uuid), Decode(msg.data));
                    break;
                case "read":
                case "write":
                case "subscribe":
                    Complete(msg);
                    break;
                case "error":
                    Error?.Invoke(msg.message ?? "BLE error");
                    break;
            }
        }

        void Complete(BleMessage msg)
        {
            if (!_pending.TryGetValue(msg.requestId, out Pending pending))
                return;
            pending.Status = msg.status;
            pending.Data = Decode(msg.data);
            pending.Done = true;
        }

        static FtmsDevice ToDevice(BleMessage msg)
        {
            var device = new FtmsDevice
            {
                Name = string.IsNullOrEmpty(msg.name) ? "(no name)" : msg.name,
                Address = msg.address ?? "",
                Rssi = msg.rssi
            };
            if (msg.uuids != null)
            {
                for (int i = 0; i < msg.uuids.Length; i++)
                {
                    string shortId = FtmsUuids.Short(msg.uuids[i]);
                    if (shortId == FtmsUuids.FtmsService)
                        device.IsFtms = true;
                    if (shortId == FtmsUuids.HrService)
                        device.IsHr = true;
                }
            }
            if (!string.IsNullOrEmpty(msg.serviceData))
            {
                try
                {
                    byte[] raw = Convert.FromBase64String(msg.serviceData);
                    device.MachineTypes = FtmsParsers.MachineTypes(raw);
                    device.Available = FtmsParsers.MachineAvailable(raw);
                }
                catch (FormatException)
                {
                    device.MachineTypes = new List<string>();
                }
            }
            return device;
        }

        static List<FtmsGattChar> ToChars(BleMessage msg)
        {
            var list = new List<FtmsGattChar>();
            if (msg.items == null)
                return list;
            for (int i = 0; i < msg.items.Length; i++)
            {
                BleChar item = msg.items[i];
                if (item == null)
                    continue;
                list.Add(new FtmsGattChar
                {
                    Service = FtmsUuids.Short(item.service),
                    Uuid = FtmsUuids.Short(item.uuid),
                    Properties = item.properties
                });
            }
            return list;
        }

        static byte[] Decode(string data)
        {
            if (string.IsNullOrEmpty(data))
                return Array.Empty<byte>();
            try
            {
                return Convert.FromBase64String(data);
            }
            catch (FormatException)
            {
                return Array.Empty<byte>();
            }
        }

        sealed class Pending
        {
            public bool Done;
            public int Status;
            public byte[] Data;
        }
    }

    [Serializable]
    public sealed class BleMessage
    {
        public string type;
        public string role;
        public string name;
        public string address;
        public int rssi;
        public string[] uuids;
        public string serviceData;
        public string state;
        public string uuid;
        public int requestId;
        public int status;
        public string data;
        public string message;
        public BleChar[] items;
    }

    [Serializable]
    public sealed class BleChar
    {
        public string service;
        public string uuid;
        public int properties;
    }

    [Preserve]
    public sealed class FtmsBleReceiver : MonoBehaviour
    {
        public event Action<string> JsonReceived;

        [Preserve]
        public void OnBleEvent(string json)
        {
            JsonReceived?.Invoke(json);
        }
    }
}
