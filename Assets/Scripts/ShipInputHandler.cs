using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// LEGACY solo handler (single player does everything). Kept for backward
/// compatibility. For 2-player co-op use PilotInputHandler (P1 flight) +
/// GunnerInputHandler (P2 weapons/shields) instead - flight/weapons/shield
/// systems prefer the crew handlers when present and only fall back to this.
/// </summary>
public class ShipInputHandler : MonoBehaviour
{
    [Header("Input Actions")]
    [SerializeField]
    [Tooltip("Drag the ShipInputActions asset here")]
    private InputActionAsset _inputAsset;

    public Vector2 LookInput { get; private set; }
    public float ThrustInput { get; private set; }
    public float YawInput { get; private set; }
    public float RollInput { get; private set; }
    public bool IsFiring { get; private set; }
    public bool IsBoosting { get; private set; }
    public bool IsShieldActive { get; private set; }
    public bool IsFreeLooking { get; private set; }
    public bool FlightAssist { get; private set; } = true;

    private InputActionMap _shipMap;
    private InputAction _thrustAction;
    private InputAction _strafeAction;
    private InputAction _lookAction;
    private InputAction _rollAction;
    private InputAction _fireAction;
    private InputAction _boostAction;
    private InputAction _shieldAction;
    private InputAction _freeLookAction;
    private InputAction _flightAssistAction;

    private void Awake()
    {
        if (_inputAsset != null)
        {
            _shipMap = _inputAsset.FindActionMap("Ship");
            if (_shipMap != null)
            {
                _thrustAction = _shipMap.FindAction("Thrust");
                _strafeAction = _shipMap.FindAction("Strafe");
                _lookAction = _shipMap.FindAction("Look");
                _rollAction = _shipMap.FindAction("Roll");
                _fireAction = _shipMap.FindAction("Fire");
                _boostAction = _shipMap.FindAction("Boost");
                _shieldAction = _shipMap.FindAction("Shield");
                _freeLookAction = _shipMap.FindAction("FreeLook");
                _flightAssistAction = _shipMap.FindAction("FlightAssist");

                if (_flightAssistAction != null)
                    _flightAssistAction.performed += OnFlightAssistPerformed;

                if (_shieldAction != null)
                {
                    _shieldAction.started += OnShieldStarted;
                    _shieldAction.canceled += OnShieldCanceled;
                }
            }
        }

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void OnEnable()
    {
        if (_shipMap != null)
            _shipMap.Enable();
    }

    private void OnDisable()
    {
        if (_shipMap != null)
            _shipMap.Disable();
    }

    private void OnFlightAssistPerformed(InputAction.CallbackContext context)
    {
        FlightAssist = !FlightAssist;
    }

    private void OnShieldStarted(InputAction.CallbackContext context)
    {
        IsShieldActive = true;
    }

    private void OnShieldCanceled(InputAction.CallbackContext context)
    {
        IsShieldActive = false;
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
        if (_fireAction != null)
            IsFiring = _fireAction.IsInProgress();
        if (_boostAction != null)
            IsBoosting = _boostAction.IsInProgress();
        if (_freeLookAction != null)
            IsFreeLooking = _freeLookAction.IsInProgress();

        if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
        {
            var weaponSystem = GetComponent<ShipWeaponSystem>();
            if (weaponSystem != null && !weaponSystem.IsReloading && weaponSystem.CurrentAmmo > 0)
            {
                weaponSystem.StartReload();
            }
        }
    }
}