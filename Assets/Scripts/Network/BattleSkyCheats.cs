using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using BattleSky.Network;

namespace BattleSky
{
    /// <summary>
    /// TEMP DEBUG - DELETE THIS FILE BEFORE SHIPPING.
    ///
    /// Cheat codes for testing the Death Star boss fight without playing
    /// through level 2 first. The component bootstraps itself, so nothing has
    /// to be added to the scene, and every action goes through the server
    /// authoritative API of the boss, so the whole crew sees the result.
    ///
    /// Only the host can use them, which is the usual rule for debug cheats.
    /// </summary>
    public class BattleSkyCheats : MonoBehaviour
    {
        /// <summary>Flip to false to switch every cheat off with one edit.</summary>
        const bool ENABLED = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (!ENABLED) return;
            // A batch run has no keyboard and no presentation, so the cheat
            // would only add noise to the automated tests.
            if (Application.isBatchMode) return;
            if (FindFirstObjectByType<BattleSkyCheats>() != null) return;

            GameObject host = new GameObject("BattleSkyCheats");
            DontDestroyOnLoad(host);
            host.AddComponent<BattleSkyCheats>();
        }

        private void Update()
        {
            if (!ENABLED) return;
            if (!NetworkServer.active) return;

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.f9Key.wasPressedThisFrame) CheatJumpToBoss();
            if (keyboard.f10Key.wasPressedThisFrame) CheatBreakForceField();
            if (keyboard.f11Key.wasPressedThisFrame) CheatSkipFighterWave();
            if (keyboard.f12Key.wasPressedThisFrame) CheatKillBoss();
        }

        /// <summary>
        /// Goes straight to level three. The crew flies the last stretch
        /// itself, so the arrival is not skipped.
        /// </summary>
        void CheatJumpToBoss()
        {
            Debug.Log("[Cheat] jumping to the Death Star (level 3)");
            NetworkLevelManager.Advance(3);
        }

        /// <summary>
        /// Drops the white force field straight away. The station answers with
        /// a superlaser volley, so the stage hand over still happens.
        /// </summary>
        void CheatBreakForceField()
        {
            DeathStar boss = DeathStar.Active;
            if (boss == null)
            {
                Debug.LogWarning("[Cheat] no Death Star in the world");
                return;
            }
            if (!boss.IsForceFieldActive)
            {
                Debug.Log("[Cheat] the force field is already down");
                return;
            }

            boss.HandleProjectileHit(boss.transform.position, 999999f);
            Debug.Log("[Cheat] force field shattered");
        }

        /// <summary>
        /// Wipes the current fighter wave by telling the station that the wave
        /// is gone. The stage only changes after the superlaser volley.
        /// </summary>
        void CheatSkipFighterWave()
        {
            DeathStar boss = DeathStar.Active;
            if (boss == null)
            {
                Debug.LogWarning("[Cheat] no Death Star in the world");
                return;
            }
            if (boss.Stage != DeathStarStage.Fighters)
            {
                Debug.Log($"[Cheat] the boss is not in the fighter stage (stage={boss.Stage})");
                return;
            }

            boss.NotifyWaveCleared();
            Debug.Log("[Cheat] fighter wave skipped, the superlaser is charging");
        }

        /// <summary>Ends the boss fight by chewing through the hull.</summary>
        void CheatKillBoss()
        {
            DeathStar boss = DeathStar.Active;
            if (boss == null)
            {
                Debug.LogWarning("[Cheat] no Death Star in the world");
                return;
            }

            boss.TakeSurfaceDamage(999999f);
            Debug.Log("[Cheat] the Death Star was destroyed");
        }
    }
}
