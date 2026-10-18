using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BattleSky.Network;
using Mirror;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static class BattleSkyDoctor
{
    static int _issues;
    static KeyValuePair<string, string>[] _assetText;
    static readonly string[] SkipTrees = { "Assets/Mirror/", "Assets/Ultimate 10 Plus Shaders/", "Assets/_Recovery/" };

    // CLI: Unity.exe -batchmode -nographics -quit -executeMethod BattleSkyDoctor.Run
    // Exits nonzero when any invariant fails so shell callers can branch on it.
    public static void Run()
    {
        Debug.Log("=== BattleSkyDoctor start ===");
        LoadActiveScene();
        LoadAssetText();
        CheckSceneNetworkManager();
        CheckRegisteredSpawnables();
        CheckPlayerPrefab();
        CheckAsteroidSpawner();
        CheckHud();
        CheckMissingScripts();
        CheckDeadScripts();
        Debug.Log($"=== BattleSkyDoctor done: {_issues} issue(s) ===");
        EditorApplication.Exit(_issues == 0 ? 0 : 1);
    }

    // Batchmode starts with an untitled scene, so the build scene must be opened explicitly.
    static void LoadActiveScene()
    {
        string path = EditorBuildSettings.scenes.FirstOrDefault(s => s.enabled)?.path;
        if (string.IsNullOrEmpty(path)) path = "Assets/Scenes/SampleScene.unity";
        if (!File.Exists(path)) { Warn($"No scene to open (looked at '{path}')"); return; }
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(path, UnityEditor.SceneManagement.OpenSceneMode.Single);
        Debug.Log($"[Doctor] opened scene {path}");
    }

    static void Fail(string msg) { _issues++; Debug.LogError("[Doctor] " + msg); }
    static void Warn(string msg) { Debug.LogWarning("[Doctor] " + msg); }

    static void CheckSceneNetworkManager()
    {
        string scenePath = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
        NetworkManager nm = Object.FindFirstObjectByType<NetworkManager>(FindObjectsInactive.Include);
        if (nm == null) { Fail($"Scene has no NetworkManager ({scenePath})"); return; }

        if (nm.GetType() == typeof(NetworkManager))
            Fail($"Scene uses base Mirror.NetworkManager, so BattleSkyNetworkManager spawn logic never runs ({scenePath})");
        else
            Debug.Log($"[Doctor] NetworkManager type = {nm.GetType().FullName}");

        if (nm.transport == null)
            Fail($"NetworkManager.transport is unset on '{nm.name}'");

        if (nm.playerPrefab == null)
            Fail("NetworkManager.playerPrefab is unset");

        int nulls = nm.spawnPrefabs.Count(p => p == null);
        if (nulls > 0)
            Fail($"spawnPrefabs holds {nulls} null entry{(nulls == 1 ? "" : "ies")}, so no non-player prefab is registered for clients");
    }

    static void CheckRegisteredSpawnables()
    {
        NetworkManager nm = Object.FindFirstObjectByType<NetworkManager>(FindObjectsInactive.Include);
        if (nm == null) return;

        var registered = new HashSet<GameObject>();
        if (nm.playerPrefab != null) registered.Add(nm.playerPrefab);
        foreach (GameObject p in nm.spawnPrefabs)
            if (p != null) registered.Add(p);

        foreach (string path in AssetPaths("t:Prefab"))
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null || !prefab.TryGetComponent(out NetworkIdentity _)) continue;
            if (registered.Contains(prefab)) continue;
            Fail($"NetworkIdentity prefab not registered on NetworkManager: {path}");
        }
    }

    static void CheckPlayerPrefab()
    {
        const string path = "Assets/models/Player.prefab";
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) { Fail($"Missing player prefab at {path}"); return; }

        if (prefab.GetComponentInChildren<ShipInputHandler>(true) == null
            && prefab.GetComponentInChildren<PilotInputHandler>(true) == null
            && prefab.GetComponentInChildren<NetworkPlayer>(true) == null)
            Warn($"{path} has no ShipInputHandler, so ShipInputHandler-driven components cannot work");

        // Bolt hits and asteroid collisions must land on something: the
        // standalone path uses ShipHealth, the networked path NetworkPlayer.
        if (prefab.GetComponentInChildren<ShipHealth>(true) == null
            && prefab.GetComponentInChildren<NetworkPlayer>(true) == null)
            Fail($"{path} has neither ShipHealth nor NetworkPlayer, so bolt and asteroid hits deal no damage");

        if (prefab.GetComponentInChildren<ShipWeaponSystem>(true) == null)
            Warn($"{path} has no ShipWeaponSystem; only NetworkPlayer supplies weapons");

        ShieldSystem shield = prefab.GetComponentInChildren<ShieldSystem>(true);
        if (shield != null)
        {
            SerializedProperty mesh = new SerializedObject(shield).FindProperty("_shieldMesh");
            if (mesh != null && mesh.objectReferenceValue is GameObject go && go == shield.gameObject)
                Fail("ShieldSystem._shieldMesh is its own GameObject, so Awake() deactivates ShieldSystem permanently");
            else if (prefab.GetComponentInChildren<ShipInputHandler>(true) == null
                && prefab.GetComponentInChildren<GunnerInputHandler>(true) == null
                && prefab.GetComponentInChildren<NetworkPlayer>(true) == null)
                Fail("ShieldSystem needs ShipInputHandler, which is absent from the prefab - shield can never activate");
        }

        ShipCameraRig rig = prefab.GetComponentInChildren<ShipCameraRig>(true);
        if (rig != null)
        {
            SerializedObject so = new SerializedObject(rig);
            foreach (string field in new[] { "shipRoot", "flightModel", "inputHandler" })
            {
                SerializedProperty prop = so.FindProperty(field);
                if (prop != null && prop.objectReferenceValue == null)
                    Fail($"ShipCameraRig.{field} is unassigned on '{rig.name}', so LateUpdate early-returns and the camera never rigs");
            }
        }

        foreach (Camera cam in prefab.GetComponentsInChildren<Camera>(true))
            if (!cam.CompareTag("MainCamera"))
                Warn($"Camera '{H(cam.transform)}' is not tagged MainCamera, so Camera.main is null");

        if (prefab.GetComponent<NetworkTransformBase>() == null
            && prefab.GetComponent<NetworkRigidbodyReliable>() == null
            && prefab.GetComponent<NetworkRigidbodyUnreliable>() == null)
            Fail($"{path} has no NetworkTransform/NetworkRigidbody - remote crew members see a frozen ship. Run BattleSky/Ensure Player Replication");
    }

    static void CheckAsteroidSpawner()
    {
        foreach (AsteroidSpawner s in Object.FindObjectsByType<AsteroidSpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            SerializedObject so = new SerializedObject(s);
            SerializedProperty prefabs = so.FindProperty("_asteroidPrefabs");
            if (prefabs != null && prefabs.arraySize == 0)
                Fail($"AsteroidSpawner '{s.name}' has no asteroid prefabs assigned");
            SerializedProperty player = so.FindProperty("_player");
            if (player != null && player.objectReferenceValue == null)
                Fail($"AsteroidSpawner '{s.name}' has no target, so it never spawns");
            if (s.GetComponent<NetworkIdentity>() == null)
                Warn($"AsteroidSpawner '{s.name}' is not networked; its asteroids never replicate");
        }

        NetworkAsteroidSpawner[] networkSpawners = Object.FindObjectsByType<NetworkAsteroidSpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (networkSpawners.Length == 0)
            Warn("NetworkAsteroidSpawner exists in code but is placed in no scene");
        foreach (NetworkAsteroidSpawner spawner in networkSpawners)
        {
            SerializedProperty prefabs = new SerializedObject(spawner).FindProperty("asteroidPrefabs");
            if (prefabs != null && prefabs.arraySize == 0)
                Fail($"NetworkAsteroidSpawner '{spawner.name}' has no asteroid prefabs assigned, so nothing ever spawns");

            NetworkIdentity identity = spawner.GetComponent<NetworkIdentity>();
            if (identity == null)
                Fail($"NetworkAsteroidSpawner '{spawner.name}' has no NetworkIdentity, so spawned asteroids never replicate");
            else if (identity.sceneId == 0)
                Fail($"NetworkAsteroidSpawner '{spawner.name}' has sceneId 0; scene NetworkIdentities need a non-zero sceneId to keep clients in sync");
        }

        if (!AssetPaths("t:Prefab").Any(p => p.IndexOf("asteroid", StringComparison.OrdinalIgnoreCase) >= 0))
            Fail("No asteroid prefab exists anywhere in Assets, so no spawner can ever produce one");

        foreach (string path in new[] { "Assets/models/Asteroid_Large.prefab", "Assets/models/Asteroid_Small.prefab" })
        {
            GameObject asteroid = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asteroid == null) { Fail($"Missing asteroid prefab {path}"); continue; }
            if (asteroid.GetComponent<NetworkAsteroid>() == null)
                Fail($"{path} has no NetworkAsteroid, so bullets cannot destroy it");
        }
    }

    static void CheckHud()
    {
        NetworkHUD hud = Object.FindFirstObjectByType<NetworkHUD>(FindObjectsInactive.Include);
        if (hud == null)
        {
            Warn("No NetworkHUD in the scene, so players never see hull/shield/ammo readouts");
            return;
        }

        if (hud.GetComponentInParent<Canvas>(true) == null)
            Fail($"NetworkHUD on '{hud.name}' has no Canvas ancestor, so its bars never render");
        else
            Debug.Log($"[Doctor] NetworkHUD on '{hud.name}'");
    }

    static void CheckMissingScripts()
    {
        foreach (string path in AssetPaths("t:Scene").Concat(AssetPaths("t:Prefab")))
        {
            if (!File.ReadAllText(path).Contains("m_Script: {fileID: 0}")) continue;
            int count = CountOccurrences(File.ReadAllText(path), "m_Script: {fileID: 0}");
            Fail($"Missing script x{count} in {path}");
        }
    }

    // Serialized fields a scene/prefab still stores but the script no longer declares.
    static void CheckOrphans(string path, string text)
    {
        foreach (string guid in ExtractGuids(text))
        {
            string scriptPath = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(scriptPath) || !scriptPath.EndsWith(".cs", StringComparison.Ordinal)) continue;
            if (!scriptPath.StartsWith("Assets/Scripts/", StringComparison.Ordinal)) continue;

            Type type = FindProjectType(Path.GetFileNameWithoutExtension(scriptPath));
            if (type == null) continue;

            var declared = new HashSet<string>(type
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => !f.IsNotSerialized)
                .Select(f => f.Name));

            foreach (string field in ExtractFieldNames(text, guid))
                if (!declared.Contains(field))
                    Warn($"Orphaned serialized field '{field}' ({type.Name}) stored in {path} but missing from the script");
        }
    }

    static void CheckDeadScripts()
    {
        foreach (string path in AssetPaths("t:Script"))
        {
            if (!path.StartsWith("Assets/Scripts/", StringComparison.Ordinal)) continue;
            if (path.Contains("/Editor/")) continue;
            if (path.EndsWith("ShipInputActions.cs", StringComparison.Ordinal)) continue;

            string guid = AssetDatabase.AssetPathToGUID(path);
            bool used = _assetText.Any(kv => kv.Value.Contains(guid));
            if (!used) Warn($"Script referenced by no scene or prefab: {path}");
        }
    }

    static void LoadAssetText()
    {
        _assetText = AssetPaths("t:Scene").Concat(AssetPaths("t:Prefab"))
            .Select(p => new KeyValuePair<string, string>(p, File.ReadAllText(p)))
            .ToArray();
        foreach (var kv in _assetText) CheckOrphans(kv.Key, kv.Value);
    }

    static string[] AssetPaths(string filter) => AssetDatabase.FindAssets(filter)
        .Select(AssetDatabase.GUIDToAssetPath)
        .Where(p => !SkipTrees.Any(s => p.StartsWith(s, StringComparison.Ordinal)))
        .ToArray();

    static IEnumerable<string> ExtractGuids(string text)
    {
        var seen = new HashSet<string>();
        int i = 0;
        while ((i = text.IndexOf("guid: ", i, StringComparison.Ordinal)) >= 0)
        {
            int start = i + 6;
            if (start + 32 <= text.Length)
            {
                string g = text.Substring(start, 32);
                if (seen.Add(g)) yield return g;
            }
            i = start + 32;
        }
    }

    // Fields serialized on a MonoBehaviour block that references this script guid.
    static IEnumerable<string> ExtractFieldNames(string text, string guid)
    {
        var seen = new HashSet<string>();
        int at = 0;
        while ((at = text.IndexOf(guid, at, StringComparison.Ordinal)) >= 0)
        {
            int lineStart = text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
            int blockEnd = text.IndexOf("\n--- ", at, StringComparison.Ordinal);
            if (blockEnd < 0) blockEnd = text.Length;
            string block = text.Substring(lineStart, blockEnd - lineStart);
            foreach (string line in block.Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim();
                if (name.Length == 0 || name.StartsWith("m_", StringComparison.Ordinal)) continue;
                if (name.StartsWith("-", StringComparison.Ordinal)) continue;
                seen.Add(name);
            }
            at = at + guid.Length;
        }
        return seen;
    }

    static Type FindProjectType(string name)
    {
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = a.GetType(name, false);
            if (t != null) return t;
        }
        return null;
    }

    static int CountOccurrences(string text, string token)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(token, i, StringComparison.Ordinal)) >= 0) { n++; i += token.Length; }
        return n;
    }

    static string H(Transform t)
    {
        string s = t.name;
        while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
        return s;
    }
}
