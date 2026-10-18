using UnityEngine;

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
        ShipHealth shipHealth = col.gameObject.GetComponent<ShipHealth>();
        if (shipHealth != null)
        {
            shipHealth.TakeDamage(_collisionDamage);
            return;
        }

        NetworkPlayer netPlayer = col.gameObject.GetComponent<NetworkPlayer>();
        if (netPlayer != null)
        {
            netPlayer.TakeDamage(_collisionDamage);
        }
    }
}