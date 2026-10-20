using System.Collections.Generic;
using UnityEngine;
using Mirror;

namespace BattleSky.Network
{
    /// <summary>
    /// Pairs connections into 2-player crews sharing one ship:
    /// 1st connection = PILOT (flies), 2nd = GUNNER (shoots + shields) on the
    /// same crewId, 3rd starts a new crew, etc. The gunner's ship object is an
    /// invisible proxy (see NetworkPlayer) that forwards gunner inputs to the
    /// crew ship and follows it for the camera ride.
    /// </summary>
    public class BattleSkyNetworkManager : NetworkManager
    {
        public static new BattleSkyNetworkManager singleton { get; private set; }

        [Header("Spawn Points")]
        [SerializeField] private Transform[] spawnPoints;

        [Header("Player Ship Prefab")]
        [SerializeField] private GameObject playerShipPrefab;

        [Header("Level Prefabs")]
        [SerializeField] private GameObject capitalShipPrefab;
        [SerializeField] private GameObject deathStarPrefab;

        [Header("Co-op Crews")]
        [SerializeField]
        [Tooltip("When true, connections are paired Pilot+Gunner per ship. When false, every connection gets a solo ship.")]
        private bool pairCrews = true;

        private int _currentSpawnIndex;
        private uint _nextCrewId = 1;
        private NetworkPlayer _waitingPilot;

        public override void Awake()
        {
            base.Awake();
            singleton = this;
            WireLevelPrefabs();
        }

        void WireLevelPrefabs()
        {
#if UNITY_EDITOR
            var spawner = FindAnyObjectByType<NetworkEnemySpawner>();
            if (spawner != null)
            {
                var so = new UnityEditor.SerializedObject(spawner);
                if (capitalShipPrefab != null)
                {
                    var p = so.FindProperty("capitalShipPrefab");
                    if (p != null) { p.objectReferenceValue = capitalShipPrefab; }
                }
            if (deathStarPrefab != null)
            {
                var p = so.FindProperty("deathStarPrefab");
                if (p != null) { p.objectReferenceValue = deathStarPrefab; }
            }
                so.ApplyModifiedPropertiesWithoutUndo();
            }
#endif
        }

        private void OnValidate()
        {
            // Keep Mirror's base playerPrefab in sync so Doctor's spawnable
            // checks and client spawn tables see the same ship prefab.
            if (playerPrefab == null && playerShipPrefab != null)
                playerPrefab = playerShipPrefab;
        }

        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            GameObject prefab = playerShipPrefab != null ? playerShipPrefab : playerPrefab;
            if (prefab == null)
            {
                Debug.LogError("[BattleSky] No player ship prefab assigned (playerShipPrefab / playerPrefab).");
                return;
            }
            Transform spawnPoint = GetNextSpawnPoint();

            if (prefab != null && prefab.GetComponent<NetworkPlayer>() != null
                && prefab.GetComponent<NetworkTransformBase>() == null
                && prefab.GetComponent<NetworkRigidbodyReliable>() == null
                && prefab.GetComponent<NetworkRigidbodyUnreliable>() == null)
            {
                Debug.LogWarning("[BattleSky] Player prefab has no NetworkTransform/NetworkRigidbody - remote crew members will see a frozen ship. Run BattleSky/Ensure Player Replication to patch the prefab.");
            }

            GameObject player = Instantiate(prefab, spawnPoint.position, spawnPoint.rotation);

            NetworkPlayer netPlayer = player.GetComponent<NetworkPlayer>();
            if (netPlayer != null && pairCrews)
            {
                if (_waitingPilot == null)
                {
                    netPlayer.crewRole = CrewRole.Pilot;
                    netPlayer.crewId = _nextCrewId;
                    netPlayer.hasGunner = false;
                    _waitingPilot = netPlayer;
                }
                else
                {
                    netPlayer.crewRole = CrewRole.Gunner;
                    netPlayer.crewId = _waitingPilot.crewId;
                    _waitingPilot.hasGunner = true;
                    _waitingPilot = null;
                    _nextCrewId++;
                }
            }
            else if (netPlayer != null)
            {
                netPlayer.crewRole = CrewRole.Solo;
                netPlayer.crewId = _nextCrewId++;
                netPlayer.hasGunner = false;
            }

            NetworkServer.AddPlayerForConnection(conn, player);
        }

        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            // Capture before base destroys the player object.
            NetworkPlayer leaving = conn.identity != null ? conn.identity.GetComponent<NetworkPlayer>() : null;
            bool wasProxy = leaving != null && leaving.IsGunnerProxy;
            uint leftCrewId = leaving != null ? leaving.crewId : 0;
            Vector3 leftPos = leaving != null ? leaving.transform.position : Vector3.zero;
            Quaternion leftRot = leaving != null ? leaving.transform.rotation : Quaternion.identity;
            bool wasWaiting = leaving != null && _waitingPilot == leaving;

            base.OnServerDisconnect(conn);

            if (leaving == null)
                return;

            if (wasProxy)
            {
                // Gunner left: pilot keeps the ship, regains solo weapons.
                NetworkPlayer crewShip = FindCrewShip(leftCrewId);
                if (crewShip != null)
                    crewShip.hasGunner = false;
            }
            else
            {
                // Pilot left.
                if (wasWaiting)
                {
                    _waitingPilot = null;
                }
                else
                {
                    // Promote the gunner proxy into the visible crew ship.
                    NetworkPlayer proxy = FindCrewProxy(leftCrewId);
                    if (proxy != null)
                    {
                        proxy.transform.SetPositionAndRotation(leftPos, leftRot);
                        proxy.PromoteProxyToPilot();
                    }
                }
            }
        }

        [Server]
        private NetworkPlayer FindCrewShip(uint crewId)
        {
            foreach (var kv in NetworkServer.spawned)
            {
                NetworkPlayer p = kv.Value != null ? kv.Value.GetComponent<NetworkPlayer>() : null;
                if (p == null || p.IsGunnerProxy) continue;
                if (p.crewId == crewId) return p;
            }
            if (_waitingPilot != null && _waitingPilot.crewId == crewId)
                return _waitingPilot;
            return null;
        }

        [Server]
        private NetworkPlayer FindCrewProxy(uint crewId)
        {
            foreach (var kv in NetworkServer.spawned)
            {
                NetworkPlayer p = kv.Value != null ? kv.Value.GetComponent<NetworkPlayer>() : null;
                if (p == null || !p.IsGunnerProxy) continue;
                if (p.crewId == crewId) return p;
            }
            return null;
        }

        private Transform GetNextSpawnPoint()
        {
            if (spawnPoints == null || spawnPoints.Length == 0)
            {
                return transform;
            }
            Transform point = spawnPoints[_currentSpawnIndex];
            _currentSpawnIndex = (_currentSpawnIndex + 1) % spawnPoints.Length;
            return point;
        }

        public void SetPlayerShipPrefab(GameObject prefab)
        {
            playerPrefab = prefab;
        }
    }
}
