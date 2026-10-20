using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace BattleSky.Network
{
    /// <summary>
    /// Capital ship for Level 2+. Large, high-health vessel that spawns
    /// fighter drones and fires heavy bolts at the nearest player.
    /// All capital ships must be destroyed to advance past Level 2.
    ///
    /// Visuals: the prefab only carries a placeholder cube, so the real hull
    /// is built from primitives at runtime (see <see cref="BuildVisualRig"/>).
    /// The rig is built identically on the server and on every client and it
    /// is sized to match the BoxCollider footprint: local 3 x 2 x 6 on a root
    /// that is scaled 4x, i.e. 12 x 8 x 24 m in world space.
    ///
    /// Damage model: the shield soaks damage first and recharges after a
    /// delay, whatever gets through damages the hull, and hull damage at
    /// zero kills the ship. The server owns all of it.
    /// </summary>
    public class CapitalShip : NetworkBehaviour
    {
        [Header("Flight")]
        [SerializeField] private float _thrust = 6f;
        [SerializeField] private float _turnRate = 3f;
        [SerializeField] private float _maxSpeed = 16f;

        [Header("Combat")]
        [SerializeField] private int _maxHealth = 900;
        [SerializeField] private int _health = 900;
        [SerializeField] private float _fireRate = 0.7f;
        [SerializeField] private float _muzzleVelocity = 700f;
        [SerializeField] private GameObject _bulletPrefab;
        [Tooltip("Metres in front of the fire point where a bolt is spawned, so it never starts inside the hull.")]
        [SerializeField] private float _muzzleClearance = 8f;

        [Header("Shields")]
        [Tooltip("Maximum shield charge. The shield soaks damage before the hull does.")]
        [SerializeField] private float _shieldHealth = 400f;
        [Tooltip("Shield points restored per second once the recharge delay has passed.")]
        [SerializeField] private float _shieldRechargeRate = 25f;
        [Tooltip("Seconds without damage before the shield starts recharging.")]
        [SerializeField] private float _shieldRechargeDelay = 4f;

        [Header("Fighters")]
        [SerializeField] private GameObject _fighterPrefab;
        [SerializeField] private int _fighterCount = 3;
        [SerializeField] private float _fighterSpawnInterval = 15f;
        [Tooltip("Metres the fighters are launched away from the centre of the hull.")]
        [SerializeField] private float _fighterLaunchDistance = 22f;
        [Tooltip("Metres above the hull the fighters are launched at.")]
        [SerializeField] private float _fighterLaunchHeight = 6f;

        [Header("Destruction")]
        [SerializeField] private GameObject _explosionPrefab;
        [SerializeField] private int _explosionCount = 12;
        [SerializeField] private float _explosionRadius = 3f;

        [Header("Visuals")]
        [Tooltip("Material used for the runtime built hull.")]
        [SerializeField] private Material _hullMaterial;
        [Tooltip("Material cloned for the engine glow. Falls back to the hull material.")]
        [SerializeField] private Material _engineGlowMaterial;
        [Tooltip("Left battery. Found by name when left empty.")]
        [SerializeField] private Transform _firePointLeft;
        [Tooltip("Right battery. Found by name when left empty.")]
        [SerializeField] private Transform _firePointRight;

        // --- Replicated state -------------------------------------------------
        // Mirror supports private SyncVars. The hook lets clients flash the
        // shield bubble the moment it soaks a hit.
        [SyncVar(hook = nameof(OnShieldChanged))]
        private float _shield;

        // The hull stays server authoritative (_health), this is only the
        // client side copy so HealthNormalized means something on a client.
        [SyncVar]
        private float _replicatedHealth;

        private static readonly Color ShieldColorLow = new Color(0.10f, 0.35f, 0.85f, 0.35f);
        private static readonly Color ShieldColorHigh = new Color(0.35f, 0.80f, 1.00f, 0.85f);
        private static readonly Color ShieldColorFlash = new Color(1.60f, 2.20f, 2.60f, 1.00f);
        private static readonly Color GlowColorBase = new Color(1.00f, 0.55f, 0.15f, 1.00f);
        private static readonly Color GlowEmissionLow = new Color(0.80f, 0.25f, 0.05f);
        private static readonly Color GlowEmissionHigh = new Color(3.00f, 1.20f, 0.30f);

        private Rigidbody _rb;
        private Transform _target;
        private float _fireTimer;
        private float _fighterTimer;
        private int _fightersAlive;
        private int _fighterLaunchIndex;
        private int _firePointIndex;
        private bool _dying;

        // shield bookkeeping (server)
        private float _shieldHitTime = -999f;
        private float _shieldRechargeBuffer;

        // visuals
        private bool _visualRigBuilt;
        private Transform _shieldVisual;
        private Renderer _shieldRenderer;
        private Vector3 _shieldBaseScale = Vector3.one;
        private float _shieldFlash;
        private readonly List<Renderer> _engineGlowRenderers = new List<Renderer>();
        private readonly List<Vector3> _engineGlowBaseScales = new List<Vector3>();
        private readonly List<Material> _runtimeMaterials = new List<Material>();
        private MaterialPropertyBlock _mpb;
        private Vector3 _lastPosition;
        private bool _hasLastPosition;

        /// <summary>Hull health as it is on the server (0 once the ship died).</summary>
        public int Health => _health;

        /// <summary>Shield charge between 0 and 1.</summary>
        public float ShieldNormalized => _shieldHealth > 0f ? Mathf.Clamp01(_shield / _shieldHealth) : 0f;

        /// <summary>Hull health between 0 and 1.</summary>
        public float HealthNormalized
        {
            get
            {
                int max = _maxHealth > 0 ? _maxHealth : 1;
                float current = isServer ? _health : _replicatedHealth;
                return Mathf.Clamp01(current / (float)max);
            }
        }

        /// <summary>True while the shield still has charge to soak damage.</summary>
        public bool IsShieldActive => _shield > 0f;

        private void Awake()
        {
            _mpb = new MaterialPropertyBlock();
            _shield = Mathf.Max(0f, _shieldHealth);
            _replicatedHealth = Mathf.Max(0, _health);
            BuildVisualRig();
        }

        public override void OnStartServer()
        {
            _rb = GetComponent<Rigidbody>();
            PickTarget();
            BuildVisualRig();
            ApplyShieldVisual();
        }

        public override void OnStartClient()
        {
            BuildVisualRig();
            ApplyShieldVisual();
        }

        private void OnDestroy()
        {
            // Runtime cloned materials must not leak.
            for (int i = 0; i < _runtimeMaterials.Count; i++)
            {
                if (_runtimeMaterials[i] != null) Destroy(_runtimeMaterials[i]);
            }
            _runtimeMaterials.Clear();
        }

        // ------------------------------------------------------------------
        // Visual rig
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds the capital ship silhouette from primitives. Safe to call
        /// from the server and from every client: the guard makes sure it
        /// only ever runs once per instance, and the code path is identical
        /// everywhere so both sides end up with the same ship.
        /// </summary>
        private void BuildVisualRig()
        {
            if (_visualRigBuilt) return;
            _visualRigBuilt = true;

            // The prefab's single cube is a placeholder - hide it so it does
            // not poke through the real hull.
            MeshRenderer placeholder = GetComponent<MeshRenderer>();
            if (placeholder != null) placeholder.enabled = false;

            Material hullMaterial = ResolveHullMaterial();

            GameObject hullRoot = new GameObject("Hull");
            hullRoot.transform.SetParent(transform, false);
            hullRoot.transform.localPosition = Vector3.zero;
            hullRoot.transform.localRotation = Quaternion.identity;
            hullRoot.transform.localScale = Vector3.one;

            // Main hull: exactly the BoxCollider footprint (local 3 x 2 x 6).
            CreatePart(hullRoot.transform, "Hull_Main", PrimitiveType.Cube,
                new Vector3(0f, 0f, 0f), new Vector3(3f, 2f, 6f), hullMaterial);
            // Tapered bow.
            CreatePart(hullRoot.transform, "Hull_Prow", PrimitiveType.Cube,
                new Vector3(0f, 0f, 2.3f), new Vector3(2.1f, 1.3f, 1.4f), hullMaterial);
            // Bridge / command tower.
            CreatePart(hullRoot.transform, "Hull_Bridge", PrimitiveType.Cube,
                new Vector3(0f, 1.25f, 0.3f), new Vector3(1.7f, 0.9f, 2.2f), hullMaterial);
            CreatePart(hullRoot.transform, "Hull_BridgeTop", PrimitiveType.Cube,
                new Vector3(0f, 1.9f, 0.3f), new Vector3(0.9f, 0.5f, 1.1f), hullMaterial);
            // Engine pods at the rear.
            CreatePart(hullRoot.transform, "Hull_EnginePod_L", PrimitiveType.Cube,
                new Vector3(-1.1f, -0.1f, -2.2f), new Vector3(0.7f, 1.3f, 1.6f), hullMaterial);
            CreatePart(hullRoot.transform, "Hull_EnginePod_R", PrimitiveType.Cube,
                new Vector3(1.1f, -0.1f, -2.2f), new Vector3(0.7f, 1.3f, 1.6f), hullMaterial);
            // Side sponsons.
            CreatePart(hullRoot.transform, "Hull_Sponson_L", PrimitiveType.Cube,
                new Vector3(-1.4f, 0.2f, -0.4f), new Vector3(0.2f, 0.5f, 2.2f), hullMaterial);
            CreatePart(hullRoot.transform, "Hull_Sponson_R", PrimitiveType.Cube,
                new Vector3(1.4f, 0.2f, -0.4f), new Vector3(0.2f, 0.5f, 2.2f), hullMaterial);
            // Ventral gun platform.
            CreatePart(hullRoot.transform, "Hull_GunPlatform", PrimitiveType.Cube,
                new Vector3(0f, -1.05f, 1.1f), new Vector3(1.8f, 0.5f, 2.4f), hullMaterial);
            CreatePart(hullRoot.transform, "Hull_GunTurret", PrimitiveType.Cube,
                new Vector3(0f, -1.4f, 1.5f), new Vector3(0.9f, 0.5f, 0.9f), hullMaterial);

            // Slight tonal separation so the silhouette reads at distance.
            TintPart(hullRoot.transform.Find("Hull_Bridge"), new Color(0.45f, 0.48f, 0.55f));
            TintPart(hullRoot.transform.Find("Hull_BridgeTop"), new Color(0.55f, 0.58f, 0.65f));
            TintPart(hullRoot.transform.Find("Hull_EnginePod_L"), new Color(0.18f, 0.19f, 0.22f));
            TintPart(hullRoot.transform.Find("Hull_EnginePod_R"), new Color(0.18f, 0.19f, 0.22f));
            TintPart(hullRoot.transform.Find("Hull_GunPlatform"), new Color(0.22f, 0.23f, 0.26f));

            // Engine glow nozzles.
            Material glowMaterial = ResolveGlowMaterial();
            if (glowMaterial != null)
            {
                AddEngineGlow(hullRoot.transform, "Engine_Glow_L",
                    new Vector3(-1.1f, -0.1f, -2.95f), new Vector3(0.45f, 0.45f, 0.45f), glowMaterial);
                AddEngineGlow(hullRoot.transform, "Engine_Glow_R",
                    new Vector3(1.1f, -0.1f, -2.95f), new Vector3(0.45f, 0.45f, 0.45f), glowMaterial);
            }

            // Shield bubble: the prefab child keeps its own material.
            _shieldVisual = transform.Find("shield");
            if (_shieldVisual != null)
            {
                _shieldRenderer = _shieldVisual.GetComponent<Renderer>();
                _shieldBaseScale = _shieldVisual.localScale;
            }

            ApplyShieldVisual();
        }

        private GameObject CreatePart(Transform parent, string partName, PrimitiveType type,
            Vector3 localPosition, Vector3 localScale, Material material)
        {
            GameObject part = GameObject.CreatePrimitive(type);
            part.name = partName;

            // The root BoxCollider is the only collider of the ship, so the
            // primitive colliders are dropped (and disabled right away so
            // they never take part in a physics step).
            Collider partCollider = part.GetComponent<Collider>();
            if (partCollider != null)
            {
                partCollider.enabled = false;
                Destroy(partCollider);
            }

            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localRotation = Quaternion.identity;
            part.transform.localScale = localScale;

            Renderer partRenderer = part.GetComponent<Renderer>();
            if (partRenderer != null && material != null) partRenderer.sharedMaterial = material;
            return part;
        }

        private void AddEngineGlow(Transform parent, string partName, Vector3 localPosition,
            Vector3 localScale, Material material)
        {
            GameObject part = CreatePart(parent, partName, PrimitiveType.Sphere, localPosition, localScale, material);
            Renderer partRenderer = part.GetComponent<Renderer>();
            if (partRenderer == null) return;
            _engineGlowRenderers.Add(partRenderer);
            _engineGlowBaseScales.Add(localScale);
        }

        private void TintPart(Transform part, Color color)
        {
            if (part == null) return;
            Renderer partRenderer = part.GetComponent<Renderer>();
            if (partRenderer == null) return;
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            _mpb.Clear();
            _mpb.SetColor("_BaseColor", color);
            _mpb.SetColor("_Color", color);
            partRenderer.SetPropertyBlock(_mpb);
        }

        private Material ResolveHullMaterial()
        {
            if (_hullMaterial != null) return _hullMaterial;

            // Fallback so the ship still looks like a ship if the material
            // reference is missing. This material is runtime owned and gets
            // destroyed in OnDestroy.
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) return null;

            Material fallback = new Material(shader);
            fallback.name = "CapitalShip_Hull";
            fallback.SetColor("_BaseColor", new Color(0.28f, 0.30f, 0.34f, 1f));
            fallback.SetColor("_Color", new Color(0.28f, 0.30f, 0.34f, 1f));
            _runtimeMaterials.Add(fallback);
            return fallback;
        }

        private Material ResolveGlowMaterial()
        {
            Material source = _engineGlowMaterial != null ? _engineGlowMaterial : _hullMaterial;
            if (source == null) return ResolveHullMaterial();

            Material glow = new Material(source);
            glow.name = "CapitalShip_EngineGlow";
            glow.EnableKeyword("_EMISSION");
            glow.SetColor("_BaseColor", GlowColorBase);
            glow.SetColor("_Color", GlowColorBase);
            glow.SetColor("_EmissionColor", GlowEmissionHigh);
            _runtimeMaterials.Add(glow);
            return glow;
        }

        // ------------------------------------------------------------------
        // Flight / combat
        // ------------------------------------------------------------------

        private void PickTarget()
        {
            NetworkPlayer best = null;
            float bestDist = float.MaxValue;
            foreach (var np in Object.FindObjectsByType<NetworkPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                float d = Vector3.Distance(transform.position, np.transform.position);
                if (d < bestDist) { bestDist = d; best = np; }
            }
            _target = best?.transform;
        }

        private void FixedUpdate()
        {
            if (!isServer) return;
            if (_dying) return;

            RechargeShield();

            if (_target == null)
            {
                PickTarget();
                if (_target == null) return;
            }

            Vector3 toTarget = (_target.position - transform.position).normalized;
            _rb.MoveRotation(Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(toTarget), _turnRate * Time.fixedDeltaTime));

            if (Vector3.Dot(transform.forward, toTarget) > 0.3f)
                _rb.AddForce(transform.forward * _thrust, ForceMode.Acceleration);

            if (_rb.linearVelocity.sqrMagnitude > _maxSpeed * _maxSpeed)
                _rb.linearVelocity = _rb.linearVelocity.normalized * _maxSpeed;

            _fireTimer -= Time.fixedDeltaTime;
            if (_fireTimer <= 0f && _bulletPrefab != null)
            {
                _fireTimer = 1f / _fireRate;
                Fire();
            }

            _fighterTimer -= Time.fixedDeltaTime;
            if (_fighterTimer <= 0f && _fighterPrefab != null && _fightersAlive < _fighterCount)
            {
                _fighterTimer = _fighterSpawnInterval;
                SpawnFighter();
            }
        }

        /// <summary>
        /// Shields come back once the ship has not been hit for the recharge
        /// delay. The SyncVar is only written once per whole point so the
        /// network is not flooded with tiny updates.
        /// </summary>
        private void RechargeShield()
        {
            if (_shield >= _shieldHealth) return;
            if (Time.time - _shieldHitTime < _shieldRechargeDelay) return;

            _shieldRechargeBuffer += _shieldRechargeRate * Time.fixedDeltaTime;
            if (_shieldRechargeBuffer < 1f) return;

            float whole = Mathf.Floor(_shieldRechargeBuffer);
            _shieldRechargeBuffer -= whole;

            float before = _shield;
            _shield = Mathf.Min(_shieldHealth, _shield + whole);
            if (!Mathf.Approximately(before, _shield)) ApplyShieldVisual();
        }

        [Server]
        private void Fire()
        {
            if (_bulletPrefab == null) return;

            Vector3 direction = transform.forward;
            Vector3 muzzle = GetMuzzlePosition(direction);

            GameObject bolt = Instantiate(_bulletPrefab, muzzle, Quaternion.LookRotation(direction));
            var proj = bolt.GetComponent<Projectile>();
            if (proj != null)
            {
                Vector3 inherited = _rb != null ? _rb.linearVelocity : Vector3.zero;
                proj.Launch(_muzzleVelocity, inherited, direction, transform);
            }
            NetworkServer.Spawn(bolt);
        }

        /// <summary>
        /// Muzzle position for the next bolt: the left and right battery
        /// alternate, and the bolt always starts clear of the hull so it
        /// never shoots itself.
        /// </summary>
        private Vector3 GetMuzzlePosition(Vector3 direction)
        {
            Transform point = NextFirePoint();
            Vector3 origin = point != null ? point.position : transform.position + direction * 6f;

            float clearance = _muzzleClearance;

            // Make sure the spawn point is beyond the front of the collider.
            Collider hull = GetComponent<Collider>();
            if (hull != null)
            {
                Vector3 extents = hull.bounds.extents;
                float extent = ExtentAlong(extents, transform.forward);
                float current = Vector3.Dot(origin - transform.position, transform.forward);
                clearance = Mathf.Max(clearance, extent + 2f - current);
            }

            return origin + direction * clearance;
        }

        private static float ExtentAlong(Vector3 extents, Vector3 direction)
        {
            return extents.x * Mathf.Abs(direction.x)
                 + extents.y * Mathf.Abs(direction.y)
                 + extents.z * Mathf.Abs(direction.z);
        }

        private Transform NextFirePoint()
        {
            Transform left = _firePointLeft != null ? _firePointLeft : transform.Find("FirePoint_Left");
            Transform right = _firePointRight != null ? _firePointRight : transform.Find("FirePoint_Right");
            if (left == null && right == null) return null;

            bool useLeft = (_firePointIndex % 2) == 0;
            _firePointIndex++;

            Transform chosen = useLeft ? left : right;
            if (chosen == null) chosen = useLeft ? right : left;
            return chosen;
        }

        [Server]
        private void SpawnFighter()
        {
            if (_dying || _health <= 0) return;
            if (_fighterPrefab == null) return;

            // Deterministic launch pattern: every fighter leaves on a
            // different bearing so they never stack up, and they always start
            // clear of the hull (which is roughly 14 m across).
            float bearing = (_fighterLaunchIndex * 60f + 30f) * Mathf.Deg2Rad;
            _fighterLaunchIndex++;
            Vector3 direction = new Vector3(Mathf.Cos(bearing), 0f, Mathf.Sin(bearing));
            Vector3 offset = direction * _fighterLaunchDistance + Vector3.up * _fighterLaunchHeight;
            Quaternion facing = Quaternion.LookRotation(direction, Vector3.up);

            GameObject fighter = Instantiate(_fighterPrefab, transform.position + offset, facing);
            NetworkServer.Spawn(fighter);
            _fightersAlive++;

            var es = fighter.GetComponent<EnemyShip>();
            if (es != null)
            {
                // Hook into fighter death so the capital ship knows a slot freed up.
                // We use a one-shot callback via a small wrapper component.
                fighter.AddComponent<_FighterDeathTracker>().OnDestroyed += () => _fightersAlive--;
            }
        }

        // ------------------------------------------------------------------
        // Damage
        // ------------------------------------------------------------------

        /// <summary>
        /// Server side damage entry point. The shield soaks damage first,
        /// only the overflow damages the hull.
        /// </summary>
        public void TakeDamage(float amount)
        {
            if (!isServer || _dying || _health <= 0) return;
            if (amount <= 0f) return;

            float remaining = amount;

            if (_shield > 0f)
            {
                float absorbed = Mathf.Min(_shield, remaining);
                _shield -= absorbed;
                remaining -= absorbed;
                _shieldFlash = 1f;
            }

            if (remaining > 0f)
            {
                _health -= Mathf.CeilToInt(remaining);
                if (_health < 0) _health = 0;
                _replicatedHealth = _health;
            }

            // Any hit restarts the recharge delay.
            _shieldHitTime = Time.time;
            ApplyShieldVisual();

            if (_health <= 0) Die();
        }

        private void Die()
        {
            if (_dying) return;
            _dying = true;

            if (_shieldRenderer != null) _shieldRenderer.enabled = false;

            if (_explosionPrefab != null)
            {
                for (int i = 0; i < _explosionCount; i++)
                {
                    Vector3 offset = Random.insideUnitSphere * (_explosionRadius * 0.5f);
                    GameObject pop = Instantiate(_explosionPrefab, transform.position + offset, Random.rotation);
                    NetworkServer.Spawn(pop);
                }
            }
            NetworkLevelManager.RegisterEnemyDestroyed();
            NetworkServer.Destroy(gameObject);
        }

        // ------------------------------------------------------------------
        // Per frame visuals
        // ------------------------------------------------------------------

        private void Update()
        {
            if (!_visualRigBuilt) return;

            _shieldFlash = Mathf.MoveTowards(_shieldFlash, 0f, Time.deltaTime * 2.5f);
            ApplyShieldVisual();
            UpdateEngineGlow();
        }

        /// <summary>
        /// Mirror hook. Runs on the client whenever the replicated shield
        /// value changes, and on the host when the server changes it.
        /// </summary>
        private void OnShieldChanged(float oldValue, float newValue)
        {
            if (newValue < oldValue)
            {
                // The shield just soaked a hit, flash it.
                _shieldFlash = 1f;
            }
            ApplyShieldVisual();
        }

        private void ApplyShieldVisual()
        {
            if (_shieldRenderer == null || _shieldVisual == null) return;

            bool active = _shield > 0.01f;
            if (_shieldRenderer.enabled != active) _shieldRenderer.enabled = active;
            if (!active) return;

            float normalized = ShieldNormalized;
            float pulse = 1f + 0.025f * Mathf.Sin(Time.time * 2.4f);
            float flash = Mathf.Clamp01(_shieldFlash);
            _shieldVisual.localScale = _shieldBaseScale * pulse * (1f + 0.08f * flash);

            Color color = Color.Lerp(ShieldColorLow, ShieldColorHigh, normalized);
            color = Color.Lerp(color, ShieldColorFlash, flash);

            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            _mpb.Clear();
            _mpb.SetColor("_Color", color);
            _mpb.SetColor("_BaseColor", color);
            _shieldRenderer.SetPropertyBlock(_mpb);
        }

        private void UpdateEngineGlow()
        {
            if (_engineGlowRenderers.Count == 0) return;

            Vector3 velocity = EstimateVelocity();
            float forwardSpeed = Vector3.Dot(velocity, transform.forward);
            float intensity = Mathf.Clamp01(forwardSpeed / Mathf.Max(1f, _maxSpeed));

            Color emission = Color.Lerp(GlowEmissionLow, GlowEmissionHigh, intensity);
            float scale = Mathf.Lerp(0.75f, 1.15f, intensity);

            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            _mpb.Clear();
            _mpb.SetColor("_EmissionColor", emission);
            _mpb.SetColor("_BaseColor", GlowColorBase);

            for (int i = 0; i < _engineGlowRenderers.Count; i++)
            {
                Renderer glow = _engineGlowRenderers[i];
                if (glow == null) continue;
                glow.SetPropertyBlock(_mpb);
                glow.transform.localScale = _engineGlowBaseScales[i] * scale;
            }
        }

        /// <summary>
        /// Speed of the ship. On the server that is the rigidbody velocity,
        /// on a client it is derived from the replicated movement, because
        /// the client side rigidbody does not simulate.
        /// </summary>
        private Vector3 EstimateVelocity()
        {
            Vector3 velocity = Vector3.zero;
            if (isServer && _rb != null)
            {
                velocity = _rb.linearVelocity;
            }
            else if (_hasLastPosition)
            {
                float dt = Time.deltaTime;
                if (dt > 0.0001f) velocity = (transform.position - _lastPosition) / dt;
            }

            _lastPosition = transform.position;
            _hasLastPosition = true;
            return velocity;
        }
    }

    public class _FighterDeathTracker : MonoBehaviour
    {
        public System.Action OnDestroyed;
        private void OnDestroy() => OnDestroyed?.Invoke();
    }
}
