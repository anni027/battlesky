using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class ShipFlightModel : MonoBehaviour
{
    private Rigidbody _rb;
    private ShipInputHandler _handler;
    private PilotInputHandler _pilot;

    [Header("Main Engine")]
    [SerializeField]
    [Tooltip("2800f")]
    private float _maxThrust = 2800f;

    [SerializeField]
    [Tooltip("2.2f")]
    private float _boostMultiplier = 2.2f;

    [SerializeField]
    [Tooltip("3f seconds of sustained boost")]
    private float _boostDuration = 3f;

    [SerializeField]
    [Tooltip("6f seconds before boost is available again")]
    private float _boostCooldown = 6f;

    

    [Header("RCS Torque")]
    [SerializeField]
    [Tooltip("900f")]
    private float _pitchTorque = 1400f;

    [SerializeField]
    [Tooltip("700f")]
    private float _yawTorque = 1100f;

    [SerializeField]
    [Tooltip("450f")]
    private float _rollTorque = 450f;

    [SerializeField]
    [Tooltip("0.65f nonlinear exponent, 0.5–0.8 range")]
    private float _mouseCurve = 0.55f;

    [SerializeField]
    [Tooltip("0.8f rad/s hard cap")]
    private float _maxAngularSpeed = 1.4f;

    [Header("Keyboard Yaw")]
    [SerializeField] private float keyboardYawTorque = 1200f;
    [SerializeField] private float keyboardYawMultiplier = 1.6f;

    [Header("Flight Assist")]
    [SerializeField]
    [Tooltip("0.91f multiplied against angularVelocity each FixedUpdate")]
    private float _flightAssistDamp = 0.91f;

    private float _boostTimer;
    private float _cooldownTimer;
    private bool _isBoosting;

    public float BoostCooldownNormalized { get; private set; }
    public bool IsBoostReady { get; private set; } = true;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.useGravity = false;
        _rb.linearDamping = 0f;
        _rb.angularDamping = 0f;
        _rb.mass = 12000f;
        _rb.inertiaTensor = new Vector3(9000f, 14000f, 3500f);
        _rb.inertiaTensorRotation = Quaternion.identity;
        _rb.interpolation = RigidbodyInterpolation.Interpolate;
        _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        _handler = GetComponent<ShipInputHandler>();
        _pilot = GetComponent<PilotInputHandler>();
    }

    private Vector2 LookValue => _pilot != null ? _pilot.LookInput : (_handler != null ? _handler.LookInput : Vector2.zero);
    private float ThrustValue => _pilot != null ? _pilot.ThrustInput : (_handler != null ? _handler.ThrustInput : 0f);
    private float YawValue => _pilot != null ? _pilot.YawInput : (_handler != null ? _handler.YawInput : 0f);
    private float RollValue => _pilot != null ? _pilot.RollInput : (_handler != null ? _handler.RollInput : 0f);
    private bool BoostHeld => _pilot != null ? _pilot.IsBoosting : (_handler != null && _handler.IsBoosting);
    private bool FreeLookHeld => _pilot != null ? _pilot.IsFreeLooking : (_handler != null && _handler.IsFreeLooking);
    private bool AssistOn => _pilot != null ? _pilot.FlightAssist : (_handler == null || _handler.FlightAssist);

    private void FixedUpdate()
    {
        ApplyMainThrust();
        if (!FreeLookHeld)
        {
            ApplyRCSTorque();
        }
        EnforceAngularSpeedCap();
        ApplyFlightAssist();
    }

    private void ApplyMainThrust()
    {
        _isBoosting = BoostHeld && IsBoostReady;

        float currentThrust = _isBoosting ? _maxThrust * _boostMultiplier : _maxThrust;

        Vector3 force = transform.forward * ThrustValue * currentThrust;
        _rb.AddRelativeForce(force, ForceMode.Force);

        if (_isBoosting)
        {
            _boostTimer += Time.fixedDeltaTime;
            if (_boostTimer >= _boostDuration)
            {
                _boostTimer = 0f;
                _cooldownTimer = 0f;
                IsBoostReady = false;
            }
        }
        else if (!IsBoostReady)
        {
            _cooldownTimer += Time.fixedDeltaTime;
            BoostCooldownNormalized = Mathf.Clamp01(_cooldownTimer / _boostCooldown);
            if (_cooldownTimer >= _boostCooldown)
            {
                IsBoostReady = true;
                BoostCooldownNormalized = 1f;
            }
        }
        else
        {
            BoostCooldownNormalized = 1f;
        }
    }

    private void ApplyRCSTorque()
    {
        Vector2 lookInput = LookValue;
        float pitchCmd = Mathf.Sign(-lookInput.y) * Mathf.Pow(Mathf.Abs(lookInput.y), _mouseCurve);
        float yawCmd = Mathf.Sign(lookInput.x) * Mathf.Pow(Mathf.Abs(lookInput.x), _mouseCurve);

        float keyYaw = YawValue * keyboardYawTorque * keyboardYawMultiplier;
        float totalYaw = (yawCmd * _yawTorque) + keyYaw;
        Vector3 torque = new Vector3(pitchCmd * _pitchTorque, totalYaw, -RollValue * _rollTorque);
        _rb.AddRelativeTorque(torque, ForceMode.Force);
    }

    private void EnforceAngularSpeedCap()
    {
        if (_rb.angularVelocity.magnitude > _maxAngularSpeed)
        {
            _rb.angularVelocity = _rb.angularVelocity.normalized * _maxAngularSpeed;
        }
    }

    private void ApplyFlightAssist()
    {
        if (!AssistOn) return;
        _rb.angularVelocity *= _flightAssistDamp;
    }

    public Vector3 ShipVelocity => _rb.linearVelocity;
}