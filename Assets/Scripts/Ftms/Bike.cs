using UnityEngine;

namespace Teamprojekt.Ftms
{
    public class Bike : MonoBehaviour
    {
        [SerializeField] private Transform head;
        FtmsMonitor _monitor;

        void Start()
        {
            _monitor = GetComponent<FtmsMonitor>();
            if (_monitor == null)
                _monitor = FindAnyObjectByType<FtmsMonitor>();

            if (_monitor != null && _monitor.autoStart)
                _monitor.StartSession();
            else if (_monitor == null)
                Debug.LogWarning("Bike: no FtmsMonitor in the scene, so speed stays at 0.", this);

            if (head == null)
                head = FindCenterEye();
            if (head == null)
                Debug.LogWarning("Bike: assign Head (CenterEyeAnchor). Movement is disabled until it is set.", this);
        }

        void Update()
        {
            if (_monitor == null || _monitor.Current == null || head == null)
                return;

            // Indoor-bike speed is kilometers per hour; the rig moves in meters.
            float speed = (_monitor.Current.Bike.SpeedKmh ?? 0f) / 3.6f;

            Vector3 forward = head.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                return;

            transform.position += forward.normalized * speed * Time.deltaTime;
        }

        Transform FindCenterEye()
        {
            Transform direct = transform.Find("TrackingSpace/CenterEyeAnchor");
            if (direct != null)
                return direct;

            foreach (Transform child in GetComponentsInChildren<Transform>(true))
            {
                if (child.name == "CenterEyeAnchor")
                    return child;
            }

            return null;
        }
    }
}
