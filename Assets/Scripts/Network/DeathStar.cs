using Mirror;
using UnityEngine;

namespace BattleSky.Network
{
    /// <summary>
    /// The phases of the Death Star boss fight. The fight has three stages.
    /// </summary>
    public enum DeathStarStage
    {
        /// <summary>Stage 1: a white force field wraps the station: the hull is immune.</summary>
        ForceField = 0,
        /// <summary>Stage 2: the field is down and a flight of escorts hunts the crew.</summary>
        Fighters = 1,
        /// <summary>Stage 3: the heavy escort swarm is on the crew and never stops coming.</summary>
        HeavyFighters = 2
    }

    /// <summary>
    /// Level 3 boss: Death Star. A massive spherical battle station that
    /// fights in three stages:
    /// - ForceField: a white field wraps the station. The hull is immune and
    ///   the crew has to break the field with their bolts
    /// - Fighters: the field is down, the first escort wave is on the crew.
    ///   The escorts chase the crew and shoot at them
    /// - Swarm: the heavy escort wave is on the crew and it never stops
    ///   coming: the crew has to grind the hull down while the swarm is on
    ///   them
    ///
    /// The station is a boss, not a target: the armour eats most of the
    /// incoming damage, so the crew has to break the field first and then
    /// keep the bolts on the hull until it gives out.
    ///
    /// Every hand over between the stages is telegraphed by the superlaser:
    /// the beam charges, fires, and only then does the next stage start.
    /// The station never moves, so it needs no NetworkTransform: Mirror sends
    /// the spawn rotation once, which is enough for the dish to face the
    /// crew.
    /// </summary>
    public class DeathStar : NetworkBehaviour
    {
        /// <summary>The hull never drops below this integrity, even with an older prefab.</summary>
        private const int MinSurfaceHealth = 1200;

        /// <summary>The hull never takes more than this share of the incoming damage.</summary>
        private const float MaxSurfaceDamageMultiplier = 0.25f;

        /// <summary>
        /// How long the station stays in the scene after it dies. The electric
        /// death effect plays for about a second and a half, and the clients
        /// drop their copy of the station as soon as the server despawns it,
        /// so the station has to stay until the burst is over.
        /// </summary>
        private const float DeathDestroyDelay = 2f;

        [Header("Death Star")]
        [SerializeField] private float _radius = 50f;
        [Tooltip("Hull integrity. The armour eats most of the damage, so the crew has to keep the fire up")]
        [SerializeField] private int _maxSurfaceHealth = 1200;
        [Tooltip("Armour multiplier: the hull only takes a quarter of the incoming damage")]
        [SerializeField] private float _surfaceDamageMultiplier = 0.25f;

        [Header("Force Field")]
        [Tooltip("Integrity of the white force field that shields the station in the first stage")]
        [SerializeField] private float _forceFieldMaxHealth = 900f;
        [Tooltip("Field radius relative to the hull radius. 1.06 puts the shell 60 m outside the 1000 m hull, so the bolts always hit the field first")]
        [SerializeField] private float _forceFieldRadius = 1.06f;
        [Tooltip("Material of the white force field shell. It is cloned at runtime so the shared asset is never modified. Falls back to a runtime white material when left empty")]
        [SerializeField] private Material _forceFieldMaterial;

        [Header("Superlaser")]
        [SerializeField] private float _superlaserChargeTime = 9f;
        [SerializeField] private float _superlaserWarningTime = 3.5f;
        [SerializeField] private float _superlaserDamage = 75f;
        [Tooltip("Radius of the visible beam in metres")]
        [SerializeField] private float _superlaserBeamRadius = 8f;
        [Tooltip("Radius in which a ship takes damage. Keep it close to the beam radius so a hit never feels unfair")]
        [SerializeField] private float _superlaserDamageRadius = 10f;
        [SerializeField] private float _superlaserRange = 900f;
        [SerializeField] private GameObject _superlaserBeamPrefab;
        [SerializeField] private Transform _superlaserDish;

        [Header("Destruction")]
        [SerializeField] private GameObject _explosionPrefab;
        [SerializeField] private int _explosionCount = 36;
        [SerializeField] private float _explosionRadius = 8f;

        /// <summary>Replicated hull integrity so every client can show a bar.</summary>
        [SyncVar]
        private int _surfaceHealth;

        /// <summary>Replicated fight stage. The HUD and the enemy spawner read it.</summary>
        [SyncVar]
        private DeathStarStage _stage;

        /// <summary>Replicated force field integrity, so the clients can show a shield bar.</summary>
        [SyncVar]
        private float _forceFieldHealth;

        private static DeathStar _active;
        private static Shader _glowShader;
        private static Shader _electricShader;
        private float _superlaserTimer;
        private bool _superlaserCharging;
        private bool _superlaserQueued;
        private Transform _lockedTarget;
        private Vector3 _lockOrigin;
        private Vector3 _lockDirection = Vector3.forward;
        private bool _isDestroyed;
        private bool _oriented;
        private bool _forceFieldBroken;

        private GameObject _forceField;

        /// <summary>The material the force field shell actually renders with.</summary>
        private Material _forceFieldShellMaterial;
        private readonly System.Collections.Generic.List<Material> _runtimeMaterials =
            new System.Collections.Generic.List<Material>();

        /// <summary>The Death Star that is currently in the world (null when none).</summary>
        public static DeathStar Active => _active;

        public float Radius => _radius;
        public bool IsDestroyed => _isDestroyed;
        public float HealthNormalized => EffectiveSurfaceHealth > 0
            ? Mathf.Clamp01((float)_surfaceHealth / EffectiveSurfaceHealth)
            : 0f;
        public bool IsChargingSuperlaser => _superlaserCharging;
        public float SuperlaserChargeNormalized => _superlaserCharging && _superlaserWarningTime > 0f
            ? Mathf.Clamp01(1f - _superlaserTimer / _superlaserWarningTime)
            : 0f;

        /// <summary>The phase the boss fight is in.</summary>
        public DeathStarStage Stage => _stage;

        /// <summary>True while the white force field still shields the hull.</summary>
        public bool IsForceFieldActive => _forceFieldHealth > 0f;

        /// <summary>Force field integrity in the 0..1 range, for the HUD shield bar.</summary>
        public float ForceFieldNormalized => _forceFieldMaxHealth > 0f
            ? Mathf.Clamp01(_forceFieldHealth / _forceFieldMaxHealth)
            : 0f;

        /// <summary>How many fighters the current stage wants (0 when no wave is due).</summary>
        public int WaveQuota
        {
            get
            {
                switch (_stage)
                {
                    case DeathStarStage.Fighters: return 8;
                    case DeathStarStage.HeavyFighters: return 16;
                    default: return 0;
                }
            }
        }

        /// <summary>
        /// True while the current stage keeps sending fighters. The last stage
        /// never ends, so the swarm is topped up instead of running dry.
        /// </summary>
        public bool WaveRepeats => _stage == DeathStarStage.HeavyFighters;

        /// <summary>
        /// True while the current stage asks for the heavy escorts. The heavy
        /// wave is tougher and fires faster than the first one.
        /// </summary>
        public bool WantsHeavyFighters => _stage == DeathStarStage.HeavyFighters;

        /// <summary>
        /// How hard the escorts should hunt the crew. Zero while the force field
        /// holds, full aggression from the second stage on, and the very top in the
        /// last stage where the swarm never stops.
        /// </summary>
        public float Aggression
        {
            get
            {
                switch (_stage)
                {
                    case DeathStarStage.ForceField: return 0f;
                    case DeathStarStage.Fighters: return 0.8f;
                    case DeathStarStage.HeavyFighters: return 1f;
                    default: return 0f;
                }
            }
        }

        /// <summary>
        /// The hull pool the station actually fights with. The floor keeps the
        /// boss tough even when an older prefab still carries the soft
        /// numbers, and every peer uses the same value, so the HUD bar is
        /// identical on the server and on the clients.
        /// </summary>
        private int EffectiveSurfaceHealth => Mathf.Max(_maxSurfaceHealth, MinSurfaceHealth);

        /// <summary>
        /// The armour factor the station actually fights with: the hull eats
        /// most of the incoming damage, so the crew has to keep the fire up
        /// for a long time.
        /// </summary>
        private float EffectiveSurfaceDamageMultiplier =>
            Mathf.Min(_surfaceDamageMultiplier, MaxSurfaceDamageMultiplier);

        public override void OnStartServer()
        {
            if (_active == null) _active = this;

            // The prefab is authored at _radius scale so clients see the same
            // size without needing a NetworkTransform (the station never moves).
            var rb = GetComponent<Rigidbody>();
            if (rb == null) rb = gameObject.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.isKinematic = true; // Death Star doesn't move
            rb.mass = 1000000f;

            var col = GetComponent<SphereCollider>();
            if (col == null) col = gameObject.AddComponent<SphereCollider>();
            // The built-in sphere mesh has a 0.5 m radius, so the collider
            // radius stays 0.5 and the transform scale supplies the real size.
            col.radius = 0.5f;

            _surfaceHealth = EffectiveSurfaceHealth;
            _forceFieldHealth = _forceFieldMaxHealth;
            _stage = DeathStarStage.ForceField;
            _superlaserTimer = _superlaserChargeTime;

            BuildForceFieldVisual();
            OrientTowardsCrew();
        }

        public override void OnStartClient()
        {
            if (_active == null) _active = this;
            BuildForceFieldVisual();
        }

        public override void OnStopClient()
        {
            if (_active == this) _active = null;
        }

        public override void OnStopServer()
        {
            if (_active == this) _active = null;
        }

        private void OnDestroy()
        {
            if (_active == this) _active = null;

            for (int i = 0; i < _runtimeMaterials.Count; i++)
            {
                if (_runtimeMaterials[i] != null) Destroy(_runtimeMaterials[i]);
            }
            _runtimeMaterials.Clear();
        }

        // ------------------------------------------------------------------
        // Server logic
        // ------------------------------------------------------------------

        private void Update()
        {
            AnimateForceField();

            if (!isServer || _isDestroyed) return;

            if (!_oriented)
                OrientTowardsCrew();

            UpdateSuperlaser();
        }

        /// <summary>
        /// The white shell breathes: a slow pulse of the colour keeps the
        /// field alive on every peer without touching the collider, so the
        /// bolts always meet the same shell. The dedicated material is an HDR
        /// _Color without an _EmissionColor, so the pulse drives _Color (and
        /// _EmissionColor when the shell has one). The shell stays white.
        /// </summary>
        private void AnimateForceField()
        {
            Material material = _forceFieldShellMaterial;
            if (material == null) return;
            bool hasEmission = material.HasProperty("_EmissionColor");
            bool hasColor = material.HasProperty("_Color");
            if (!hasEmission && !hasColor) return;

            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 1.6f);
            float strength = Mathf.Lerp(0.7f, 1.15f, pulse);

            if (hasEmission)
            {
                Color emission = material.GetColor("_EmissionColor");
                emission.r = emission.g = emission.b = Mathf.Max(1f, emission.r) * strength;
                material.SetColor("_EmissionColor", emission);
            }

            if (hasColor)
            {
                // Pulse AROUND the material's own colour: the alpha of the
                // shell comes from the material and has to survive, otherwise
                // the field turns into an opaque ball that hides the station.
                Color color = material.GetColor("_Color");
                float baseBrightness = Mathf.Max(0.001f, Mathf.Max(color.r, Mathf.Max(color.g, color.b)));
                float scale = baseBrightness * strength;
                material.SetColor("_Color", new Color(
                    color.r / baseBrightness * scale,
                    color.g / baseBrightness * scale,
                    color.b / baseBrightness * scale,
                    color.a));
            }
        }

        /// <summary>
        /// Points the station's +Z face (where the dish lives) at the crew,
        /// so the superlaser is always aimed from the front. Runs before the
        /// spawn message is sent, so the rotation reaches the clients with
        /// the spawn payload.
        /// </summary>
        private void OrientTowardsCrew()
        {
            NetworkPlayer player = FindNearestPlayer();
            if (player == null) return;

            Vector3 toPlayer = player.transform.position - transform.position;
            if (toPlayer.sqrMagnitude < 1f) return;

            transform.rotation = Quaternion.LookRotation(toPlayer.normalized, Vector3.up);
            _oriented = true;
        }

        private void UpdateSuperlaser()
        {
            _superlaserTimer -= Time.deltaTime;

            if (!_superlaserCharging)
            {
                // A queued volley goes out right away: a stage transition is
                // waiting for it, so the fight does not idle between the
                // waves.
                if (_superlaserQueued || _superlaserTimer <= 0f)
                {
                    // Lock the current target and publish the aiming line, so
                    // the crew has the whole warning window to break the line.
                    NetworkPlayer locked = FindNearestPlayer();
                    _lockedTarget = locked != null ? locked.transform : null;
                    _lockOrigin = DishPosition();
                    _lockDirection = _lockedTarget != null
                        ? (_lockedTarget.position - _lockOrigin).normalized
                        : transform.forward;

                    _superlaserCharging = true;
                    _superlaserTimer = _superlaserWarningTime;
                    RpcSuperlaserWarning(_lockOrigin, _lockDirection);
                    Debug.Log("[DeathStar] Superlaser charging - break the targeting line!");
                }
                return;
            }

            if (_superlaserTimer <= 0f)
            {
                FireSuperlaser();
                _superlaserCharging = false;
                _superlaserTimer = CurrentChargeTime;

                // The volley that ends a stage hands over to the next one.
                if (_superlaserQueued)
                {
                    _superlaserQueued = false;
                    AdvanceStage();
                }
            }
        }

        /// <summary>
        /// How long the station waits between two beams. The last stage is
        /// the hottest one: the swarm is on the crew and the station fires
        /// the beam more often there.
        /// </summary>
        private float CurrentChargeTime => _stage == DeathStarStage.HeavyFighters
            ? _superlaserChargeTime * 0.55f
            : _superlaserChargeTime;

        [Server]
        private void FireSuperlaser()
        {
            Vector3 origin = _lockOrigin;
            Vector3 direction = _lockDirection.sqrMagnitude > 0.001f ? _lockDirection : transform.forward;

            // Visible beam on every client (the server never simulates it).
            float length = _superlaserRange;
            RpcSuperlaserFired(origin, direction, length);

            // Damage: only ships that are still sitting on the beam line.
            foreach (NetworkPlayer player in FindObjectsByType<NetworkPlayer>(FindObjectsSortMode.None))
            {
                if (player == null) continue;
                Vector3 toPlayer = player.transform.position - origin;
                float along = Vector3.Dot(toPlayer, direction);
                if (along < 0f || along > length) continue;

                float distance = Vector3.Distance(player.transform.position, origin + direction * along);
                if (distance <= _superlaserDamageRadius)
                    player.TakeDamage(_superlaserDamage);
            }
        }

        /// <summary>
        /// Asks for the superlaser volley that hands the fight over to the
        /// next stage. The stage itself only flips when the beam fires, so
        /// the next wave never starts before the telegraph.
        /// </summary>
        private void RequestStageAdvance()
        {
            _superlaserQueued = true;
            // A volley that is already charging keeps its own warning window,
            // an idle station fires the queued volley straight away.
            if (!_superlaserCharging) _superlaserTimer = _superlaserWarningTime;
            Debug.Log("[DeathStar] Stage transition queued: " + _stage + " -> " + NextStage(_stage));
        }

        /// <summary>
        /// Moves the fight on to the next stage. Only the server does this,
        /// the stage is replicated from here.
        /// </summary>
        private void AdvanceStage()
        {
            DeathStarStage next = NextStage(_stage);
            if (next == _stage) return;

            _stage = next;
            Debug.Log("[DeathStar] Stage " + _stage + " started.");
        }

        /// <summary>The stage that follows the given one.</summary>
        private static DeathStarStage NextStage(DeathStarStage stage)
        {
            switch (stage)
            {
                case DeathStarStage.ForceField: return DeathStarStage.Fighters;
                case DeathStarStage.Fighters: return DeathStarStage.HeavyFighters;
                default: return DeathStarStage.HeavyFighters;
            }
        }

        /// <summary>
        /// Called by the spawner when the current fighter wave is wiped out.
        /// The station answers with a superlaser volley and moves on to the
        /// next stage once the beam has fired. The last stage never hands
        /// over: it is cleared by grinding the hull down, not by a wave.
        /// </summary>
        public void NotifyWaveCleared()
        {
            if (!isServer || _isDestroyed) return;
            if (_stage != DeathStarStage.Fighters) return;

            RequestStageAdvance();
        }

        /// <summary>
        /// Called by Projectile when a bolt hits the station. While the force
        /// field is up the hull is immune and the bolt is absorbed by the
        /// shell. Once the field is down the bolt chips the armour, and the
        /// hull at zero health is the only way to kill the station.
        /// </summary>
        public void HandleProjectileHit(Vector3 hitPoint, float amount)
        {
            if (!isServer || _isDestroyed) return;

            if (_forceFieldHealth > 0f)
            {
                TakeForceFieldDamage(amount);
                return;
            }

            TakeSurfaceDamage(amount);
        }

        /// <summary>
        /// The white field soaks the incoming bolt damage. When it collapses
        /// the station flashes, the shell disappears and the fight moves on.
        /// </summary>
        private void TakeForceFieldDamage(float amount)
        {
            if (!isServer || _isDestroyed) return;
            if (_forceFieldHealth <= 0f) return;

            _forceFieldHealth = Mathf.Max(0f, _forceFieldHealth - amount);
            if (_forceFieldHealth > 0f) return;

            // The field is down: flash it on every peer and hand the fight
            // over to the fighter wave.
            RpcForceFieldBreak();
            BreakForceField();
        }

        /// <summary>
        /// Server side half of the field collapse. Idempotent, because a host
        /// also runs it through the client rpc.
        /// </summary>
        private void BreakForceField()
        {
            if (_forceFieldBroken) return;
            _forceFieldBroken = true;

            _forceFieldHealth = 0f;
            SetForceFieldVisible(false);

            // The stage change waits for the volley: the superlaser fires
            // first, then the station moves on to the fighter wave.
            RequestStageAdvance();
        }

        public void TakeSurfaceDamage(float amount)
        {
            if (!isServer || _isDestroyed) return;

            float actual = amount * EffectiveSurfaceDamageMultiplier;
            _surfaceHealth -= Mathf.CeilToInt(actual);
            _surfaceHealth = Mathf.Max(0, _surfaceHealth);

            // Sustained fire finishes the station, so the boss is always
            // beatable once the field is down.
            if (_surfaceHealth <= 0)
                Die();
        }

        private void Die()
        {
            if (_isDestroyed) return;
            _isDestroyed = true;

            // Stop the boss from eating the rest of the volley.
            var col = GetComponent<SphereCollider>();
            if (col != null) col.enabled = false;

            RpcExplode();

            if (_active == this) _active = null;

            NetworkLevelManager.RegisterBossDefeated();

            if (isServer)
                StartCoroutine(DestroyAfterDelay(DeathDestroyDelay));
        }

        private System.Collections.IEnumerator DestroyAfterDelay(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            if (this == null || !isServer) yield break;
            NetworkServer.Destroy(gameObject);
        }

        // ------------------------------------------------------------------
        // Client visuals
        // ------------------------------------------------------------------

        [ClientRpc]
        private void RpcSuperlaserWarning(Vector3 origin, Vector3 direction)
        {
            // A thin red targeting line shows exactly where the beam will go.
            CreateBeamVisual(origin, direction, _superlaserRange, Mathf.Max(1.2f, _radius * 0.006f),
                new Color(1f, 0.25f, 0.2f, 0.85f), _superlaserWarningTime);
        }

        [ClientRpc]
        private void RpcSuperlaserFired(Vector3 origin, Vector3 direction, float length)
        {
            if (_superlaserBeamPrefab != null)
            {
                GameObject prefabBeam = Instantiate(_superlaserBeamPrefab, origin, Quaternion.LookRotation(direction));
                prefabBeam.transform.localScale = new Vector3(1f, 1f, length / 10f);
                StartCoroutine(DestroyVisualAfter(0.9f, prefabBeam));
            }
            else
            {
                CreateBeamVisual(origin, direction, length, _superlaserBeamRadius,
                    new Color(0.7f, 1f, 0.85f, 1f), 0.9f);
            }
            CreateFlash(origin + direction * (length * 0.15f), _radius * 0.4f, 0.6f);
        }

        /// <summary>
        /// The force field collapses: a white shockwave sweeps the station and
        /// the shell disappears everywhere.
        /// </summary>
        [ClientRpc]
        private void RpcForceFieldBreak()
        {
            // The field bursts, it does not explode: the flash is white.
            CreateFlash(transform.position, _radius * 1.25f, 1.1f, new Color(1f, 1f, 1f));
            SetForceFieldVisible(false);

            // On a host this also runs on the server. A dedicated server
            // does the same work through TakeForceFieldDamage.
            if (isServer) BreakForceField();
        }

        [ClientRpc]
        private void RpcExplode()
        {
            // The station is dead on every peer, so the HUD can drop it right
            // away and the hull stops being a target.
            _isDestroyed = true;

            // The hull still breaks apart: the debris prefab keeps working
            // exactly as before. The electric burst plays around it.
            if (_explosionPrefab != null)
            {
                for (int i = 0; i < _explosionCount; i++)
                {
                    Vector3 offset = Random.insideUnitSphere * (_explosionRadius * 1.6f);
                    GameObject pop = Instantiate(_explosionPrefab,
                        transform.position + offset, Random.rotation);
                    StartCoroutine(DestroyVisualAfter(1.2f, pop));
                }
            }

            // The station does not go up in orange fire: it burns out in a
            // blue-white electric burst.
            SpawnElectricDeath();
        }

        // ------------------------------------------------------------------
        // The electric death
        // ------------------------------------------------------------------

        /// <summary>The HDR blue-white the electric burst burns with.</summary>
        private static readonly Color ElectricBlue = new Color(0.25f, 1.3f, 4.5f, 1f);

        /// <summary>How many expanding shells the burst plays.</summary>
        private const int ElectricShellCount = 5;

        /// <summary>How many lightning arcs the burst plays.</summary>
        private const int ElectricArcCount = 14;

        /// <summary>How many shockwave rings the burst plays.</summary>
        private const int ElectricRingCount = 3;

        /// <summary>
        /// The death of the station: a blue-white electric burst instead of
        /// the old orange fire. A near-white core flash, five expanding
        /// energy shells, a fan of lightning arcs and three shockwave rings,
        /// all scaled to the 2 km station, plus the light of the burst.
        ///
        /// Runs on every client through the client rpc, so all the peers
        /// see the same death.
        /// </summary>
        private void SpawnElectricDeath()
        {
            Material electric = CreateElectricMaterial();
            if (electric == null) return;

            Vector3 centre = transform.position;

            SpawnEnergyShells(electric, centre);
            SpawnLightningArcs(electric, centre);
            SpawnShockwaveRings(electric, centre);
            SpawnCoreFlash(electric, centre);
            SpawnElectricLights(centre);
        }

        /// <summary>
        /// The expanding energy shells: translucent spheres that grow from a
        /// seed at the centre of the station out past the hull and fade away
        /// on the way. Every shell gets a material of its own, so the shells
        /// fade independently.
        /// </summary>
        private void SpawnEnergyShells(Material electric, Vector3 centre)
        {
            // The shells get bigger one after the other, so the burst reads
            // as a wave and not as five identical bubbles.
            float[] sizeFactors = { 1f, 1.12f, 0.94f, 1.26f, 1.08f };

            for (int i = 0; i < ElectricShellCount; i++)
            {
                GameObject shell = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                shell.name = "DeathStarElectricShell";
                Collider shellCollider = shell.GetComponent<Collider>();
                if (shellCollider != null) Destroy(shellCollider);

                shell.transform.position = centre;
                shell.transform.localScale = Vector3.one * SphereScale(0.15f * _radius);

                Material shellMaterial = CloneElectricMaterial(electric, "DeathStarElectricShell");
                if (shellMaterial == null)
                {
                    Destroy(shell);
                    continue;
                }
                shell.GetComponent<MeshRenderer>().sharedMaterial = shellMaterial;

                StartCoroutine(ShellRoutine(shell, shellMaterial,
                    sizeFactors[i % sizeFactors.Length], i * 0.06f));
            }
        }

        /// <summary>
        /// Grows one shell out to its final size and fades it away. The
        /// additive blend ignores the alpha, so the colour itself has to
        /// dim down to black for the shell to disappear.
        /// </summary>
        private System.Collections.IEnumerator ShellRoutine(GameObject shell, Material material, float sizeFactor, float delay)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);

            const float duration = 1.1f;
            float elapsed = 0f;
            while (elapsed < duration && shell != null)
            {
                elapsed += Time.deltaTime;
                float k = Mathf.Clamp01(elapsed / duration);
                // The shell eases out: it slows down as it runs out of energy.
                float grow = 1f - Mathf.Pow(1f - k, 2.2f);
                float radius = Mathf.Lerp(0.15f, 2.2f * sizeFactor, grow) * _radius;
                shell.transform.localScale = Vector3.one * SphereScale(radius);

                float fade = 1f - k;
                ApplyColor(material, new Color(
                    ElectricBlue.r * fade, ElectricBlue.g * fade, ElectricBlue.b * fade, fade));
                yield return null;
            }

            if (shell != null) Destroy(shell);
            if (material != null) Destroy(material);
        }

        /// <summary>
        /// The lightning: a fan of jagged bolts that leave the centre of the
        /// station in every direction. Every arc gets a material of its own,
        /// so it can flicker and burn down on its own.
        /// </summary>
        private void SpawnLightningArcs(Material electric, Vector3 centre)
        {
            for (int i = 0; i < ElectricArcCount; i++)
                SpawnLightningArc(electric, centre);
        }

        private void SpawnLightningArc(Material electric, Vector3 centre)
        {
            GameObject arc = new GameObject("DeathStarElectricArc");
            arc.transform.position = centre;

            LineRenderer line = arc.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.positionCount = 7;
            line.startWidth = 18f;
            line.endWidth = 2f;
            line.numCapVertices = 2;
            line.numCornerVertices = 2;
            line.alignment = LineAlignment.View;
            line.textureMode = LineTextureMode.Stretch;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;

            Material arcMaterial = CloneElectricMaterial(electric, "DeathStarElectricArc");
            if (arcMaterial == null)
            {
                Destroy(arc);
                return;
            }
            line.sharedMaterial = arcMaterial;

            // The bolt leaves the centre of the station in a random direction
            // and is about as long as the hull is wide.
            Vector3 direction = Random.onUnitSphere;
            float length = Random.Range(0.8f, 1.35f) * _radius;

            for (int i = 0; i < line.positionCount; i++)
            {
                float t = i / (float)(line.positionCount - 1);
                Vector3 point = direction * (length * t);

                // The bolt zigzags: every point is pushed sideways by a
                // random amount that gets smaller towards the tip, so it
                // reads as a bolt and not as a straight line.
                float jitter = _radius * 0.12f * (1f - t) * Random.Range(0.5f, 1f);
                Vector3 sideways = Random.insideUnitSphere;
                sideways -= direction * Vector3.Dot(sideways, direction);
                if (sideways.sqrMagnitude > 0.0001f)
                    point += sideways.normalized * jitter;

                line.SetPosition(i, point);
            }

            StartCoroutine(ArcRoutine(arc, arcMaterial, 0.35f));
        }

        /// <summary>
        /// The bolt flickers while it burns down and is gone after a third of
        /// a second.
        /// </summary>
        private System.Collections.IEnumerator ArcRoutine(GameObject arc, Material material, float lifetime)
        {
            float elapsed = 0f;
            while (elapsed < lifetime && arc != null)
            {
                elapsed += Time.deltaTime;
                float k = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, lifetime));
                // The bolt flickers while it fades out.
                float flicker = Random.Range(0.55f, 1.25f) * (1f - k);
                ApplyColor(material, new Color(
                    ElectricBlue.r * flicker, ElectricBlue.g * flicker, ElectricBlue.b * flicker, 1f - k));
                yield return null;
            }

            if (arc != null) Destroy(arc);
            if (material != null) Destroy(material);
        }

        /// <summary>
        /// The shockwave rings: flat discs that race outwards from the
        /// station, each one in a different direction, and fade out fast.
        /// </summary>
        private void SpawnShockwaveRings(Material electric, Vector3 centre)
        {
            float[] sizeFactors = { 1f, 1.12f, 0.92f };

            for (int i = 0; i < ElectricRingCount; i++)
            {
                GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                ring.name = "DeathStarShockwave";
                Collider ringCollider = ring.GetComponent<Collider>();
                if (ringCollider != null) Destroy(ringCollider);

                // The disc faces outwards: the cylinder is a disc around its
                // local Y axis, so the ring is turned until that axis points
                // along the direction of the wave.
                Vector3 direction = Random.onUnitSphere;
                Vector3 up = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.99f
                    ? Vector3.forward
                    : Vector3.up;
                ring.transform.rotation = Quaternion.LookRotation(direction, up) * Quaternion.Euler(90f, 0f, 0f);
                ring.transform.position = centre;
                ring.transform.localScale = new Vector3(0.3f * _radius * 2f, 1f, 0.3f * _radius * 2f);

                Material ringMaterial = CloneElectricMaterial(electric, "DeathStarShockwave");
                if (ringMaterial == null)
                {
                    Destroy(ring);
                    continue;
                }
                ring.GetComponent<MeshRenderer>().sharedMaterial = ringMaterial;

                StartCoroutine(RingRoutine(ring, ringMaterial,
                    sizeFactors[i % sizeFactors.Length], i * 0.1f));
            }
        }

        /// <summary>
        /// Expands one ring out to three station radii and fades it out.
        /// </summary>
        private System.Collections.IEnumerator RingRoutine(GameObject ring, Material material, float sizeFactor, float delay)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);

            const float duration = 0.9f;
            float elapsed = 0f;
            while (elapsed < duration && ring != null)
            {
                elapsed += Time.deltaTime;
                float k = Mathf.Clamp01(elapsed / duration);
                float radius = Mathf.Lerp(0.15f, 3f * sizeFactor, k) * _radius;
                // The cylinder is 1 unit across, so the radius is doubled to
                // get the wanted size in metres. The disc stays flat.
                ring.transform.localScale = new Vector3(radius * 2f, 1f, radius * 2f);

                // The ring fades out fast: it is only readable for a moment.
                float fade = Mathf.Clamp01(1f - k * 1.7f);
                ApplyColor(material, new Color(
                    ElectricBlue.r * fade, ElectricBlue.g * fade, ElectricBlue.b * fade, fade));
                yield return null;
            }

            if (ring != null) Destroy(ring);
            if (material != null) Destroy(material);
        }

        /// <summary>
        /// The core flash: the near-white heart of the burst at the centre of
        /// the station. It pops out to a bit more than the hull radius and
        /// burns down.
        /// </summary>
        private void SpawnCoreFlash(Material electric, Vector3 centre)
        {
            GameObject core = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            core.name = "DeathStarElectricCore";
            Collider coreCollider = core.GetComponent<Collider>();
            if (coreCollider != null) Destroy(coreCollider);

            core.transform.position = centre;
            core.transform.localScale = Vector3.one * SphereScale(0.9f * _radius);

            // The core is the white heart of the burst, so it burns in a
            // near-white of its own.
            Material coreMaterial = CloneElectricMaterial(electric, "DeathStarElectricCore");
            if (coreMaterial == null)
            {
                Destroy(core);
                return;
            }
            ApplyColor(coreMaterial, new Color(6f, 7f, 9f, 1f));
            core.GetComponent<MeshRenderer>().sharedMaterial = coreMaterial;

            StartCoroutine(CoreRoutine(core, coreMaterial));
        }

        /// <summary>
        /// The core pops out and fades away in a fraction of a second.
        /// </summary>
        private System.Collections.IEnumerator CoreRoutine(GameObject core, Material material)
        {
            const float duration = 0.4f;
            float elapsed = 0f;
            while (elapsed < duration && core != null)
            {
                elapsed += Time.deltaTime;
                float k = Mathf.Clamp01(elapsed / duration);
                // The core pops out fast and then burns down.
                float size = k < 0.25f
                    ? Mathf.Lerp(0.9f, 1.3f, k / 0.25f)
                    : Mathf.Lerp(1.3f, 1.15f, (k - 0.25f) / 0.75f);
                core.transform.localScale = Vector3.one * SphereScale(size * _radius);

                float fade = 1f - k;
                ApplyColor(material, new Color(6f * fade, 7f * fade, 9f * fade, fade));
                yield return null;
            }

            if (core != null) Destroy(core);
            if (material != null) Destroy(material);
        }

        /// <summary>
        /// The light of the burst: three electric point lights that burn out
        /// over a second and a half.
        /// </summary>
        private void SpawnElectricLights(Vector3 centre)
        {
            for (int i = 0; i < 3; i++)
            {
                GameObject lightGo = new GameObject("DeathStarElectricLight");
                lightGo.transform.position = centre;

                Light light = lightGo.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = new Color(0.6f, 0.8f, 1f);
                light.range = 4f * _radius;
                light.intensity = 40f;
                light.shadows = LightShadows.None;

                StartCoroutine(ElectricLightRoutine(lightGo, light, i * 0.08f));
            }
        }

        /// <summary>
        /// The light fades out and takes its GameObject with it.
        /// </summary>
        private System.Collections.IEnumerator ElectricLightRoutine(GameObject lightGo, Light light, float delay)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);

            const float duration = 1.5f;
            float elapsed = 0f;
            while (elapsed < duration && lightGo != null)
            {
                elapsed += Time.deltaTime;
                float k = Mathf.Clamp01(elapsed / duration);
                if (light != null) light.intensity = Mathf.Lerp(40f, 0f, k * k);
                yield return null;
            }

            if (lightGo != null) Destroy(lightGo);
        }

        /// <summary>
        /// The material the whole electric burst renders with: an additive
        /// HDR blue-white. The values above one are what make it bloom, and
        /// the additive blend keeps the shells, the bolts and the rings from
        /// hiding each other.
        /// </summary>
        private Material CreateElectricMaterial()
        {
            Shader shader = ElectricShader();
            if (shader == null) return null;

            Material material = new Material(shader)
            {
                name = "DeathStarElectric",
                hideFlags = HideFlags.DontSave
            };

            ApplyColor(material, ElectricBlue);

            // Additive: the parts of the burst have to add up instead of
            // covering each other.
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f); // transparent
            if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 1f); // additive
            if (material.HasProperty("_SrcBlend"))
                material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            if (material.HasProperty("_DstBlend"))
                material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = 3100;

            // The material belongs to the station, so it goes away with it.
            _runtimeMaterials.Add(material);
            return material;
        }

        /// <summary>
        /// A private copy of the electric material. The shells, the bolts and
        /// the rings fade on their own, so every one of them needs a material
        /// of its own. The copy belongs to the effect, so it is destroyed
        /// with the effect and not with the station.
        /// </summary>
        private static Material CloneElectricMaterial(Material source, string materialName)
        {
            if (source == null) return null;
            return new Material(source)
            {
                name = materialName,
                hideFlags = HideFlags.DontSave
            };
        }

        /// <summary>
        /// The shader of the electric burst. The URP unlit shader is the
        /// first choice, the built-in unlit shaders are the fallback so the
        /// burst is never invisible.
        /// </summary>
        private static Shader ElectricShader()
        {
            if (_electricShader != null) return _electricShader;
            _electricShader = Shader.Find("Universal Render Pipeline/Unlit");
            if (_electricShader == null) _electricShader = Shader.Find("Unlit/Color");
            if (_electricShader == null) _electricShader = Shader.Find("Sprites/Default");
            return _electricShader;
        }

        /// <summary>
        /// The scale a built-in sphere needs for the wanted radius in metres:
        /// the sphere mesh is 0.5 units across.
        /// </summary>
        private static float SphereScale(float radius) => radius * 2f;

        /// <summary>
        /// Builds the white force field shell around the station. Runs on the
        /// server and on every client, because it is a child object and child
        /// objects are not replicated separately.
        /// </summary>
        private void BuildForceFieldVisual()
        {
            if (_forceField == null)
            {
                _forceField = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _forceField.name = "DeathStarForceField";
                _forceField.transform.SetParent(transform, false);
                // 0.5 x 1.06 x 2000 = a 1060 m shell around the 1000 m hull,
                // so the bolts always meet the field before the hull.
                _forceField.transform.localScale = Vector3.one * _forceFieldRadius;

                // The dedicated material is cloned, so the shared asset in
                // the project is never modified by the pulse. Without an
                // assigned material the runtime white shell is the fallback.
                Material material = _forceFieldMaterial != null
                    ? CloneForceFieldMaterial()
                    : CreateForceFieldMaterial();
                if (material != null)
                {
                    _forceField.GetComponent<MeshRenderer>().sharedMaterial = material;
                    _forceFieldShellMaterial = material;
                }
            }

            // Only the server needs the collider: the clients just draw the
            // shell, their bolts are simulated by the server anyway.
            Collider fieldCollider = _forceField.GetComponent<Collider>();
            if (fieldCollider != null) fieldCollider.enabled = isServer;
        }

        /// <summary>Shows or hides the force field shell on this peer.</summary>
        private void SetForceFieldVisible(bool visible)
        {
            if (_forceField == null) return;
            if (_forceField.activeSelf == visible) return;
            _forceField.SetActive(visible);
        }

        /// <summary>
        /// Clones the force field material that is assigned in the inspector.
        /// The clone is what the shell renders with and what the pulse
        /// animates, so the shared asset in the project is never modified.
        /// </summary>
        private Material CloneForceFieldMaterial()
        {
            Material clone = new Material(_forceFieldMaterial)
            {
                name = "DeathStarForceField",
                hideFlags = HideFlags.DontSave
            };
            _runtimeMaterials.Add(clone);
            return clone;
        }

        /// <summary>
        /// The fallback white shell material of the force field. The URP lit
        /// shader is preferred because it can glow, the unlit shaders are the
        /// fallback so the field is never invisible.
        /// </summary>
        private Material CreateForceFieldMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) return null;

            Material material = new Material(shader) { name = "DeathStarForceField" };
            material.hideFlags = HideFlags.DontSave;

            Color baseColor = new Color(1f, 1f, 1f, 0.35f);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", baseColor);
            if (material.HasProperty("_Color")) material.SetColor("_Color", baseColor);

            // HDR white emission: the field has to read as a glowing white
            // shell, not as a grey ball.
            if (material.HasProperty("_EmissionColor"))
            {
                material.SetColor("_EmissionColor", new Color(3f, 3f, 3f, 1f));
                material.EnableKeyword("_EMISSION");
            }

            // URP needs the surface mode and the blend state by hand,
            // otherwise the shell renders as an opaque ball.
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f); // transparent
            if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f); // alpha
            if (material.HasProperty("_SrcBlend"))
                material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend"))
                material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            if (material.HasProperty("_AlphaClip")) material.SetFloat("_AlphaClip", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            _runtimeMaterials.Add(material);
            return material;
        }

        private void CreateBeamVisual(Vector3 origin, Vector3 direction, float length, float radius, Color color, float lifetime)
        {
            if (direction.sqrMagnitude < 0.0001f) return;

            GameObject beam = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            beam.name = "SuperlaserBeam";
            Collider beamCollider = beam.GetComponent<Collider>();
            if (beamCollider != null) Destroy(beamCollider);

            Vector3 dir = direction.normalized;
            beam.transform.position = origin + dir * (length * 0.5f);
            beam.transform.rotation = Quaternion.FromToRotation(Vector3.up, dir);
            beam.transform.localScale = new Vector3(radius * 2f, length * 0.5f, radius * 2f);

            // Transient material: it belongs to the beam, not to the station,
            // so it must not be destroyed with the station (that would turn
            // the beam magenta while it is still on screen).
            Material material = CreateTransientMaterial(color);
            beam.GetComponent<MeshRenderer>().sharedMaterial = material;

            StartCoroutine(DestroyVisualAfter(lifetime, beam, material));
        }

        private void CreateFlash(Vector3 position, float size, float lifetime)
        {
            CreateFlash(position, size, lifetime, new Color(1f, 0.85f, 0.5f));
        }

        private void CreateFlash(Vector3 position, float size, float lifetime, Color color)
        {
            GameObject flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            flash.name = "DeathStarFlash";
            Collider flashCollider = flash.GetComponent<Collider>();
            if (flashCollider != null) Destroy(flashCollider);
            flash.transform.position = position;
            flash.transform.localScale = Vector3.one * size;

            Material material = CreateTransientMaterial(color);
            flash.GetComponent<MeshRenderer>().sharedMaterial = material;

            var lightGo = new GameObject("DeathStarFlashLight");
            lightGo.transform.position = position;
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.range = Mathf.Clamp(size * 3f, 10f, 2500f);
            light.intensity = 8f;
            light.shadows = LightShadows.None;

            StartCoroutine(FlashRoutine(flash, lightGo, size, lifetime, material));
        }

        private System.Collections.IEnumerator FlashRoutine(GameObject flash, GameObject lightGo, float size, float lifetime, Material material)
        {
            float elapsed = 0f;
            Light light = lightGo != null ? lightGo.GetComponent<Light>() : null;
            while (elapsed < lifetime && flash != null)
            {
                elapsed += Time.deltaTime;
                float k = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, lifetime));
                flash.transform.localScale = Vector3.one * Mathf.Lerp(size * 0.4f, size * 1.6f, k);
                if (light != null) light.intensity = Mathf.Lerp(9f, 0f, k);
                yield return null;
            }
            if (flash != null) Destroy(flash);
            if (lightGo != null) Destroy(lightGo);
            if (material != null) Destroy(material);
        }

        private System.Collections.IEnumerator DestroyVisualAfter(float seconds, GameObject target, Material material = null)
        {
            yield return new WaitForSeconds(seconds);
            if (target != null) Destroy(target);
            if (material != null) Destroy(material);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private Vector3 DishPosition()
        {
            // The prefab points _superlaserDish at the anchor, so prefer the
            // real dish child when it exists: the beam should come out of the
            // superlaser, not out of the side of the hull.
            Transform dish = transform.Find("SuperlaserDish");
            if (dish != null) return dish.position;
            if (_superlaserDish != null) return _superlaserDish.position;
            return transform.position + transform.forward * _radius;
        }

        private NetworkPlayer FindNearestPlayer()
        {
            NetworkPlayer best = null;
            float bestDistance = float.MaxValue;
            foreach (NetworkPlayer player in FindObjectsByType<NetworkPlayer>(FindObjectsSortMode.None))
            {
                if (player == null) continue;
                float distance = Vector3.Distance(transform.position, player.transform.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = player;
                }
            }
            return best;
        }

        /// <summary>
        /// Material for a short lived effect. It is owned by the effect, not
        /// by the station, so it survives the station's destruction and is
        /// destroyed with the effect.
        /// </summary>
        private Material CreateTransientMaterial(Color color)
        {
            Shader shader = GlowShader();
            if (shader == null) return null;
            Material material = new Material(shader) { name = "DeathStarEffect" };
            ApplyColor(material, color);
            material.hideFlags = HideFlags.DontSave;
            return material;
        }

        private static void ApplyColor(Material material, Color color)
        {
            if (material == null) return;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        }

        /// <summary>Cached shader lookup: Shader.Find is not free.</summary>
        private static Shader GlowShader()
        {
            if (_glowShader != null) return _glowShader;
            _glowShader = Shader.Find("Universal Render Pipeline/Unlit");
            if (_glowShader == null) _glowShader = Shader.Find("Unlit/Color");
            if (_glowShader == null) _glowShader = Shader.Find("Sprites/Default");
            return _glowShader;
        }
    }
}
