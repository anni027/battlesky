using UnityEngine;

[DefaultExecutionOrder(100)]
public class ShipCameraRig : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform shipRoot;
    [SerializeField] private ShipFlightModel flightModel;
    [SerializeField] private ShipInputHandler inputHandler;
    [SerializeField] private PilotInputHandler pilotInputHandler;
    [SerializeField] private Transform cameraTransform;

    [Header("Velocity Stretch")]
    [SerializeField] private float stretchPerSpeed = 0.04f;
    [SerializeField] private float maxStretch = 8f;
    [SerializeField] private float stretchLerpSpeed = 4f;
    [SerializeField] private float returnLerpSpeed = 8f;

    [Header("Camera Roll")]
    [SerializeField] private float rollMultiplier = 0.3f;
    [SerializeField] private float rollLerpSpeed = 4f;
    [SerializeField] private float rollReturnLerpSpeed = 8f;

    [Header("Free-Look")]
    [SerializeField] private float freeLookSensitivity = 2f;
    [SerializeField] private float freeLookPitchLimit = 55f;
    [SerializeField] private float freeLookReturnSpeed = 8f;

    private float _minZOffset;
    private float _currentStretch;
    private float _currentRoll;
    private Vector2 _freeLookOffset;
    private float _originalLocalZ;

    void Awake()
    {
        if (cameraTransform == null)
            cameraTransform = transform;

        if (pilotInputHandler == null)
            pilotInputHandler = GetComponent<PilotInputHandler>();
        if (pilotInputHandler == null)
            pilotInputHandler = GetComponentInParent<PilotInputHandler>();

        _minZOffset = cameraTransform.localPosition.z;
        _originalLocalZ = cameraTransform.localPosition.z;
    }

    private bool FreeLooking => pilotInputHandler != null ? pilotInputHandler.IsFreeLooking : (inputHandler != null && inputHandler.IsFreeLooking);
    private Vector2 LookValue => pilotInputHandler != null ? pilotInputHandler.LookInput : (inputHandler != null ? inputHandler.LookInput : Vector2.zero);

    void LateUpdate()
    {
        if (shipRoot == null || cameraTransform == null) return;

        UpdateStretch();
        UpdateRoll();
        LookAtModel();
        HandleFreeLook();
    }

    private void UpdateStretch()
    {
        float forwardSpeed = Vector3.Dot(flightModel.ShipVelocity, shipRoot.forward);
        float targetStretch = Mathf.Clamp(forwardSpeed * stretchPerSpeed, 0f, maxStretch);

        float lerpSpeed = targetStretch > _currentStretch ? stretchLerpSpeed : returnLerpSpeed;
        _currentStretch = Mathf.Lerp(_currentStretch, targetStretch, lerpSpeed * Time.deltaTime);

        float newZ = _minZOffset - _currentStretch;
        cameraTransform.localPosition = new Vector3(
            cameraTransform.localPosition.x,
            cameraTransform.localPosition.y,
            newZ
        );
    }

    private void UpdateRoll()
    {
        float shipRoll = GetShipRoll();
        float targetRoll = shipRoll * rollMultiplier;

        float lerpSpeed = Mathf.Abs(targetRoll) > Mathf.Abs(_currentRoll) ? rollLerpSpeed : rollReturnLerpSpeed;
        _currentRoll = Mathf.Lerp(_currentRoll, targetRoll, lerpSpeed * Time.deltaTime);
    }

    private float GetShipRoll()
    {
        Quaternion worldRotation = shipRoot.rotation;
        Quaternion localRotation = Quaternion.Inverse(transform.parent != null ? transform.parent.rotation : Quaternion.identity) * worldRotation;
        return localRotation.eulerAngles.z;
    }

    private void LookAtModel()
    {
        if (FreeLooking) return;

        Vector3 lookTarget = shipRoot.position;
        cameraTransform.LookAt(lookTarget);
    }

    private void HandleFreeLook()
    {
        if (FreeLooking)
        {
            Vector2 look = LookValue;
            _freeLookOffset.x += look.x * freeLookSensitivity;
            _freeLookOffset.y = Mathf.Clamp(
                _freeLookOffset.y + look.y * freeLookSensitivity,
                -freeLookPitchLimit,
                freeLookPitchLimit
            );
        }
        else
        {
            _freeLookOffset = Vector2.Lerp(_freeLookOffset, Vector2.zero, freeLookReturnSpeed * Time.deltaTime);
        }

        Vector3 currentEuler = cameraTransform.localRotation.eulerAngles;
        cameraTransform.localRotation = Quaternion.Euler(-_freeLookOffset.y, _freeLookOffset.x, _currentRoll);
    }
}