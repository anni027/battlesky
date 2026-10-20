using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Mirror;
using BattleSky.Network;

/// <summary>
/// Turns the hand-authored Death Star prefab (a variant of the downloaded
/// FBX) into the Level 3 boss: adds the network/battle components, the
/// superlaser anchor and the force field, and normalises the station size
/// to the 1000 m hull radius the level design is built around.
///
/// The user's own model, hierarchy and material choices are preserved; only
/// what the boss fight needs is added. Safe to re-run: everything is skipped
/// when it already exists.
///
/// Run: BattleSky / Wire Death Star Boss Prefab
/// </summary>
public static class BattleSkyDeathStarBossWiring
{
    const string PrefabPath = "Assets/models/DeathStar.prefab";

    /// <summary>Force field shader material; the boss tints its emission with it.</summary>
    const string ForceFieldMaterialPath = "Assets/models/New Material 1.mat";

    /// <summary>Matches DeathStar._radius; approach distances and the despawn
    /// ring are sized for this.</summary>
    const float HullRadius = 1000f;

    [MenuItem("BattleSky/Wire Death Star Boss Prefab")]
    public static void Run()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        bool completed = false;
        try
        {
            // -------------------------------------------------- station size
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            // The boss code sizes the superlaser anchor and the collider for
            // an x2000 root, so keep that and counter-scale the model children
            // instead.
            root.transform.localScale = Vector3.one * 2000f;

            // The old 50 m station carried eight visible turret spheres; the
            // 2 km station uses four invisible origins instead. Remove them
            // before anything measures the hull.
            var legacyBumps = new System.Collections.Generic.List<Transform>();
            foreach (Transform child in root.transform)
            {
                if (child.name.StartsWith("TurretBump_")) legacyBumps.Add(child);
            }
            for (int i = 0; i < legacyBumps.Count; i++)
                Object.DestroyImmediate(legacyBumps[i].gameObject);

            // The station no longer has an exhaust port, so the old anchor has
            // no meaning any more: drop it instead of leaving a child that
            // nothing looks for any more.
            var legacyExhaustAnchors = new System.Collections.Generic.List<Transform>();
            foreach (Transform child in root.transform)
            {
                if (child.name.StartsWith("ExhaustPortAnchor")) legacyExhaustAnchors.Add(child);
            }
            for (int i = 0; i < legacyExhaustAnchors.Count; i++)
                Object.DestroyImmediate(legacyExhaustAnchors[i].gameObject);

            var modelRenderers = root.GetComponentsInChildren<MeshRenderer>(true);
            if (modelRenderers.Length == 0)
            {
                Debug.LogError("[BossWiring] no model renderers found in " + PrefabPath);
                return;
            }

            float extent = MaxExtent(modelRenderers);
            if (extent <= 0f)
            {
                Debug.LogError("[BossWiring] could not measure the hull");
                return;
            }

            float factor = HullRadius / extent;
            if (!Mathf.Approximately(factor, 1f))
            {
                foreach (Transform child in root.transform)
                {
                    if (IsBossAnchor(child.name)) continue;
                    child.localScale *= factor;
                }
            }
            extent = MaxExtent(modelRenderers);
            Debug.Log($"[BossWiring] hull radius {extent:F2} m (target {HullRadius}) rootScale={root.transform.localScale.x} childFactor={factor:F4}");

            // ------------------------------------------------ battle wiring
            if (root.GetComponent<NetworkIdentity>() == null)
                root.AddComponent<NetworkIdentity>();

            var body = root.GetComponent<Rigidbody>();
            if (body == null) body = root.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.isKinematic = true;
            body.mass = 1000000f;

            var hull = root.GetComponent<SphereCollider>();
            if (hull == null) hull = root.AddComponent<SphereCollider>();
            hull.center = Vector3.zero;
            hull.radius = 0.5f; // 0.5 x the x2000 scale = the 1000 m hull

            var deathStar = root.GetComponent<DeathStar>();
            if (deathStar == null) deathStar = root.AddComponent<DeathStar>();

            // ------------------------------------------------------ anchors
            Transform dish = GetOrCreate(root.transform, "SuperlaserDish");
            dish.localPosition = FindDishLocalPoint(root, modelRenderers);
            if (dish.localPosition.sqrMagnitude > 0.0001f)
                dish.localRotation = Quaternion.LookRotation(dish.localPosition.normalized, Vector3.up);
            dish.localScale = Vector3.one;

            // The boss fight is a clean station: no turret bumps and no turret
            // origins, the crew fights the hull and the force field only.
            var legacyTurretPoints = new System.Collections.Generic.List<Transform>();
            foreach (Transform child in root.transform)
            {
                if (child.name.StartsWith("TurretPoint_")) legacyTurretPoints.Add(child);
            }
            for (int i = 0; i < legacyTurretPoints.Count; i++)
                Object.DestroyImmediate(legacyTurretPoints[i].gameObject);

            // ------------------------------------------------------- fields
            var serialized = new SerializedObject(deathStar);
            serialized.FindProperty("_radius").floatValue = HullRadius;
            // The station is a boss: heavy armour, the force field is the wall.
            serialized.FindProperty("_maxSurfaceHealth").intValue = 1200;
            serialized.FindProperty("_surfaceDamageMultiplier").floatValue = 0.25f;
            // The white force field that shields the hull in stage one.
            serialized.FindProperty("_forceFieldMaxHealth").floatValue = 900f;
            serialized.FindProperty("_forceFieldRadius").floatValue = 1.25f;
            serialized.FindProperty("_superlaserRange").floatValue = 5000f;
            serialized.FindProperty("_superlaserBeamRadius").floatValue = 48f;
            serialized.FindProperty("_superlaserDamageRadius").floatValue = 40f;
            serialized.FindProperty("_explosionRadius").floatValue = 200f;
            serialized.FindProperty("_superlaserDish").objectReferenceValue = dish;

            // The force field material drives the shield's emission colour.
            Material forceFieldMaterial = AssetDatabase.LoadAssetAtPath<Material>(ForceFieldMaterialPath);
            if (forceFieldMaterial == null)
            {
                Debug.LogError("[BossWiring] missing force field material at " + ForceFieldMaterialPath);
            }
            else
            {
                SerializedProperty forceFieldMaterialProp = serialized.FindProperty("_forceFieldMaterial");
                if (forceFieldMaterialProp == null)
                    Debug.LogError("[BossWiring] DeathStar has no _forceFieldMaterial field, so the force field stays untinted");
                else
                    forceFieldMaterialProp.objectReferenceValue = forceFieldMaterial;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            completed = true;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
            AssetDatabase.SaveAssets();
            if (completed)
            {
                // A prefab variant stores a different root fileID than the old
                // plain prefab, so the scene's serialized references have to be
                // re-pointed or Mirror loses its spawn registration.
                FixSceneReferences();
                Debug.Log($"[BossWiring] wired boss prefab: dish anchor on the model dish, white force field ({ForceFieldMaterialPath}), 1000 m armoured hull");
                Debug.Log("=== BossWiring done ===");
            }
            else
            {
                Debug.LogError("[BossWiring] wiring failed - prefab left untouched");
            }
        }
    }

    /// <summary>
    /// Re-points the SampleScene references (NetworkManager.spawnPrefabs and
    /// NetworkEnemySpawner.deathStarPrefab) at the boss prefab, dropping any
    /// null entries left behind by the fileID change.
    /// </summary>
    static void FixSceneReferences()
    {
        const string scenePath = "Assets/Scenes/SampleScene.unity";
        GameObject bossPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (bossPrefab == null)
        {
            Debug.LogError("[BossWiring] could not reload " + PrefabPath + " for the scene fix");
            return;
        }

        var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        bool dirty = false;

        var networkManager = Object.FindFirstObjectByType<BattleSkyNetworkManager>(FindObjectsInactive.Include);
        if (networkManager != null)
        {
            var serialized = new SerializedObject(networkManager);
            SerializedProperty spawnPrefabs = serialized.FindProperty("spawnPrefabs");
            if (spawnPrefabs != null)
            {
                for (int i = spawnPrefabs.arraySize - 1; i >= 0; i--)
                {
                    if (spawnPrefabs.GetArrayElementAtIndex(i).objectReferenceValue == null)
                    {
                        spawnPrefabs.DeleteArrayElementAtIndex(i);
                        dirty = true;
                    }
                }

                bool registered = false;
                for (int i = 0; i < spawnPrefabs.arraySize; i++)
                {
                    if (spawnPrefabs.GetArrayElementAtIndex(i).objectReferenceValue == bossPrefab)
                    {
                        registered = true;
                        break;
                    }
                }
                if (!registered)
                {
                    spawnPrefabs.InsertArrayElementAtIndex(spawnPrefabs.arraySize);
                    spawnPrefabs.GetArrayElementAtIndex(spawnPrefabs.arraySize - 1).objectReferenceValue = bossPrefab;
                    dirty = true;
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        var spawner = Object.FindFirstObjectByType<NetworkEnemySpawner>(FindObjectsInactive.Include);
        if (spawner != null)
        {
            var serialized = new SerializedObject(spawner);
            SerializedProperty deathStarPrefab = serialized.FindProperty("deathStarPrefab");
            if (deathStarPrefab != null && deathStarPrefab.objectReferenceValue != bossPrefab)
            {
                deathStarPrefab.objectReferenceValue = bossPrefab;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                dirty = true;
            }
        }

        // The manager mirrors its level prefabs into the spawner on Awake; keep
        // that field filled too so the scene inspector stays consistent.
        if (networkManager != null)
        {
            var serialized = new SerializedObject(networkManager);
            SerializedProperty managerDeathStar = serialized.FindProperty("deathStarPrefab");
            if (managerDeathStar != null && managerDeathStar.objectReferenceValue != bossPrefab)
            {
                managerDeathStar.objectReferenceValue = bossPrefab;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                dirty = true;
            }
        }

        if (dirty)
        {
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[BossWiring] repaired SampleScene references to the boss prefab");
        }
        else
        {
            Debug.Log("[BossWiring] SampleScene references already correct");
        }
    }

    static float MaxExtent(MeshRenderer[] renderers)
    {
        float extent = 0f;
        foreach (var r in renderers)
            extent = Mathf.Max(extent, Mathf.Max(r.bounds.extents.x,
                Mathf.Max(r.bounds.extents.y, r.bounds.extents.z)));
        return extent;
    }

    /// <summary>
    /// The downloaded model carries its superlaser dish as a separate mesh
    /// ("DeathStar_misa001"); its centre is where the beam should originate.
    /// Falls back to the classic upper-front position when no dish mesh exists.
    /// </summary>
    static Vector3 FindDishLocalPoint(GameObject root, MeshRenderer[] renderers)
    {
        foreach (var r in renderers)
        {
            string n = r.name.ToLowerInvariant();
            if (!n.Contains("dish") && !n.Contains("misa")) continue;
            Vector3 local = root.transform.InverseTransformPoint(r.bounds.center);
            if (local.sqrMagnitude > 0.01f) return local;
        }
        return new Vector3(0f, 0.37f, 0.93f).normalized * 0.47f;
    }

    static bool IsBossAnchor(string name) =>
        name.StartsWith("SuperlaserDish") ||
        name.StartsWith("TurretPoint_");

    static Transform GetOrCreate(Transform parent, string name)
    {
        Transform existing = parent.Find(name);
        if (existing != null) return existing;
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.transform;
    }
}
