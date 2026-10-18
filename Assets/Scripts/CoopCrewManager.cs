using UnityEngine;

/// <summary>
/// Local (shared-screen) co-op bootstrap. Place on the ship root alongside the
/// pilot + gunner handlers. Validates the crew setup, locks the cursor, and
/// exposes role state for the HUD. Add PilotInputHandler + GunnerInputHandler
/// to the same GameObject; flight reads the pilot, weapons/shields read the gunner.
/// </summary>
[DefaultExecutionOrder(-10)]
public class CoopCrewManager : MonoBehaviour
{
    [Header("Crew")]
    [SerializeField] private PilotInputHandler _pilot;
    [SerializeField] private GunnerInputHandler _gunner;

    [Header("Behaviour")]
    [SerializeField]
    [Tooltip("When true and no Gunner handler exists, the legacy ShipInputHandler drives weapons+shields (solo mode).")]
    private bool _allowSoloFallback = true;

    [Header("Controls Help")]
    [SerializeField]
    [TextArea(4, 8)]
    private string _pilotHelp = "P1 PILOT - W/S thrust, A/D yaw, Mouse steer, Q/E roll, Shift boost, G flight-assist";

    [SerializeField]
    [TextArea(4, 8)]
    private string _gunnerHelp = "P2 GUNNER - Arrows aim turret, Enter fire, Space shield, R reload";

    public PilotInputHandler Pilot => _pilot;
    public GunnerInputHandler Gunner => _gunner;
    public bool IsCoop => _pilot != null && _gunner != null;
    public bool AllowSoloFallback => _allowSoloFallback;
    public string PilotHelp => _pilotHelp;
    public string GunnerHelp => _gunnerHelp;

    public CrewRole LocalRole
    {
        get
        {
            if (_pilot != null && _gunner != null) return CrewRole.Solo; // local ship hosts both
            if (_pilot != null) return CrewRole.Pilot;
            if (_gunner != null) return CrewRole.Gunner;
            return CrewRole.Solo;
        }
    }

    private void Awake()
    {
        if (_pilot == null)
            _pilot = GetComponent<PilotInputHandler>();
        if (_gunner == null)
            _gunner = GetComponent<GunnerInputHandler>();

        if (_pilot == null && GetComponent<ShipInputHandler>() == null && _allowSoloFallback)
            Debug.LogWarning("[CoOp] Ship has no PilotInputHandler and no legacy ShipInputHandler - flight will be dead. Add PilotInputHandler (P1) to the ship.", this);
        if (_gunner == null && GetComponent<ShipInputHandler>() == null && _allowSoloFallback)
            Debug.LogWarning("[CoOp] Ship has no GunnerInputHandler and no legacy ShipInputHandler - weapons/shields will be dead. Add GunnerInputHandler (P2) to the ship.", this);
    }

    private void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }
}
