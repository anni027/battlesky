using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using Mirror;
using BattleSky.Network;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>
/// Automates the remaining level-system setup steps:
/// 1. Creates/updates Player_StarWars.prefab and assigns it to the NetworkManager
/// 2. Generates CapitalShip.prefab and DeathStar.prefab (Level 3 boss)
/// 3. Registers all spawnables on BattleSkyNetworkManager.spawnPrefabs
/// 4. Sets up the scene: Canvas + Minimap, NetworkAsteroidSpawner + NetworkEnemySpawner
/// 5. Validates prefabs have required components
///
/// Run: BattleSky / Complete Level System Setup
/// </summary>
public static class CompleteLevelSystemSetup
{
    const string PlayerSource = "Assets/models/Player.prefab";
    const string PlayerStarWars = "Assets/models/Player_StarWars.prefab";
    const string CapitalPath = "Assets/models/CapitalShip.prefab";
    const string DeathStarPath = "Assets/models/DeathStar.prefab";
    const string BulletPath = "Assets/models/bullet.prefab";
    const string AsteroidLargePath = "Assets/models/Asteroid_Large.prefab";
    const string AsteroidSmallPath = "Assets/models/Asteroid_Small.prefab";
    const string ScenePath = "Assets/Scenes/SampleScene.unity";

    // Real material assets. The old in-memory fallback material did not
    // serialize into the saved prefabs, so both bosses rendered with the
    // magenta error material.
    const string CapitalHullMaterialPath = "Assets/models/CapitalShip.mat";
    const string CapitalShieldMaterialPath = "Assets/models/New Material.mat"; // shared force-field shield
    const string DeathStarHullMaterialPath = "Assets/models/DeathStar.mat";
    const string DeathStarDishMaterialPath = "Assets/models/DeathStarDish.mat";
    const string TrenchMaterialPath = "Assets/models/BattleSkyLaserBolt.mat";

    [MenuItem("BattleSky/Complete Level System Setup")]
    public static void Run()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        EnsureStarWarsPlayer();
        EnsureCapitalShip();
        EnsureDeathStar();

        var nm = EnsureNetworkManagerInScene();
        if (nm == null)
        {
            Debug.LogError("[LevelSetup] No BattleSkyNetworkManager found in scene.");
            return;
        }

        AssignPrefabsToManager(nm);
        RegisterSpawnables(nm);
        SetupSceneSpawners(nm);
        SetupMinimap();

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();
        Debug.Log("[LevelSetup] Complete level system setup finished.");
    }

    static void EnsureStarWarsPlayer()
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerSource);
        if (source == null)
        {
            Debug.LogError($"[LevelSetup] Source player prefab missing: {PlayerSource}");
            return;
        }

        GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerStarWars);
        GameObject clone;
        if (existing != null)
            clone = Object.Instantiate(existing);
        else
            clone = Object.Instantiate(source);

        clone.name = "Player_StarWars";
        clone.transform.localScale = new Vector3(1.2f, 1.0f, 1.6f);

        if (clone.GetComponent<NetworkRigidbodyReliable>() == null
            && clone.GetComponent<NetworkRigidbodyUnreliable>() == null
            && clone.GetComponent<NetworkTransformBase>() == null)
        {
            var sync = clone.AddComponent<NetworkRigidbodyReliable>();
            sync.syncDirection = SyncDirection.ServerToClient;
            sync.syncPosition = true;
            sync.syncRotation = true;
        }

        bool hasShield = false;
        foreach (var t in clone.GetComponentsInChildren<Transform>(true))
        {
            if (t.name.Contains("shield", System.StringComparison.OrdinalIgnoreCase))
            {
                t.name = "shield";
                hasShield = true;
            }
        }
        if (!hasShield)
        {
            var shield = new GameObject("shield");
            shield.transform.SetParent(clone.transform, false);
            shield.transform.localPosition = Vector3.zero;
            shield.transform.localScale = Vector3.one * 2f;
            var mf = shield.AddComponent<MeshFilter>();
            mf.sharedMesh = PrimitiveMesh("sphere");
            var mr = shield.AddComponent<MeshRenderer>();
            mr.sharedMaterial = FallbackMaterial();
        }

        PrefabUtility.SaveAsPrefabAsset(clone, PlayerStarWars);
        Object.DestroyImmediate(clone);
        Debug.Log($"[LevelSetup] Created {PlayerStarWars}");
    }

    static void EnsureCapitalShip()
    {
        var go = new GameObject("CapitalShip");
        // Scaled up from the original 3x block; the collider, fire points and
        // shield are all local-space, so they grow with the root.
        go.transform.localScale = Vector3.one * 4f;

        go.AddComponent<NetworkIdentity>();
        var rb = go.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.linearDamping = 0f;
        rb.angularDamping = 0f;
        rb.mass = 5000f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        go.AddComponent<BoxCollider>().size = new Vector3(3f, 2f, 6f);

        var netRb = go.AddComponent<NetworkRigidbodyReliable>();
        netRb.syncDirection = SyncDirection.ServerToClient;
        netRb.syncPosition = true;
        netRb.syncRotation = true;
        netRb.target = go.transform;

        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = PrimitiveMesh("cube");
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = LevelMaterial(CapitalHullMaterialPath);

        CreateFirePoint(go, "FirePoint_Left", new Vector3(-1.5f, 0f, 2f));
        CreateFirePoint(go, "FirePoint_Right", new Vector3(1.5f, 0f, 2f));

        var capital = go.AddComponent<CapitalShip>();
        SetPrivateRef(capital, "_bulletPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(BulletPath));
        SetPrivateRef(capital, "_fighterPrefab", AssetDatabase.LoadAssetAtPath<GameObject>("Assets/models/EnemyShip.prefab"));

        var shield = new GameObject("shield");
        shield.transform.SetParent(go.transform, false);
        shield.transform.localScale = Vector3.one * 4f;
        var smf = shield.AddComponent<MeshFilter>();
        smf.sharedMesh = PrimitiveMesh("sphere");
        var smr = shield.AddComponent<MeshRenderer>();
        smr.sharedMaterial = LevelMaterial(CapitalShieldMaterialPath);

        PrefabUtility.SaveAsPrefabAsset(go, CapitalPath);
        Object.DestroyImmediate(go);
        Debug.Log($"[LevelSetup] Created {CapitalPath}");
    }

    static void EnsureDeathStar()
    {
        // Preserve a hand-authored station: rebuilding here would replace the
        // prefab variant of the external FBX with a hull-less placeholder.
        if (AssetDatabase.LoadAssetAtPath<GameObject>(DeathStarPath) != null)
        {
            Debug.Log($"[LevelSetup] Kept existing {DeathStarPath} (hand-authored boss preserved)");
            if (AssetDatabase.LoadAssetAtPath<GameObject>("Assets/models/DeathStar_External/DeathStar.fbx") != null)
                BattleSkyDeathStarBossWiring.Run();
            return;
        }

        var go = new GameObject("DeathStar");
        go.transform.localScale = Vector3.one * 100f; // massive station

        go.AddComponent<NetworkIdentity>();
        var rb = go.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.isKinematic = true; // Death Star doesn't move
        rb.mass = 1000000f;

        go.AddComponent<SphereCollider>().radius = 0.5f;

        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = PrimitiveMesh("sphere");
        var hullMaterial = LevelMaterial(DeathStarHullMaterialPath);
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = hullMaterial;

        var deathStar = go.AddComponent<DeathStar>();
        SetPrivateFloat(deathStar, "_radius", 50f);

        // Equatorial trench band: thin cylinder slightly wider than the hull
        // (unit sphere radius 0.5, band radius ~0.52). The Unity cylinder mesh
        // is 2 units tall, so scale y 0.03 gives a ~6 m band at x100.
        CreateDetail(go, "Trench", "cylinder", LevelMaterial(TrenchMaterialPath),
            new Vector3(1.04f, 0.03f, 1.04f), Vector3.zero, Quaternion.identity);

        // Superlaser dish: flattened sphere seated on the upper-front surface,
        // flattened along its local Z so LookRotation points that face outward.
        Vector3 dishDir = new Vector3(-0.16f, 0.30f, 0.26f).normalized;
        // Optional material: fall back to the hull instead of logging a
        // missing-asset error on a fresh checkout.
        Material dishMaterial = LevelMaterial(DeathStarDishMaterialPath);
        if (dishMaterial == null) dishMaterial = hullMaterial;
        GameObject dishDetail = CreateDetail(go, "SuperlaserDish", "sphere", dishMaterial,
            new Vector3(0.34f, 0.34f, 0.14f), dishDir * 0.46f, Quaternion.LookRotation(dishDir));

        // Cosmetic turret bumps spread around the equator (no colliders).
        const int bumpCount = 8;
        for (int i = 0; i < bumpCount; i++)
        {
            float angle = Mathf.PI * 2f * i / bumpCount;
            CreateDetail(go, $"TurretBump_{i + 1}", "sphere", hullMaterial,
                Vector3.one * 0.07f,
                new Vector3(Mathf.Cos(angle) * 0.5f, 0.1f, Mathf.Sin(angle) * 0.5f),
                Quaternion.identity);
        }

        // The superlaser beam spawns from the dish detail created above.
        SetPrivateRef(deathStar, "_superlaserDish", dishDetail.transform);

        PrefabUtility.SaveAsPrefabAsset(go, DeathStarPath);
        Object.DestroyImmediate(go);

        // Keep the detailed station hull when this older setup tool regenerates
        // the prefab. The wiring is a no-op on a checkout without the FBX and
        // never discards a hand-authored model.
        if (AssetDatabase.LoadAssetAtPath<GameObject>("Assets/models/DeathStar_External/DeathStar.fbx") != null)
            BattleSkyDeathStarBossWiring.Run();

        Debug.Log($"[LevelSetup] Created {DeathStarPath}");
    }

    static BattleSkyNetworkManager EnsureNetworkManagerInScene()
    {
        var nm = Object.FindFirstObjectByType<BattleSkyNetworkManager>(FindObjectsInactive.Include);
        if (nm != null) return nm;

        var go = new GameObject("NetworkManager");
        return go.AddComponent<BattleSkyNetworkManager>();
    }

    static void AssignPrefabsToManager(BattleSkyNetworkManager nm)
    {
        var so = new SerializedObject(nm);

        var playerStarWars = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerStarWars);
        var capital = AssetDatabase.LoadAssetAtPath<GameObject>(CapitalPath);
        var deathStar = AssetDatabase.LoadAssetAtPath<GameObject>(DeathStarPath);

        SetSerializedRef(so, "playerShipPrefab", playerStarWars);
        SetSerializedRef(so, "capitalShipPrefab", capital);
        SetSerializedRef(so, "deathStarPrefab", deathStar);

        so.ApplyModifiedPropertiesWithoutUndo();

        if (nm.playerPrefab == null && playerStarWars != null)
            nm.playerPrefab = playerStarWars;

        EditorUtility.SetDirty(nm);
        Debug.Log("[LevelSetup] Assigned prefabs to BattleSkyNetworkManager.");
    }

    static void RegisterSpawnables(BattleSkyNetworkManager nm)
    {
        var so = new SerializedObject(nm);
        var spawnPrefabs = so.FindProperty("spawnPrefabs");

        var wanted = new System.Collections.Generic.List<Object>();
        for (int i = 0; i < spawnPrefabs.arraySize; i++)
        {
            var entry = spawnPrefabs.GetArrayElementAtIndex(i).objectReferenceValue;
            if (entry != null && !wanted.Contains(entry)) wanted.Add(entry);
        }

        AddIfMissing(wanted, BulletPath);
        AddIfMissing(wanted, AsteroidLargePath);
        AddIfMissing(wanted, AsteroidSmallPath);
        AddIfMissing(wanted, CapitalPath);
        AddIfMissing(wanted, DeathStarPath);
        AddIfMissing(wanted, PlayerStarWars);

        spawnPrefabs.arraySize = wanted.Count;
        for (int i = 0; i < wanted.Count; i++)
            spawnPrefabs.GetArrayElementAtIndex(i).objectReferenceValue = wanted[i];

        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(nm);
        Debug.Log($"[LevelSetup] Registered {wanted.Count} spawnables on NetworkManager.");
    }

    static void AddIfMissing(System.Collections.Generic.List<Object> wanted, string path)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) return;
        if (!wanted.Contains(prefab)) wanted.Add(prefab);
    }

    static void SetSerializedRef(SerializedObject so, string fieldName, GameObject value)
    {
        var prop = so.FindProperty(fieldName);
        if (prop != null) prop.objectReferenceValue = value;
    }

    static void SetupSceneSpawners(BattleSkyNetworkManager nm)
    {
        var asteroidSpawner = Object.FindFirstObjectByType<NetworkAsteroidSpawner>(FindObjectsInactive.Include);
        if (asteroidSpawner == null)
        {
            var go = new GameObject("NetworkAsteroidSpawner");
            asteroidSpawner = go.AddComponent<NetworkAsteroidSpawner>();
        }

        var enemySpawner = Object.FindFirstObjectByType<NetworkEnemySpawner>(FindObjectsInactive.Include);
        if (enemySpawner == null)
        {
            var go = new GameObject("NetworkEnemySpawner");
            enemySpawner = go.AddComponent<NetworkEnemySpawner>();
        }

        var enemyso = new SerializedObject(enemySpawner);
        SetSerializedRef(enemyso, "capitalShipPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(CapitalPath));
        SetSerializedRef(enemyso, "deathStarPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(DeathStarPath));
        SetSerializedRef(enemyso, "enemyPrefab", AssetDatabase.LoadAssetAtPath<GameObject>("Assets/models/EnemyShip.prefab"));
        enemyso.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(enemySpawner);

        Debug.Log("[LevelSetup] Configured asteroid + enemy spawners.");
    }

    static void SetupMinimap()
    {
        var canvasGo = Object.FindFirstObjectByType<Canvas>(FindObjectsInactive.Include)?.gameObject;
        if (canvasGo == null)
        {
            canvasGo = new GameObject("Canvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();
        }

        if (canvasGo.GetComponent<Minimap>() == null)
            canvasGo.AddComponent<Minimap>();

        Debug.Log("[LevelSetup] Minimap added to Canvas.");
    }

    static GameObject CreateFirePoint(GameObject parent, string name, Vector3 localPos)
    {
        var fp = new GameObject(name);
        fp.transform.SetParent(parent.transform, false);
        fp.transform.localPosition = localPos;
        return fp;
    }

    static Mesh PrimitiveMesh(string shape)
    {
        GameObject temp;
        switch (shape)
        {
            case "sphere":
                temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                break;
            case "cylinder":
                temp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                break;
            default:
                temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                break;
        }
        var mesh = temp.GetComponent<MeshFilter>().sharedMesh;
        Object.DestroyImmediate(temp);
        return mesh;
    }

    /// <summary>
    /// Creates a mesh-only child (no collider) from a built-in primitive mesh,
    /// used for purely cosmetic Death Star surface detail. Everything is set
    /// in the parent's local space so the root's x100 scale applies.
    /// </summary>
    static GameObject CreateDetail(GameObject parent, string name, string shape, Material material,
        Vector3 localScale, Vector3 localPosition, Quaternion localRotation)
    {
        var detail = new GameObject(name);
        detail.transform.SetParent(parent.transform, false);
        detail.transform.localPosition = localPosition;
        detail.transform.localRotation = localRotation;
        detail.transform.localScale = localScale;
        detail.AddComponent<MeshFilter>().sharedMesh = PrimitiveMesh(shape);
        detail.AddComponent<MeshRenderer>().sharedMaterial = material;
        return detail;
    }

    /// <summary>Loads a shared material asset, logging loudly when it is missing.</summary>
    static Material LevelMaterial(string path)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
            Debug.LogError($"[LevelSetup] Missing material asset: {path}");
        return material;
    }

    static Material FallbackMaterial()
    {
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "LevelPrefabFallback" };
        return mat;
    }

    static void SetPrivateRef(Object target, string fieldName, Object value)
    {
        var so = new SerializedObject(target);
        var prop = so.FindProperty(fieldName);
        if (prop != null) prop.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetPrivateFloat(Object target, string fieldName, float value)
    {
        var so = new SerializedObject(target);
        var prop = so.FindProperty(fieldName);
        if (prop != null) prop.floatValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}