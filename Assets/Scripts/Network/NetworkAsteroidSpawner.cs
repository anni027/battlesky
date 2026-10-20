using UnityEngine;
using System.Collections.Generic;
using Mirror;

namespace BattleSky.Network
{
    /// <summary>
    /// Populates the level 1 asteroid belt (persistent, drifting, colliding
    /// rocks laid out along the flight path to the checkpoint) and streams
    /// asteroids around the crew ship during levels 2 and 3.
    /// </summary>
    public class NetworkAsteroidSpawner : NetworkBehaviour
    {
        [Header("Streaming (levels 2-3)")]
        [SerializeField] private GameObject[] asteroidPrefabs;
        [SerializeField] private float spawnRate = 0.8f;
        [SerializeField] private int maxAsteroids = 40;
        [SerializeField] private float spawnRadius = 300f;
        [SerializeField] private float despawnRadius = 900f;

        [Header("Level 1 Belt")]
        [SerializeField] private int beltAsteroidCount = 320;
        [SerializeField] private float beltStartDistance = 120f;
        [SerializeField] private float beltEndDistance = 1720f;
        // A tighter belt volume packs the same rock count closer to the flight
        // path, raising near-path density without extra NetworkTransforms.
        [SerializeField] private float beltHalfWidth = 140f;
        [SerializeField] private float beltHalfHeight = 90f;
        [SerializeField] private float beltScaleMin = 0.55f;
        [SerializeField] private float beltScaleMax = 1.9f;
        [SerializeField] private float beltTopUpInterval = 1.5f;

        private readonly List<GameObject> activeAsteroids = new List<GameObject>();
        private readonly List<GameObject> beltAsteroids = new List<GameObject>();
        private float spawnTimer;
        private float beltTopUpTimer;
        private bool beltPopulated;
        private Vector3 beltOrigin;
        private Quaternion beltRotation = Quaternion.identity;

        private void Update()
        {
            if (!isServer) return;

            if (NetworkLevelManager.CurrentLevel == 1)
            {
                UpdateLevel1Belt();
                return;
            }

            if (beltPopulated)
                ClearLevel1Belt();

            spawnTimer += Time.deltaTime;
            float spawnInterval = 1f / Mathf.Max(0.01f, spawnRate);

            if (spawnTimer >= spawnInterval)
            {
                spawnTimer = 0f;
                TrySpawn();
            }

            DespawnFarAsteroids();
        }

        // ---- Level 1: a persistent, colliding asteroid belt ----

        private void UpdateLevel1Belt()
        {
            if (!beltPopulated)
            {
                Transform player = GetFirstPlayer();
                if (player == null) return;

                beltOrigin = player.position;
                beltRotation = player.rotation;
                for (int i = 0; i < beltAsteroidCount; i++)
                    SpawnBeltAsteroid(false);

                beltPopulated = true;
                Debug.Log($"[NetworkAsteroidSpawner] Level 1 belt populated with {beltAsteroids.Count} asteroids");
                return;
            }

            // Replace rocks the crew destroyed so the belt stays dense, but
            // never remove rocks that are simply behind the ship.
            beltTopUpTimer += Time.deltaTime;
            if (beltTopUpTimer < beltTopUpInterval) return;
            beltTopUpTimer = 0f;

            for (int i = beltAsteroids.Count - 1; i >= 0; i--)
            {
                if (beltAsteroids[i] == null)
                    beltAsteroids.RemoveAt(i);
            }

            if (beltAsteroids.Count < beltAsteroidCount)
                SpawnBeltAsteroid(true);
        }

        private void SpawnBeltAsteroid(bool aheadOfPlayer)
        {
            if (asteroidPrefabs == null || asteroidPrefabs.Length == 0) return;

            float startZ = beltStartDistance;
            if (aheadOfPlayer)
            {
                Transform player = GetFirstPlayer();
                if (player != null)
                {
                    Vector3 local = Quaternion.Inverse(beltRotation) * (player.position - beltOrigin);
                    startZ = Mathf.Clamp(local.z + 80f, beltStartDistance, beltEndDistance - 20f);
                }
            }

            float x = Random.Range(-beltHalfWidth, beltHalfWidth);
            float y = Random.Range(-beltHalfHeight, beltHalfHeight);
            float z = Random.Range(startZ, beltEndDistance);
            Vector3 position = beltOrigin + beltRotation * new Vector3(x, y, z);

            GameObject prefab = asteroidPrefabs[Random.Range(0, asteroidPrefabs.Length)];
            GameObject asteroid = Instantiate(prefab, position, Random.rotation);

            // The spawn message carries localScale, so every client sees the
            // same per-instance rock size without an extra transform sync.
            float scale = Random.Range(beltScaleMin, beltScaleMax);
            asteroid.transform.localScale = prefab.transform.localScale * scale;

            NetworkServer.Spawn(asteroid);
            beltAsteroids.Add(asteroid);
        }

        private void ClearLevel1Belt()
        {
            for (int i = 0; i < beltAsteroids.Count; i++)
            {
                if (beltAsteroids[i] != null)
                    NetworkServer.Destroy(beltAsteroids[i]);
            }

            beltAsteroids.Clear();
            beltPopulated = false;
            Debug.Log("[NetworkAsteroidSpawner] Level 1 belt cleared");
        }

        // ---- Levels 2-3: stream rocks around the crew ship ----

        private void TrySpawn()
        {
            if (activeAsteroids.Count >= maxAsteroids) return;
            if (asteroidPrefabs == null || asteroidPrefabs.Length == 0) return;

            Transform player = GetFirstPlayer();
            if (player == null) return;

            // Keep the field centred on the crew ship: the ship always drifts
            // forward, so a fixed belt would be left behind and culled.
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
