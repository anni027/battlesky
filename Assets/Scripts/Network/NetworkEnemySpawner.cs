using System.Collections;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace BattleSky.Network
{
    /// <summary>
    /// Spawns enemies based on the current level:
    /// Level 2 = capital ships + fighter drones (defeat six to advance),
    /// Level 3 = boss sphere (very large sphere), fought in stages.
    /// The Death Star owns the stage machine: its wave quota says how
    /// many escort fighters the crew has to clear in the current stage.
    /// This spawner keeps that wave topped up and reports it as cleared
    /// once the last fighter of it is gone.
    /// Enemies still alive when level 3 starts carry forward and are moved
    /// into the boss arena so they keep fighting instead of trailing behind.
    /// The escorts fight as hard as the stage the station is in: the boss owns
    /// the aggression value and this spawner hands it to the fighters and to
    /// the capital ships, so the whole escort speeds up when the fight
    /// escalates.
    /// Enemy count and spawn rate scale with NetworkLevelManager.DifficultyMultiplier.
    /// </summary>
    public class NetworkEnemySpawner : NetworkBehaviour
    {
        [Header("Spawning")]
        [SerializeField] private GameObject enemyPrefab;
        [SerializeField] private GameObject capitalShipPrefab;
        [SerializeField] private GameObject deathStarPrefab;
        [SerializeField] private float spawnRate = 0.5f;
        [SerializeField] private int maxEnemies = 12;
        [SerializeField] private float spawnRadius = 220f;
        [SerializeField] private float despawnRadius = 340f;

        [Header("Boss (Level 3) - Death Star")]
        [SerializeField] private float _bossSpawnDelay = 5f;
        [Tooltip("Distance the 2 km station is parked in front of the crew (its front surface ends up about 1.6 km ahead)")]
        [SerializeField] private float _bossDistance = 2600f;
        [Tooltip("Where survivors of the blockade are placed on the crew -> boss line, in metres ahead of the crew")]
        [SerializeField] private Vector2 _carryForwardDistances = new Vector2(140f, 300f);
        [Tooltip("Seconds between two fighters of a boss wave. A wave has to arrive as a wall of ships, not one every few seconds.")]
        [SerializeField] private float _waveSpawnInterval = 0.6f;

        /// <summary>
        /// Number of fighters the current boss stage asks the crew to
        /// clear. Zero while no wave is running.
        /// </summary>
        public static int WaveTotal { get; private set; }

        /// <summary>
        /// Number of fighters of the running wave that are still alive.
        /// </summary>
        public static int WaveRemaining { get; private set; }

        private List<GameObject> _activeEnemies = new List<GameObject>();

        /// <summary>
        /// Fighters of the running wave that this spawner sent out. The
        /// survivors carried over from level 2 are deliberately not in
        /// here: they do not count toward the wave.
        /// </summary>
        private List<GameObject> _waveFighters = new List<GameObject>();

        private float _spawnTimer;
        private float _bossTimer;
        private bool _bossSpawned;
        private bool _arrivalAnnounced;

        /// <summary>Stage the running wave belongs to (-1 until the first one).</summary>
        private int _trackedStage = -1;

        /// <summary>True once the running wave has been reported as cleared.</summary>
        private bool _waveCredited;

        /// <summary>True once the running wave has spawned a fighter of its own.</summary>
        private bool _waveSpawned;

        private void Update()
        {
            if (!isServer) return;

            if (NetworkLevelManager.CurrentLevel < 2)
            {
                ResetWaveTracking();
                return;
            }

            float difficulty = NetworkLevelManager.DifficultyMultiplier;
            float rate = spawnRate / difficulty;
            int max = Mathf.CeilToInt(maxEnemies * difficulty);

            if (NetworkLevelManager.CurrentLevel == 3)
            {
                if (!_bossSpawned)
                {
                    _bossTimer += Time.deltaTime;
                    if (_bossTimer >= _bossSpawnDelay)
                    {
                        _bossTimer = 0f;
                        // Only mark the boss as spawned when it actually
                        // spawned, otherwise level 3 could never start.
                        _bossSpawned = SpawnBoss();
                    }
                    // Survivors are still chasing the crew during the
                    // countdown, so keep the straggler cleanup running.
                    DespawnFarEnemies();
                    return;
                }

                // The boss decides what the crew has to fight: its quota
                // says how many fighters the current stage needs. The
                // quota is zero while the force field holds and once the
                // station is vulnerable, so no fighters are sent then.
                DeathStar boss = DeathStar.Active;
                int quota = boss != null ? boss.WaveQuota : 0;
                UpdateBossWave(boss, quota, max, rate);
                // The station escalates in stages, so the escorts have to
                // get more aggressive with it: hand the current stage
                // aggression to everything that is already in the arena, so a
                // running fight picks up the new aggression right away.
                if (boss != null) ApplyBossAggression(boss.transform);

                DespawnFarEnemies();
                return;
            }

            if (NetworkLevelManager.CurrentLevel == 2)
            {
                // No boss wave runs before the station shows up.
                ResetWaveTracking();

                // Six kills start the boss fight whether or not the field is
                // clear: whatever is still alive carries forward into level 3.
                if (NetworkLevelManager.EnemiesDestroyed >= NetworkLevelManager.Level3KillThreshold)
                {
                    int survivors = CountAllEnemies();
                    Debug.Log($"[NetworkEnemySpawner] level 3 triggered by kills with {survivors} hostiles still alive");
                    NetworkLevelManager.Advance(3);
                    return;
                }

                // Level 2 spawns mini Enemy ships (fighters) most of the time,
                // with occasional capital ships mixed in.
                _spawnTimer += Time.deltaTime;
                if (_spawnTimer >= 1f / rate)
                {
                    _spawnTimer = 0f;
                    TrySpawn(max, true);
                }
                DespawnFarEnemies();
                return;
            }

            _spawnTimer += Time.deltaTime;
            if (_spawnTimer >= 1f / rate)
            {
                _spawnTimer = 0f;
                TrySpawn(max, true);
            }

            DespawnFarEnemies();
        }

        /// <summary>
        /// Spawns a single enemy next to the crew and returns it, or null
        /// when nothing was spawned (the field is full, there is no
        /// prefab, or no crew ship to spawn around).
        /// </summary>
        private GameObject TrySpawn(int maxEnemies, bool allowCapitals)
        {
            if (CountLiveEnemies() >= maxEnemies) return null;

            GameObject prefab = allowCapitals && capitalShipPrefab != null
                && Random.value < 0.3f ? capitalShipPrefab : enemyPrefab;
            if (prefab == null) return null;

            var players = FindCrewShips(FindObjectsInactive.Include);
            if (players.Length == 0) return null;

            var target = players[Random.Range(0, players.Length)];
            Vector3 spawnPosition = target.transform.position + Random.onUnitSphere * spawnRadius;
            spawnPosition.y = Mathf.Max(spawnPosition.y, 5f);

            GameObject enemy = Instantiate(prefab, spawnPosition, Random.rotation);
            NetworkServer.Spawn(enemy);
            _activeEnemies.Add(enemy);
            return enemy;
        }

        /// <summary>
        /// Runs the stage driven escort waves of the boss fight.
        ///
        /// The boss owns the stage machine: its quota says how many
        /// fighters the crew has to kill in the current stage. This
        /// spawner only refills the wave and hands the clear over to the
        /// boss once the last fighter of the wave is gone.
        /// </summary>
        private void UpdateBossWave(DeathStar boss, int quota, int max, float rate)
        {
            if (boss == null)
            {
                // No station in the world: there is no wave to run.
                ResetWaveTracking();
                return;
            }

            if ((int)boss.Stage != _trackedStage)
            {
                // A new stage starts a new wave: the fighters of the
                // previous one stop counting and the clear credit is
                // handed out again with the new wave.
                _trackedStage = (int)boss.Stage;
                _waveFighters.Clear();
                _waveCredited = false;
                _waveSpawned = false;
                WaveTotal = quota;
                WaveRemaining = 0;
            }

            PruneWaveFighters();
            WaveRemaining = _waveFighters.Count;

            // The wave is wiped. The boss moves its stage machine on, but
            // only once per wave: a wave that never got a fighter of its
            // own cannot be wiped either.
            if (quota > 0 && _waveSpawned && !_waveCredited && WaveRemaining == 0)
            {
                _waveCredited = true;
                Debug.Log("[NetworkEnemySpawner] boss wave cleared");
                boss.NotifyWaveCleared();
            }

            // A cleared wave stays empty until the next stage asks for a
            // new one, and no fighter is sent while no wave runs.
            if (quota <= 0 || _waveCredited) return;
            if (WaveRemaining >= quota) return;

            _spawnTimer += Time.deltaTime;
            if (_spawnTimer < _waveSpawnInterval) return;

            _spawnTimer = 0f;
            // The boss decides how many fighters its stage needs, so the wave
            // is not limited by the normal enemy cap: the survivors that were
            // carried into the arena sit on top of the wave quota, and a cap
            // of "survivors + quota" lets the wave actually reach full size.
            int survivors = Mathf.Max(0, CountLiveEnemies() - WaveRemaining);
            GameObject fighter = SpawnWaveFighter(survivors + quota, boss);
            if (fighter == null) return;

            _waveFighters.Add(fighter);
            _waveSpawned = true;
            Debug.Log($"[NetworkEnemySpawner] wave fighter {WaveRemaining}/{quota} spawned (stage {boss.Stage})");
        }

        /// <summary>
        /// Drops the destroyed fighters of the running wave, so the
        /// remaining count only holds live hostiles.
        /// </summary>
        private void PruneWaveFighters()
        {
            for (int i = _waveFighters.Count - 1; i >= 0; i--)
                if (_waveFighters[i] == null)
                    _waveFighters.RemoveAt(i);
        }

        /// <summary>
        /// Clears the wave bookkeeping. Used whenever no wave is running,
        /// so the HUD never shows a stale counter.
        /// </summary>
        private void ResetWaveTracking()
        {
            _trackedStage = -1;
            _waveFighters.Clear();
            _waveCredited = false;
            _waveSpawned = false;
            WaveTotal = 0;
            WaveRemaining = 0;
        }

        private bool SpawnBoss()
        {
            if (deathStarPrefab == null)
            {
                Debug.LogError("[NetworkEnemySpawner] No Death Star prefab assigned - level 3 cannot start.");
                return false;
            }

            var players = FindCrewShips(FindObjectsInactive.Include);
            if (players.Length == 0) return false;

            var target = players[Random.Range(0, players.Length)];
            // Park the station in front of the crew so the crew has to fly the
            // distance and line up the exhaust port.
            Vector3 forward = target.transform.forward;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            Vector3 spawnPosition = target.transform.position + forward.normalized * _bossDistance;
            spawnPosition.y = Mathf.Max(spawnPosition.y, target.transform.position.y + 25f);

            // The station's +Z face carries the exhaust port, so aim it back at
            // the crew. DeathStar.OnStartServer re-aims at the nearest player
            // before the spawn message goes out.
            Quaternion facing = Quaternion.LookRotation(
                (target.transform.position - spawnPosition).normalized,
                Vector3.up);

            GameObject boss = Instantiate(deathStarPrefab, spawnPosition, facing);
            NetworkServer.Spawn(boss);
            // The boss is intentionally NOT tracked in _activeEnemies: it spawns
            // beyond despawnRadius and must stay alive for the whole level.
            // Whatever survived the blockade rides in with the station.
            BringSurvivorsToBoss(boss.transform, target.transform);
            // The station is huge and spawns far out: give every client a
            // short revealing shot of it before the chase camera takes over.
            if (!_arrivalAnnounced)
            {
                _arrivalAnnounced = true;
                StartCoroutine(AnnounceArrival());
            }
            return true;
        }

        /// <summary>
        /// Waits just long enough for the spawn payload to reach the clients,
        /// then plays the arrival cutscene on all of them.
        /// </summary>
        private IEnumerator AnnounceArrival()
        {
            yield return new WaitForSeconds(0.4f);
            if (this == null || !isServer) yield break;
            RpcDeathStarArrival();
        }

        [ClientRpc]
        private void RpcDeathStarArrival()
        {
            DeathStarArrivalCutscene.PlayLocal();
        }

        /// <summary>
        /// Moves the enemies that survived the blockade into the boss arena.
        /// Fighters fly at about 22 m/s while the crew does 60-140 m/s toward
        /// a 2.6 km distant station, so left alone they would never reach the
        /// fight. They are dropped on the crew -> boss approach line, a few
        /// hundred metres ahead of the crew, outside the hull and inside the
        /// despawn radius, facing the crew.
        /// </summary>
        private void BringSurvivorsToBoss(Transform boss, Transform crew)
        {
            if (boss == null || crew == null) return;

            // The station decides how hard the escorts hit. The survivors
            // that are carried forward and the ones that are already in the
            // arena both get the aggression of the current stage, so the
            // whole escort fights with the same rules.
            ApplyBossAggression(boss);

            Vector3 bossPosition = boss.position;
            Vector3 crewPosition = crew.position;
            Vector3 axis = bossPosition - crewPosition;
            if (axis.sqrMagnitude < 0.01f) return;

            Vector3 direction = axis.normalized;

            DeathStar station = boss.GetComponent<DeathStar>();
            float hullRadius = station != null ? station.Radius : 1000f;
            float minFromBoss = hullRadius + 35f;
            // Keep them clear of the despawn ring so they are not reaped the
            // moment they arrive.
            float maxFromCrew = despawnRadius - 10f;

            // Every hostile still alive is carried forward, not just the ones
            // the spawner tracks: capital ships launch fighters of their own
            // and those never enter _activeEnemies.
            var fighters = Object.FindObjectsByType<EnemyShip>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            var capitals = Object.FindObjectsByType<CapitalShip>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            int count = fighters.Length + capitals.Length;
            if (count == 0) return;

            Debug.Log($"[NetworkEnemySpawner] carrying {count} survivors into the boss arena");

            int slot = 0;

            void Place(GameObject enemy)
            {
                if (enemy == null) return;

                // Spread them down the approach instead of stacking them all
                // on one point. Distances are absolute metres ahead of the
                // crew, so the placement fits the much longer approach to the
                // 2 km station.
                float distance = Mathf.Lerp(_carryForwardDistances.x, _carryForwardDistances.y,
                    (slot + 0.5f) / count) + Random.Range(-15f, 15f);
                slot++;
                distance = Mathf.Clamp(distance, 20f, maxFromCrew - 20f);

                Vector3 position = crewPosition + direction * distance;

                // Jitter across the approach line, never along it.
                Vector3 lateral = Random.insideUnitSphere;
                lateral -= Vector3.Project(lateral, direction);
                if (lateral.sqrMagnitude > 0.0001f)
                    position += lateral.normalized * Random.Range(0f, 40f);
                position.y += Random.Range(-30f, 30f);

                // Never place a survivor inside the station.
                Vector3 fromBoss = position - bossPosition;
                if (fromBoss.magnitude < minFromBoss)
                {
                    Vector3 away = fromBoss.sqrMagnitude > 0.01f ? fromBoss.normalized : -direction;
                    position = bossPosition + away * minFromBoss;
                }

                // Nor outside the cleanup ring.
                Vector3 fromCrew = position - crewPosition;
                if (fromCrew.magnitude > maxFromCrew)
                    position = crewPosition + fromCrew.normalized * maxFromCrew;

                // Face the crew so the survivor joins the fight already
                // pointed at them.
                Vector3 toCrew = crewPosition - position;
                Quaternion facing = toCrew.sqrMagnitude > 0.01f
                    ? Quaternion.LookRotation(toCrew.normalized, Vector3.up)
                    : enemy.transform.rotation;

                // Teleport through Mirror instead of only writing the
                // transform: the NetworkTransform teleport clears its
                // interpolation buffers and syncs the rigidbody pose, so the
                // placement cannot be overwritten by a stale snapshot on the
                // next network tick.
                NetworkTransformBase networkTransform = enemy.GetComponent<NetworkTransformBase>();
                if (networkTransform != null)
                    networkTransform.ServerTeleport(position, facing);
                else
                    enemy.transform.SetPositionAndRotation(position, facing);

                Rigidbody body = enemy.GetComponent<Rigidbody>();
                if (body != null)
                {
                    body.position = position;
                    body.rotation = facing;
                    body.linearVelocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
                Physics.SyncTransforms();
            }

            for (int i = 0; i < fighters.Length; i++) Place(fighters[i].gameObject);
            for (int i = 0; i < capitals.Length; i++) Place(capitals[i].gameObject);
        }

        /// <summary>
        /// Hands the aggression of the current boss stage to every hostile in
        /// the scene.
        ///
        /// The escorts have to get more aggressive as the fight escalates, so
        /// the station owns the value and the spawner passes it on. Capital
        /// ships launch fighters of their own, so both kinds of hostile are
        /// updated here, and the survivors that were carried into the arena
        /// are covered as well.
        /// </summary>
        private void ApplyBossAggression(Transform bossTransform)
        {
            DeathStar boss = bossTransform != null ? bossTransform.GetComponent<DeathStar>() : DeathStar.Active;
            if (boss == null) return;

            float aggression = boss.Aggression;

            foreach (var ship in Object.FindObjectsByType<BattleSky.Network.EnemyShip>(FindObjectsSortMode.None))
                if (ship != null) ship.SetAggression(aggression);

            foreach (var capital in Object.FindObjectsByType<BattleSky.Network.CapitalShip>(FindObjectsSortMode.None))
                if (capital != null) capital.SetAggression(aggression);
        }

        /// <summary>
        /// Spawns one fighter of a boss wave IN FRONT of the crew.
        ///
        /// The crew cruises at 60-140 m/s and a fighter only manages 48 m/s,
        /// so a fighter that appears behind the ship is left behind and
        /// reaped by the despawn ring before it can even turn around. The
        /// wave is therefore placed along the crew's approach, spread out
        /// across it, so the whole wave comes at the crew as a wall of ships.
        ///
        /// The fighter takes the aggression of the stage the station is in,
        /// so a wave fights as hard as the stage it belongs to.
        /// </summary>
        private GameObject SpawnWaveFighter(int cap, DeathStar boss)
        {
            if (enemyPrefab == null) return null;
            if (CountLiveEnemies() >= cap) return null;

            var players = FindCrewShips(FindObjectsInactive.Include);
            if (players.Length == 0) return null;

            var target = players[Random.Range(0, players.Length)];
            Vector3 forward = target.transform.forward;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward = forward.normalized;

            float ahead = Random.Range(140f, 280f);
            Vector3 lateral = Random.insideUnitSphere;
            lateral -= forward * Vector3.Dot(lateral, forward);
            if (lateral.sqrMagnitude > 0.0001f)
                lateral = lateral.normalized * Random.Range(0f, 110f);

            Vector3 position = target.transform.position + forward * ahead + lateral;
            position.y = Mathf.Max(position.y, 5f);

            GameObject enemy = Instantiate(enemyPrefab, position, Quaternion.LookRotation(forward, Vector3.up));
            NetworkServer.Spawn(enemy);
            // The wave fights as hard as the stage the station is in.
            var ship = enemy.GetComponent<BattleSky.Network.EnemyShip>();
            if (ship != null && boss != null) ship.SetAggression(boss.Aggression);
            _activeEnemies.Add(enemy);
            return enemy;
        }

        /// <summary>
        /// Spawn/despawn references must be flyable crew ships. Gunner proxies
        /// are invisible control forwarders that follow a crew ship, so using
        /// one as a spatial anchor can drop survivors around a stale pose.
        /// Falls back to every player if no crew ship is spawned yet.
        /// </summary>
        private static NetworkPlayer[] FindCrewShips(FindObjectsInactive inactive)
        {
            var players = Object.FindObjectsByType<NetworkPlayer>(inactive, FindObjectsSortMode.None);
            var crew = new List<NetworkPlayer>(players.Length);
            for (int i = 0; i < players.Length; i++)
                if (players[i] != null && !players[i].IsGunnerProxy)
                    crew.Add(players[i]);
            return crew.Count > 0 ? crew.ToArray() : players;
        }

        /// <summary>
        /// Head count of every hostile currently in the scene, including the
        /// fighters capital ships launch on their own.
        /// </summary>
        private static int CountAllEnemies()
        {
            return Object.FindObjectsByType<EnemyShip>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length
                + Object.FindObjectsByType<CapitalShip>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length;
        }

        private int CountLiveEnemies()
        {
            int live = 0;
            for (int i = 0; i < _activeEnemies.Count; i++)
                if (_activeEnemies[i] != null) live++;
            return live;
        }

        private void DespawnFarEnemies()
        {
            var players = FindCrewShips(FindObjectsInactive.Exclude);
            if (players.Length == 0) return;

            for (int i = _activeEnemies.Count - 1; i >= 0; i--)
            {
                GameObject enemy = _activeEnemies[i];
                if (enemy == null)
                {
                    _activeEnemies.RemoveAt(i);
                    continue;
                }

                // Distance to the nearest crew member: the first connection's
                // identity is not always the ship the enemy was placed around.
                float nearest = float.MaxValue;
                for (int p = 0; p < players.Length; p++)
                {
                    float distance = Vector3.Distance(enemy.transform.position, players[p].transform.position);
                    if (distance < nearest) nearest = distance;
                }

                if (nearest > despawnRadius)
                {
                    // Only networked roots can be destroyed through the
                    // server; a stray child without an identity is removed
                    // directly instead of throwing every frame.
                    if (enemy.GetComponent<NetworkIdentity>() != null)
                        NetworkServer.Destroy(enemy);
                    else
                        Destroy(enemy);
                    _activeEnemies.RemoveAt(i);
                }
            }
        }
    }
}
