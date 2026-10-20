using UnityEngine;
using UnityEditor;
using Mirror;
using BattleSky.Network;

/// <summary>
/// One-time tool: duplicates Assets/models/Player.prefab into
/// Assets/models/Player_StarWars.prefab with Star Wars-style tuning and
/// registers it as the networked player ship prefab.
///
/// Run: BattleSky / Create Star Wars Player Ship
/// </summary>
public static class CreateStarWarsShip
{
    const string SourcePath = "Assets/models/Player.prefab";
    const string DestPath = "Assets/models/Player_StarWars.prefab";

    [MenuItem("BattleSky/Create Star Wars Player Ship")]
    public static void Create()
    {
        GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePath);
        if (source == null)
        {
            Debug.LogError($"[StarWars] Source prefab not found at {SourcePath}");
            return;
        }

        GameObject clone = Object.Instantiate(source);
        clone.name = "Player_StarWars";

        // Star Wars-style scaling: longer, sleeker hull.
        clone.transform.localScale = new Vector3(1.2f, 1.0f, 1.6f);

        // Ensure replication is present.
        if (clone.GetComponent<NetworkRigidbodyReliable>() == null
            && clone.GetComponent<NetworkRigidbodyUnreliable>() == null
            && clone.GetComponent<NetworkTransformBase>() == null)
        {
            var sync = clone.AddComponent<NetworkRigidbodyReliable>();
            sync.syncDirection = SyncDirection.ServerToClient;
            sync.syncPosition = true;
            sync.syncRotation = true;
        }

        // Name any shield mesh child "shield" so NetworkPlayer finds it.
        foreach (var t in clone.GetComponentsInChildren<Transform>(true))
        {
            if (t.name.Contains("shield", System.StringComparison.OrdinalIgnoreCase))
                t.name = "shield";
        }

        PrefabUtility.SaveAsPrefabAsset(clone, DestPath);
        Object.DestroyImmediate(clone);
        AssetDatabase.Refresh();

        Debug.Log($"[StarWars] Created {DestPath}. Assign it to BattleSkyNetworkManager.playerShipPrefab and add to spawnPrefabs.");
    }

    [MenuItem("BattleSky/Register Level Spawnables")]
    public static void RegisterSpawnables()
    {
        // Ensure the NetworkManager in the scene has capital ship + death star in spawnPrefabs.
        var nm = Object.FindFirstObjectByType<BattleSkyNetworkManager>(FindObjectsInactive.Include);
        if (nm == null)
        {
            Debug.LogError("[StarWars] No BattleSkyNetworkManager in scene.");
            return;
        }

        var so = new SerializedObject(nm);
        var spawnPrefabs = so.FindProperty("spawnPrefabs");

        var capital = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/models/CapitalShip.prefab");
        var deathStar = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/models/DeathStar.prefab");
        var playerStarWars = AssetDatabase.LoadAssetAtPath<GameObject>(DestPath);

        // This is informational; actual prefab assignment is done in the Inspector.
        Debug.Log($"[StarWars] Player prefab: {(playerStarWars != null ? DestPath : "MISSING")}");
        Debug.Log($"[StarWars] CapitalShip prefab: {(capital != null ? "Assets/models/CapitalShip.prefab" : "MISSING")}");
        Debug.Log($"[StarWars] DeathStar prefab: {(deathStar != null ? "Assets/models/DeathStar.prefab" : "MISSING")}");
        Debug.Log("[StarWars] Assign these prefabs to BattleSkyNetworkManager.spawnPrefabs in the Inspector.");
    }
}