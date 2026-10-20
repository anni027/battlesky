using Mirror;
using UnityEngine;

namespace BattleSky.Network
{
    /// <summary>
    /// Level 3 boss: Death Star. A massive spherical battle station with:
    /// - A glowing exhaust port on its surface: the weak point that kills it
    /// - Heavy armour on the rest of the hull (reduced damage)
    /// - Four surface turrets that fire at the crew
    /// - A charging superlaser (telegraphed with a targeting line first)
    ///
    /// The station never moves, so it needs no NetworkTransform: Mirror sends
    /// the spawn rotation once, which is enough for the port and the dish to
    /// face the crew.
    /// </summary>
    public class DeathStar : NetworkBehaviour
    {
        [Header("Death Star")]
        [SerializeField] private float _radius = 50f;
        [SerializeField] private float _trenchWidth = 8f;
        [SerializeField] private float _trenchDepth = 6f;
        [SerializeField] private int _maxSurfaceHealth = 1000;
        [SerializeField] private float _surfaceDamageMultiplier = 0.35f; // armoured

        [Header("Superlaser")]
        [SerializeField] private float _superlaserChargeTime = 9f;
        [SerializeField] private float _superlaserWarningTime = 3.5f;
        [SerializeField] private float _superlaserDamage = 85f;
        [Tooltip("Radius of the visible beam in metres")]
        [SerializeField] private float _superlaserBeamRadius = 8f;
        [Tooltip("Radius in which a ship takes damage. Keep it close to the beam radius so a hit never feels unfair")]
        [SerializeField] private float _superlaserDamageRadius = 10f;
        [SerializeField] private float _superlaserRange = 900f;
        [SerializeField] private GameObject _superlaserBeamPrefab;
        [SerializeField] private Transform _superlaserDish;

        [Header("Turrets")]
        [SerializeField] private int _turretCount = 4;
        [Tooltip("Invisible hull turret origins authored by the boss wiring tool; the visible bumps are not needed")]
        [SerializeField] private Transform[] _turretPoints;
        [SerializeField] private float _turretFireInterval = 2.1f;
        [Tooltip("Muzzle velocity high enough to reach a ship near the 2 km hull")]
        [SerializeField] private float _turretMuzzleVelocity = 1100f;
        [Tooltip("Multiplier applied to the turret bolt visual so shots read at long range")]
        [SerializeField] private float _turretBoltScale = 4f;

        [Header("Exhaust Port")]
        [SerializeField] private Transform _exhaustPortAnchor;
        [Tooltip("A projectile landing this close to the port destroys the station")]
        [SerializeField] private float _exhaustPortHitRadius = 13f;

        [Header("Destruction")]
        [SerializeField] private GameObject _explosionPrefab;
        [SerializeField] private int _explosionCount = 36;
        [SerializeField] private float _explosionRadius = 8f;

        /// <summary>Replicated hull integrity so every client can show a bar.</summary>
        [SyncVar]
        private int _surfaceHealth;

        private static DeathStar _active;
        private static Shader _glowShader;
        private float _superlaserTimer;
        private bool _superlaserCharging;
        private Transform _lockedTarget;
        private Vector3 _lockOrigin;
        private Vector3 _lockDirection = Vector3.forward;
        private bool _isDestroyed;
        private bool _oriented;
        private Transform[] _turrets;
        private float[] _turretNextFire;
        private GameObject _boltPrefab;
        private NetworkPlayer _turretTarget;
        private float _turretTargetRefresh;

        private GameObject _portCore;
        private Light _portLight;
        private readonly System.Collections.Generic.List<Material> _runtimeMaterials =
            new System.Collections.Generic.List<Material>();

        /// <summary>The Death Star that is currently in the world (null when none).</summary>
        public static DeathStar Active => _active;

        public float Radius => _radius;
        public bool IsDestroyed => _isDestroyed;
        public float HealthNormalized => _maxSurfaceHealth > 0
            ? Mathf.Clamp01((float)_surfaceHealth / _maxSurfaceHealth)
            : 0f;
        public bool IsChargingSuperlaser => _superlaserCharging;
        public float SuperlaserChargeNormalized => _superlaserCharging && _superlaserWarningTime > 0f
            ? Mathf.Clamp01(1f - _superlaserTimer / _superlaserWarningTime)
            : 0f;

        /// <summary>World position of the glowing weak point.</summary>
        public Vector3 ExhaustPortWorldPosition => _exhaustPortAnchor != null
            ? _exhaustPortAnchor.position
            : transform.position + transform.forward * _radius;

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

            _surfaceHealth = _maxSurfaceHealth;
            _superlaserTimer = _superlaserChargeTime;
            _boltPrefab = FindBoltPrefab();

            CollectTurrets();
            BuildExhaustPortVisual();
            OrientTowardsCrew();
        }

        public override void OnStartClient()
        {
            if (_active == null) _active = this;
            BuildExhaustPortVisual();
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
            // The port pulses on every peer, not just the host.
            AnimatePort();

            if (!isServer || _isDestroyed) return;

            if (!_oriented)
                OrientTowardsCrew();

            UpdateSuperlaser();
            UpdateTurrets();
        }

        /// <summary>
        /// Points the station's +Z face (where the exhaust port and the dish
        /// live) at the crew, so the weak point is always in front of the
        /// players. Runs before the spawn message is sent, so the rotation
        /// reaches the clients with the spawn payload.
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
                if (_superlaserTimer <= 0f)
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
                _superlaserTimer = _superlaserChargeTime;
            }
        }

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

        private void UpdateTurrets()
        {
            if (_turrets == null || _turrets.Length == 0) return;
            if (_boltPrefab == null)
            {
                _boltPrefab = FindBoltPrefab();
                if (_boltPrefab == null) return;
            }

            // The target is cached: a scene-wide search every frame would be
            // wasteful once the belt is full of asteroids and fighters.
            NetworkPlayer target = TurretTarget();
            if (target == null) return;

            for (int i = 0; i < _turrets.Length; i++)
            {
                if (_turrets[i] == null) continue;
                if (Time.time < _turretNextFire[i]) continue;

                _turretNextFire[i] = Time.time + _turretFireInterval + Random.Range(-0.3f, 0.5f);
                FireTurret(_turrets[i], target.transform);
            }
        }

        private NetworkPlayer TurretTarget()
        {
            if (_turretTarget == null || Time.time >= _turretTargetRefresh)
            {
                _turretTarget = FindNearestPlayer();
                _turretTargetRefresh = Time.time + 0.5f;
            }
            return _turretTarget;
        }

        [Server]
        private void FireTurret(Transform turret, Transform target)
        {
            Vector3 origin = turret.position;
            Vector3 direction = (target.position - origin).normalized;
            Vector3 spawnPosition = origin + direction * 3f;

            GameObject bolt = Instantiate(_boltPrefab, spawnPosition, Quaternion.LookRotation(direction));
            bolt.transform.localScale *= _turretBoltScale;
            Projectile proj = bolt.GetComponent<Projectile>();
            if (proj != null)
                proj.Launch(_turretMuzzleVelocity, Vector3.zero, direction, transform);
            NetworkServer.Spawn(bolt);
        }

        /// <summary>
        /// Called by Projectile when a bolt hits the hull. A hit next to the
        /// glowing port destroys the station; anything else only chips the
        /// armour, so the port stays the intended solution.
        /// </summary>
        public void HandleProjectileHit(Vector3 hitPoint, float amount)
        {
            if (!isServer || _isDestroyed) return;

            if (Vector3.Distance(hitPoint, ExhaustPortWorldPosition) <= _exhaustPortHitRadius)
            {
                ExhaustPortHit();
                return;
            }

            TakeSurfaceDamage(amount);
        }

        public void TakeSurfaceDamage(float amount)
        {
            if (!isServer || _isDestroyed) return;

            float actual = amount * _surfaceDamageMultiplier;
            _surfaceHealth -= Mathf.CeilToInt(actual);
            _surfaceHealth = Mathf.Max(0, _surfaceHealth);

            // Sustained fire can still finish the station if the exhaust port
            // is missed, so the boss is always beatable.
            if (_surfaceHealth <= 0)
                Die();
        }

        public void ExhaustPortHit()
        {
            if (!isServer || _isDestroyed) return;
            Debug.Log("[DeathStar] Exhaust port hit - the station is breaking up!");
            _surfaceHealth = 0;
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
                StartCoroutine(DestroyAfterDelay(0.3f));
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
            PulsePort(new Color(1f, 0.95f, 0.85f), _superlaserWarningTime);
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

        [ClientRpc]
        private void RpcExplode()
        {
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
            else
            {
                // Keep the flash count sane: every flash is a dynamic light
                // and a material, and URP has a hard budget for both.
                int flashes = Mathf.Min(_explosionCount, 12);
                for (int i = 0; i < flashes; i++)
                {
                    Vector3 offset = Random.insideUnitSphere * (_explosionRadius * 1.6f);
                    CreateFlash(transform.position + offset,
                        Random.Range(Mathf.Max(4f, _radius * 0.02f), Mathf.Max(8f, _radius * 0.06f)),
                        Random.Range(0.5f, 1.4f));
                }
            }
            CreateFlash(transform.position, _radius * 1.2f, 1.6f);
        }

        /// <summary>
        /// Creates the glowing weak point. Runs on the server and on every
        /// client, because it is a child object and child objects are not
        /// replicated separately.
        /// </summary>
        private void BuildExhaustPortVisual()
        {
            if (_portCore != null) return;
            if (_exhaustPortAnchor == null) return;

            _portCore = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _portCore.name = "ExhaustPortCore";
            Collider coreCollider = _portCore.GetComponent<Collider>();
            if (coreCollider != null) Destroy(coreCollider);
            _portCore.transform.SetParent(_exhaustPortAnchor, false);
            _portCore.transform.localPosition = new Vector3(0f, 0f, 0.012f);
            // 44 m on the 2 km hull. The old marker was a 600 m orange-red
            // plate plus a 180 m orange core, which read as a glowing belt
            // wrapped around the station and made the weak point obvious.
            _portCore.transform.localScale = Vector3.one * 0.022f;
            _portCore.GetComponent<MeshRenderer>().sharedMaterial =
                CreateGlowMaterial(new Color(0.72f, 0.88f, 1f));

            // No orange/red plate: the hull stays a clean grey shell and the
            // weak point has to be found, not read off a glowing band.
            var lightGo = new GameObject("ExhaustPortLight");
            lightGo.transform.SetParent(_exhaustPortAnchor, false);
            lightGo.transform.localPosition = new Vector3(0f, 0f, 0.04f);
            _portLight = lightGo.AddComponent<Light>();
            _portLight.type = LightType.Point;
            _portLight.color = new Color(0.6f, 0.8f, 1f);
            _portLight.range = 150f;
            _portLight.intensity = 3f;
            _portLight.shadows = LightShadows.None;
        }

        private void AnimatePort()
        {
            if (_portCore == null) return;

            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 3.2f);
            if (_portCore.transform.localScale != Vector3.one * 0.022f)
                _portCore.transform.localScale = Vector3.one * Mathf.Lerp(0.020f, 0.024f, pulse);
            if (_portLight != null)
                _portLight.intensity = Mathf.Lerp(1.8f, 3.5f, pulse);
        }

        private void PulsePort(Color color, float duration)
        {
            if (_portCore == null) return;
            StartCoroutine(PulsePortRoutine(color, duration));
        }

        private System.Collections.IEnumerator PulsePortRoutine(Color color, float duration)
        {
            float elapsed = 0f;
            while (elapsed < duration && this != null)
            {
                elapsed += Time.deltaTime;
                float pulse = 0.5f + 0.5f * Mathf.Sin(elapsed * 22f);
                if (_portLight != null)
                {
                    _portLight.color = Color.Lerp(new Color(0.6f, 0.8f, 1f), color, pulse);
                    _portLight.intensity = Mathf.Lerp(2.5f, 9f, pulse);
                }
                yield return null;
            }
            if (_portLight != null)
            {
                _portLight.color = new Color(0.6f, 0.8f, 1f);
                _portLight.intensity = 3f;
            }
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
            GameObject flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            flash.name = "DeathStarFlash";
            Collider flashCollider = flash.GetComponent<Collider>();
            if (flashCollider != null) Destroy(flashCollider);
            flash.transform.position = position;
            flash.transform.localScale = Vector3.one * size;

            Material material = CreateTransientMaterial(new Color(1f, 0.85f, 0.5f));
            flash.GetComponent<MeshRenderer>().sharedMaterial = material;

            var lightGo = new GameObject("DeathStarFlashLight");
            lightGo.transform.position = position;
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.8f, 0.45f);
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

        private void CollectTurrets()
        {
            if (_turrets != null) return;

            var candidates = new System.Collections.Generic.List<Transform>();

            // Prefer the invisible hull origins the wiring tool authors; the
            // named lookups below keep older prefabs working.
            if (_turretPoints != null)
            {
                foreach (Transform point in _turretPoints)
                {
                    if (point != null && !candidates.Contains(point)) candidates.Add(point);
                }
            }

            for (int i = 1; candidates.Count < _turretCount && i <= 4; i++)
            {
                Transform point = transform.Find("TurretPoint_" + i);
                if (point != null && !candidates.Contains(point)) candidates.Add(point);
            }

            for (int i = 1; candidates.Count < _turretCount && i <= 8; i++)
            {
                Transform bump = transform.Find("TurretBump_" + i);
                if (bump != null && !candidates.Contains(bump)) candidates.Add(bump);
            }

            int count = Mathf.Min(_turretCount, candidates.Count);
            _turrets = new Transform[count];
            _turretNextFire = new float[count];
            for (int i = 0; i < count; i++)
            {
                _turrets[i] = candidates[i];
                // Stagger the first shots so the four guns do not fire as one.
                _turretNextFire[i] = Time.time + 0.8f + i * 0.55f;
            }
        }

        private Vector3 DishPosition()
        {
            // The prefab points _superlaserDish at the anchor, so prefer the
            // real dish child when it exists: the beam should come out of the
            // superlaser, not out of the exhaust port.
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

        private GameObject FindBoltPrefab()
        {
            if (NetworkManager.singleton == null) return null;
            foreach (GameObject prefab in NetworkManager.singleton.spawnPrefabs)
            {
                if (prefab != null && prefab.GetComponent<Projectile>() != null)
                    return prefab;
            }
            return null;
        }

        private Material CreateGlowMaterial(Color color)
        {
            Shader shader = GlowShader();
            if (shader == null) return null;
            Material material = new Material(shader) { name = "DeathStarGlow" };
            ApplyColor(material, color);
            material.hideFlags = HideFlags.DontSave;
            _runtimeMaterials.Add(material);
            return material;
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
