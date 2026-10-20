using UnityEngine;
using Mirror;
using UnityEngine.InputSystem;
using BattleSky.Network;

/// <summary>
/// Networked crew ship. One ship is crewed by up to 2 players:
/// P1 PILOT flies (thrust/yaw/roll/look/boost), P2 GUNNER shoots + shields.
/// Pairing is automatic in BattleSkyNetworkManager: 1st connection spawns the
/// crew ship as Pilot, 2nd connection joins the same crew as Gunner (its own
/// ship object becomes an invisible control proxy that forwards gunner inputs
/// to the crew ship and follows it so the gunner's camera rides along).
/// 3rd connection starts a new crew, etc. If no gunner is present the pilot
/// flies solo with full weapons+shields.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class NetworkPlayer : NetworkBehaviour
{
    [Header("Input Actions")]
    [SerializeField]
    [Tooltip("Drag the ShipInputActions asset here")]
    private InputActionAsset _inputAsset;

    [Header("Main Engine")]
    [Tooltip("20 m/s^2 at full throttle, applied as ForceMode.Acceleration so hull mass or collider size never change the felt response")]
    [SerializeField] private float thrustAccel = 20f;
    [SerializeField] private float boostMultiplier = 2.4f;
    [SerializeField] private float boostDuration = 3f;
    [SerializeField] private float boostCooldown = 5f;
    [Tooltip("50 m/s^2 straight against current velocity while Brake is held; cancels velocity cleanly instead of reversing it")]
    [SerializeField] private float brakeAccel = 50f;

    [Header("Air Brake")]
    [Tooltip("8 m/s^2 of drag at full deployment, opposite the current velocity. Unlike the hard brake it bleeds speed off gradually instead of stopping the ship dead")]
    [SerializeField] private float airBrakeAccel = 8f;
    [Tooltip("0.9 s for the drag to ramp from nothing to full. The soft ramp-in is the whole point: it feels like deploying air brakes, not like hitting a wall")]
    [SerializeField] private float airBrakeRampTime = 0.9f;
    [Tooltip("0.6 of the drag also scrubs the sideways drift, so the ship settles instead of sliding sideways across its own nose")]
    [SerializeField] private float airBrakeLateralFactor = 0.6f;
    [Tooltip("Extra drag per m/s of speed. Air brakes bite harder the faster you go, and this is what makes them bite back against a full boost at high speed")]
    [SerializeField] private float airBrakeDragCoefficient = 0.22f;

    [Header("RCS Acceleration")]
    [Tooltip("10 rad/s^2: pitch reaches its 86 deg/s cap in about 0.24 s")]
    [SerializeField] private float pitchAccel = 10f;
    [Tooltip("7.5 rad/s^2: yaw reaches its 57 deg/s cap in about 0.2 s; the A/D keys feed this channel too")]
    [SerializeField] private float yawAccel = 7.5f;
    [Tooltip("16 rad/s^2: roll reaches its 137 deg/s cap in about 0.24 s")]
    [SerializeField] private float rollAccel = 16f;
    [Tooltip("Mouse pixels accumulated within one 50 Hz step for full deflection. 0.12 is a 400 px/s hand sweep")]
    [SerializeField] private float mouseSensitivity = 0.12f;
    [Tooltip("0.6f nonlinear exponent: small deltas give finer aim")]
    [SerializeField] private float mouseCurve = 0.6f;

    [Header("Angular Rate Limits")]
    [SerializeField]
    [Tooltip("86 deg/s in rad/s, applied per ship-local axis")]
    private float maxPitchRate = 1.5f;
    [SerializeField]
    [Tooltip("57 deg/s")]
    private float maxYawRate = 1.0f;
    [SerializeField]
    [Tooltip("137 deg/s")]
    private float maxRollRate = 2.4f;

    [Header("Flight Assist")]
    [Tooltip("0.92f per 0.02 s step (dt-normalized), about a 0.24 s time constant")]
    [SerializeField] private float flightAssistDamp = 0.92f;

    [Header("Auto-Forward")]
    [Tooltip("Constant forward acceleration applied every step so the ship always drifts forward")]
    [SerializeField] private float baseForwardAccel = 8f;
    [Tooltip("Auto-forward drift stops accelerating once forward speed reaches this (m/s)")]
    [SerializeField] private float driftMaxSpeed = 60f;

    [Header("Weapons")]
    [SerializeField] private Transform[] firePoints;
    [SerializeField] private GameObject projectilePrefab;
    [Tooltip("1200 m/s bolt speed (was 20000, which read as an invisible instant hit)")]
    [SerializeField] private float muzzleVelocity = 1200f;
    [Tooltip("Meters ahead of the nose the bolt spawns so it never starts inside the hull collider")]
    [SerializeField] private float muzzleSpawnOffset = 12f;
    [SerializeField] private int maxAmmo = 180;
    [SerializeField] private float reloadTime = 1.6f;
    [SerializeField] private float fireRate = 10f;

    [Header("Aim Assist")]
    [Tooltip("Server-only: bends player-fired bolts a little toward the enemy already in the crosshair. Enemy fire and the Death Star's superlaser are never assisted")]
    [SerializeField] private bool aimAssistEnabled = true;
    [Tooltip("Maximum distance (m) at which an enemy is considered as an aim assist target")]
    [SerializeField] private float aimAssistRange = 500f;
    [Tooltip("Only nudge when the raw shot is within this many degrees of the target; further off the player is aiming somewhere else on purpose")]
    [SerializeField] private float aimAssistMaxAngle = 7f;
    [Tooltip("Inside this many degrees the bolt snaps fully onto the target; between this and the max angle the correction fades out linearly")]
    [SerializeField] private float aimAssistSnapAngle = 3f;
    [Tooltip("Death Star is a ~50-100 m sphere that needs no help, and its exhaust-port weak point must be aimed by hand, so it is skipped by default")]
    [SerializeField] private bool aimAssistIncludeDeathStar = false;

    [Header("Gunner Turret Aim")]
    [Tooltip("Degrees of camera rotation per pixel of mouse movement for the gunner's free-look turret")]
    [SerializeField] private float gunnerMouseSensitivity = 0.14f;
    [Tooltip("Turret yaw/pitch speed in degrees per second for arrow keys / right stick")]
    [SerializeField] private float gunnerStickAimSpeed = 80f;
    [Tooltip("Maximum left/right turret yaw in degrees")]
    [SerializeField] private float gunnerYawLimit = 180f;
    [Tooltip("Maximum up/down turret pitch in degrees")]
    [SerializeField] private float gunnerPitchLimit = 90f;

    [Header("Health")]
    [SerializeField] private float maxHealth = 100f;
    [SerializeField] private float deathDelay = 5f;

    [Header("Shields (server-authoritative)")]
    [SerializeField] private float shieldMaxEnergy = 100f;
    [SerializeField] private float shieldDrainRate = 20f;
    [SerializeField] private float shieldRechargeRate = 10f;
    [SerializeField] private float shieldRechargeDelay = 3f;
    [SerializeField] private GameObject shieldMesh;
    [SerializeField] private float shieldRotationSpeed = 45f;

    // ---- Crew state (server-assigned) ----
    [SyncVar] public CrewRole crewRole = CrewRole.Solo;
    [SyncVar] public uint crewId;
    [SyncVar] public bool hasGunner;

    [SyncVar(hook = nameof(OnHealthChanged))]
    private float currentHealth;
    [SyncVar(hook = nameof(OnAmmoChanged))]
    private int currentAmmo;
    [SyncVar]
    private bool isDead;
    [SyncVar]
    private float shieldEnergy;
    [SyncVar]
    private bool shieldActive;

    /// <summary>
    /// Server side air brake state, replicated so the gunner's HUD shows it
    /// too. The local pilot keeps its own immediate key state, so the read
    /// is the OR of both.
    /// </summary>
    [SyncVar]
    private bool _airBrakeActive;

    private Rigidbody rb;
    private float boostTimer;
    private float cooldownTimer;
    private bool isBoosting;
    private float fireCooldown;
    private int currentFirePointIndex;
    [SyncVar] private bool isReloading;
    [SyncVar] private float reloadProgress;
    private float rechargeTimer;

    public float BoostCooldownNormalized { get; private set; } = 1f;
    public bool IsBoostReady { get; private set; } = true;
    public int CurrentAmmo => currentAmmo;
    public int MaxAmmo => maxAmmo;
    public bool IsReloading => isReloading;
    public float ReloadProgress => reloadProgress;
    public float HealthNormalized => maxHealth > 0 ? currentHealth / maxHealth : 0f;
    public float ShieldNormalized => shieldMaxEnergy > 0 ? shieldEnergy / shieldMaxEnergy : 0f;
    public bool IsShieldActive => shieldActive;
    public bool IsDead => isDead;
    public bool IsGunnerProxy => crewRole == CrewRole.Gunner;
    public bool IsBoosting => isBoosting;
    public bool IsPilotFreeLooking => pilotFreeLookHeld;
    /// <summary>
    /// True while the air brake is held: on the server that is the value the
    /// pilot sent through CmdUpdateInput, and on the local pilot's own client
    /// it is the live key state, so the HUD and the camera can react to it.
    /// The replicated flag lets the gunner see the crew's air brake too.
    /// </summary>
    public bool IsAirBraking => airBrakeHeld || _airBrakeActive;

    // Pilot inputs (owner client -> server).
    // Mouse look accumulates raw delta in Update() and the accumulator is
    // consumed exactly once per fixed step (ApplyRCSTorque), which makes the
    // turn rate framerate-independent instead of per-frame additive.
    private Vector2 lookInput;
    private float thrustInput;
    private float yawInput;
    private float rollInput;
    private bool pilotFiring;
    private bool pilotShieldHeld;
    private bool isBoostingInput;
    private bool brakeHeld;
    // Air brake: a held key, plus the 0..1 deployment ramp that fades the
    // drag in. The ramp resets as soon as the brake is released.
    private bool airBrakeHeld;
    private float airBrakeRamp;
    private bool flightAssist = true;
    private Vector3 cameraForward;

    // Pilot free-look keeps the mouse out of the ship torque channel. The
    // delta is accumulated in Update and consumed by the pilot camera in
    // LateUpdate, so head-turn latency stays at one frame.
    private bool pilotFreeLookHeld;
    private Vector2 pilotFreeLookDelta;

    // Gunner inputs (forwarded from the gunner's proxy object on the server).
    private bool gunnerFiring;
    private bool gunnerShieldHeld;
    private Vector3 gunnerAimDir;
    private bool gunnerReloadRequested;

    // Gunner free-look turret camera (client-side visual + aim source). The
    // proxy root stays welded to the crew ship; only this camera rotates.
    private Camera _gunnerCamera;
    private Quaternion _gunnerBaseCamRot = Quaternion.identity;
    private float _gunnerYaw;
    private float _gunnerPitch;

    // Cached crew ship lookup. The gunner proxy needs the crew ship every
    // frame, and a full scene scan per frame is far too expensive.
    private NetworkPlayer _cachedCrewShip;
    private float _crewShipLookupTimer;

    // Gunner input command throttling: CmdSendGunnerInput only goes out when
    // state changes or at most every 0.05 s (20 Hz) so a gunner does not spam
    // 60-144 commands per second.
    private float _lastGunnerSendTime;
    private bool _lastGunnerFiring;
    private bool _lastGunnerShield;
    private Vector3 _lastGunnerAimDir = Vector3.zero;

    // Pilot camera feel (client-side visual only, never the gunner turret).
    private const float PilotMaxSpeed = 140f;
    private const float PilotMaxFov = 80f;
    private const float PilotFovSmoothRate = 3f;
    private const float PilotAirBrakeFovSqueeze = 3f;
    private const float PilotPullbackPerSpeed = 0.04f;
    private const float PilotMaxPullback = 6f;
    private const float PilotPullbackSmoothRate = 4f;
    private const float PilotRollDegreesPerRate = 2.5f;
    private const float PilotMaxRollOffset = 6f;
    private const float PilotRollSmoothRate = 6f;
    private const float PilotShakeAmplitude = 0.04f;
    private const float PilotShakeSmoothRate = 10f;
    private const float PilotFreeLookSensitivity = 0.12f;
    private const float PilotFreeLookYawLimit = 75f;
    private const float PilotFreeLookPitchLimit = 55f;
    private const float PilotFreeLookReturnRate = 8f;

    private Camera _pilotCamera;
    private Camera _mainCamera;
    private Vector3 _pilotBaseLocalPos;
    private Quaternion _pilotBaseLocalRot = Quaternion.identity;
    private float _pilotBaseFov = 60f;
    private float _pilotPullback;
    private float _pilotRollOffset;
    private float _pilotShakeAmplitude;
    private float _pilotFreeLookYaw;
    private float _pilotFreeLookPitch;

    private InputActionMap _shipMap;
    private InputActionMap _gunnerMap;
    private InputAction _lookAction;
    private InputAction _thrustAction;
    private InputAction _strafeAction;
    private InputAction _rollAction;
    private InputAction _fireAction;
    private InputAction _boostAction;
    private InputAction _brakeAction;
    private InputAction _airBrakeAction;
    private InputAction _shieldAction;
    private InputAction _freeLookAction;
    private InputAction _flightAssistAction;
    private InputAction _gunnerFireAction;
    private InputAction _gunnerAimAction;
    private InputAction _gunnerShieldAction;
    private InputAction _gunnerReloadAction;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false;
        rb.linearDamping = 0f;
        rb.angularDamping = 0f;
        rb.mass = 2000f;
        rb.inertiaTensor = new Vector3(9000f, 14000f, 3500f);
        rb.inertiaTensorRotation = Quaternion.identity;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        Camera shipCamera = GetComponentInChildren<Camera>(true);
        _gunnerCamera = shipCamera;
        if (_gunnerCamera != null)
            _gunnerBaseCamRot = _gunnerCamera.transform.localRotation;

        _pilotCamera = shipCamera;
        if (_pilotCamera != null)
        {
            _pilotBaseLocalPos = _pilotCamera.transform.localPosition;
            _pilotBaseLocalRot = _pilotCamera.transform.localRotation;
            _pilotBaseFov = _pilotCamera.fieldOfView;
        }

        _mainCamera = Camera.main;

        if (shieldMesh == null)
        {
            // The shield bubble is nested under the ship model, not a direct
            // child, so search the whole subtree by name.
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "shield" && t != transform)
                {
                    shieldMesh = t.gameObject;
                    break;
                }
            }
        }

        if (_inputAsset != null)
        {
            _shipMap = _inputAsset.FindActionMap("Ship");
            if (_shipMap != null)
            {
                _lookAction = _shipMap.FindAction("Look");
                _thrustAction = _shipMap.FindAction("Thrust");
                _strafeAction = _shipMap.FindAction("Strafe");
                _rollAction = _shipMap.FindAction("Roll");
                _fireAction = _shipMap.FindAction("Fire");
                _boostAction = _shipMap.FindAction("Boost");
                _brakeAction = _shipMap.FindAction("Brake");
                _airBrakeAction = _shipMap.FindAction("AirBrake");
                _shieldAction = _shipMap.FindAction("Shield");
                _freeLookAction = _shipMap.FindAction("FreeLook");
                _flightAssistAction = _shipMap.FindAction("FlightAssist");

                if (_flightAssistAction != null)
                    _flightAssistAction.performed += OnFlightAssistPerformed;
                if (_shieldAction != null)
                {
                    _shieldAction.started += OnPilotShieldStarted;
                    _shieldAction.canceled += OnPilotShieldCanceled;
                }
            }

            _gunnerMap = _inputAsset.FindActionMap("Gunner");
            if (_gunnerMap != null)
            {
                _gunnerFireAction = _gunnerMap.FindAction("Fire");
                _gunnerAimAction = _gunnerMap.FindAction("Aim");
                _gunnerShieldAction = _gunnerMap.FindAction("Shield");
                _gunnerReloadAction = _gunnerMap.FindAction("Reload");
            }
        }
    }

    private void OnEnable()
    {
        // Enable both maps: the pilot ship reads Ship, the gunner proxy reads
        // Gunner (+ Ship fallback). Enabling is idempotent and avoids SyncVar
        // ordering issues (crewRole may not be synced yet when OnEnable fires).
        if (_shipMap != null)
            _shipMap.Enable();
        if (_gunnerMap != null)
            _gunnerMap.Enable();
    }

    private void OnDisable()
    {
        if (_shipMap != null)
            _shipMap.Disable();
        if (_gunnerMap != null)
            _gunnerMap.Disable();
    }

    private void OnDestroy()
    {
        if (_flightAssistAction != null)
            _flightAssistAction.performed -= OnFlightAssistPerformed;
        if (_shieldAction != null)
        {
            _shieldAction.started -= OnPilotShieldStarted;
            _shieldAction.canceled -= OnPilotShieldCanceled;
        }
    }

    private void OnFlightAssistPerformed(InputAction.CallbackContext context)
    {
        flightAssist = !flightAssist;
    }

    private void OnPilotShieldStarted(InputAction.CallbackContext context)
    {
        pilotShieldHeld = true;
    }

    private void OnPilotShieldCanceled(InputAction.CallbackContext context)
    {
        pilotShieldHeld = false;
    }

    public override void OnStartServer()
    {
        currentHealth = maxHealth;
        currentAmmo = maxAmmo;
        shieldEnergy = shieldMaxEnergy;
        shieldActive = false;

        if (IsGunnerProxy)
            ApplyProxyMode();
    }

    public override void OnStartClient()
    {
        // Only the local player's cameras render. Everyone else's are muted
        // so each client gets exactly one view (pilot: own ship, gunner: proxy
        // that follows the crew ship).
        bool local = isLocalPlayer;
        foreach (Camera cam in GetComponentsInChildren<Camera>(true))
            cam.enabled = local;
        foreach (AudioListener al in GetComponentsInChildren<AudioListener>(true))
            al.enabled = local;

        if (local)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        if (IsGunnerProxy)
            ApplyProxyMode();

        UpdateShieldVisual();
    }

    /// <summary>
    /// Gunner proxies are invisible control forwarders: no collisions, no
    /// physics, no weapon visuals of their own. Their camera follows the crew
    /// ship on the gunner client (see FollowCrewShip).
    /// </summary>
    private void ApplyProxyMode()
    {
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        foreach (Collider c in GetComponentsInChildren<Collider>(true))
            c.enabled = false;
        foreach (MeshRenderer r in GetComponentsInChildren<MeshRenderer>(true))
        {
            // Keep the shield bubble logic on the real ship only.
            if (shieldMesh == null || r.gameObject != shieldMesh)
                r.enabled = false;
        }
        if (shieldMesh != null)
            shieldMesh.SetActive(false);
    }

    /// <summary>Server: promote a gunner proxy into the visible crew ship (pilot left).</summary>
    [Server]
    public void PromoteProxyToPilot()
    {
        crewRole = CrewRole.Pilot;
        hasGunner = false;
        if (rb != null)
            rb.isKinematic = false;
        foreach (Collider c in GetComponentsInChildren<Collider>(true))
            c.enabled = true;
        foreach (MeshRenderer r in GetComponentsInChildren<MeshRenderer>(true))
        {
            if (shieldMesh == null || r.gameObject != shieldMesh)
                r.enabled = true;
        }
        RpcRefreshProxyVisual();
    }

    [ClientRpc]
    private void RpcRefreshProxyVisual()
    {
        if (crewRole == CrewRole.Pilot)
        {
            // Promoted proxy: become the visible ship on every client.
            ResetGunnerAim();
            if (rb != null)
                rb.isKinematic = false;
            foreach (Collider c in GetComponentsInChildren<Collider>(true))
                c.enabled = true;
            foreach (MeshRenderer r in GetComponentsInChildren<MeshRenderer>(true))
            {
                if (shieldMesh == null || r.gameObject != shieldMesh)
                    r.enabled = true;
            }
        }
        else if (IsGunnerProxy)
        {
            ApplyProxyMode();
        }
    }

    private void FixedUpdate()
    {
        if (isDead) return;
        if (IsGunnerProxy)
        {
            // Proxies never simulate; the crew ship does. The server still
            // mirrors the crew ship's pose onto the proxy so the replicated
            // position on the gunner's client matches the real ship.
            if (isServer)
                FollowCrewShipServer();
            return;
        }

        if (fireCooldown > 0)
            fireCooldown -= Time.fixedDeltaTime;

        if (isServer)
        {
            ApplyPhysics();
            UpdateShieldServer();
        }
        else if (isLocalPlayer)
        {
            // Send the accumulated mouse delta before the local prediction
            // consumes it, so the server receives exactly one copy of it.
            SendInputsToServer();
            ApplyPhysics();
        }
    }

    private void Update()
    {
        if (IsGunnerProxy)
        {
            if (isLocalPlayer && !isDead)
            {
                // Snap to the crew ship first so the turret camera aims from
                // this frame's pose, then read aim/fire into the server.
                FollowCrewShip();

                // The Death Star arrival cinematic flies the camera itself, so
                // the gunner must not steer it or push fire input into the
                // server while it plays. The proxy still follows the ship.
                if (DeathStarArrivalCutscene.IsPlaying)
                    return;

                ReadGunnerInput();
            }
            return;
        }

        if (!isLocalPlayer || isDead) return;

        // The cutscene owns the camera for its duration: local input is ignored
        // and no reload is kicked off behind the cinematic.
        if (DeathStarArrivalCutscene.IsPlaying)
        {
            UpdateShieldVisual();
            return;
        }

        if (!isReloading && currentAmmo == 0)
        {
            CmdStartReload();
        }

        ProcessInput();
        UpdateShieldVisual();
    }

    private void LateUpdate()
    {
        if (IsGunnerProxy || !isLocalPlayer)
            return;

        // Never fight the cinematic camera for the pilot's view.
        if (DeathStarArrivalCutscene.IsPlaying)
            return;

        UpdateShieldVisual();
        UpdatePilotCamera(Time.deltaTime);
    }

    /// <summary>
    /// Speed FOV, pullback, roll lean, boost shake, and cockpit free-look for
    /// the local pilot only. Runs after Update so the gunner camera's existing
    /// rotation logic is never touched on a proxy.
    /// </summary>
    private void UpdatePilotCamera(float dt)
    {
        if (_pilotCamera == null || rb == null)
            return;

        float speed = rb.linearVelocity.magnitude;
        float speed01 = Mathf.Clamp01(speed / PilotMaxSpeed);

        // Authored 60 deg FOV opens to 80 deg at top speed, about 3/s.
        float targetFov = Mathf.Lerp(_pilotBaseFov, PilotMaxFov, speed01);
        // The air brake squeezes the FOV in a little so the deceleration is
        // felt. It is subtracted from the value computed above on every frame
        // and the speed mapping is untouched, so it can never accumulate.
        if (IsAirBraking)
            targetFov -= PilotAirBrakeFovSqueeze;
        _pilotCamera.fieldOfView = Mathf.Lerp(
            _pilotCamera.fieldOfView, targetFov, SmoothAlpha(PilotFovSmoothRate, dt));

        // Slide the eye back as speed builds: 0.04 m per m/s, capped at 6 m.
        float targetPullback = Mathf.Min(speed * PilotPullbackPerSpeed, PilotMaxPullback);
        _pilotPullback = Mathf.Lerp(
            _pilotPullback, targetPullback, SmoothAlpha(PilotPullbackSmoothRate, dt));

        // A little camera lean from the ship-local roll rate, capped at 6 deg.
        float rollRate = transform.InverseTransformDirection(rb.angularVelocity).z;
        float targetRoll = Mathf.Clamp(
            rollRate * PilotRollDegreesPerRate, -PilotMaxRollOffset, PilotMaxRollOffset);
        _pilotRollOffset = Mathf.Lerp(
            _pilotRollOffset, targetRoll, SmoothAlpha(PilotRollSmoothRate, dt));

        // Perlin shake only while the engine is actually boosting; the
        // amplitude itself fades so starting/ending boost never pops.
        float targetShake = isBoosting ? PilotShakeAmplitude : 0f;
        _pilotShakeAmplitude = Mathf.Lerp(
            _pilotShakeAmplitude, targetShake, SmoothAlpha(PilotShakeSmoothRate, dt));

        // Free-look head turn. While held, mouse pixels map directly to yaw
        // and pitch; on release both ease back to center.
        Vector2 freeLookDelta = pilotFreeLookDelta;
        pilotFreeLookDelta = Vector2.zero;
        if (pilotFreeLookHeld)
        {
            _pilotFreeLookYaw = Mathf.Clamp(
                _pilotFreeLookYaw + freeLookDelta.x * PilotFreeLookSensitivity,
                -PilotFreeLookYawLimit, PilotFreeLookYawLimit);
            _pilotFreeLookPitch = Mathf.Clamp(
                _pilotFreeLookPitch + freeLookDelta.y * PilotFreeLookSensitivity,
                -PilotFreeLookPitchLimit, PilotFreeLookPitchLimit);
        }
        else
        {
            float returnAlpha = SmoothAlpha(PilotFreeLookReturnRate, dt);
            _pilotFreeLookYaw = Mathf.Lerp(_pilotFreeLookYaw, 0f, returnAlpha);
            _pilotFreeLookPitch = Mathf.Lerp(_pilotFreeLookPitch, 0f, returnAlpha);
        }

        float shakeX = (Mathf.PerlinNoise(Time.time * 17f, 0.31f) - 0.5f) * 2f;
        float shakeY = (Mathf.PerlinNoise(0.73f, Time.time * 15f) - 0.5f) * 2f;
        Vector3 localOffset = Vector3.back * _pilotPullback
            + new Vector3(shakeX, shakeY, 0f) * _pilotShakeAmplitude;

        _pilotCamera.transform.localPosition =
            _pilotBaseLocalPos + _pilotBaseLocalRot * localOffset;
        _pilotCamera.transform.localRotation = _pilotBaseLocalRot * Quaternion.Euler(
            -_pilotFreeLookPitch, _pilotFreeLookYaw, _pilotRollOffset);
    }

    private static float SmoothAlpha(float rate, float dt)
    {
        return 1f - Mathf.Exp(-rate * dt);
    }

    // ---------- Local input ----------

    private void ProcessInput()
    {
        Vector2 lookDelta = Vector2.zero;
        if (_lookAction != null)
            lookDelta = _lookAction.ReadValue<Vector2>();

        pilotFreeLookHeld = _freeLookAction != null && _freeLookAction.IsInProgress();
        if (pilotFreeLookHeld)
        {
            // Cockpit head turn: keep the mouse out of the ship torque
            // accumulator, so looking around never steers the hull.
            pilotFreeLookDelta += lookDelta;
            lookInput = Vector2.zero;
        }
        else
        {
            lookInput += lookDelta;
        }

        if (_thrustAction != null)
            thrustInput = _thrustAction.ReadValue<float>();
        if (_strafeAction != null)
            yawInput = _strafeAction.ReadValue<float>();
        if (_rollAction != null)
            rollInput = _rollAction.ReadValue<float>();

        // Pilot fires only when solo (no gunner aboard). With a gunner, the
        // gunner owns the trigger and the pilot's Fire is ignored.
        bool solo = (crewRole == CrewRole.Solo) || !hasGunner;
        if (_fireAction != null)
            pilotFiring = solo && _fireAction.IsInProgress();
        else
            pilotFiring = false;
        if (!solo)
            pilotShieldHeld = false; // gunner owns shields when aboard

        if (_boostAction != null)
            isBoostingInput = _boostAction.IsInProgress();
        if (_brakeAction != null)
            brakeHeld = _brakeAction.IsInProgress();
        // The air brake is read straight off the action map (never through the
        // generated ShipInputActions wrapper) so a stale generated file cannot
        // break the build.
        airBrakeHeld = _airBrakeAction != null && _airBrakeAction.IsInProgress();

        if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame && currentAmmo > 0 && !isReloading)
        {
            CmdStartReload();
        }

        if (_mainCamera != null)
            cameraForward = _mainCamera.transform.forward;
        else
            cameraForward = transform.forward;
    }

    private void ReadGunnerInput()
    {
        bool firing = false;
        bool shield = false;
        bool reload = false;

        // The gunner can fire with either the Gunner Fire action (right click)
        // or the Ship Fire action (left click).
        if (_gunnerFireAction != null)
            firing = _gunnerFireAction.IsInProgress();
        if (!firing && _fireAction != null)
            firing = _fireAction.IsInProgress();

        if (_gunnerShieldAction != null)
            shield = _gunnerShieldAction.IsInProgress();
        else if (_shieldAction != null)
            shield = _shieldAction.IsInProgress();

        if (_gunnerReloadAction != null && _gunnerReloadAction.WasPressedThisFrame())
            reload = true;
        if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
            reload = true;

        // Free-look turret: mouse delta (Ship/Look) and arrows/right stick
        // (Gunner/Aim) rotate only the camera, so the gunner tracks targets
        // independently of where the pilot points the ship. The proxy root
        // still follows the crew ship every frame.
        Vector2 aimDelta = Vector2.zero;
        if (_lookAction != null)
            aimDelta += _lookAction.ReadValue<Vector2>() * gunnerMouseSensitivity;
        if (_gunnerAimAction != null)
            aimDelta += _gunnerAimAction.ReadValue<Vector2>() * gunnerStickAimSpeed * Time.deltaTime;

        _gunnerYaw = Mathf.Clamp(_gunnerYaw + aimDelta.x, -gunnerYawLimit, gunnerYawLimit);
        _gunnerPitch = Mathf.Clamp(_gunnerPitch - aimDelta.y, -gunnerPitchLimit, gunnerPitchLimit);

        if (_gunnerCamera != null)
            _gunnerCamera.transform.localRotation = _gunnerBaseCamRot * Quaternion.Euler(_gunnerPitch, _gunnerYaw, 0f);

        Vector3 aimDir = _gunnerCamera != null
            ? _gunnerCamera.transform.forward
            : (_mainCamera != null ? _mainCamera.transform.forward : transform.forward);

        gunnerFiring = firing;
        gunnerShieldHeld = shield;
        gunnerAimDir = aimDir;

        // Throttle gunner input commands: only send when state actually
        // changes, or at most every 0.05 s (20 Hz), so a gunner does not spam
        // 60-144 commands per second.
        bool stateChanged = firing != _lastGunnerFiring
            || shield != _lastGunnerShield
            || aimDir != _lastGunnerAimDir;
        bool timeReady = Time.time - _lastGunnerSendTime >= 0.05f;
        if (stateChanged || timeReady)
        {
            CmdSendGunnerInput(firing, shield, aimDir, reload);
            _lastGunnerSendTime = Time.time;
            _lastGunnerFiring = firing;
            _lastGunnerShield = shield;
            _lastGunnerAimDir = aimDir;
        }

        if (reload && currentAmmo > 0 && !isReloading)
            CmdRequestGunnerReload();
    }

    /// <summary>
    /// Centers the turret camera again (used when a gunner is promoted to
    /// pilot so the new pilot does not inherit the old turret offset).
    /// </summary>
    private void ResetGunnerAim()
    {
        _gunnerYaw = 0f;
        _gunnerPitch = 0f;
        if (_gunnerCamera != null)
            _gunnerCamera.transform.localRotation = _gunnerBaseCamRot;
    }

    /// <summary>
    /// Gunner proxy follows the real crew ship so the gunner's camera rides
    /// along with the pilot. Runs on the gunner client only.
    /// </summary>
    private void FollowCrewShip()
    {
        NetworkPlayer crewShip = FindCrewShip();
        if (crewShip == null || crewShip == this)
            return;
        // Smoothly interpolate toward the crew ship so the gunner's camera
        // does not jitter as the ship snaps its pose each frame.
        transform.position = Vector3.MoveTowards(transform.position, crewShip.transform.position, 60f * Time.deltaTime);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, crewShip.transform.rotation, 180f * Time.deltaTime);
    }

    /// <summary>
    /// Server: keeps the gunner proxy parked on the crew ship so the pose that
    /// replicates to the gunner's client is already correct.
    /// </summary>
    private void FollowCrewShipServer()
    {
        NetworkPlayer crewShip = FindCrewShipOnServer();
        if (crewShip == null || crewShip == this)
            return;
        transform.SetPositionAndRotation(crewShip.transform.position, crewShip.transform.rotation);
    }

    /// <summary>
    /// Client-side lookup of the shared crew ship: the non-proxy NetworkPlayer
    /// with the same crewId. Null on the crew ship itself, before the crew is
    /// assigned, or while the crew ship is not spawned yet on this client.
    /// </summary>
    public NetworkPlayer FindCrewShip()
    {
        // The crew ship only changes when the pilot leaves, so the lookup is
        // cached. Scanning the whole scene every frame is far too expensive:
        // the gunner proxy does this in Update and the server copy of the
        // proxy does it in FixedUpdate.
        if (_cachedCrewShip != null && _cachedCrewShip.crewId == crewId && !_cachedCrewShip.IsGunnerProxy)
            return _cachedCrewShip;

        _crewShipLookupTimer -= Time.deltaTime;
        if (_crewShipLookupTimer > 0f) return _cachedCrewShip;
        _crewShipLookupTimer = 0.5f;

        NetworkPlayer[] ships = FindObjectsByType<NetworkPlayer>(FindObjectsSortMode.None);
        foreach (NetworkPlayer s in ships)
        {
            if (s == this) continue;
            if (s.crewId == crewId && !s.IsGunnerProxy)
            {
                _cachedCrewShip = s;
                return s;
            }
        }
        _cachedCrewShip = null;
        return null;
    }

    private void SendInputsToServer()
    {
        CmdUpdateInput(lookInput, thrustInput, yawInput, rollInput, pilotFiring, pilotShieldHeld, isBoostingInput, brakeHeld, airBrakeHeld, flightAssist, cameraForward);
    }

    [Command]
    private void CmdUpdateInput(Vector2 look, float thrust, float yaw, float roll, bool firing, bool shieldHeld, bool boosting, bool brake, bool airBrake, bool assist, Vector3 camForward)
    {
        // Accumulate: the server consumes one fixed step's worth at a time,
        // exactly like the owner's local prediction does.
        lookInput += look;
        thrustInput = thrust;
        yawInput = yaw;
        rollInput = roll;
        pilotFiring = firing;
        pilotShieldHeld = shieldHeld;
        isBoostingInput = boosting;
        brakeHeld = brake;
        airBrakeHeld = airBrake;
        if (isServer) _airBrakeActive = airBrake;
        flightAssist = assist;
        cameraForward = camForward;
    }

    // ---------- Gunner -> crew ship forwarding ----------

    [Command]
    private void CmdSendGunnerInput(bool firing, bool shieldHeld, Vector3 aimDir, bool reloadPressed)
    {
        // This runs on the gunner's PROXY. Forward to the real crew ship.
        NetworkPlayer crewShip = FindCrewShipOnServer();
        if (crewShip != null && crewShip != this)
        {
            crewShip.SetGunnerState(firing, shieldHeld, aimDir, reloadPressed);
        }
        else
        {
            // Solo fallback (should not happen): store locally.
            gunnerFiring = firing;
            gunnerShieldHeld = shieldHeld;
            gunnerAimDir = aimDir;
            if (reloadPressed) gunnerReloadRequested = true;
        }
    }

    [Command]
    private void CmdRequestGunnerReload()
    {
        NetworkPlayer crewShip = FindCrewShipOnServer();
        if (crewShip != null && crewShip != this)
            crewShip.RequestReloadFromGunner();
    }

    [Server]
    private NetworkPlayer FindCrewShipOnServer()
    {
        // Server-side lookup across spawned identities.
        foreach (var kv in Mirror.NetworkServer.spawned)
        {
            NetworkPlayer p = kv.Value?.GetComponent<NetworkPlayer>();
            if (p == null || p == this) continue;
            if (p.crewId == crewId && !p.IsGunnerProxy)
                return p;
        }
        return null;
    }

    [Server]
    private void SetGunnerState(bool firing, bool shieldHeld, Vector3 aimDir, bool reloadPressed)
    {
        gunnerFiring = firing;
        gunnerShieldHeld = shieldHeld;
        gunnerAimDir = aimDir;
        if (reloadPressed)
            gunnerReloadRequested = true;
    }

    [Server]
    private void RequestReloadFromGunner()
    {
        gunnerReloadRequested = true;
    }

    // ---------- Simulation (server + predicted client) ----------

    private void ApplyPhysics()
    {
        ApplyMainThrust();
        ApplyAirBrake();
        ApplyRCSTorque();
        EnforceAngularRateLimits();
        if (flightAssist)
        {
            // Damping is authored per 0.02 s step; normalize it to the actual
            // fixed timestep so the ~0.24 s time constant holds.
            rb.angularVelocity *= Mathf.Pow(flightAssistDamp, Time.fixedDeltaTime / 0.02f);
        }

        bool wantFire = hasGunner ? gunnerFiring : pilotFiring;
        if (gunnerReloadRequested && currentAmmo > 0 && !isReloading)
        {
            gunnerReloadRequested = false;
            if (isServer)
                StartCoroutine(ServerReload());
            else
                CmdStartReload();
        }

        // Server-authoritative fire so dedicated servers work too; clients
        // predict via Command, the server simulates directly.
        if (wantFire && currentAmmo > 0 && fireCooldown <= 0)
        {
            if (isServer)
                ServerFire();
            else if (isLocalPlayer)
                CmdFire();
        }
    }

    private void ApplyMainThrust()
    {
        // Boost only burns while the throttle is actually open, so the ready
        // bar cannot drain while the pilot is off the throttle. Brake always
        // wins over an active boost.
        isBoosting = !brakeHeld && isBoostingInput && IsBoostReady && Mathf.Abs(thrustInput) > 0.01f;

        if (brakeHeld)
        {
            // Burn straight against the current velocity vector. Capping the
            // step's deceleration at speed/dt stops the ship at near zero
            // instead of letting the brake push it backwards.
            Vector3 velocity = rb.linearVelocity;
            float speed = velocity.magnitude;
            if (speed > 0.0001f)
            {
                float decel = Mathf.Min(brakeAccel, speed / Time.fixedDeltaTime);
                rb.AddForce(-velocity / speed * decel, ForceMode.Acceleration);
            }
        }
        else
        {
            float currentAccel = isBoosting ? thrustAccel * boostMultiplier : thrustAccel;

            // Constant auto-forward drift so the ship always moves forward
            // even with no throttle input. W/S overrides with normal thrust
            // when pressed. The drift is capped so the ship cannot accelerate
            // without bound.
            float thrust;
            if (Mathf.Abs(thrustInput) > 0.01f)
            {
                thrust = thrustInput * currentAccel;
            }
            else
            {
                float forwardSpeed = Vector3.Dot(rb.linearVelocity, transform.forward);
                // The constant auto-forward drift is suppressed while the air
                // brake is deployed, otherwise the drift would keep pushing
                // the ship forward and it could never actually slow down.
                float drift = airBrakeHeld ? 0f : baseForwardAccel;
                thrust = forwardSpeed < driftMaxSpeed ? drift : 0f;
            }
            if (Mathf.Abs(thrust) > 0.0001f)
                rb.AddRelativeForce(Vector3.forward * thrust, ForceMode.Acceleration);
        }

        if (isBoosting)
        {
            boostTimer += Time.fixedDeltaTime;
            if (boostTimer >= boostDuration)
            {
                boostTimer = 0f;
                cooldownTimer = 0f;
                IsBoostReady = false;
            }
        }
        else if (!IsBoostReady)
        {
            cooldownTimer += Time.fixedDeltaTime;
            BoostCooldownNormalized = Mathf.Clamp01(cooldownTimer / boostCooldown);
            if (cooldownTimer >= boostCooldown)
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

    /// <summary>
    /// Air brake: a drag force opposite the current velocity that fades in
    /// over airBrakeRampTime instead of hitting the ship at full strength.
    /// The soft ramp-in is the whole point - it feels like deploying air
    /// brakes in atmosphere, not like slamming on the hard brake.
    /// It never reverses the ship: the per-step deceleration is capped at
    /// speed/dt, the same guard the hard brake uses. It also leaves an active
    /// boost alone - the boost keeps burning and its timer keeps running, so
    /// the pilot can bleed speed while boosting.
    /// </summary>
    private void ApplyAirBrake()
    {
        // The hard brake (X) is far stronger and owns the ship while it is
        // held, so the air brake steps aside instead of stacking on top of it.
        if (!airBrakeHeld || brakeHeld)
        {
            airBrakeRamp = 0f;
            return;
        }

        float rampTime = Mathf.Max(0.01f, airBrakeRampTime);
        airBrakeRamp = Mathf.Clamp01(airBrakeRamp + Time.fixedDeltaTime / rampTime);

        // Smoothstep the linear ramp so the drag eases in and out instead of
        // starting and stopping with a step.
        float t = airBrakeRamp;
        float strength = t * t * (3f - 2f * t);

        Vector3 velocity = rb.linearVelocity;
        float speed = velocity.magnitude;
        if (speed <= 0.0001f)
            return;

        // Burn against the current velocity vector. The drag has a constant
        // part and a speed proportional part, so it bites harder the faster
        // the ship goes: that is what lets the air brake win against a full
        // boost (48 m/s^2) at high speed instead of being shrugged off.
        float dragAccel = airBrakeAccel + speed * Mathf.Max(0f, airBrakeDragCoefficient);
        float decel = Mathf.Min(dragAccel * strength, speed / Time.fixedDeltaTime);
        Vector3 drag = -velocity / speed * decel;

        // Scrub part of the sideways drift (the velocity component across the
        // ship's forward axis) so the ship settles instead of sliding.
        Vector3 forward = transform.forward;
        Vector3 lateral = velocity - Vector3.Dot(velocity, forward) * forward;
        float lateralSpeed = lateral.magnitude;
        if (lateralSpeed > 0.0001f && airBrakeLateralFactor > 0f)
        {
            float lateralDecel = Mathf.Min(
                decel * airBrakeLateralFactor, lateralSpeed / Time.fixedDeltaTime);
            drag += -lateral / lateralSpeed * lateralDecel;
        }

        // Both contributions together must still not flip the velocity
        // vector, so the combined magnitude is capped as well.
        float maxDecel = speed / Time.fixedDeltaTime;
        if (drag.magnitude > maxDecel)
            drag = drag.normalized * maxDecel;

        rb.AddForce(drag, ForceMode.Acceleration);
    }

    private void ApplyRCSTorque()
    {
        // Consume the mouse delta accumulated since the last fixed step.
        // Sensitivity is per fixed step: ~0.12 means a 400 px/s hand sweep
        // holds full deflection on any framerate.
        float px = Mathf.Clamp(lookInput.x * mouseSensitivity, -1f, 1f);
        float py = Mathf.Clamp(lookInput.y * mouseSensitivity, -1f, 1f);
        lookInput = Vector2.zero;

        float pitchCmd = Mathf.Sign(-py) * Mathf.Pow(Mathf.Abs(py), mouseCurve);
        float yawCmd = Mathf.Sign(px) * Mathf.Pow(Mathf.Abs(px), mouseCurve);

        // A/D yaw rides the same channel as the mouse with full authority so
        // keyboard and mouse both reach the yaw cap.
        float totalYaw = (yawCmd + yawInput) * yawAccel;
        Vector3 torque = new Vector3(pitchCmd * pitchAccel, totalYaw, -rollInput * rollAccel);
        rb.AddRelativeTorque(torque, ForceMode.Acceleration);
    }

    // Caps each ship-local axis separately instead of capping the combined
    // magnitude, so yaw/roll are no longer starved to a few deg/s while
    // pitching.
    private void EnforceAngularRateLimits()
    {
        Vector3 local = transform.InverseTransformDirection(rb.angularVelocity);
        local.x = Mathf.Clamp(local.x, -maxPitchRate, maxPitchRate);
        local.y = Mathf.Clamp(local.y, -maxYawRate, maxYawRate);
        local.z = Mathf.Clamp(local.z, -maxRollRate, maxRollRate);
        rb.angularVelocity = transform.TransformDirection(local);
    }

    // ---------- Shields (server-authoritative) ----------

    [Server]
    private void UpdateShieldServer()
    {
        bool held = hasGunner ? gunnerShieldHeld : pilotShieldHeld;
        bool wantActive = held && shieldEnergy > 0f && !isDead;

        if (wantActive)
        {
            shieldActive = true;
            shieldEnergy -= shieldDrainRate * Time.fixedDeltaTime;
            rechargeTimer = 0f;
            if (shieldEnergy <= 0f)
            {
                shieldEnergy = 0f;
                shieldActive = false;
            }
        }
        else
        {
            shieldActive = false;
            rechargeTimer += Time.fixedDeltaTime;
            if (rechargeTimer >= shieldRechargeDelay)
                shieldEnergy = Mathf.Min(shieldMaxEnergy, shieldEnergy + shieldRechargeRate * Time.fixedDeltaTime);
        }
    }

    private void UpdateShieldVisual()
    {
        if (shieldMesh == null || IsGunnerProxy) return;
        bool show = shieldActive && !isDead;
        if (shieldMesh.activeSelf != show)
            shieldMesh.SetActive(show);
        if (show)
            shieldMesh.transform.Rotate(Vector3.up, shieldRotationSpeed * Time.deltaTime, Space.World);
    }

    [Server]
    public float AbsorbShieldDamage(float amount)
    {
        if (!shieldActive || shieldEnergy <= 0f)
            return amount;
        shieldEnergy -= amount;
        if (shieldEnergy < 0f)
        {
            float overflow = Mathf.Abs(shieldEnergy);
            shieldEnergy = 0f;
            shieldActive = false;
            return overflow;
        }
        return 0f;
    }

    // ---------- Weapons ----------

    [Command]
    private void CmdFire()
    {
        ServerFire();
    }

    [Server]
    private void ServerFire()
    {
        if (IsGunnerProxy) return; // proxies never fire their own guns
        if (firePoints == null || firePoints.Length == 0 || projectilePrefab == null || currentAmmo <= 0) return;
        if (fireCooldown > 0) return;

        Transform firePoint = firePoints[currentFirePointIndex];

        // Pilot-aimed nose guns normally; gunner-aimed when a gunner is aboard
        // and sent a valid aim direction from their own camera.
        Vector3 shootDirection;
        if (hasGunner && gunnerAimDir.sqrMagnitude > 0.001f)
            shootDirection = gunnerAimDir.normalized;
        else if (cameraForward.sqrMagnitude > 0.001f)
            shootDirection = cameraForward;
        else
            shootDirection = firePoint.forward;

        // Subtle server-side aim assist for the crew's own bolts only. Enemy
        // fire goes through EnemyShip/CapitalShip/Turret Fire() and is never
        // assisted, so the player still has to do the aiming.
        shootDirection = ApplyAimAssist(shootDirection, firePoint.position);

        // Spawn clear of the hull collider (and of the shield bubble) so the
        // bolt never starts life overlapping the ship that fired it.
        Vector3 spawnPosition = firePoint.position + shootDirection * muzzleSpawnOffset;
        GameObject projectile = Instantiate(projectilePrefab, spawnPosition, Quaternion.LookRotation(shootDirection));

        Projectile proj = projectile.GetComponent<Projectile>();
        if (proj != null)
        {
            proj.Launch(muzzleVelocity, rb.linearVelocity, shootDirection, transform);
        }

        NetworkServer.Spawn(projectile);

        currentAmmo--;
        fireCooldown = 1f / fireRate;
        currentFirePointIndex = (currentFirePointIndex + 1) % firePoints.Length;
    }

    /// <summary>
    /// Server-only aim assist: nudges a player-fired bolt toward the enemy that
    /// is already closest to the raw shot. Only enemies inside
    /// <see cref="aimAssistMaxAngle"/> of the raw direction and inside
    /// <see cref="aimAssistRange"/> of the muzzle count; the smallest angle
    /// wins. Inside the tight snap cone the bolt points exactly at the target,
    /// outside it the correction fades to zero at the max angle, so a shot
    /// aimed well off target (or past max range) is left alone. This runs at
    /// most once per shot (~fireRate/s), so the FindObjectsByType search is
    /// well within budget; nothing here runs per frame.
    /// </summary>
    [Server]
    private Vector3 ApplyAimAssist(Vector3 shootDirection, Vector3 muzzlePosition)
    {
        if (!aimAssistEnabled)
            return shootDirection;

        Transform bestTarget = null;
        float bestAngle = float.MaxValue;

        void Consider(Transform candidate)
        {
            if (candidate == null) return;

            Vector3 toTarget = candidate.position - muzzlePosition;
            float distance = toTarget.magnitude;
            if (distance < 0.01f || distance > aimAssistRange) return;

            float angle = Vector3.Angle(shootDirection, toTarget);
            if (angle > aimAssistMaxAngle || angle >= bestAngle) return;

            bestAngle = angle;
            bestTarget = candidate;
        }

        foreach (EnemyShip enemy in FindObjectsByType<EnemyShip>(FindObjectsSortMode.None))
        {
            if (enemy != null && enemy.Health > 0)
                Consider(enemy.transform);
        }
        foreach (CapitalShip capital in FindObjectsByType<CapitalShip>(FindObjectsSortMode.None))
        {
            if (capital != null && capital.Health > 0)
                Consider(capital.transform);
        }
        if (aimAssistIncludeDeathStar)
        {
            foreach (DeathStar deathStar in FindObjectsByType<DeathStar>(FindObjectsSortMode.None))
            {
                // Its centre is the right aim point even though the hull is a
                // sphere; skipped by default so the trench/exhaust-port run
                // still has to be flown and aimed by hand.
                if (deathStar != null && !deathStar.IsDestroyed)
                    Consider(deathStar.transform);
            }
        }

        if (bestTarget == null)
            return shootDirection;

        // InverseLerp(max, snap, angle) is 1 at the snap angle and 0 at the
        // max angle, so "full snap" and "partial rotate" meet continuously
        // instead of stepping at the cone boundary.
        float strength = bestAngle <= aimAssistSnapAngle
            ? 1f
            : Mathf.InverseLerp(aimAssistMaxAngle, aimAssistSnapAngle, bestAngle);

        Vector3 assisted = Vector3.Slerp(shootDirection, (bestTarget.position - muzzlePosition).normalized, strength);
        return assisted.sqrMagnitude > 0.001f ? assisted.normalized : shootDirection;
    }

    [Command]
    private void CmdStartReload()
    {
        if (IsGunnerProxy) return;
        if (isReloading || currentAmmo >= maxAmmo) return;
        StartCoroutine(ServerReload());
    }

    private System.Collections.IEnumerator ServerReload()
    {
        isReloading = true;
        reloadProgress = 0f;

        float elapsed = 0f;
        while (elapsed < reloadTime)
        {
            elapsed += Time.deltaTime;
            reloadProgress = elapsed / reloadTime;
            yield return null;
        }

        currentAmmo = maxAmmo;
        isReloading = false;
        reloadProgress = 0f;
    }

    // ---------- Damage ----------

    [Server]
    public void TakeDamage(float amount)
    {
        if (isDead || IsGunnerProxy) return;

        float remaining = AbsorbShieldDamage(amount);
        if (remaining <= 0f)
            return;

        currentHealth -= remaining;
        currentHealth = Mathf.Max(0, currentHealth);

        if (currentHealth <= 0f)
        {
            Die();
        }
    }

    private void Die()
    {
        isDead = true;
        shieldActive = false;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        enabled = false;

        RpcOnDeath();
    }

    [ClientRpc]
    private void RpcOnDeath()
    {
        if (!isLocalPlayer) return;
        StartCoroutine(DeathSequence());
    }

    private System.Collections.IEnumerator DeathSequence()
    {
        yield return new WaitForSeconds(deathDelay);

        var manager = NetworkManager.singleton;
        if (manager == null) yield break;

        // A host has to stop the whole session, otherwise the server keeps the
        // port bound and the next HOST press would fail.
        if (NetworkServer.active)
            manager.StopHost();
        else
            manager.StopClient();
    }

    private void OnHealthChanged(float oldValue, float newValue)
    {
    }

    private void OnAmmoChanged(int oldValue, int newValue)
    {
    }

    public Vector3 ShipVelocity => rb != null ? rb.linearVelocity : Vector3.zero;
}
