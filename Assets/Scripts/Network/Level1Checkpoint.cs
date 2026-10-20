using Mirror;
using UnityEngine;

namespace BattleSky.Network
{
    /// <summary>
    /// Level 1 objective gate. Every client builds the same glowing ring from
    /// primitives (no prefab/network identity needed); the server advances to
    /// level 2 as soon as a crew ship flies through it. Clients hide the ring
    /// once their own ship reaches it because level changes are not replicated.
    /// </summary>
    public class Level1Checkpoint : MonoBehaviour
    {
        /// <summary>
        /// The active level 1 gate, or null when the scene has none. The HUD
        /// uses this for its gate-distance readout.
        /// </summary>
        public static Level1Checkpoint Instance { get; private set; }

        /// <summary>
        /// True once the gate has been cleared. Clients also set this when
        /// their own crew ship passes the gate, since level changes are not
        /// replicated.
        /// </summary>
        public bool Reached => _reached;

        [Header("Placement")]
        [SerializeField]
        [Tooltip("Spawn origin (usually the NetworkManager). The gate snaps this far ahead of it at runtime so it always sits on the level 1 flight path")]
        private Transform _origin;
        [SerializeField]
        [Tooltip("Distance ahead of the spawn origin (metres)")]
        private float _distanceAhead = 1800f;

        [Header("Ring")]
        [SerializeField]
        [Tooltip("Radius of the visible gate ring (metres)")]
        private float _ringRadius = 100f;
        [SerializeField]
        [Tooltip("How many glowing segments make up the ring")]
        private int _ringSegments = 28;
        [SerializeField]
        [Tooltip("Length of each segment (metres)")]
        private float _segmentLength = 20f;
        [SerializeField]
        [Tooltip("Thickness of each segment (metres)")]
        private float _segmentThickness = 5f;
        [SerializeField]
        [Tooltip("Slow visual spin (degrees per second)")]
        private float _spinSpeed = 6f;
        [SerializeField]
        [Tooltip("Glow material for the ring. Falls back to an emissive URP material when empty")]
        private Material _ringMaterial;

        [Header("Trigger")]
        [SerializeField]
        [Tooltip("A crew ship within this distance of the gate centre clears level 1")]
        private float _triggerRadius = 140f;

        private Transform _ring;
        private bool _reached;

        private void Awake()
        {
            Instance = this;

            if (_origin != null)
            {
                transform.SetPositionAndRotation(
                    _origin.position + _origin.forward * _distanceAhead,
                    _origin.rotation);
            }

            BuildRing();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        private void Start()
        {
            // The level is replicated now, so a client that joins during
            // level 2 or 3 also hides the level 1 gate.
            if (NetworkLevelManager.CurrentLevel > 1)
                HideRing();
        }

        private void Update()
        {
            if (_ring != null && _ring.gameObject.activeSelf)
                _ring.Rotate(0f, 0f, _spinSpeed * Time.deltaTime, Space.Self);

            if (NetworkServer.active)
            {
                if (!_reached && NetworkLevelManager.CurrentLevel == 1)
                    CheckCrewShipReached();
            }
            else
            {
                HideWhenLocalShipReached();
            }
        }

        private void CheckCrewShipReached()
        {
            foreach (var conn in NetworkServer.connections)
            {
                NetworkPlayer player = conn.Value?.identity != null
                    ? conn.Value.identity.GetComponent<NetworkPlayer>()
                    : null;
                if (player == null || player.IsGunnerProxy)
                    continue;

                if (Vector3.Distance(player.transform.position, transform.position) <= _triggerRadius)
                {
                    _reached = true;
                    HideRing();
                    NetworkLevelManager.Advance(2);
                    Debug.Log("[Level1Checkpoint] Crew ship reached the gate - level 2 begins");
                    return;
                }
            }
        }

        private void HideWhenLocalShipReached()
        {
            if (_ring == null || !_ring.gameObject.activeSelf)
                return;

            Transform ship = ResolveLocalShip();
            if (ship == null)
                return;

            if (Vector3.Distance(ship.position, transform.position) <= _triggerRadius + 25f)
            {
                // The server owns level changes; marking the gate as reached
                // here lets client-side HUDs drop their distance readout too.
                _reached = true;
                HideRing();
            }
        }

        /// <summary>Distance in metres from a world position to the gate centre.</summary>
        public float DistanceTo(Vector3 worldPosition)
        {
            return Vector3.Distance(worldPosition, transform.position);
        }

        private Transform ResolveLocalShip()
        {
            NetworkPlayer local = LocalPlayerExtensions.FindLocalPlayerSafe();
            if (local == null)
                return null;
            if (!local.IsGunnerProxy)
                return local.transform;

            if (local.crewId == 0)
                return null;

            foreach (NetworkPlayer player in FindObjectsByType<NetworkPlayer>(FindObjectsSortMode.None))
            {
                if (!player.IsGunnerProxy && player.crewId == local.crewId)
                    return player.transform;
            }
            return null;
        }

        private void HideRing()
        {
            if (_ring != null)
                _ring.gameObject.SetActive(false);
        }

        private void BuildRing()
        {
            GameObject ringObject = new GameObject("Ring");
            ringObject.transform.SetParent(transform, false);
            _ring = ringObject.transform;

            Material material = _ringMaterial;
            if (material == null)
                material = BuildFallbackMaterial();

            for (int i = 0; i < _ringSegments; i++)
            {
                float angle = i * Mathf.PI * 2f / _ringSegments;
                Vector3 radial = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
                Vector3 tangent = new Vector3(-Mathf.Sin(angle), Mathf.Cos(angle), 0f);

                GameObject segment = GameObject.CreatePrimitive(PrimitiveType.Cube);
                segment.name = $"RingSegment_{i}";
                Collider collider = segment.GetComponent<Collider>();
                if (collider != null)
                    Destroy(collider);

                segment.transform.SetParent(_ring, false);
                segment.transform.localPosition = radial * _ringRadius;
                segment.transform.localRotation = Quaternion.LookRotation(tangent, Vector3.forward);
                segment.transform.localScale = new Vector3(_segmentThickness, _segmentThickness, _segmentLength);

                MeshRenderer renderer = segment.GetComponent<MeshRenderer>();
                if (renderer != null && material != null)
                    renderer.sharedMaterial = material;
            }
        }

        private static Material BuildFallbackMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                return null;

            Material material = new Material(shader);
            material.SetColor("_BaseColor", new Color(1f, 0.55f, 0.15f, 1f));
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", new Color(1f, 0.4f, 0.05f) * 2f);
            return material;
        }
    }
}
