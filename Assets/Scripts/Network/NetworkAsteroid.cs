using Mirror;
using UnityEngine;

namespace BattleSky.Network
{
    /// <summary>
    /// Makes a networked asteroid destructible. Bolts deal damage on the
    /// server through TakeDamage(); when health hits zero the asteroid
    /// spawns a few replicated impact pops and destroys itself on every
    /// client (NetworkServer.Destroy replicates the removal).
    /// FloatingAsteroid keeps handling drift/tumble and the damage a ship
    /// takes from ramming an asteroid.
    /// </summary>
    public class NetworkAsteroid : NetworkBehaviour
    {
        [Header("Health")]
        [SerializeField]
        [Tooltip("Damage the asteroid can take before breaking apart")]
        private int _health = 90;
        public int Health => _health;

        [Header("Destruction")]
        [SerializeField]
        [Tooltip("Impact flash spawned at the break point (replicated to all clients)")]
        private GameObject _explosionPrefab;
        [SerializeField]
        [Tooltip("How many impact pops burst when the asteroid breaks")]
        private int _explosionCount = 6;
        [SerializeField]
        [Tooltip("How far from the center the pops scatter")]
        private float _explosionRadius = 1.5f;

        /// <summary>Call from the server (e.g. from a Projectile hit).</summary>
        public void TakeDamage(float amount)
        {
            if (!isServer || _health <= 0)
                return;

            _health -= Mathf.CeilToInt(amount);
            if (_health <= 0)
                Die();
        }

        private void Die()
        {
            if (_explosionPrefab != null)
            {
                for (int i = 0; i < _explosionCount; i++)
                {
                    Vector3 offset = Random.insideUnitSphere * (_explosionRadius * 0.5f);
                    GameObject pop = Instantiate(_explosionPrefab,
                        transform.position + offset, Random.rotation);
                    NetworkServer.Spawn(pop);
                }
            }

            NetworkServer.Destroy(gameObject);
        }
    }
}
