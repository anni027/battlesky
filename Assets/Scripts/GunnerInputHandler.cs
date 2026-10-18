using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player 2 - Gunner. Owns weapons and shields: turret aim, fire, shield hold,
/// manual reload. Reads from the "Gunner" action map, falls back to the legacy
/// "Ship" map (Fire/Shield) so solo setups keep working.
/// Local co-op defaults: Aim = Arrow keys (+ right stick), Fire = Enter /
/// RightCtrl / RightMouse, Shield = Space / RightShift, Reload = R / O.
/// </summary>
public class GunnerInputHandler : MonoBehaviour
{
    [Header("Input Actions")]
    [SerializeField]
    [Tooltip("Drag the ShipInputActions asset here")]
    private InputActionAsset _inputAsset;

    [Header("Online Co-op")]
    [SerializeField]
    [Tooltip("When true (online gunner with their own mouse), mouse delta also drives Aim. Leave false for shared-screen co-op where the mouse belongs to the pilot.")]
    private bool _useMouseAim;

    /// <summary>Turret aim input: arrows / right-stick (and mouse delta when _useMouseAim).</summary>
    public Vector2 AimInput { get; private set; }
    public bool IsFiring { get; private set; }
    public bool IsShieldActive { get; private set; }

    /// <summary>Set true for one frame when reload is pressed. Consumed by ShipWeaponSystem.</summary>
    public bool ReloadPressed { get; private set; }

    private InputActionMap _gunnerMap;
    private InputAction _aimAction;
    private InputAction _fireAction;
    private InputAction _shieldAction;
    private InputAction _reloadAction;

    // Legacy fallback (Ship map Fire/Shield/R).
    private InputAction _legacyFire;
    private InputAction _legacyShield;
    private bool _usingLegacyMap;

    public bool UseMouseAim
    {
        get => _useMouseAim;
        set => _useMouseAim = value;
    }

    private void Awake()
    {
        if (_inputAsset != null)
        {
            _gunnerMap = _inputAsset.FindActionMap("Gunner");
            if (_gunnerMap != null)
            {
                _aimAction = _gunnerMap.FindAction("Aim");
                _fireAction = _gunnerMap.FindAction("Fire");
                _shieldAction = _gunnerMap.FindAction("Shield");
                _reloadAction = _gunnerMap.FindAction("Reload");

                if (_shieldAction != null)
                {
                    _shieldAction.started += OnShieldStarted;
                    _shieldAction.canceled += OnShieldCanceled;
                }
            }
            else
            {
                // Legacy fallback: share the Ship map's Fire/Shield.
                _usingLegacyMap = true;
                InputActionMap shipMap = _inputAsset.FindActionMap("Ship");
                if (shipMap != null)
                {
                    _legacyFire = shipMap.FindAction("Fire");
                    _legacyShield = shipMap.FindAction("Shield");
                    if (_legacyShield != null)
                    {
                        _legacyShield.started += OnShieldStarted;
                        _legacyShield.canceled += OnShieldCanceled;
                    }
                }
            }
        }
    }

    private void OnEnable()
    {
        if (_gunnerMap != null)
            _gunnerMap.Enable();
        // NOTE: legacy Ship map is enabled by PilotInputHandler / ShipInputHandler,
        // do not enable it here or it double-enables on shared-screen setups.
    }

    private void OnDisable()
    {
        if (_gunnerMap != null)
            _gunnerMap.Disable();
    }

    private void OnDestroy()
    {
        if (_shieldAction != null)
        {
            _shieldAction.started -= OnShieldStarted;
            _shieldAction.canceled -= OnShieldCanceled;
        }
        if (_legacyShield != null)
        {
            _legacyShield.started -= OnShieldStarted;
            _legacyShield.canceled -= OnShieldCanceled;
        }
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
        ReloadPressed = false;

        if (!_usingLegacyMap)
        {
            if (_aimAction != null)
            {
                Vector2 aim = _aimAction.ReadValue<Vector2>();
                if (_useMouseAim && Mouse.current != null)
                {
                    // Mouse delta arrives as pixels/frame; scale down to match
                    // the ~[-1,1] arrow/stick range used by the turret.
                    Vector2 mouseDelta = Mouse.current.delta.ReadValue() * 0.05f;
                    aim += mouseDelta;
                    aim = Vector2.ClampMagnitude(aim, 2f);
                }
                AimInput = aim;
            }
            if (_fireAction != null)
                IsFiring = _fireAction.IsInProgress();
            if (_reloadAction != null && _reloadAction.WasPressedThisFrame())
                ReloadPressed = true;
        }
        else
        {
            if (_legacyFire != null)
                IsFiring = _legacyFire.IsInProgress();
            // IsShieldActive driven by legacy shield started/canceled callbacks.
            AimInput = Vector2.zero;
        }

        // Shared keyboard reload fallback (solo muscle memory).
        if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
            ReloadPressed = true;
    }

    /// <summary>Called by ShipWeaponSystem after it starts a reload.</summary>
    public void ConsumeReloadPress()
    {
        ReloadPressed = false;
    }
}
