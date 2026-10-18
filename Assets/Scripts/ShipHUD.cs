using UnityEngine;
using UnityEngine.UI;

public class ShipHUD : MonoBehaviour
{
    [Header("References")]
    [SerializeField]
    private ShipFlightModel _flightModel;

    [SerializeField]
    private ShipWeaponSystem _weaponSystem;

    [SerializeField]
    private ShipHealth _health;

    [SerializeField]
    private ShieldSystem _shield;

    [SerializeField]
    private ShipInputHandler _handler;

    [Header("Co-op Crew (optional)")]
    [SerializeField]
    private PilotInputHandler _pilot;

    [SerializeField]
    private GunnerInputHandler _gunner;

    [SerializeField]
    private ShipTurret _turret;

    [SerializeField]
    private CoopCrewManager _crew;

    [Header("Role Labels (optional)")]
    [SerializeField]
    private Text _roleText;

    [SerializeField]
    private Text _controlsHelpText;

    [Header("Health Bar")]
    [SerializeField]
    private Slider _healthBar;

    [SerializeField]
    private Text _healthText;

    [Header("Shield Bar")]
    [SerializeField]
    private Slider _shieldBar;

    [SerializeField]
    private Text _shieldText;

    [Header("Ammo")]
    [SerializeField]
    private Text _ammoText;

    [SerializeField]
    private Slider _reloadBar;

    [Header("Boost")]
    [SerializeField]
    private Slider _boostBar;

    [SerializeField]
    private Image _boostReadyIcon;

    [Header("Flight Assist")]
    [SerializeField]
    private Text _flightAssistText;

    [Header("Speed")]
    [SerializeField]
    private Text _speedText;

    [Header("Nose Reticle")]
    [SerializeField]
    private RectTransform _noseReticle;

    [SerializeField]
    private Camera _mainCamera;

    [SerializeField]
    private Transform _shipTransform;

    [SerializeField]
    [Tooltip("500f")]
    private float _noseProjectDist = 500f;

    [Header("Center Crosshair")]
    [SerializeField]
    private RectTransform _centerCrosshair;

    [SerializeField]
    private float _crosshairSize = 20f;

    private void LateUpdate()
    {
        if (_crew == null)
            _crew = FindAnyObjectByType<CoopCrewManager>();
        if (_health != null && _healthBar != null)
        {
            _healthBar.value = _health.HealthNormalized;
        }

        if (_health != null && _healthText != null)
        {
            _healthText.text = $"{Mathf.CeilToInt(_health.CurrentHealth)} HP";
        }

        if (_shield != null && _shieldBar != null)
        {
            _shieldBar.value = _shield.ShieldEnergyNormalized;
        }

        if (_shield != null && _shieldText != null)
        {
            _shieldText.text = _shield.IsShieldActive ? "SHIELD ACTIVE" : "SHIELD OFF";
        }

        if (_weaponSystem != null && _ammoText != null)
        {
            if (_weaponSystem.IsReloading)
            {
                _ammoText.text = $"RELOADING {(_weaponSystem.ReloadProgress * 100f):0}%";
            }
            else
            {
                _ammoText.text = $"{_weaponSystem.CurrentAmmo} RDS";
            }
        }

        if (_weaponSystem != null && _reloadBar != null)
        {
            _reloadBar.gameObject.SetActive(_weaponSystem.IsReloading);
            if (_weaponSystem.IsReloading)
            {
                _reloadBar.value = _weaponSystem.ReloadProgress;
            }
        }

        if (_flightModel != null && _boostBar != null)
        {
            _boostBar.value = _flightModel.BoostCooldownNormalized;
        }

        if (_flightModel != null && _boostReadyIcon != null)
        {
            _boostReadyIcon.enabled = _flightModel.IsBoostReady;
        }

        if ((_handler != null || _pilot != null) && _flightAssistText != null)
        {
            bool faOn = _pilot != null ? _pilot.FlightAssist : (_handler != null && _handler.FlightAssist);
            _flightAssistText.text = faOn ? "FA  ON" : "FA OFF";
        }

        if (_roleText != null)
        {
            if (_crew != null && _crew.IsCoop)
                _roleText.text = "CO-OP: P1 PILOT flies / P2 GUNNER shoots+shield";
            else if (_gunner != null)
                _roleText.text = "GUNNER: aim + fire + shield";
            else if (_pilot != null)
                _roleText.text = "PILOT: fly the ship";
            else
                _roleText.text = "SOLO";
        }

        if (_controlsHelpText != null)
        {
            if (_crew != null && _crew.IsCoop)
                _controlsHelpText.text = _crew.PilotHelp + "\n" + _crew.GunnerHelp;
            else if (_crew != null)
                _controlsHelpText.text = _crew.PilotHelp;
            else
                _controlsHelpText.text = "";
        }

        if (_flightModel != null && _speedText != null)
        {
            Rigidbody rb = _flightModel.GetComponent<Rigidbody>();
            if (rb != null)
            {
                _speedText.text = $"{rb.linearVelocity.magnitude:0} m/s";
            }
        }

        if (_noseReticle != null && _mainCamera != null && _shipTransform != null)
        {
            // Co-op: nose reticle tracks the PILOT's nose, gunner crosshair stays centered
            // (turret aim). Solo: same as before.
            Vector3 worldPoint = _shipTransform.position + _shipTransform.forward * _noseProjectDist;
            Vector3 screenPoint = _mainCamera.WorldToScreenPoint(worldPoint);
            bool inFront = screenPoint.z > 0f;

            _noseReticle.gameObject.SetActive(inFront);
            if (inFront)
            {
                _noseReticle.position = new Vector3(screenPoint.x, screenPoint.y, 0f);
            }
        }

        if (_centerCrosshair != null)
        {
            _centerCrosshair.position = new Vector3(Screen.width / 2f, Screen.height / 2f, 0f);
        }
    }
}