using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;

public class ShipHealth : MonoBehaviour
{
    [Header("Health")]
    [SerializeField]
    [Tooltip("100f")]
    private float _maxHealth = 100f;

    [Header("Death")]
    [SerializeField]
    [Tooltip("2f seconds before scene reload")]
    private float _deathDelay = 2f;

    private float _currentHealth;
    private bool _isDead;
    private ShieldSystem _shieldSystem;
    private ShipFlightModel _flightModel;
    private ShipWeaponSystem _weaponSystem;

    public float CurrentHealth => _currentHealth;
    public float HealthNormalized => _maxHealth > 0 ? _currentHealth / _maxHealth : 0f;
    public bool IsDead => _isDead;

    private void Awake()
    {
        _currentHealth = _maxHealth;
    }

    private void Start()
    {
        _shieldSystem = GetComponent<ShieldSystem>();
        _flightModel = GetComponent<ShipFlightModel>();
        _weaponSystem = GetComponent<ShipWeaponSystem>();
    }

    public float TakeDamage(float amount)
    {
        if (_isDead) return amount;

        float actualDamage = amount;

        if (_shieldSystem != null && _shieldSystem.IsShieldActive)
        {
            float overflow = _shieldSystem.AbsorbDamage(amount);
            actualDamage = overflow;
        }

        if (actualDamage > 0)
        {
            _currentHealth -= actualDamage;
            _currentHealth = Mathf.Max(0, _currentHealth);

            if (_currentHealth <= 0f && !_isDead)
            {
                Die();
            }
        }

        return actualDamage;
    }

    public void Heal(float amount)
    {
        if (_isDead) return;
        _currentHealth = Mathf.Min(_maxHealth, _currentHealth + amount);
    }

    private void Die()
    {
        _isDead = true;

        if (_flightModel != null)
        {
            _flightModel.enabled = false;
        }

        if (_weaponSystem != null)
        {
            _weaponSystem.enabled = false;
        }

        StartCoroutine(DeathSequence());
    }

    private IEnumerator DeathSequence()
    {
        yield return new WaitForSeconds(_deathDelay);
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }
}