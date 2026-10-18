using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player 1 - Pilot. Owns all flight controls: thrust, yaw/strafe, roll,
/// mouse look (pitch/yaw torque), boost, flight assist and free-look.
/// Reads from the "Pilot" action map, falls back to the legacy "Ship" map
/// so existing setups keep working.
/// </summary>
public class PilotInputHandler : MonoBehaviour
{
    [Header("Input Actions")]
    [SerializeField]
    [Tooltip("Drag the ShipInputActions asset here")]
    private InputActionAsset _inputAsset;

    public Vector2 LookInput { get; private set; }
    public float ThrustInput { get; private set; }
    public float YawInput { get; private set; }
    public float RollInput { get; private set; }
    public bool IsBoosting { get; private set; }
    public bool IsFreeLooking { get; private set; }
    public bool FlightAssist { get; private set; } = true;

    private InputActionMap _pilotMap;
    private InputAction _thrustAction;
    private InputAction _strafeAction;
    private InputAction _lookAction;
    private InputAction _rollAction;
    private InputAction _boostAction;
    private InputAction _freeLookAction;
    private InputAction _flightAssistAction;

    private void Awake()
    {
        if (_inputAsset != null)
        {
            // Prefer the dedicated Pilot map, fall back to legacy Ship map.
            _pilotMap = _inputAsset.FindActionMap("Pilot") ?? _inputAsset.FindActionMap("Ship");
            if (_pilotMap != null)
            {
                _thrustAction = _pilotMap.FindAction("Thrust");
                _strafeAction = _pilotMap.FindAction("Strafe");
                _lookAction = _pilotMap.FindAction("Look");
                _rollAction = _pilotMap.FindAction("Roll");
                _boostAction = _pilotMap.FindAction("Boost");
                _freeLookAction = _pilotMap.FindAction("FreeLook");
                _flightAssistAction = _pilotMap.FindAction("FlightAssist");

                if (_flightAssistAction != null)
                    _flightAssistAction.performed += OnFlightAssistPerformed;
            }
        }

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void OnEnable()
    {
        if (_pilotMap != null)
            _pilotMap.Enable();
    }

    private void OnDisable()
    {
        if (_pilotMap != null)
            _pilotMap.Disable();
    }

    private void OnDestroy()
    {
        if (_flightAssistAction != null)
            _flightAssistAction.performed -= OnFlightAssistPerformed;
    }

    private void OnFlightAssistPerformed(InputAction.CallbackContext context)
    {
        FlightAssist = !FlightAssist;
    }

    private void Update()
    {
        if (_thrustAction != null)
            ThrustInput = _thrustAction.ReadValue<float>();
        if (_strafeAction != null)
            YawInput = _strafeAction.ReadValue<float>();
        if (_lookAction != null)
            LookInput = _lookAction.ReadValue<Vector2>();
        if (_rollAction != null)
            RollInput = _rollAction.ReadValue<float>();
        if (_boostAction != null)
            IsBoosting = _boostAction.IsInProgress();
        if (_freeLookAction != null)
            IsFreeLooking = _freeLookAction.IsInProgress();
    }
}
