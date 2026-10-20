using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace BattleSky.Network
{
    /// <summary>
    /// Enemy ship flown by server AI. It hunts the nearest player ship,
    /// fires networked bolts at it, and takes damage from player bolts.
    /// Destroyed when health reaches zero.
    ///
    /// The AI keeps a target (the nearest real crew ship, a gunner proxy
    /// is only used as a fallback), always thrusts forward so it can never
    /// stall, points its nose at the predicted intercept and only fires
    /// when the target is in range and in front of the nose.
    ///
    /// Guns: the bolts leave from the fire point transforms on the hull
    /// (the "firepoint" markers that live under the model). They are found
    /// by name when nothing is assigned in the Inspector, so a fresh model
    /// with new markers keeps working without rewiring the prefab.
    /// </summary>
    public class EnemyShip : NetworkBehaviour
    {
        [Header("Flight")]
        [SerializeField] private float _thrust = 26f;
        [SerializeField] private float _turnRate = 5f;
        [SerializeField] private float _maxSpeed = 48f;

        [Header("Combat")]
        [SerializeField] private float _fireRate = 2.2f;
        [SerializeField] private float _muzzleVelocity = 900f;
        [SerializeField] private GameObject _bulletPrefab;
        [Tooltip("Gun transforms on the hull. Found by name ('firepoint', 'muzzle') when left empty.")]
        [SerializeField] private Transform[] _firePoints;
        [Tooltip("On: the guns take turns, one bolt per salvo. Off: every gun fires on each salvo.")]
        [SerializeField] private bool _alternateFirePoints;
        [Tooltip("Metres in front of the gun where a bolt is spawned, so it never starts inside the hull.")]
        [SerializeField] private float _muzzleClearance = 4f;
        [Tooltip("Closest range the guns fire at. Below it the ship closes the distance first.")]
        [SerializeField] private float _minFireRange = 25f;
        [Tooltip("Furthest range the guns fire at. Beyond it the ship has to close in.")]
        [SerializeField] private float _maxFireRange = 450f;
        [Tooltip("Half angle of the cone the shots are spread over, in degrees. Keeps the guns from being laser accurate.")]
        [SerializeField] private float _spreadDegrees = 3f;
        [SerializeField] private int _health = 140;
        public int Health => _health;

        [Header("Targeting")]
        [Tooltip("Seconds between two target searches. The ship re-picks at once when its target is gone.")]
        [SerializeField] private float _retargetInterval = 1f;

        [SerializeField] private GameObject _explosionPrefab;
        [SerializeField] private int _explosionCount = 4;
        [SerializeField] private float _explosionRadius = 1f;

        private Rigidbody _rb;
        private Transform _target;
        private float _fireTimer;
        private float _retargetTimer;
        private int _firePointIndex;
        private Transform[] _resolvedFirePoints;

        private void Awake()
        {
            ApplyTunedNumbers();
        }

        public override void OnStartServer()
        {
            _rb = GetComponent<Rigidbody>();
            PickTarget();
        }

        /// <summary>
        /// Applies the tuned flight and gun numbers.
        ///
        /// The shipped prefab still carries the values from before the
        /// retune, and a serialized prefab value always wins over the
        /// default that is written in the script. Only the exact old
        /// numbers are upgraded, so a value that was tuned by hand in the
        /// Inspector is left alone.
        /// </summary>
        private void ApplyTunedNumbers()
        {
            if (_thrust == 12f) _thrust = 26f;                          // 12 -> 26
            if (_turnRate == 6f) _turnRate = 5f;                         // 6 -> 5
            if (_maxSpeed == 22f) _maxSpeed = 48f;                       // 22 -> 48
            if (_fireRate == 1.2f) _fireRate = 2.2f;                     // 1.2 -> 2.2
            if (_muzzleVelocity == 600f) _muzzleVelocity = 900f;        // 600 -> 900
            if (_health == 100) _health = 140;                           // 100 -> 140
        }

        /// <summary>
        /// Picks the player ship to shoot at: the nearest real crew ship.
        /// A gunner proxy is an invisible control forwarder, so it is only
        /// used when no crew ship is around - without any target the ship
        /// would sit still, and a proxy is better than nothing.
        /// </summary>
        private void PickTarget()
        {
            NetworkPlayer best = null;
            float bestDist = float.MaxValue;
            NetworkPlayer fallback = null;
            float fallbackDist = float.MaxValue;

            foreach (var np in Object.FindObjectsByType<NetworkPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (np == null) continue;

                float d = Vector3.Distance(transform.position, np.transform.position);
                if (np.IsGunnerProxy)
                {
                    if (d < fallbackDist) { fallbackDist = d; fallback = np; }
                    continue;
                }
                if (d < bestDist) { bestDist = d; best = np; }
            }

            _target = (best != null ? best : fallback)?.transform;
        }

        private void FixedUpdate()
        {
            if (!isServer) return;

            // Re-pick the target on a timer and the moment it is gone.
            _retargetTimer -= Time.fixedDeltaTime;
            if (_target == null || _retargetTimer <= 0f)
            {
                PickTarget();
                _retargetTimer = _retargetInterval;
            }
            if (_target == null) return;

            Vector3 toTarget = _target.position - transform.position;
            float distance = toTarget.magnitude;
            toTarget = distance > 0.0001f ? toTarget / distance : transform.forward;

            // The nose points at the predicted intercept, so the ship leads
            // its own shots instead of chasing the tail of the target.
            Vector3 toAim = PredictTargetPoint(transform.position, _muzzleVelocity) - transform.position;
            toAim = toAim.sqrMagnitude > 0.0001f ? toAim.normalized : toTarget;

            _rb.MoveRotation(Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(toAim), _turnRate * Time.fixedDeltaTime));

            // Always thrust along the nose: a ship that only thrusts when it
            // is already lined up stalls the moment it overshoots.
            _rb.AddForce(transform.forward * _thrust, ForceMode.Acceleration);

            // Scrub the sideways drift while the nose is off the target, so
            // the ship turns in a tight arc instead of skidding away.
            float alignment = Vector3.Dot(transform.forward, toTarget);
            if (alignment < 0.9f)
            {
                Vector3 lateral = _rb.linearVelocity - transform.forward * Vector3.Dot(_rb.linearVelocity, transform.forward);
                _rb.linearVelocity -= lateral * Mathf.Clamp01(4f * Time.fixedDeltaTime);
            }

            if (_rb.linearVelocity.sqrMagnitude > _maxSpeed * _maxSpeed)
                _rb.linearVelocity = _rb.linearVelocity.normalized * _maxSpeed;

            _fireTimer -= Time.fixedDeltaTime;
            if (_fireTimer <= 0f && _bulletPrefab != null)
            {
                _fireTimer = 1f / _fireRate;
                Fire();
            }
        }

        /// <summary>
        /// Where the target will be when the bolt arrives, so the guns can
        /// lead the shot instead of firing into the empty space behind it.
        /// </summary>
        private Vector3 PredictTargetPoint(Vector3 origin, float projectileSpeed)
        {
            if (_target == null) return origin + transform.forward;
            Rigidbody targetBody = _target.GetComponent<Rigidbody>();
            Vector3 targetVelocity = targetBody != null ? targetBody.linearVelocity : Vector3.zero;
            float distance = Vector3.Distance(origin, _target.position);
            float travel = distance / Mathf.Max(1f, projectileSpeed);
            return _target.position + targetVelocity * travel;
        }

        /// <summary>
        /// Direction of a shot from a gun to the aim point, with a small
        /// random cone of spread so the guns are not laser accurate.
        /// </summary>
        private Vector3 AimDirection(Vector3 origin, Vector3 aimPoint)
        {
            Vector3 direction = aimPoint - origin;
            direction = direction.sqrMagnitude > 0.0001f ? direction.normalized : transform.forward;

            if (_spreadDegrees <= 0f) return direction;

            return Quaternion.Euler(
                Random.Range(-_spreadDegrees, _spreadDegrees),
                Random.Range(-_spreadDegrees, _spreadDegrees),
                0f) * direction;
        }

        [Server]
        private void Fire()
        {
            if (_bulletPrefab == null) return;
            if (_target == null) return;

            // Fire discipline: only shoot when the target is inside the gun
            // range and roughly in front of the nose.
            Vector3 toTarget = _target.position - transform.position;
            float distance = toTarget.magnitude;
            if (distance < _minFireRange || distance > _maxFireRange) return;
            if (distance > 0.0001f) toTarget /= distance;
            if (Vector3.Dot(transform.forward, toTarget) <= 0.7f) return;

            // Lead the shot: aim at the place the target will be when the
            // bolt arrives.
            Vector3 aimPoint = PredictTargetPoint(transform.position, _muzzleVelocity);

            Transform[] guns = ResolveFirePoints();

            if (guns.Length == 0)
            {
                // No guns on the model: fire from the nose so the ship still
                // shoots instead of going quiet.
                Vector3 direction = AimDirection(transform.position, aimPoint);
                SpawnBolt(transform.position + direction * 4f, direction);
                return;
            }

            if (_alternateFirePoints)
            {
                Transform gun = guns[_firePointIndex % guns.Length];
                _firePointIndex++;
                Vector3 direction = AimDirection(gun.position, aimPoint);
                SpawnBolt(MuzzlePosition(gun.position, direction), direction);
                return;
            }

            for (int i = 0; i < guns.Length; i++)
            {
                Vector3 direction = AimDirection(guns[i].position, aimPoint);
                SpawnBolt(MuzzlePosition(guns[i].position, direction), direction);
            }
        }

        private void SpawnBolt(Vector3 muzzle, Vector3 direction)
        {
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
        /// The guns of this ship: the ones assigned in the Inspector, or the
        /// markers that were placed on the model when nothing is assigned.
        /// The list is resolved once and then cached.
        /// </summary>
        private Transform[] ResolveFirePoints()
        {
            if (_resolvedFirePoints == null) _resolvedFirePoints = BuildFirePoints();
            return _resolvedFirePoints;
        }

        private Transform[] BuildFirePoints()
        {
            var guns = new List<Transform>();

            if (_firePoints != null)
            {
                for (int i = 0; i < _firePoints.Length; i++)
                {
                    // Skip the slots that were left empty in the Inspector.
                    if (_firePoints[i] != null) guns.Add(_firePoints[i]);
                }
            }

            if (guns.Count == 0) guns.AddRange(FindFirePointsOnModel());

            return guns.ToArray();
        }

        /// <summary>
        /// Looks for the gun markers under the model. Any child whose name
        /// contains "firepoint" or "muzzle" counts as a gun.
        /// </summary>
        private Transform[] FindFirePointsOnModel()
        {
            var found = new List<Transform>();
            foreach (Transform child in GetComponentsInChildren<Transform>(true))
            {
                if (child == transform) continue;
                string name = child.name.ToLowerInvariant();
                if (name.Contains("firepoint") || name.Contains("muzzle"))
                    found.Add(child);
            }
            return found.ToArray();
        }

        /// <summary>
        /// Pushes the spawn point a little ahead of the gun so a bolt never
        /// starts inside the hull mesh.
        ///
        /// The clearance is a plain distance and not a collider test on
        /// purpose: the hull of this ship is a coarse box that spans the
        /// whole model, so pushing the bolt to the edge of that box would
        /// move it dozens of metres away from the gun it belongs to.
        /// </summary>
        private Vector3 MuzzlePosition(Vector3 origin, Vector3 direction)
        {
            return origin + direction * _muzzleClearance;
        }

        public void TakeDamage(float amount)
        {
            if (!isServer || _health <= 0) return;
            _health -= Mathf.CeilToInt(amount);
            if (_health <= 0) Die();
        }

        private void Die()
        {
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
    }
}
