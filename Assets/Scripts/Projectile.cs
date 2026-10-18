using UnityEngine;

public class Projectile : MonoBehaviour
{
    [Header("Lifetime")]
    [SerializeField] private float _lifetime = 4f;

    [Header("Impact")]
    [SerializeField] private GameObject _impactEffectPrefab;

    [Header("Damage")]
    [SerializeField]
    [Tooltip("10 damage dealt to ShipHealth / NetworkPlayer on hit")]
    private float _damage = 10f;

    public float Damage => _damage;

    private Rigidbody _rb;
    private float _spawnTime;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        if (_rb != null)
        {
            _rb.useGravity = false;
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }
    }

    public void Launch(float muzzleVelocity, Vector3 inheritedVelocity, Vector3 direction)
    {
        if (_rb != null)
        {
            _rb.linearVelocity = direction * muzzleVelocity + inheritedVelocity;
            transform.rotation = Quaternion.LookRotation(direction);
        }
        _spawnTime = Time.time;
    }

    private void Update()
    {
        if (Time.time - _spawnTime > _lifetime)
        {
            Destroy(gameObject);
        }
    }

    private void OnCollisionEnter(Collision col)
    {
        ShipHealth shipHealth = col.gameObject.GetComponent<ShipHealth>();
        if (shipHealth != null)
        {
            shipHealth.TakeDamage(_damage);
        }
        else
        {
            // Networked ships use NetworkPlayer health instead of ShipHealth.
            NetworkPlayer netPlayer = col.gameObject.GetComponent<NetworkPlayer>();
            if (netPlayer != null)
                netPlayer.TakeDamage(_damage);
        }

        if (_impactEffectPrefab != null)
        {
            ContactPoint contact = col.contacts[0];
            Instantiate(_impactEffectPrefab, contact.point, Quaternion.LookRotation(contact.normal));
        }
        Destroy(gameObject);
    }
}