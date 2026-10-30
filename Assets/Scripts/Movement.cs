using UnityEngine;

public class ForwardMovement : MonoBehaviour
{
    [SerializeField] private Transform head;   // CenterEyeAnchor
    [SerializeField] private float speed = 2f;

    void Update()
    {
        // Thumbstick der linken Hand (Primary = links, Secondary = rechts)
        Vector2 stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);

        // Oder nur ein einzelner Wert, z. B. der rechte Trigger:
        // float trigger = OVRInput.Get(OVRInput.Axis1D.SecondaryIndexTrigger);

        Vector3 forward = head.forward;
        forward.y = 0;
        forward.Normalize();

        transform.position += forward * stick.y * speed * Time.deltaTime;
    }
}