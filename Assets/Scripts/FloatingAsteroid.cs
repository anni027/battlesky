using UnityEngine;
using Mirror;

public class FloatingAsteroid : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField]
    [Tooltip("Base initial linear speed assigned on spawn (m/s)")]
    private float _driftSpeed = 4f;

    [SerializeField]
    [Range(0f, 0.9f)]
    [Tooltip("Random +/- fraction applied to the drift speed so rocks move at different rates")]
    private float _driftSpeedJitter = 0.6f;

    [SerializeField]
    [Tooltip("Base tumble rate in degrees per second, random axis")]
    private float _tumbleSpeed = 20f;

    [SerializeField]
    [Range(0f, 0.9f)]
    [Tooltip("Random +/- fraction applied to the tumble rate")]
    private float _tumbleSpeedJitter = 0.6f;

    [Header("Damage")]
    [SerializeField]
    [Tooltip("15f")]
    private float _collisionDamage = 15f;

    [SerializeField]
    [Tooltip("10f - the damage a rock does to an enemy ship. Lower than the player damage because the enemies have no shields")]
    private float _enemyCollisionDamage = 10f;

    private Rigidbody _rb;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        if (_rb != null)
        {
            _rb.useGravity = false;
            _rb.linearDamping = 0f;
            _rb.angularDamping = 0f;
            float drift = _driftSpeed * Random.Range(1f - _driftSpeedJitter, 1f + _driftSpeedJitter);
            float tumble = _tumbleSpeed * Random.Range(1f - _tumbleSpeedJitter, 1f + _tumbleSpeedJitter);
            _rb.linearVelocity = Random.onUnitSphere * drift;
            _rb.angularVelocity = Random.onUnitSphere * (tumble * Mathf.Deg2Rad);
        }
    }

    private void OnCollisionEnter(Collision col)
    {
        // Networked asteroids are server-authoritative: client copies are
        // kinematic puppets, so only the server resolves collision damage.
        NetworkIdentity identity = GetComponent<NetworkIdentity>();
        if (identity != null && !identity.isServer)
            return;

        // GetComponentInParent so hull colliders on child objects still
        // resolve to the ship they belong to.
        ShipHealth shipHealth = col.gameObject.GetComponentInParent<ShipHealth>();
        if (shipHealth != null)
        {
            shipHealth.TakeDamage(_collisionDamage);
            return;
        }

        NetworkPlayer netPlayer = col.gameObject.GetComponentInParent<NetworkPlayer>();
        if (netPlayer != null)
        {
            netPlayer.TakeDamage(_collisionDamage);
            return;
        }

        // Enemy ships are not immune to the belt either: a rock that clips a
        // fighter hurts it. The enemy hulls live on child objects, so the
        // lookup has to walk up to the root that carries the health.
        BattleSky.Network.EnemyShip enemy = col.gameObject.GetComponentInParent<BattleSky.Network.EnemyShip>();
        if (enemy != null)
        {
            enemy.TakeDamage(_enemyCollisionDamage);
            DamageThisRock(_enemyCollisionDamage);
            return;
        }

        BattleSky.Network.CapitalShip capital = col.gameObject.GetComponentInParent<BattleSky.Network.CapitalShip>();
        if (capital != null)
        {
            capital.TakeDamage(_enemyCollisionDamage);
            DamageThisRock(_enemyCollisionDamage);
        }
    }

    /// <summary>
    /// The rock takes a share of the impact, so an asteroid that clips a
    /// fighter is chewed up instead of bouncing off untouched.
    /// </summary>
    private void DamageThisRock(float amount)
    {
        BattleSky.Network.NetworkAsteroid asteroid = GetComponent<BattleSky.Network.NetworkAsteroid>();
        if (asteroid != null) asteroid.TakeDamage(amount);
    }
}