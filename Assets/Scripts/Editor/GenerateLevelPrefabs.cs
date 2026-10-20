using UnityEngine;
using UnityEditor;
using Mirror;
using BattleSky.Network;

/// <summary>
/// One-time tool: generates the Level 2 CapitalShip.prefab and Level 3
/// DeathStar.prefab (the trench-run boss) with all required Mirror networking components.
///
/// Run: BattleSky / Generate Level Prefabs (Capital + Death Star)
/// </summary>
public static class GenerateLevelPrefabs
{
    const string CapitalPath = "Assets/models/CapitalShip.prefab";
    const string DeathStarPath = "Assets/models/DeathStar.prefab";
    const string BulletPath = "Assets/models/bullet.prefab";
    const string FighterPath = "Assets/models/EnemyShip.prefab";

    // Real material assets. The first pass used in-memory fallback materials,
    // which do not serialize into the saved prefabs, so both ships rendered
    // with the magenta error material.
    const string CapitalHullMaterialPath = "Assets/models/CapitalShip.mat";
    const string CapitalShieldMaterialPath = "Assets/models/New Material.mat"; // shared force-field shield
    const string DeathStarHullMaterialPath = "Assets/models/DeathStar.mat";
    const string DeathStarDishMaterialPath = "Assets/models/DeathStarDish.mat";
    const string TrenchMaterialPath = "Assets/models/BattleSkyLaserBolt.mat";
    const string DeathStarModelPath = "Assets/models/DeathStar_External/DeathStar.fbx";

    [MenuItem("BattleSky/Generate Level Prefabs (Capital + Death Star)")]
    public static void Generate()
    {
        GenerateCapitalShip();
        GenerateDeathStar();
        AssetDatabase.Refresh();
        Debug.Log("[LevelPrefabs] Generation complete. Assign to BattleSkyNetworkManager.spawnPrefabs.");
    }

    static void GenerateCapitalShip()
    {
        GameObject go = new GameObject("CapitalShip");
        // Scaled up from the original 3x block; the collider, fire points and
        // shield are all local-space, so they grow with the root.
        go.transform.localScale = Vector3.one * 4f;

        var identity = go.AddComponent<NetworkIdentity>();
        var rb = go.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.linearDamping = 0f;
        rb.angularDamping = 0f;
        rb.mass = 5000f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        var col = go.AddComponent<BoxCollider>();
        col.size = new Vector3(3f, 2f, 6f);

        var netRb = go.AddComponent<NetworkRigidbodyReliable>();
        netRb.syncDirection = SyncDirection.ServerToClient;
        netRb.syncPosition = true;
        netRb.syncRotation = true;
        netRb.target = go.transform;

        var meshFilter = go.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = GetFallbackMesh("cube");

        var meshRenderer = go.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = LoadMaterial(CapitalHullMaterialPath);

        CreateFirePoint(go, "FirePoint_Left", new Vector3(-1.5f, 0f, 2f));
        CreateFirePoint(go, "FirePoint_Right", new Vector3(1.5f, 0f, 2f));

        var capital = go.AddComponent<CapitalShip>();
        SetPrivateRef(capital, "_bulletPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(BulletPath));
        SetPrivateRef(capital, "_fighterPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(FighterPath));

        var shield = new GameObject("shield");
        shield.transform.SetParent(go.transform, false);
        shield.transform.localPosition = Vector3.zero;
        shield.transform.localScale = Vector3.one * 4f;
        var smf = shield.AddComponent<MeshFilter>();
        smf.sharedMesh = GetFallbackMesh("sphere");
        var smr = shield.AddComponent<MeshRenderer>();
        smr.sharedMaterial = LoadMaterial(CapitalShieldMaterialPath);

        PrefabUtility.SaveAsPrefabAsset(go, CapitalPath);
        Object.DestroyImmediate(go);
        Debug.Log($"[LevelPrefabs] Created {CapitalPath}");
    }

    static void GenerateDeathStar()
    {
        // Never clobber a hand-authored station (for example the prefab variant
        // of the external FBX): this placeholder has no hull, so saving over it
        // would silently destroy the boss model. Delete the prefab first if a
        // generic placeholder is really wanted.
        if (AssetDatabase.LoadAssetAtPath<GameObject>(DeathStarPath) != null)
        {
            Debug.Log($"[LevelPrefabs] Kept existing {DeathStarPath} (hand-authored boss preserved)");
            if (AssetDatabase.LoadAssetAtPath<GameObject>(DeathStarModelPath) != null)
                BattleSkyDeathStarBossWiring.Run();
            return;
        }

        GameObject go = new GameObject("DeathStar");
        go.transform.localScale = Vector3.one * 100f; // massive station

        go.AddComponent<NetworkIdentity>();
        var rb = go.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.isKinematic = true;
        rb.mass = 1000000f;

        go.AddComponent<SphereCollider>().radius = 0.5f;

        var meshFilter = go.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = GetFallbackMesh("sphere");

        var hullMaterial = LoadMaterial(DeathStarHullMaterialPath);
        var meshRenderer = go.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = hullMaterial;

        var deathStar = go.AddComponent<DeathStar>();
        SetPrivateFloat(deathStar, "_radius", 50f);

        // Equatorial trench band: a thin cylinder slightly wider than the hull
        // (unit sphere radius 0.5, band radius ~0.52) that reads as the
        // glowing trench ring the player flies into. The Unity cylinder mesh
        // is 2 units tall, so scale y 0.03 gives a ~6 m band at x100.
        CreateDetail(go, "Trench", "cylinder", LoadMaterial(TrenchMaterialPath),
            new Vector3(1.04f, 0.03f, 1.04f), Vector3.zero, Quaternion.identity);

        // Superlaser dish: flattened sphere seated on the upper-front surface.
        // Flattened along its local Z so LookRotation points that face outward.
        Vector3 dishDir = new Vector3(-0.16f, 0.30f, 0.26f).normalized;
        // The dish material is optional: fall back to the hull so a fresh
        // checkout does not log a missing-asset error or render magenta.
        var dishMaterial = LoadMaterial(DeathStarDishMaterialPath);
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

        // If a hand-authored station model is present (a variant of the
        // external FBX), wire it up as the boss instead of shipping the
        // placeholder sphere. Re-runnable and never discards the model.
        if (AssetDatabase.LoadAssetAtPath<GameObject>(DeathStarModelPath) != null)
            BattleSkyDeathStarBossWiring.Run();

        Debug.Log($"[LevelPrefabs] Created {DeathStarPath}");
    }

    static void CreateFirePoint(GameObject parent, string name, Vector3 localPos)
    {
        var fp = new GameObject(name);
        fp.transform.SetParent(parent.transform, false);
        fp.transform.localPosition = localPos;
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
        detail.AddComponent<MeshFilter>().sharedMesh = GetFallbackMesh(shape);
        detail.AddComponent<MeshRenderer>().sharedMaterial = material;
        return detail;
    }

    static Mesh GetFallbackMesh(string shape)
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

    /// <summary>Loads a shared material asset, logging loudly when it is missing.</summary>
    static Material LoadMaterial(string path)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
            Debug.LogError($"[LevelPrefabs] Missing material asset: {path}");
        return material;
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
