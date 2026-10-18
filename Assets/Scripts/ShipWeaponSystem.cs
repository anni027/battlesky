using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;

public class ShipWeaponSystem : MonoBehaviour
{
    [Header("Firepoints")]
    [SerializeField]
    [Tooltip("Assign gun barrel transforms in Inspector")]
    private Transform[] _firePoints;

    [Header("Projectile")]
    [SerializeField]
    [Tooltip("Projectile prefab")]
    private GameObject _projectilePrefab;

    [Header("Aiming")]
    [SerializeField]
    [Tooltip("Camera used for aim direction. Falls back to Camera.main if null.")]
    private Camera _aimCamera;

    [SerializeField]
    [Tooltip("1200f m/s, added to ship velocity")]
    private float _muzzleVelocity = 1200f;

    [SerializeField]
    [Tooltip("Meters ahead of the muzzle the bolt spawns so it never starts inside the hull collider")]
    private float _muzzleSpawnOffset = 12f;

    [Header("Ammo")]
    [SerializeField]
    [Tooltip("180")]
    private int _maxAmmo = 180;

    [SerializeField]
    [Tooltip("1.6f seconds")]
    private float _reloadTime = 1.6f;

    [Header("Fire Rate")]
    [SerializeField]
    [Tooltip("10f rounds per second")]
    private float _fireRate = 10f;

    private int _currentAmmo;
    private float _fireCooldown;
    private int _currentFirePointIndex;
    private bool _isReloading;
    private float _reloadProgress;
    private Rigidbody _shipRigidbody;

    private ShipInputHandler _handler;
    private GunnerInputHandler _gunner;
    private ShipTurret _turret;

    public int CurrentAmmo => _currentAmmo;
    public bool IsReloading => _isReloading;
    public float ReloadProgress => _reloadProgress;

    private bool FireHeld => _gunner != null ? _gunner.IsFiring : (_handler != null && _handler.IsFiring);
    private bool ReloadWanted
    {
        get
        {
            if (_gunner != null && _gunner.ReloadPressed) return true;
            return Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame;
        }
    }

    private void Awake()
    {
        _currentAmmo = _maxAmmo;
        _shipRigidbody = GetComponent<Rigidbody>();
    }

    private void Start()
    {
        _handler = GetComponent<ShipInputHandler>();
        _gunner = GetComponent<GunnerInputHandler>();
        if (_gunner == null)
            _gunner = GetComponentInChildren<GunnerInputHandler>();
        _turret = GetComponent<ShipTurret>();
        if (_turret == null)
            _turret = GetComponentInChildren<ShipTurret>();
    }

    private void Update()
    {
        if (_isReloading) return;

        _fireCooldown -= Time.deltaTime;

        if (FireHeld && _currentAmmo > 0 && _fireCooldown <= 0)
        {
            FireProjectile();
        }

        if (_currentAmmo == 0 && !_isReloading)
        {
            StartReload();
        }

        if (ReloadWanted && _currentAmmo > 0 && !_isReloading)
        {
            if (_gunner != null)
                _gunner.ConsumeReloadPress();
            StartReload();
        }
    }

    private void FireProjectile()
    {
        if (_firePoints == null || _firePoints.Length == 0 || _projectilePrefab == null) return;

        Transform firePoint = _firePoints[_currentFirePointIndex];

        // Co-op: gunner turret aims independent of the ship nose.
        // Solo: camera-forward (pilot aims where they look).
        Vector3 shootDirection;
        if (_turret != null && _gunner != null)
            shootDirection = _turret.AimDirection;
        else if (_gunner != null && _gunner.AimInput.sqrMagnitude > 0.01f && _turret == null)
            shootDirection = firePoint.forward; // gunner present but no turret: nose guns
        else
        {
            Camera cam = _aimCamera != null ? _aimCamera : Camera.main;
            shootDirection = cam != null ? cam.transform.forward : firePoint.forward;
        }

        GameObject projectile = Instantiate(_projectilePrefab, firePoint.position + shootDirection * _muzzleSpawnOffset, Quaternion.LookRotation(shootDirection));

        Projectile proj = projectile.GetComponent<Projectile>();
        if (proj != null)
        {
            proj.Launch(_muzzleVelocity, _shipRigidbody.linearVelocity, shootDirection, transform);
        }

        _currentAmmo--;
        _fireCooldown = 1f / _fireRate;
        _currentFirePointIndex = (_currentFirePointIndex + 1) % _firePoints.Length;
    }

    public void StartReload()
    {
        if (_isReloading || _currentAmmo >= _maxAmmo) return;
        StartCoroutine(ReloadCoroutine());
    }

    private IEnumerator ReloadCoroutine()
    {
        _isReloading = true;
        _reloadProgress = 0f;

        float elapsed = 0f;
        while (elapsed < _reloadTime)
        {
            elapsed += Time.deltaTime;
            _reloadProgress = elapsed / _reloadTime;
            yield return null;
        }

        _currentAmmo = _maxAmmo;
        _isReloading = false;
        _reloadProgress = 0f;
    }
}