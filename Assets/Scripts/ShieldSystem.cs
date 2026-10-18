using UnityEngine;

public class ShieldSystem : MonoBehaviour
{
    [Header("Shield Stats")]
    [SerializeField]
    [Tooltip("100f")]
    private float _maxShieldEnergy = 100f;

    [SerializeField]
    [Tooltip("20f energy per second while active")]
    private float _drainRate = 20f;

    [SerializeField]
    [Tooltip("10f energy per second while inactive")]
    private float _rechargeRate = 10f;

    [SerializeField]
    [Tooltip("3f seconds after deactivation before recharge begins")]
    private float _rechargeDelay = 3f;

    [Header("Visual")]
    [SerializeField]
    [Tooltip("The shield bubble child object")]
    private GameObject _shieldMesh;

    [SerializeField]
    [Tooltip("45f degrees per second on Y axis while active")]
    private float _rotationSpeed = 45f;

    private float _currentEnergy;
    private float _rechargeTimer;
    private bool _wasActive;
    private ShipInputHandler _handler;
    private GunnerInputHandler _gunner;

    public float ShieldEnergyNormalized => _maxShieldEnergy > 0 ? _currentEnergy / _maxShieldEnergy : 0f;
    public bool IsShieldActive { get; private set; }

    private bool ShieldHeld => _gunner != null ? _gunner.IsShieldActive : (_handler != null && _handler.IsShieldActive);

    private void Awake()
    {
        _currentEnergy = _maxShieldEnergy;
        if (_shieldMesh != null)
        {
            _shieldMesh.SetActive(false);
        }
    }

    private void Start()
    {
        // Networked ships drive shields via NetworkPlayer (server-authoritative
        // SyncVars). A local ShieldSystem would fight it over the same mesh,
        // so stand down when a NetworkPlayer is in charge.
        if (GetComponent<NetworkPlayer>() != null || GetComponentInParent<NetworkPlayer>() != null)
        {
            enabled = false;
            return;
        }

        _handler = GetComponent<ShipInputHandler>();
        _gunner = GetComponent<GunnerInputHandler>();
        if (_gunner == null)
            _gunner = GetComponentInChildren<GunnerInputHandler>();
    }

    private void Update()
    {
        bool shieldHeld = ShieldHeld;

        bool shouldBeActive = shieldHeld && _currentEnergy > 0;

        if (shouldBeActive)
        {
            IsShieldActive = true;
            _currentEnergy -= _drainRate * Time.deltaTime;
            _rechargeTimer = 0f;

            if (_currentEnergy <= 0)
            {
                _currentEnergy = 0f;
                IsShieldActive = false;
            }
        }
        else
        {
            if (_wasActive)
            {
                _rechargeTimer += Time.deltaTime;
                if (_rechargeTimer >= _rechargeDelay)
                {
                    _currentEnergy += _rechargeRate * Time.deltaTime;
                }
            }
            else
            {
                _currentEnergy += _rechargeRate * Time.deltaTime;
            }

            _currentEnergy = Mathf.Clamp(_currentEnergy, 0f, _maxShieldEnergy);
            IsShieldActive = _currentEnergy > 0 && shieldHeld;
        }

        _wasActive = shouldBeActive;

        if (_shieldMesh != null)
        {
            _shieldMesh.SetActive(IsShieldActive);
            if (IsShieldActive)
            {
                _shieldMesh.transform.Rotate(Vector3.up, _rotationSpeed * Time.deltaTime, Space.World);
            }
        }
    }

    public float AbsorbDamage(float amount)
    {
        if (!IsShieldActive || _currentEnergy <= 0)
        {
            return amount;
        }

        _currentEnergy -= amount;

        if (_currentEnergy < 0)
        {
            float overflow = Mathf.Abs(_currentEnergy);
            _currentEnergy = 0f;
            return overflow;
        }

        return 0f;
    }
}