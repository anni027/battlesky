using UnityEngine;
using System.Collections.Generic;
using Mirror;

namespace BattleSky.Network
{
    public class NetworkAsteroidSpawner : NetworkBehaviour
    {
        [Header("Spawning")]
        [SerializeField] private GameObject[] asteroidPrefabs;
        [SerializeField] private float spawnRate = 0.8f;
        [SerializeField] private int maxAsteroids = 40;
        [SerializeField] private float spawnRadius = 300f;
        [SerializeField] private float despawnRadius = 400f;

        private List<GameObject> activeAsteroids = new List<GameObject>();
        private float spawnTimer;

        private void Update()
        {
            if (!isServer) return;

            spawnTimer += Time.deltaTime;
            float spawnInterval = 1f / spawnRate;

            if (spawnTimer >= spawnInterval)
            {
                spawnTimer = 0f;
                TrySpawn();
            }

            DespawnFarAsteroids();
        }

        private void TrySpawn()
        {
            if (activeAsteroids.Count >= maxAsteroids) return;
            if (asteroidPrefabs == null || asteroidPrefabs.Length == 0) return;

            Transform player = GetFirstPlayer();
            if (player == null) return;

            Vector3 randomDir = Random.onUnitSphere;
            Vector3 spawnPosition = player.position + randomDir * spawnRadius;

            int randomIndex = Random.Range(0, asteroidPrefabs.Length);
            GameObject asteroid = Instantiate(asteroidPrefabs[randomIndex], spawnPosition, Random.rotation);
            NetworkServer.Spawn(asteroid);
            activeAsteroids.Add(asteroid);
        }

        private Transform GetFirstPlayer()
        {
            foreach (var conn in NetworkServer.connections)
            {
                if (conn.Value?.identity != null)
                {
                    return conn.Value.identity.transform;
                }
            }
            return null;
        }

        private void DespawnFarAsteroids()
        {
            Transform player = GetFirstPlayer();
            if (player == null)
            {
                foreach (var asteroid in activeAsteroids.ToArray())
                {
                    if (asteroid != null) NetworkServer.Destroy(asteroid);
                }
                activeAsteroids.Clear();
                return;
            }

            for (int i = activeAsteroids.Count - 1; i >= 0; i--)
            {
                GameObject asteroid = activeAsteroids[i];
                if (asteroid == null)
                {
                    activeAsteroids.RemoveAt(i);
                    continue;
                }

                float distance = Vector3.Distance(asteroid.transform.position, player.position);
                if (distance > despawnRadius)
                {
                    NetworkServer.Destroy(asteroid);
                    activeAsteroids.RemoveAt(i);
                }
            }
        }
    }
}