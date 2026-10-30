using System.Reflection.Metadata.Ecma335;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;
using UnityEngine.UI;

namespace Teamprojekt.Ftms
{
    public class Bike : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created

    [SerializeField] private Transform head; 
    FtmsMonitor _monitor;
    void Start()
    {
        _monitor = GetComponent<FtmsMonitor>();
        if (_monitor != null)
            {
                
                if (_monitor.autoStart)
                    _monitor.StartSession();
            }
    }

    // Update is called once per frame
    void Update()
    {

        FtmsSnapshot snap = _monitor.Current;

        BikeReading bike = snap.Bike;
        float speed = bike.SpeedKmh ?? 0f;
        
        Vector3 forward = head.forward;
        forward.y = 0;
        forward.Normalize();

        transform.position += forward * speed * Time.deltaTime;
        
    }
}
}

