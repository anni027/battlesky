using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class ShipFlightModel : MonoBehaviour
{
    private Rigidbody _rb;
    private ShipInputHandler _handler;
    private PilotInputHandler _pilot;

    [Header("Main Engine")]
    [SerializeField]
    [Tooltip("15 m/s^2 at full throttle, applied as ForceMode.Acceleration")]
    private float _thrustAccel = 15f;

    [SerializeField]
    [Tooltip("2.2f")]
    private float _boostMultiplier = 2.2f;

    [SerializeField]
    [Tooltip("3f seconds of sustained boost")]
    private float _boostDuration = 3f;

    [SerializeField]
    [Tooltip("5f seconds before boost is available again")]
    private float _boostCooldown = 5f;

    [Header("RCS Acceleration")]
    [SerializeField]
    [Tooltip("10 rad/s^2: pitch reaches its cap in about 0.24 s")]
    private float _pitchAccel = 10f;

    [SerializeField]
    [Tooltip("7.5 rad/s^2: yaw reaches its cap in about 0.2 s; A/D keys use this channel")]
    private float _yawAccel = 7.5f;

    [SerializeField]
    [Tooltip("16 rad/s^2: roll reaches its cap in about 0.24 s")]
    private float _rollAccel = 16f;

    [SerializeField]
    [Tooltip("Mouse pixels per 50 Hz step for full deflection (0.12 ~ 400 px/s sweep)")]
    private float _mouseSensitivity = 0.12f;

    [SerializeField]
    [Tooltip("0.6f nonlinear exponent, 0.5-0.8 range")]
    private float _mouseCurve = 0.6f;

    [SerializeField]
    [Tooltip("1.5f rad/s (86 deg/s) pitch cap")]
    private float _maxPitchRate = 1.5f;

    [SerializeField]
    [Tooltip("1.0f rad/s (57 deg/s) yaw cap")]
    private float _maxYawRate = 1.0f;

    [SerializeField]
    [Tooltip("2.4f rad/s (137 deg/s) roll cap")]
    private float _maxRollRate = 2.4f;

    [Header("Flight Assist")]
    [SerializeField]
    [Tooltip("0.92f per 0.02 s step, dt-normalized (~0.24 s time constant)")]
    private float _flightAssistDamp = 0.92f;

    [Header("Auto-Forward")]
    [SerializeField]
    [Tooltip("Constant forward acceleration applied every step so the ship always drifts forward")]
    private float _baseForwardAccel = 5f;
    [SerializeField]
    [Tooltip("Auto-forward drift stops accelerating once forward speed reaches this (m/s)")]
    private float _driftMaxSpeed = 40f;

    private float _boostTimer;
    private float _cooldownTimer;
    private bool _isBoosting;
    private Vector2 _lookAccum;

    public float BoostCooldownNormalized { get; private set; }
    public bool IsBoostReady { get; private set; } = true;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.useGravity = false;
        _rb.linearDamping = 0f;
        _rb.angularDamping = 0f;
        _rb.mass = 2000f;
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

    private void Update()
    {
        // Accumulate raw mouse delta so the turn rate no longer depends on the
        // framerate the deltas are sampled at.
        if (!FreeLookHeld)
            _lookAccum += LookValue;
    }

    private void FixedUpdate()
    {
        ApplyMainThrust();
        if (!FreeLookHeld)
        {
            ApplyRCSTorque();
        }
        else
        {
            _lookAccum = Vector2.zero;
        }
        EnforceAngularRateLimits();
        ApplyFlightAssist();
    }

    private void ApplyMainThrust()
    {
        // Boost only burns while the throttle is open.
        _isBoosting = BoostHeld && IsBoostReady && Mathf.Abs(ThrustValue) > 0.01f;

        float currentAccel = _isBoosting ? _thrustAccel * _boostMultiplier : _thrustAccel;

        // Constant auto-forward drift so the ship always moves forward even
        // with no throttle input. The drift is capped so speed stays bounded.
        float thrust;
        if (Mathf.Abs(ThrustValue) > 0.01f)
        {
            thrust = ThrustValue * currentAccel;
        }
        else
        {
            float forwardSpeed = Vector3.Dot(_rb.linearVelocity, transform.forward);
            thrust = forwardSpeed < _driftMaxSpeed ? _baseForwardAccel : 0f;
        }
        if (Mathf.Abs(thrust) > 0.0001f)
            _rb.AddRelativeForce(Vector3.forward * thrust, ForceMode.Acceleration);

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
        float px = Mathf.Clamp(_lookAccum.x * _mouseSensitivity, -1f, 1f);
        float py = Mathf.Clamp(_lookAccum.y * _mouseSensitivity, -1f, 1f);
        _lookAccum = Vector2.zero;

        float pitchCmd = Mathf.Sign(-py) * Mathf.Pow(Mathf.Abs(py), _mouseCurve);
        float yawCmd = Mathf.Sign(px) * Mathf.Pow(Mathf.Abs(px), _mouseCurve);

        // A/D yaw rides the same channel as the mouse with full authority.
        float totalYaw = (yawCmd + YawValue) * _yawAccel;
        Vector3 torque = new Vector3(pitchCmd * _pitchAccel, totalYaw, -RollValue * _rollAccel);
        _rb.AddRelativeTorque(torque, ForceMode.Acceleration);
    }

    // Per-axis caps in ship-local space so yaw and roll keep full authority
    // while pitching.
    private void EnforceAngularRateLimits()
    {
        Vector3 local = transform.InverseTransformDirection(_rb.angularVelocity);
        local.x = Mathf.Clamp(local.x, -_maxPitchRate, _maxPitchRate);
        local.y = Mathf.Clamp(local.y, -_maxYawRate, _maxYawRate);
        local.z = Mathf.Clamp(local.z, -_maxRollRate, _maxRollRate);
        _rb.angularVelocity = transform.TransformDirection(local);
    }

    private void ApplyFlightAssist()
    {
        if (!AssistOn) return;
        _rb.angularVelocity *= Mathf.Pow(_flightAssistDamp, Time.fixedDeltaTime / 0.02f);
    }

    public Vector3 ShipVelocity => _rb.linearVelocity;
}
