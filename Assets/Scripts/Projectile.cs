using UnityEngine;
using Mirror;

public class Projectile : NetworkBehaviour
{
    [Header("Lifetime")]
    [SerializeField] private float _lifetime = 2f;

    [Header("Impact")]
    [SerializeField] private GameObject _impactEffectPrefab;

    [Header("Damage")]
    [SerializeField]
    [Tooltip("10 damage dealt to ShipHealth / NetworkPlayer on hit")]
    private float _damage = 10f;

    public float Damage => _damage;

    private Rigidbody _rb;
    private float _spawnTime;

    // Spawned bolts are server-authoritative: only the server simulates hits,
    // destroys, and replicates the impact effect. Client copies just get
    // interpolated by NetworkRigidbodyReliable and wait for the despawn.
    // Bullets that were never spawned (standalone/legacy scenes) count as
    // local-only and resolve their own hits.
    private NetworkIdentity _netIdentity;
    private bool IsNetworked => _netIdentity != null && _netIdentity.netId != 0;
    private bool IsServerSide => !IsNetworked || _netIdentity.isServer;

    private void Awake()
    {
        _netIdentity = GetComponent<NetworkIdentity>();
        _rb = GetComponent<Rigidbody>();
        if (_rb != null)
        {
            _rb.useGravity = false;
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }
    }

    /// <summary>
    /// Fires the bolt. <paramref name="ownerRoot"/> is the ship that fired it;
    /// collision with every collider under it is ignored so bolts never hit
    /// the hull they spawned inside of.
    /// </summary>
    public void Launch(float muzzleVelocity, Vector3 inheritedVelocity, Vector3 direction, Transform ownerRoot = null)
    {
        if (_rb != null)
        {
            _rb.linearVelocity = direction * muzzleVelocity + inheritedVelocity;
            transform.rotation = Quaternion.LookRotation(direction);
        }

        IgnoreOwnerColliders(ownerRoot);
        _spawnTime = Time.time;
    }

    private void IgnoreOwnerColliders(Transform ownerRoot)
    {
        if (ownerRoot == null) return;

        Collider self = GetComponent<Collider>();
        if (self == null) self = GetComponentInChildren<Collider>();
        if (self == null) return;

        foreach (Collider other in ownerRoot.GetComponentsInChildren<Collider>(true))
        {
            if (other != null && other != self)
                Physics.IgnoreCollision(self, other, true);
        }
    }

    private void Update()
    {
        if (Time.time - _spawnTime > _lifetime)
            Despawn();
    }

    private void OnCollisionEnter(Collision col)
    {
        // Clients only watch bolts fly; the server owns hits and despawning.
        if (!IsServerSide) return;

        // GetComponentInParent so child colliders (hull spheres, asteroid
        // meshes) still resolve to the ship/asteroid health on the root.
        ShipHealth shipHealth = col.gameObject.GetComponentInParent<ShipHealth>();
        if (shipHealth != null)
        {
            shipHealth.TakeDamage(_damage);
        }
        else
        {
            // Networked ships use NetworkPlayer health instead of ShipHealth.
            NetworkPlayer netPlayer = col.gameObject.GetComponentInParent<NetworkPlayer>();
            if (netPlayer != null)
                netPlayer.TakeDamage(_damage);
        }

        if (_impactEffectPrefab != null && col.contactCount > 0)
        {
            ContactPoint contact = col.GetContact(0);
            if (IsNetworked)
                RpcImpact(contact.point, contact.normal);
            else
                SpawnImpact(contact.point, contact.normal);
        }

        Despawn();
    }

    [ClientRpc]
    private void RpcImpact(Vector3 point, Vector3 normal)
    {
        SpawnImpact(point, normal);
    }

    private void SpawnImpact(Vector3 point, Vector3 normal)
    {
        if (_impactEffectPrefab == null) return;
        Instantiate(_impactEffectPrefab, point, Quaternion.LookRotation(normal));
    }

    private void Despawn()
    {
        if (!IsServerSide)
            return;

        if (IsNetworked)
            NetworkServer.Destroy(gameObject);
        else
            Destroy(gameObject);
    }
}
