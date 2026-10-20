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
    /// Guns: the bolts leave from the fire point transforms on the hull
    /// (the "firepoint" markers that live under the model). They are found
    /// by name when nothing is assigned in the Inspector, so a fresh model
    /// with new markers keeps working without rewiring the prefab.
    /// </summary>
    public class EnemyShip : NetworkBehaviour
    {
        [Header("Flight")]
        [SerializeField] private float _thrust = 12f;
        [SerializeField] private float _turnRate = 6f;
        [SerializeField] private float _maxSpeed = 22f;

        [Header("Combat")]
        [SerializeField] private float _fireRate = 1.2f;
        [SerializeField] private float _muzzleVelocity = 600f;
        [SerializeField] private GameObject _bulletPrefab;
        [Tooltip("Gun transforms on the hull. Found by name ('firepoint', 'muzzle') when left empty.")]
        [SerializeField] private Transform[] _firePoints;
        [Tooltip("On: the guns take turns, one bolt per salvo. Off: every gun fires on each salvo.")]
        [SerializeField] private bool _alternateFirePoints;
        [Tooltip("Metres in front of the gun where a bolt is spawned, so it never starts inside the hull.")]
        [SerializeField] private float _muzzleClearance = 4f;
        [SerializeField] private int _health = 100;
        public int Health => _health;

        [SerializeField] private GameObject _explosionPrefab;
        [SerializeField] private int _explosionCount = 4;
        [SerializeField] private float _explosionRadius = 1f;

        private Rigidbody _rb;
        private Transform _target;
        private float _fireTimer;
        private int _firePointIndex;
        private Transform[] _resolvedFirePoints;

        public override void OnStartServer()
        {
            _rb = GetComponent<Rigidbody>();
            PickTarget();
        }

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
        }

        [Server]
        private void Fire()
        {
            if (_bulletPrefab == null) return;

            // The ship always points its nose at the target, so that is the
            // direction every gun shoots along.
            Vector3 direction = transform.forward;
            Transform[] guns = ResolveFirePoints();

            if (guns.Length == 0)
            {
                // No guns on the model: fire from the nose so the ship still
                // shoots instead of going quiet.
                SpawnBolt(transform.position + direction * 4f, direction);
                return;
            }

            if (_alternateFirePoints)
            {
                Transform gun = guns[_firePointIndex % guns.Length];
                _firePointIndex++;
                SpawnBolt(MuzzlePosition(gun.position, direction), direction);
                return;
            }

            for (int i = 0; i < guns.Length; i++)
                SpawnBolt(MuzzlePosition(guns[i].position, direction), direction);
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
