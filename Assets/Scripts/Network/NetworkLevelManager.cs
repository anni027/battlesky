using Mirror;
using UnityEngine;

namespace BattleSky.Network
{
    /// <summary>
    /// Drives level progression on the server only:
    /// level 1 = fly through the asteroid belt and reach the checkpoint gate,
    /// level 2 = fight through the capital-ship blockade until six hostiles
    ///           are down (whatever is still alive carries into level 3),
    /// level 3 = boss battle against a massive sphere.
    ///
    /// Difficulty ramps continuously within each level. Level transitions are
    /// gated: L1->L2 when a crew ship reaches the checkpoint, L2->L3 on the
    /// sixth kill, L3->end when the boss is destroyed.
    ///
    /// The scene object is a NetworkBehaviour so the current level, the kill
    /// count and the boss result are replicated to every client. Clients used
    /// to keep seeing "level 1" forever, which broke the objective text, the
    /// gate readout and the mission-end screen.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkLevelManager : NetworkBehaviour
    {
        /// <summary>
        /// Kills needed in level 2 before the Death Star shows up. Enemies
        /// still alive at that point carry forward into the boss fight.
        /// </summary>
        public const int Level3KillThreshold = 6;

        public static int CurrentLevel { get; private set; } = 1;
        public static float DifficultyMultiplier { get; private set; } = 1f;
        public static int EnemiesDestroyed { get; private set; }
        public static bool BossDefeated { get; private set; }
        public static System.Action<int> OnLevelChanged;
        public static System.Action OnBossDefeated;

        [Header("Difficulty ramp (per 30 seconds within a level)")]
        [SerializeField] private float _difficultyStep = 0.25f;
        [SerializeField] private float _difficultyCap = 2f;
        [SerializeField] private float _difficultyInterval = 30f;

        [Header("Replicated state")]
        [SyncVar(hook = nameof(OnLevelSynced))]
        private int _syncedLevel = 1;
        [SyncVar(hook = nameof(OnEnemiesSynced))]
        private int _syncedEnemiesDestroyed;
        [SyncVar(hook = nameof(OnBossSynced))]
        private bool _syncedBossDefeated;

        private static float _difficultyTimer;
        private static NetworkLevelManager _instance;

        /// <summary>
        /// Resets statics when a play session starts, including when the
        /// editor is configured to skip domain reloads between play sessions.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticsOnPlay()
        {
            CurrentLevel = 1;
            DifficultyMultiplier = 1f;
            EnemiesDestroyed = 0;
            BossDefeated = false;
            _difficultyTimer = 0f;
            _instance = null;
            OnLevelChanged = null;
            OnBossDefeated = null;
        }

        public override void OnStartServer()
        {
            _instance = this;

            // A new session must always start at level 1. The statics survive
            // a host -> menu -> host round trip because the domain is not
            // reloaded between play sessions.
            CurrentLevel = 1;
            DifficultyMultiplier = 1f;
            EnemiesDestroyed = 0;
            BossDefeated = false;
            _difficultyTimer = 0f;

            _syncedLevel = CurrentLevel;
            _syncedEnemiesDestroyed = 0;
            _syncedBossDefeated = false;
        }

        public override void OnStartClient()
        {
            _instance = this;
            // The first sync arrives right after this; apply it immediately so
            // the HUD is never a frame behind.
            CurrentLevel = _syncedLevel;
            EnemiesDestroyed = _syncedEnemiesDestroyed;
            BossDefeated = _syncedBossDefeated;
        }

        public override void OnStopServer()
        {
            if (_instance == this) _instance = null;
        }

        public override void OnStopClient()
        {
            if (_instance == this) _instance = null;
        }

        private void Update()
        {
            if (!NetworkServer.active) return;

            _difficultyTimer += Time.deltaTime;

            if (_difficultyTimer >= _difficultyInterval)
            {
                _difficultyTimer = 0f;
                DifficultyMultiplier = Mathf.Min(_difficultyCap, DifficultyMultiplier + _difficultyStep);
            }

            // Level 1 -> 2 is triggered by Level1Checkpoint when the crew
            // ship flies through the gate. Level 2 -> 3 is gated on defeating
            // all enemies (handled by NetworkEnemySpawner).
        }

        public static void Advance(int level)
        {
            CurrentLevel = level;
            DifficultyMultiplier = 1f;
            EnemiesDestroyed = 0;
            _difficultyTimer = 0f;

            if (_instance != null)
            {
                _instance._syncedLevel = level;
                _instance._syncedEnemiesDestroyed = 0;
            }

            OnLevelChanged?.Invoke(level);
            Debug.Log($"[NetworkLevelManager] Advanced to level {level} (difficulty reset)");
        }

        public static void RegisterEnemyDestroyed()
        {
            EnemiesDestroyed++;
            if (_instance != null)
                _instance._syncedEnemiesDestroyed = EnemiesDestroyed;
        }

        public static void RegisterBossDefeated()
        {
            BossDefeated = true;
            if (_instance != null)
                _instance._syncedBossDefeated = true;
            OnBossDefeated?.Invoke();
            Debug.Log("[NetworkLevelManager] Boss defeated");
        }

        private void OnLevelSynced(int oldValue, int newValue)
        {
            if (isServer) return; // the server already applied it in Advance()
            CurrentLevel = newValue;
            OnLevelChanged?.Invoke(newValue);
        }

        private void OnEnemiesSynced(int oldValue, int newValue)
        {
            if (isServer) return;
            EnemiesDestroyed = newValue;
        }

        private void OnBossSynced(bool oldValue, bool newValue)
        {
            if (isServer) return;
            BossDefeated = newValue;
            if (newValue)
                OnBossDefeated?.Invoke();
        }
    }
}
