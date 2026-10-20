using UnityEditor;
using UnityEngine;
using Mirror;

public static class BattleSkyEnemyBaseCleanup
{
    const string Path = "Assets/models/Enemy base 1.prefab";

    /// <summary>
    /// "Enemy base 1" is a visual model that is nested inside EnemyShip.prefab.
    /// It must not carry any networking or physics components of its own: a
    /// NetworkIdentity on a child breaks Mirror's spawn registration and makes
    /// Mirror complain that a prefab has more than one identity.
    ///
    /// This tool strips those components with Unity's own API so the saved
    /// prefab is always written in a form Unity can read back unchanged.
    /// </summary>
    [MenuItem("BattleSky/Clean Enemy Base Model")]
    public static void Run()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(Path);
        try
        {
            int removed = 0;

            // The stray components sit on the PREFAB ROOT, not on a child:
            // the model root is the object the network components were added
            // to. The root must be cleaned as well, otherwise the prefab keeps
            // a NetworkIdentity and Mirror refuses the nested instance.
            var identities = root.GetComponentsInChildren<NetworkIdentity>(true);
            foreach (var identity in identities)
            {
                Debug.Log($"[EnemyBaseCleanup] removing NetworkIdentity on '{identity.gameObject.name}'");
                Object.DestroyImmediate(identity, true);
                removed++;
            }

            var rigidbodies = root.GetComponentsInChildren<Rigidbody>(true);
            foreach (var body in rigidbodies)
            {
                Debug.Log($"[EnemyBaseCleanup] removing Rigidbody on '{body.gameObject.name}'");
                Object.DestroyImmediate(body, true);
                removed++;
            }

            // Any Mirror network transform component on the nested model.
            var behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var behaviour in behaviours)
            {
                if (behaviour == null) continue;
                string typeName = behaviour.GetType().Name;
                if (!typeName.StartsWith("NetworkRigidbody") && !typeName.StartsWith("NetworkTransform")) continue;
                Debug.Log($"[EnemyBaseCleanup] removing {typeName} on '{behaviour.gameObject.name}'");
                Object.DestroyImmediate(behaviour, true);
                removed++;
            }

            // Only the prefab's own root offset is reset: a prefab asset has
            // to sit at the origin so the nested instance lines up with the
            // ship. The children carry the model layout (the fire points sit
            // on the guns) and must never be moved.
            if (root.transform.localPosition.sqrMagnitude > 0f)
            {
                Debug.Log($"[EnemyBaseCleanup] zeroing the prefab root position ({root.transform.localPosition})");
                root.transform.localPosition = Vector3.zero;
            }

            PrefabUtility.SaveAsPrefabAsset(root, Path);
            Debug.Log($"[EnemyBaseCleanup] cleaned {Path} ({removed} component(s) removed)");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
