using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Mirror;
using Mirror.Discovery;

namespace BattleSky.Network
{
    public class MultiplayerHUD : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private GameObject menuPanel;
        [SerializeField] private GameObject hostButton;
        [SerializeField] private GameObject joinButton;
        [SerializeField] private GameObject disconnectButton;
        [SerializeField] private InputField addressInput;
        [SerializeField] private Text statusText;
        [SerializeField] private GameObject serverListPanel;
        [SerializeField] private Transform serverListContent;
        [SerializeField] private GameObject serverEntryPrefab;
        [SerializeField] private Button refreshButton;

        private BattleSkyNetworkManager _networkManager;
        private BattleSkyNetworkDiscovery _networkDiscovery;
        private List<ServerResponse> _discoveredServers = new List<ServerResponse>();

        private void Awake()
        {
            _networkManager = FindAnyObjectByType<BattleSkyNetworkManager>();
            _networkDiscovery = FindAnyObjectByType<BattleSkyNetworkDiscovery>();

            if (_networkDiscovery != null)
            {
                _networkDiscovery.OnServerFound += OnServerFound;
            }

            if (refreshButton != null)
            {
                refreshButton.onClick.AddListener(StartDiscovery);
            }
        }

        private void Start()
        {
            ShowMenu(true);
        }

        private void OnDestroy()
        {
            if (_networkDiscovery != null)
            {
                _networkDiscovery.OnServerFound -= OnServerFound;
            }
        }

        private void Update()
        {
            if (NetworkClient.isConnected)
            {
                ShowMenu(false);
                statusText.text = $"Connected - {LocalCrewRoleText()} | {LocalCrewHelp()}";
            }
            else if (NetworkServer.active)
            {
                ShowMenu(false);
                statusText.text = $"Hosting - {LocalCrewRoleText()} | {LocalCrewHelp()}";
            }
        }

        private string LocalCrewRoleText()
        {
            NetworkPlayer local = FindLocalPlayer();
            if (local == null) return "joining crew...";
            if (local.IsGunnerProxy)
                return "P2 GUNNER: you shoot + shield, pilot flies";
            if (local.hasGunner)
                return "P1 PILOT: you fly, gunner shoots + shields";
            return "SOLO: you fly + shoot (gunner joins next)";
        }

        private string LocalCrewHelp()
        {
            NetworkPlayer local = FindLocalPlayer();
            if (local != null && local.IsGunnerProxy)
                return "Click fire, Space shield, R reload";
            return "W/S thrust, mouse steer, Click fire, Space shield";
        }

        private NetworkPlayer FindLocalPlayer()
        {
            if (NetworkClient.localPlayer != null)
            {
                NetworkPlayer p = NetworkClient.localPlayer.GetComponent<NetworkPlayer>();
                if (p != null) return p;
            }
            foreach (NetworkPlayer p in FindObjectsByType<NetworkPlayer>(FindObjectsSortMode.None))
            {
                if (p.isLocalPlayer) return p;
            }
            return null;
        }

        public void ShowMenu(bool show)
        {
            menuPanel.SetActive(show);
            if (show)
            {
                hostButton.SetActive(!NetworkServer.active && !NetworkClient.active);
                joinButton.SetActive(!NetworkServer.active && !NetworkClient.active);
                disconnectButton.SetActive(NetworkServer.active || NetworkClient.active);
            }
        }

        public void StartHost()
        {
            _networkManager.StartHost();
            if (_networkDiscovery != null)
            {
                _networkDiscovery.AdvertiseServer();
            }
        }

        public void StartClient()
        {
            string address = addressInput.text;
            if (!string.IsNullOrEmpty(address))
            {
                _networkManager.networkAddress = address;
                _networkManager.StartClient();
            }
        }

        public void StartDiscovery()
        {
            _discoveredServers.Clear();
            ClearServerList();

            if (_networkDiscovery != null)
            {
                _networkDiscovery.StartDiscovery();
            }
        }

        public void JoinServer(ServerResponse server)
        {
            _networkManager.networkAddress = server.uri.Host;
            _networkManager.StartClient();
        }

        private void OnServerFound(ServerResponse server)
        {
            if (!_discoveredServers.Contains(server))
            {
                _discoveredServers.Add(server);
                AddServerToList(server);
            }
        }

        private void AddServerToList(ServerResponse server)
        {
            if (serverEntryPrefab == null || serverListContent == null) return;

            GameObject entry = Instantiate(serverEntryPrefab, serverListContent);
            entry.GetComponentInChildren<Text>().text = $"Server - {server.uri.Host}";
            entry.GetComponent<Button>().onClick.AddListener(() => JoinServer(server));
        }

        private void ClearServerList()
        {
            foreach (Transform child in serverListContent)
            {
                Destroy(child.gameObject);
            }
        }

        public void StopNetwork()
        {
            _networkManager.StopHost();
            _networkManager.StopClient();
            _networkManager.StopServer();

            if (_networkDiscovery != null)
            {
                _networkDiscovery.StopDiscovery();
            }

            ShowMenu(true);
        }
    }
}