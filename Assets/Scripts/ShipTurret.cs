using UnityEngine;

/// <summary>
/// Gunner-controlled turret for shared-screen co-op. The pilot flies the ship
/// with the mouse; the gunner rotates this turret with Arrow keys / gamepad
/// right-stick (via GunnerInputHandler.AimInput) and fires along the turret.
/// Assign firePoints as children of the pitch node so shots follow the aim.
/// When no GunnerInputHandler is present the turret recenters and weapons
/// fall back to camera-forward firing (solo behaviour).
/// </summary>
public class ShipTurret : MonoBehaviour
{
    [Header("Nodes (optional)")]
    [SerializeField]
    [Tooltip("Yaw node. Null = rotate this transform's Y.")]
    private Transform _yawNode;

    [SerializeField]
    [Tooltip("Pitch node (child of yaw). Null = no separate pitch, yaw node pitches.")]
    private Transform _pitchNode;

    [Header("Tuning")]
    [SerializeField]
    [Tooltip("120 deg/s at full deflection")]
    private float _turnSpeed = 120f;

    [SerializeField]
    [Tooltip("60 deg up/down")]
    private float _pitchLimit = 60f;

    [SerializeField]
    [Tooltip("120 deg left/right from center")]
    private float _yawLimit = 120f;

    [SerializeField]
    [Tooltip("8 deg/s return to center when no gunner")]
    private float _recenterSpeed = 8f;

    private GunnerInputHandler _gunner;
    private float _yaw;
    private float _pitch;

    /// <summary>Current aim direction in world space.</summary>
    public Vector3 AimDirection
    {
        get
        {
            Transform pitch = _pitchNode != null ? _pitchNode : (_yawNode != null ? _yawNode : transform);
            return pitch.forward;
        }
    }

    private void Awake()
    {
        _gunner = GetComponent<GunnerInputHandler>();
        if (_gunner == null)
            _gunner = GetComponentInParent<GunnerInputHandler>();
    }

    private void Update()
    {
        Vector2 aim = _gunner != null ? _gunner.AimInput : Vector2.zero;

        if (aim.sqrMagnitude > 0.0001f)
        {
            _yaw = Mathf.Clamp(_yaw + aim.x * _turnSpeed * Time.deltaTime, -_yawLimit, _yawLimit);
            _pitch = Mathf.Clamp(_pitch - aim.y * _turnSpeed * Time.deltaTime, -_pitchLimit, _pitchLimit);
        }
        else if (_gunner == null)
        {
            _yaw = Mathf.MoveTowards(_yaw, 0f, _recenterSpeed * Time.deltaTime);
            _pitch = Mathf.MoveTowards(_pitch, 0f, _recenterSpeed * Time.deltaTime);
        }

        Transform yawT = _yawNode != null ? _yawNode : transform;
        yawT.localRotation = Quaternion.Euler(0f, _yaw, 0f);

        if (_pitchNode != null)
            _pitchNode.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        else if (_yawNode == null)
            transform.localRotation = Quaternion.Euler(_pitch, _yaw, 0f);
    }

    /// <summary>World-space aim ray for HUD crosshair projection.</summary>
    public Ray AimRay(float distance = 500f)
    {
        Transform pitch = _pitchNode != null ? _pitchNode : (_yawNode != null ? _yawNode : transform);
        return new Ray(pitch.position, pitch.forward);
    }
}
