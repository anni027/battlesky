using UnityEngine;
using System.Collections.Generic;

public class AsteroidSpawner : MonoBehaviour
{
    [Header("Spawning")]
    [SerializeField]
    [Tooltip("Assign 2–3 asteroid prefab variants")]
    private GameObject[] _asteroidPrefabs;

    [SerializeField]
    [Tooltip("0.8f asteroids per second")]
    private float _spawnRate = 0.8f;

    [SerializeField]
    [Tooltip("40")]
    private int _maxAsteroids = 40;

    [SerializeField]
    [Tooltip("300f spawn this far from player")]
    private float _spawnRadius = 300f;

    [SerializeField]
    [Tooltip("400f destroy when this far from player")]
    private float _despawnRadius = 400f;

    [Header("Target")]
    [SerializeField]
    [Tooltip("Drag ship root here")]
    private Transform _player;

    private List<GameObject> _activeAsteroids = new List<GameObject>();
    private float _spawnTimer;

    private void Update()
    {
        _spawnTimer += Time.deltaTime;
        float spawnInterval = 1f / _spawnRate;

        if (_spawnTimer >= spawnInterval)
        {
            _spawnTimer = 0f;
            TrySpawn();
        }

        DespawnFarAsteroids();
    }

    private void TrySpawn()
    {
        if (_activeAsteroids.Count >= _maxAsteroids) return;
        if (_asteroidPrefabs == null || _asteroidPrefabs.Length == 0) return;
        if (_player == null) return;

        Vector3 randomDir = Random.onUnitSphere;
        Vector3 spawnPosition = _player.position + randomDir * _spawnRadius;

        int randomIndex = Random.Range(0, _asteroidPrefabs.Length);
        GameObject asteroid = Instantiate(_asteroidPrefabs[randomIndex], spawnPosition, Random.rotation);

        _activeAsteroids.Add(asteroid);
    }

    private void DespawnFarAsteroids()
    {
        if (_player == null) return;

        for (int i = _activeAsteroids.Count - 1; i >= 0; i--)
        {
            GameObject asteroid = _activeAsteroids[i];
            if (asteroid == null)
            {
                _activeAsteroids.RemoveAt(i);
                continue;
            }

            float distance = Vector3.Distance(asteroid.transform.position, _player.position);
            if (distance > _despawnRadius)
            {
                Destroy(asteroid);
                _activeAsteroids.RemoveAt(i);
            }
        }
    }
}