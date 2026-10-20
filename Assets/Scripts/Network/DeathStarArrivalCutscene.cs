using System.Collections;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BattleSky.Network
{
    /// <summary>
    /// Client-local cinematic reveal that plays when the Death Star spawns.
    ///
    /// Flow: the spawner sends a client RPC a fraction of a second after the
    /// station exists, <see cref="PlayLocal"/> creates a scene-level helper,
    /// and for <see cref="Duration"/> seconds the local camera is driven in
    /// world space from the cockpit out to a wide shot of the station, in to
    /// a closer angle, and finally back onto the ship's chase-camera pose.
    /// The station itself briefly starts a little small and grows to its real
    /// size during the first part of the shot.
    ///
    /// Controls are locked while <see cref="IsPlaying"/> is true (NetworkPlayer
    /// gates the pilot and gunner input on it). The shot can be skipped after
    /// half a second with any key, mouse button, or the south/start gamepad
    /// buttons; skipping blends back to the chase camera over 0.2 s.
    ///
    /// This is visual-only: nothing here is networked or replicated, and every
    /// client runs its own copy. The camera is returned to its saved local
    /// pose, the station scale to its prefab scale, and the helper object
    /// destroys itself when the shot ends.
    /// </summary>
    [DefaultExecutionOrder(1000)] // LateUpdate must win over NetworkPlayer's camera.
    public class DeathStarArrivalCutscene : MonoBehaviour
    {
        /// <summary>Total length of the shot in seconds.</summary>
        public const float Duration = 5f;

        /// <summary>True on this client while the shot is driving the camera.</summary>
        public static bool IsPlaying { get; private set; }

        private const float WideShotEnd = 0.15f;
        private const float CloseShotEnd = 0.80f;
        private const float CinematicFieldOfView = 48f;
        private const float StationGrowDuration = 3f;
        private const float SkipBlendDuration = 0.2f;
        private const float SkipDelay = 0.5f;

        private Camera _camera;
        private Transform _cameraParent;
        private Vector3 _savedLocalPos;
        private Quaternion _savedLocalRot;
        private float _savedFov;
        private Vector3 _savedWorldPos;
        private Quaternion _savedWorldRot;

        private DeathStar _station;
        private Vector3 _stationBaseScale = Vector3.one;
        private Vector3 _lookTarget;

        // Ship-relative pose snapshot: the camera is driven in world space so
        // the shot stays stable even while the ship drifts.
        private Vector3 _shipPos;
        private Vector3 _shipRight;
        private Vector3 _shipUp;
        private Vector3 _shipFwd;
        private Vector3 _wide;
        private Vector3 _close;
        private Vector3 _startPosition;
        private Quaternion _startRotation;

        private float _progress;
        private bool _skipRequested;
        private bool _skipping;
        private float _skipBlend;
        private Vector3 _skipFromPosition;
        private Quaternion _skipFromRotation;
        private float _skipFromFov;
        private bool _restored;

        /// <summary>
        /// Starts the arrival shot on this client. Safe to call from an RPC:
        /// it does nothing if a shot is already running, if there is no local
        /// player yet, or if the client has no camera.
        /// </summary>
        public static void PlayLocal()
        {
            if (IsPlaying) return;
            // Batch/CI runs have no presentation and must not lock simulated
            // input for five seconds; the cinematic is a player-facing feature.
            if (Application.isBatchMode) return;
            if (NetworkClient.localPlayer == null) return;

            Camera cam = NetworkClient.localPlayer.GetComponentInChildren<Camera>(true);
            if (cam == null) cam = Camera.main;
            if (cam == null) return;

            // Mark as playing before the component exists so a second RPC in
            // the same frame cannot create two helpers.
            IsPlaying = true;
            GameObject helper = new GameObject("DeathStarArrivalCutscene");
            helper.AddComponent<DeathStarArrivalCutscene>();
        }

        private void Start()
        {
            IsPlaying = true;

            NetworkIdentity localPlayer = NetworkClient.localPlayer;
            _camera = localPlayer != null ? localPlayer.GetComponentInChildren<Camera>(true) : null;
            if (_camera == null) _camera = Camera.main;
            if (_camera == null || localPlayer == null)
            {
                Restore();
                Destroy(gameObject);
                return;
            }

            Transform cameraTransform = _camera.transform;
            _cameraParent = cameraTransform.parent;
            _savedLocalPos = cameraTransform.localPosition;
            _savedLocalRot = cameraTransform.localRotation;
            _savedFov = _camera.fieldOfView;
            _savedWorldPos = cameraTransform.position;
            _savedWorldRot = cameraTransform.rotation;
            _startPosition = _savedWorldPos;
            _startRotation = _savedWorldRot;

            Transform ship = localPlayer.transform;
            _shipPos = ship.position;
            _shipRight = ship.right;
            _shipUp = ship.up;
            _shipFwd = ship.forward;

            _station = Object.FindFirstObjectByType<DeathStar>();
            if (_station != null)
            {
                _stationBaseScale = _station.transform.localScale;
                _lookTarget = _station.transform.position;
                // Visual-only arrival flourish: the station "drops in" a
                // little small and grows to its real 2 km size.
                _station.transform.localScale = _stationBaseScale * 0.8f;
            }
            else
            {
                _lookTarget = _shipPos + _shipFwd * 2600f;
            }

            _wide = _shipPos + _shipUp * 110f + _shipRight * 130f - _shipFwd * 60f;
            Vector3 towardStation = _station != null
                ? (_station.transform.position - _shipPos).normalized
                : _shipFwd;
            _close = _wide + _shipUp * 50f + _shipRight * 60f + towardStation * 240f;

            StartCoroutine(RunCutscene());
        }

        private void Update()
        {
            if (_restored || _skipRequested) return;
            if (_progress * Duration < SkipDelay) return;
            if (SkipPressed()) _skipRequested = true;
        }

        private void LateUpdate()
        {
            if (_restored || _camera == null) return;

            UpdateStationScale();

            // Computed live so the hand-back lands on wherever the ship's
            // chase camera is right now, not where it was when the shot began.
            Vector3 chasePosition = _cameraParent != null
                ? _cameraParent.TransformPoint(_savedLocalPos)
                : _savedWorldPos;
            Quaternion chaseRotation = _cameraParent != null
                ? _cameraParent.rotation * _savedLocalRot
                : _savedWorldRot;

            if (_skipping)
            {
                float skipT = Mathf.SmoothStep(0f, 1f, _skipBlend);
                _camera.transform.SetPositionAndRotation(
                    Vector3.Lerp(_skipFromPosition, chasePosition, skipT),
                    Quaternion.Slerp(_skipFromRotation, chaseRotation, skipT));
                _camera.fieldOfView = Mathf.Lerp(_skipFromFov, _savedFov, skipT);
                return;
            }

            Vector3 position;
            Quaternion lookRotation;

            if (_progress <= WideShotEnd)
            {
                float t = Mathf.SmoothStep(0f, 1f, _progress / WideShotEnd);
                position = Vector3.Lerp(_startPosition, _wide, t);
                // Blend the aim too, so the first frame is exactly the view
                // the pilot had before the shot started.
                lookRotation = Quaternion.Slerp(_startRotation, LookAt(_lookTarget, position), t);
            }
            else if (_progress <= CloseShotEnd)
            {
                float t = Mathf.SmoothStep(0f, 1f,
                    (_progress - WideShotEnd) / (CloseShotEnd - WideShotEnd));
                position = Vector3.Lerp(_wide, _close, t);
                lookRotation = LookAt(_lookTarget, position);
            }
            else
            {
                float t = Mathf.SmoothStep(0f, 1f,
                    (_progress - CloseShotEnd) / (1f - CloseShotEnd));
                position = Vector3.Lerp(_close, chasePosition, t);
                // Blend the aim onto the chase rotation as well, otherwise the
                // hand-back would snap the view direction on the last frame.
                lookRotation = Quaternion.Slerp(LookAt(_lookTarget, position), chaseRotation, t);
            }

            _camera.transform.SetPositionAndRotation(position, lookRotation);

            // FOV peaks in the middle of the shot: saved -> 48 -> saved.
            float fovBlend = 1f - Mathf.Abs(_progress * 2f - 1f);
            _camera.fieldOfView = Mathf.Lerp(_savedFov, CinematicFieldOfView,
                Mathf.SmoothStep(0f, 1f, fovBlend));
        }

        private void OnDestroy()
        {
            // Safety net: a helper destroyed mid-shot (scene change, disabled
            // player) must never leave the crew locked out of their controls.
            if (!_restored) Restore();
            IsPlaying = false;
        }

        /// <summary>
        /// Runs the 5 s timeline, then the optional skip blend and the restore.
        /// </summary>
        private IEnumerator RunCutscene()
        {
            float elapsed = 0f;
            while (elapsed < Duration)
            {
                if (_skipRequested) break;
                elapsed += Time.deltaTime;
                _progress = Mathf.Clamp01(elapsed / Duration);
                yield return null;
            }

            if (_skipRequested)
                yield return SkipBackRoutine();

            Restore();

            // One frame so the chase camera can take over before this object
            // disappears (its LateUpdate is gone as soon as it is destroyed).
            yield return null;
            if (this != null) Destroy(gameObject);
        }

        /// <summary>
        /// Blends from wherever the shot currently is back onto the live chase
        /// camera pose over <see cref="SkipBlendDuration"/>.
        /// </summary>
        private IEnumerator SkipBackRoutine()
        {
            _skipping = true;
            _skipBlend = 0f;
            _skipFromPosition = _camera != null ? _camera.transform.position : _savedWorldPos;
            _skipFromRotation = _camera != null ? _camera.transform.rotation : _savedWorldRot;
            _skipFromFov = _camera != null ? _camera.fieldOfView : _savedFov;

            float elapsed = 0f;
            while (elapsed < SkipBlendDuration)
            {
                elapsed += Time.deltaTime;
                _skipBlend = Mathf.Clamp01(elapsed / SkipBlendDuration);
                yield return null;
            }
            _skipBlend = 1f;
        }

        /// <summary>
        /// Hands the camera back and returns the station to its real scale.
        /// Idempotent, so the OnDestroy safety net can call it again.
        /// </summary>
        private void Restore()
        {
            if (_restored) return;
            _restored = true;

            try
            {
                if (_camera != null)
                {
                    _camera.transform.localPosition = _savedLocalPos;
                    _camera.transform.localRotation = _savedLocalRot;
                    _camera.fieldOfView = _savedFov;
                }

                if (_station != null)
                    _station.transform.localScale = _stationBaseScale;
            }
            finally
            {
                IsPlaying = false;
            }
        }

        private void UpdateStationScale()
        {
            if (_station == null) return;

            float grow = Mathf.Clamp01(_progress * Duration / StationGrowDuration);
            _station.transform.localScale = Vector3.Lerp(
                _stationBaseScale * 0.8f, _stationBaseScale, Mathf.SmoothStep(0f, 1f, grow));
        }

        private Quaternion LookAt(Vector3 target, Vector3 from)
        {
            Vector3 direction = target - from;
            if (direction.sqrMagnitude < 0.0001f) return _startRotation;

            direction = direction.normalized;
            Vector3 up = _shipUp;
            // A nearly vertical look direction has no stable up vector, so
            // fall back to the rig's up rather than getting a degenerate aim.
            if (Mathf.Abs(Vector3.Dot(direction, up)) > 0.99f)
                up = _cameraParent != null ? _cameraParent.up : Vector3.up;
            return Quaternion.LookRotation(direction, up);
        }

        private static bool SkipPressed()
        {
            if (Keyboard.current != null && Keyboard.current.anyKey.wasPressedThisFrame)
                return true;

            if (Mouse.current != null
                && (Mouse.current.leftButton.wasPressedThisFrame
                    || Mouse.current.rightButton.wasPressedThisFrame))
                return true;

            if (Gamepad.current != null
                && (Gamepad.current.buttonSouth.wasPressedThisFrame
                    || Gamepad.current.startButton.wasPressedThisFrame))
                return true;

            return false;
        }
    }
}
