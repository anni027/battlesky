using Mirror;
using UnityEngine;

namespace BattleSky.Network
{
    /// <summary>
    /// Drives level progression on the server only:
    /// level 1 = asteroids only (random drift), level 2 = asteroids +
    /// attacking enemy ships, level 3 = asteroids + more enemies.
    /// Nothing here is replicated - clients simply see what spawns.
    /// </summary>
    public class NetworkLevelManager : MonoBehaviour
    {
        public static int CurrentLevel { get; private set; } = 1;
        public static System.Action<int> OnLevelChanged;

        [Header("Level timing (server only)")]
        [SerializeField] private float _level1Duration = 45f;
        [SerializeField] private float _level2Duration = 60f;

        private float _levelTimer;

        private void Update()
        {
            if (!NetworkServer.active) return;
            _levelTimer += Time.deltaTime;

            if (CurrentLevel == 1 && _levelTimer >= _level1Duration)
                Advance(2);
            else if (CurrentLevel == 2 && _levelTimer >= _level1Duration + _level2Duration)
                Advance(3);
        }

        private void Advance(int level)
        {
            CurrentLevel = level;
            OnLevelChanged?.Invoke(level);
            Debug.Log($"[NetworkLevelManager] Advanced to level {level}");
        }
    }
}
