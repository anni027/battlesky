using Mirror;
using UnityEngine;

namespace BattleSky.Network
{
    /// <summary>
    /// Enemy ship flown by server AI. It hunts the nearest player ship,
    /// fires networked bolts at it, and takes damage from player bolts.
    /// Destroyed when health reaches zero.
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
        [SerializeField] private int _health = 100;
        public int Health => _health;

        [SerializeField] private GameObject _explosionPrefab;
        [SerializeField] private int _explosionCount = 4;
        [SerializeField] private float _explosionRadius = 1f;

        private Rigidbody _rb;
        private Transform _target;
        private float _fireTimer;

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
            Vector3 muzzle = transform.position + transform.forward * 4f;
            GameObject bolt = Instantiate(_bulletPrefab, muzzle, transform.rotation);
            var proj = bolt.GetComponent<Projectile>();
            if (proj != null)
                proj.Launch(_muzzleVelocity, _rb.linearVelocity, transform.forward, transform);
            NetworkServer.Spawn(bolt);
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
            NetworkServer.Destroy(gameObject);
        }

        private void OnCollisionEnter(Collision col)
        {
            if (!isServer) return;
            var proj = col.gameObject.GetComponent<Projectile>();
            if (proj != null)
                TakeDamage(proj.Damage);
        }
    }
}
