using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace BattleSky.Network
{
    /// <summary>
    /// Spawns enemy ships once the level is 2 or higher. Enemies chase the
    /// nearest player and shoot at them; player bolts damage them back.
    /// </summary>
    public class NetworkEnemySpawner : NetworkBehaviour
    {
        [Header("Spawning")]
        [SerializeField] private GameObject enemyPrefab;
        [SerializeField] private float spawnRate = 0.5f;
        [SerializeField] private int maxEnemies = 12;
        [SerializeField] private float spawnRadius = 220f;
        [SerializeField] private float despawnRadius = 340f;

        private List<GameObject> _activeEnemies = new List<GameObject>();
        private float _spawnTimer;

        private void Update()
        {
            if (!isServer) return;
            if (NetworkLevelManager.CurrentLevel < 2) return;

            float rate = NetworkLevelManager.CurrentLevel == 3 ? spawnRate * 1.5f : spawnRate;
            int max = NetworkLevelManager.CurrentLevel == 3 ? maxEnemies + 8 : maxEnemies;

            _spawnTimer += Time.deltaTime;
            if (_spawnTimer >= 1f / rate)
            {
                _spawnTimer = 0f;
                TrySpawn(max);
            }

            DespawnFarEnemies();
        }

        private void TrySpawn(int maxEnemies)
        {
            if (_activeEnemies.Count >= maxEnemies) return;
            if (enemyPrefab == null) return;

            var players = Object.FindObjectsByType<NetworkPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (players.Length == 0) return;

            var target = players[Random.Range(0, players.Length)];
            Vector3 spawnPosition = target.transform.position + Random.onUnitSphere * spawnRadius;
            spawnPosition.y = Mathf.Max(spawnPosition.y, 5f);

            GameObject enemy = Instantiate(enemyPrefab, spawnPosition, Random.rotation);
            NetworkServer.Spawn(enemy);
            _activeEnemies.Add(enemy);
        }

        private void DespawnFarEnemies()
        {
            Transform player = GetFirstPlayer();
            if (player == null) return;

            for (int i = _activeEnemies.Count - 1; i >= 0; i--)
            {
                GameObject enemy = _activeEnemies[i];
                if (enemy == null)
                {
                    _activeEnemies.RemoveAt(i);
                    continue;
                }

                if (Vector3.Distance(enemy.transform.position, player.position) > despawnRadius)
                {
                    NetworkServer.Destroy(enemy);
                    _activeEnemies.RemoveAt(i);
                }
            }
        }

        private Transform GetFirstPlayer()
        {
            foreach (var conn in NetworkServer.connections)
                if (conn.Value?.identity != null)
                    return conn.Value.identity.transform;
            return null;
        }
    }
}
