using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

/// <summary>
/// Applies a full sci-fi visual overhaul:
/// 1. Creates materials using BattleSky shaders (laser, shield, ship, asteroid)
/// 2. Assigns materials to prefabs (Player, asteroids, bullet, shield mesh)
/// 3. Sets up post-processing Volume with Bloom, Color Grading, Vignette for realism
/// 4. Configures skybox for deep space
///
/// Run: BattleSky/Apply Sci-Fi Visual Overhaul
/// </summary>
public static class ApplySciFiVisuals
{
    const string PlayerPrefabPath = "Assets/models/Player.prefab";
    const string PlayerStarWarsPath = "Assets/models/Player_StarWars.prefab";
    const string BulletPrefabPath = "Assets/models/bullet.prefab";
    const string AsteroidLargePath = "Assets/models/Asteroid_Large.prefab";
    const string AsteroidSmallPath = "Assets/models/Asteroid_Small.prefab";
    const string LaserMatPath = "Assets/models/BattleSkyLaserBolt.mat";
    const string VolumeProfilePath = "Assets/Settings/SampleSceneProfile.asset";
    const string ScenePath = "Assets/Scenes/SampleScene.unity";

    [MenuItem("BattleSky/Apply Sci-Fi Visual Overhaul")]
    public static void Run()
    {
        CreateMaterials();
        AssignMaterialsToPrefabs();
        SetupPostProcessing();
        SetupSpaceSkybox();

        AssetDatabase.SaveAssets();
        Debug.Log("[SciFi] Visual overhaul complete.");
    }

    static void CreateMaterials()
    {
        // Laser bolt material using BattleSky/SciFi Laser
        var laserShader = Shader.Find("BattleSky/SciFi Laser");
        var laserMat = AssetDatabase.LoadAssetAtPath<Material>(LaserMatPath);
        if (laserMat == null)
        {
            laserMat = new Material(laserShader) { name = "BattleSkyLaserBolt" };
            AssetDatabase.CreateAsset(laserMat, LaserMatPath);
        }
        laserMat.shader = laserShader;
        laserMat.SetColor("_CoreColor", new Color(3f, 0.9f, 0.3f, 1f));
        laserMat.SetColor("_GlowColor", new Color(1f, 0.6f, 0.3f, 1f));
        laserMat.SetFloat("_GlowIntensity", 4f);
        laserMat.SetFloat("_CoreWidth", 0.4f);
        laserMat.SetFloat("_ScrollSpeed", 6f);
        laserMat.SetColor("_RimColor", new Color(1f, 0.9f, 0.6f, 1f));
        laserMat.SetFloat("_RimPower", 3f);
        EditorUtility.SetDirty(laserMat);
        Debug.Log($"[SciFi] Laser material: {LaserMatPath}");

        // Ship material using BattleSky/SciFi Ship
        var shipShader = Shader.Find("BattleSky/SciFi Ship");
        var shipMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/models/ship.mat");
        if (shipMat == null)
        {
            shipMat = new Material(shipShader) { name = "ship" };
            AssetDatabase.CreateAsset(shipMat, "Assets/models/ship.mat");
        }
        shipMat.shader = shipShader;
        shipMat.SetColor("_BaseColor", new Color(0.08f, 0.12f, 0.18f, 1f));
        shipMat.SetFloat("_Metallic", 0.95f);
        shipMat.SetFloat("_Smoothness", 0.9f);
        shipMat.SetColor("_PanelColor", new Color(0.2f, 0.3f, 0.4f, 1f));
        shipMat.SetFloat("_PanelScale", 6f);
        shipMat.SetFloat("_PanelThickness", 0.02f);
        shipMat.SetColor("_EmissiveColor", new Color(0f, 0.4f, 1f, 1f));
        shipMat.SetFloat("_EmissiveIntensity", 3f);
        shipMat.SetColor("_RimColor", new Color(0.3f, 0.6f, 1f, 1f));
        shipMat.SetFloat("_RimPower", 4f);
        EditorUtility.SetDirty(shipMat);
        Debug.Log("[SciFi] Ship material: Assets/models/ship.mat");

        // Asteroid material using BattleSky/SciFi Asteroid
        var asteroidShader = Shader.Find("BattleSky/SciFi Asteroid");
        var rockMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/models/AsteroidRock.mat");
        if (rockMat == null)
        {
            rockMat = new Material(asteroidShader) { name = "AsteroidRock" };
            AssetDatabase.CreateAsset(rockMat, "Assets/models/AsteroidRock.mat");
        }
        rockMat.shader = asteroidShader;
        rockMat.SetColor("_Color", new Color(0.45f, 0.42f, 0.38f, 1f));
        rockMat.SetFloat("_Roughness", 0.85f);
        rockMat.SetColor("_RockColor", new Color(0.5f, 0.48f, 0.42f, 1f));
        rockMat.SetColor("_CrackColor", new Color(0.25f, 0.22f, 0.18f, 1f));
        rockMat.SetFloat("_CrackScale", 10f);
        rockMat.SetFloat("_CrackThickness", 0.05f);
        rockMat.SetColor("_GlowColor", new Color(0.1f, 0.2f, 0.3f, 1f));
        rockMat.SetFloat("_GlowIntensity", 0.4f);
        EditorUtility.SetDirty(rockMat);
        Debug.Log("[SciFi] Asteroid material: Assets/models/AsteroidRock.mat");

        // Shield material using BattleSky/SciFi Shield
        var shieldShader = Shader.Find("BattleSky/SciFi Shield");
        var shieldMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/models/New Material.mat");
        if (shieldMat == null)
        {
            shieldMat = new Material(shieldShader) { name = "ShieldBubble" };
            AssetDatabase.CreateAsset(shieldMat, "Assets/models/ShieldBubble.mat");
        }
        shieldMat.shader = shieldShader;
        shieldMat.SetColor("_BaseColor", new Color(0.1f, 0.4f, 1f, 0.3f));
        shieldMat.SetColor("_GridColor", new Color(0.6f, 0.8f, 1f, 0.8f));
        shieldMat.SetFloat("_GridScale", 25f);
        shieldMat.SetFloat("_GridThickness", 0.03f);
        shieldMat.SetFloat("_FresnelPower", 5f);
        shieldMat.SetFloat("_PulseSpeed", 3f);
        shieldMat.SetFloat("_PulseIntensity", 0.4f);
        shieldMat.SetColor("_GlowColor", new Color(0.3f, 0.7f, 1f, 1f));
        shieldMat.SetFloat("_GlowIntensity", 3f);
        shieldMat.SetFloat("_Cutoff", 0.1f);
        EditorUtility.SetDirty(shieldMat);
        Debug.Log("[SciFi] Shield material: Assets/models/ShieldBubble.mat");
    }

    static void AssignMaterialsToPrefabs()
    {
        // Player prefab - assign ship material to mesh renderers
        var player = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
        if (player != null)
        {
            var root = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
            try
            {
                var shipMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/models/ship.mat");
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (mr.sharedMaterial != null && mr.sharedMaterial.name == "Default-Material")
                        mr.sharedMaterial = shipMat;
                }
                PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
                Debug.Log($"[SciFi] Assigned ship material to {PlayerPrefabPath}");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        // Player_StarWars prefab
        var swPlayer = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerStarWarsPath);
        if (swPlayer != null)
        {
            var root = PrefabUtility.LoadPrefabContents(PlayerStarWarsPath);
            try
            {
                var shipMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/models/ship.mat");
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (mr.sharedMaterial != null && mr.sharedMaterial.name == "Default-Material")
                        mr.sharedMaterial = shipMat;
                }
                PrefabUtility.SaveAsPrefabAsset(root, PlayerStarWarsPath);
                Debug.Log($"[SciFi] Assigned ship material to {PlayerStarWarsPath}");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        // Asteroid prefabs - assign asteroid material
        foreach (var path in new[] { AsteroidLargePath, AsteroidSmallPath })
        {
            var asteroid = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asteroid == null) continue;
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var rockMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/models/AsteroidRock.mat");
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
                    mr.sharedMaterial = rockMat;
                PrefabUtility.SaveAsPrefabAsset(root, path);
                Debug.Log($"[SciFi] Assigned asteroid material to {path}");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        // Bullet prefab - assign laser material
        var bullet = AssetDatabase.LoadAssetAtPath<GameObject>(BulletPrefabPath);
        if (bullet != null)
        {
            var root = PrefabUtility.LoadPrefabContents(BulletPrefabPath);
            try
            {
                var laserMat = AssetDatabase.LoadAssetAtPath<Material>(LaserMatPath);
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
                    mr.sharedMaterial = laserMat;
                PrefabUtility.SaveAsPrefabAsset(root, BulletPrefabPath);
                Debug.Log($"[SciFi] Assigned laser material to {BulletPrefabPath}");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
    }

    static void SetupPostProcessing()
    {
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, VolumeProfilePath);
        }

        // Bloom - strong sci-fi glow
        if (!profile.TryGet(out Bloom bloom))
        {
            bloom = profile.Add<Bloom>();
        }
        bloom.intensity.overrideState = true;
        bloom.intensity.value = 1.2f;
        bloom.threshold.overrideState = true;
        bloom.threshold.value = 0.7f;
        bloom.clamp.overrideState = true;
        bloom.clamp.value = 65472f;
        bloom.scatter.overrideState = true;
        bloom.scatter.value = 0.7f;

        // Color Grading - deep space look
        if (!profile.TryGet(out ColorAdjustments color))
        {
            color = profile.Add<ColorAdjustments>();
        }
        color.postExposure.overrideState = true;
        color.postExposure.value = 0.2f;
        color.contrast.overrideState = true;
        color.contrast.value = 1.1f;
        color.saturation.overrideState = true;
        color.saturation.value = 0.9f;
        color.hueShift.overrideState = true;
        color.hueShift.value = 0f;

        // Vignette - subtle
        if (!profile.TryGet(out Vignette vignette))
        {
            vignette = profile.Add<Vignette>();
        }
        vignette.intensity.overrideState = true;
        vignette.intensity.value = 0.3f;
        vignette.smoothness.overrideState = true;
        vignette.smoothness.value = 0.5f;
        vignette.rounded.overrideState = true;
        vignette.rounded.value = true;

        EditorUtility.SetDirty(profile);
        Debug.Log($"[SciFi] Post-processing volume profile: {VolumeProfilePath}");
    }

    static void SetupSpaceSkybox()
    {
        var skyboxMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/models/skybox.mat");
        if (skyboxMat == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Skybox");
            if (shader == null) shader = Shader.Find("Skybox/Cubemap");
            skyboxMat = new Material(shader) { name = "skybox" };
            AssetDatabase.CreateAsset(skyboxMat, "Assets/models/skybox.mat");
        }

        // Deep space color
        var shaderName = skyboxMat.shader.name;
        if (shaderName.Contains("Universal Render Pipeline/Skybox"))
        {
            skyboxMat.SetColor("_SkyTint", new Color(0.02f, 0.02f, 0.05f, 1f));
            skyboxMat.SetColor("_SunColor", new Color(0.1f, 0.15f, 0.25f, 1f));
            skyboxMat.SetFloat("_SunSize", 0.02f);
            skyboxMat.SetFloat("_SunSizeConvergence", 8f);
            skyboxMat.SetFloat("_AtmosphereThickness", 0f);
            skyboxMat.SetFloat("_Exposure", 1.2f);
        }

        EditorUtility.SetDirty(skyboxMat);

        // Apply skybox to scene
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        RenderSettings.skybox = skyboxMat;
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[SciFi] Deep space skybox configured.");
    }
}