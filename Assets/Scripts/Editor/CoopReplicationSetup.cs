using UnityEngine;
using UnityEditor;
using Mirror;

/// <summary>
/// Patches the player ship prefab so remote crew members see it move.
/// Adds server-to-client NetworkRigidbodyReliable when the prefab has no
/// transform sync yet. Must run in the Editor (runtime AddComponent would
/// miss NetworkIdentity's behaviour cache and never replicate).
/// </summary>
public static class CoopReplicationSetup
{
    const string PlayerPrefabPath = "Assets/models/Player.prefab";

    [MenuItem("BattleSky/Ensure Player Replication")]
    public static void EnsureReplication()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
        try
        {
            if (root.GetComponent<NetworkTransformBase>() == null
                && root.GetComponent<NetworkRigidbodyReliable>() == null
                && root.GetComponent<NetworkRigidbodyUnreliable>() == null)
            {
                var sync = root.AddComponent<NetworkRigidbodyReliable>();
                sync.syncDirection = SyncDirection.ServerToClient;
                sync.syncPosition = true;
                sync.syncRotation = true;
                PrefabUtility.SavePrefabAsset(root);
                Debug.Log("[CoOp] Added NetworkRigidbodyReliable (server->client) to " + PlayerPrefabPath);
            }
            else
            {
                Debug.Log("[CoOp] Player prefab already has transform replication.");
            }
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
