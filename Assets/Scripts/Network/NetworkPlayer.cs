using UnityEngine;
using Mirror;
using UnityEngine.InputSystem;

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
    [Tooltip("15 m/s^2 at full throttle, applied as ForceMode.Acceleration so hull mass or collider size never change the felt response")]
    [SerializeField] private float thrustAccel = 15f;
    [SerializeField] private float boostMultiplier = 2.2f;
    [SerializeField] private float boostDuration = 3f;
    [SerializeField] private float boostCooldown = 5f;

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

    [Header("Health")]
    [SerializeField] private float maxHealth = 100f;
    [SerializeField] private float deathDelay = 2f;

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
    public bool IsReloading => isReloading;
    public float ReloadProgress => reloadProgress;
    public float HealthNormalized => maxHealth > 0 ? currentHealth / maxHealth : 0f;
    public float ShieldNormalized => shieldMaxEnergy > 0 ? shieldEnergy / shieldMaxEnergy : 0f;
    public bool IsShieldActive => shieldActive;
    public bool IsDead => isDead;
    public bool IsGunnerProxy => crewRole == CrewRole.Gunner;

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
    private bool flightAssist = true;
    private Vector3 cameraForward;

    // Gunner inputs (forwarded from the gunner's proxy object on the server).
    private bool gunnerFiring;
    private bool gunnerShieldHeld;
    private Vector3 gunnerAimDir;
    private bool gunnerReloadRequested;

    private InputActionMap _shipMap;
    private InputActionMap _gunnerMap;
    private InputAction _lookAction;
    private InputAction _thrustAction;
    private InputAction _strafeAction;
    private InputAction _rollAction;
    private InputAction _fireAction;
    private InputAction _boostAction;
    private InputAction _shieldAction;
    private InputAction _flightAssistAction;
    private InputAction _gunnerFireAction;
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
                _shieldAction = _shipMap.FindAction("Shield");
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
                ReadGunnerInput();
                FollowCrewShip();
            }
            return;
        }

        if (!isLocalPlayer || isDead) return;

        if (!isReloading && currentAmmo == 0)
        {
            CmdStartReload();
        }

        ProcessInput();
        UpdateShieldVisual();
    }

    private void LateUpdate()
    {
        if (!IsGunnerProxy && isLocalPlayer)
            UpdateShieldVisual();
    }

    // ---------- Local input ----------

    private void ProcessInput()
    {
        if (_lookAction != null)
            lookInput += _lookAction.ReadValue<Vector2>();
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

        if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame && currentAmmo > 0 && !isReloading)
        {
            CmdStartReload();
        }

        if (Camera.main != null)
            cameraForward = Camera.main.transform.forward;
        else
            cameraForward = transform.forward;
    }

    private void ReadGunnerInput()
    {
        bool firing = false;
        bool shield = false;
        bool reload = false;

        if (_gunnerFireAction != null)
            firing = _gunnerFireAction.IsInProgress();
        else if (_fireAction != null)
            firing = _fireAction.IsInProgress();

        if (_gunnerShieldAction != null)
            shield = _gunnerShieldAction.IsInProgress();
        else if (_shieldAction != null)
            shield = _shieldAction.IsInProgress();

        if (_gunnerReloadAction != null && _gunnerReloadAction.WasPressedThisFrame())
            reload = true;
        if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
            reload = true;

        Vector3 aimDir;
        if (Camera.main != null)
            aimDir = Camera.main.transform.forward;
        else
            aimDir = transform.forward;

        gunnerFiring = firing;
        gunnerShieldHeld = shield;
        gunnerAimDir = aimDir;

        CmdSendGunnerInput(firing, shield, aimDir, reload);

        if (reload && currentAmmo > 0 && !isReloading)
            CmdRequestGunnerReload();
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
        transform.position = crewShip.transform.position;
        transform.rotation = crewShip.transform.rotation;
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

    private NetworkPlayer FindCrewShip()
    {
        NetworkPlayer[] ships = FindObjectsByType<NetworkPlayer>(FindObjectsSortMode.None);
        foreach (NetworkPlayer s in ships)
        {
            if (s == this) continue;
            if (s.crewId == crewId && !s.IsGunnerProxy)
                return s;
        }
        return null;
    }

    private void SendInputsToServer()
    {
        CmdUpdateInput(lookInput, thrustInput, yawInput, rollInput, pilotFiring, pilotShieldHeld, isBoostingInput, flightAssist, cameraForward);
    }

    [Command]
    private void CmdUpdateInput(Vector2 look, float thrust, float yaw, float roll, bool firing, bool shieldHeld, bool boosting, bool assist, Vector3 camForward)
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
        // bar cannot drain while the pilot is off the throttle.
        isBoosting = isBoostingInput && IsBoostReady && Mathf.Abs(thrustInput) > 0.01f;

        float currentAccel = isBoosting ? thrustAccel * boostMultiplier : thrustAccel;
        rb.AddRelativeForce(Vector3.forward * (thrustInput * currentAccel), ForceMode.Acceleration);

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
        NetworkManager.singleton.StopClient();
    }

    private void OnHealthChanged(float oldValue, float newValue)
    {
    }

    private void OnAmmoChanged(int oldValue, int newValue)
    {
    }

    public Vector3 ShipVelocity => rb != null ? rb.linearVelocity : Vector3.zero;
}
