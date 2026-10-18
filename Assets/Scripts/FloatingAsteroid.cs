using UnityEngine;
using Mirror;

public class FloatingAsteroid : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField]
    [Tooltip("2f initial linear speed assigned on spawn")]
    private float _driftSpeed = 2f;

    [SerializeField]
    [Tooltip("15f degrees per second, random axis")]
    private float _tumbleSpeed = 15f;

    [Header("Damage")]
    [SerializeField]
    [Tooltip("15f")]
    private float _collisionDamage = 15f;

    private Rigidbody _rb;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        if (_rb != null)
        {
            _rb.useGravity = false;
            _rb.linearDamping = 0f;
            _rb.angularDamping = 0f;
            _rb.linearVelocity = Random.onUnitSphere * _driftSpeed;
            _rb.angularVelocity = Random.onUnitSphere * (_tumbleSpeed * Mathf.Deg2Rad);
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
        }
    }
}