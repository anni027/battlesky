using System;
using System.IO;
using Mirror;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

// One-shot data patch for the projectile: makes the bolt actually move, replicate,
// and read as a glowing blaster bolt instead of a static white sphere.
// CLI: Unity.exe -batchmode -nographics -quit -executeMethod BattleSkyLaserTune.ApplyBatch
public static class BattleSkyLaserTune
{
    const string PlayerPrefabPath = "Assets/models/Player.prefab";
    const string BulletPrefabPath = "Assets/models/bullet.prefab";
    const string ScenePath = "Assets/Scenes/SampleScene.unity";
    const string MaterialPath = "Assets/models/BattleSkyLaserBolt.mat";
    const string ClipPath = "Assets/models/BattleSkyLaserPew.wav";
    const string VolumeProfilePath = "Assets/Settings/SampleSceneProfile.asset";

    // Fast enough to cross a 300m duel in ~0.1s, slow enough to render as a moving bolt.
    const float MuzzleVelocity = 3000f;
    const float LifetimeSeconds = 1.5f;

    // Hot orange-red core; values >1 are written to the HDR buffer so the existing
    // Bloom (threshold 1) haloes it without needing an emissive keyword.
    static readonly Color BoltColor = new Color(2.2f, 0.75f, 0.28f, 1f);
    static readonly Color BoltLight = new Color(1f, 0.55f, 0.25f, 1f);

    [MenuItem("BattleSky/Apply Laser Bolt")]
    public static void Apply()
    {
        EnsureClip();
        EnsureMaterial();
        TunePlayer();
        TuneBullet();
        TuneScene();
        TuneBloom();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[Laser] apply complete");
    }

    public static void ApplyBatch()
    {
        try
        {
            Apply();
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    static void TunePlayer()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
        try
        {
            foreach (NetworkPlayer player in root.GetComponentsInChildren<NetworkPlayer>(true))
            {
                var so = new SerializedObject(player);
                SerializedProperty muzzle = so.FindProperty("muzzleVelocity");
                if (muzzle == null) throw new InvalidOperationException("NetworkPlayer.muzzleVelocity not found");
                muzzle.floatValue = MuzzleVelocity;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            PrefabUtility.SavePrefabAsset(root);
            Debug.Log($"[Laser] {PlayerPrefabPath}: muzzleVelocity -> {MuzzleVelocity}");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    static void TuneBullet()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(BulletPrefabPath);
        try
        {
            // Projectile.Launch early-returns without a Rigidbody, which is why the
            // serialized 20000 muzzle velocity never translated into motion.
            Rigidbody rb = root.GetComponent<Rigidbody>();
            if (rb == null) rb = root.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.isKinematic = false;
            rb.mass = 1f;
            rb.linearDamping = 0f;
            rb.angularDamping = 0f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // Server owns the bolt. The old NetworkTransformReliable ran with
            // syncDirection=ClientToServer, so NetworkIdentity never serialized it.
            foreach (NetworkTransformReliable stale in root.GetComponents<NetworkTransformReliable>())
                Object.DestroyImmediate(stale);
            var netRb = root.AddComponent<NetworkRigidbodyReliable>();
            netRb.target = root.transform;
            netRb.syncDirection = SyncDirection.ServerToClient;
            netRb.syncPosition = true;
            netRb.syncRotation = true;
            netRb.syncScale = false;

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            MeshFilter sourceFilter = root.GetComponent<MeshFilter>();
            if (sourceFilter == null || sourceFilter.sharedMesh == null)
                throw new InvalidOperationException($"{BulletPrefabPath} has no source mesh");

            // Bright head. Uniform scale only: a non-uniform root would break the Rigidbody.
            root.transform.localScale = new Vector3(0.45f, 0.45f, 0.45f);
            MeshRenderer headRenderer = root.GetComponent<MeshRenderer>();
            if (headRenderer == null) headRenderer = root.AddComponent<MeshRenderer>();
            ApplyLaserMaterial(headRenderer, mat);

            // Stretched child sphere = the bolt body, front tip flush with the head.
            Transform beam = root.transform.Find("Beam");
            if (beam == null)
            {
                var go = new GameObject("Beam");
                go.transform.SetParent(root.transform, false);
                beam = go.transform;
            }
            beam.localPosition = new Vector3(0f, 0f, -9.5f);
            beam.localScale = new Vector3(0.4f, 0.4f, 20f);

            MeshFilter beamFilter = beam.GetComponent<MeshFilter>();
            if (beamFilter == null) beamFilter = beam.gameObject.AddComponent<MeshFilter>();
            beamFilter.sharedMesh = sourceFilter.sharedMesh;

            MeshRenderer beamRenderer = beam.GetComponent<MeshRenderer>();
            if (beamRenderer == null) beamRenderer = beam.gameObject.AddComponent<MeshRenderer>();
            ApplyLaserMaterial(beamRenderer, mat);

            Light glow = root.GetComponent<Light>();
            if (glow == null) glow = root.AddComponent<Light>();
            glow.type = LightType.Point;
            glow.color = BoltLight;
            glow.intensity = 6f;
            glow.range = 8f;
            glow.shadows = LightShadows.None;
            glow.renderMode = LightRenderMode.Auto;

            AudioSource audio = root.GetComponent<AudioSource>();
            if (audio == null) audio = root.AddComponent<AudioSource>();
            audio.clip = AssetDatabase.LoadAssetAtPath<AudioClip>(ClipPath);
            if (audio.clip == null) throw new InvalidOperationException($"Missing clip at {ClipPath}");
            audio.playOnAwake = true;
            audio.loop = false;
            audio.spatialBlend = 1f;
            audio.volume = 0.75f;
            audio.dopplerLevel = 0f;
            audio.priority = 100;
            audio.minDistance = 4f;
            audio.maxDistance = 60f;
            audio.rolloffMode = AudioRolloffMode.Logarithmic;

            Projectile projectile = root.GetComponent<Projectile>();
            if (projectile == null) throw new InvalidOperationException($"{BulletPrefabPath} has no Projectile");
            var pso = new SerializedObject(projectile);
            SerializedProperty lifetime = pso.FindProperty("_lifetime");
            if (lifetime == null) throw new InvalidOperationException("Projectile._lifetime not found");
            lifetime.floatValue = LifetimeSeconds;
            pso.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SavePrefabAsset(root);
            Debug.Log($"[Laser] {BulletPrefabPath}: Rigidbody + NetworkRigidbodyReliable(server) + beam + glow + audio");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    static void ApplyLaserMaterial(MeshRenderer renderer, Material mat)
    {
        renderer.sharedMaterial = mat;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
    }

    static void TuneScene()
    {
        UnityEngine.SceneManagement.Scene scene =
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        NetworkManager nm = Object.FindFirstObjectByType<NetworkManager>(FindObjectsInactive.Include);
        if (nm == null) throw new InvalidOperationException($"No NetworkManager in {ScenePath}");

        var so = new SerializedObject(nm);
        SerializedProperty spawnPrefabs = so.FindProperty("spawnPrefabs");
        if (spawnPrefabs == null) throw new InvalidOperationException("NetworkManager.spawnPrefabs not found");

        GameObject bullet = AssetDatabase.LoadAssetAtPath<GameObject>(BulletPrefabPath);
        if (bullet == null) throw new InvalidOperationException($"Missing prefab at {BulletPrefabPath}");

        spawnPrefabs.ClearArray();
        spawnPrefabs.arraySize = 1;
        spawnPrefabs.GetArrayElementAtIndex(0).objectReferenceValue = bullet;
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[Laser] {ScenePath}: spawnPrefabs -> {BulletPrefabPath}");
    }

    static void TuneBloom()
    {
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);
        if (profile == null) throw new InvalidOperationException($"Missing volume profile at {VolumeProfilePath}");
        if (!profile.TryGet(out Bloom bloom)) throw new InvalidOperationException("Volume profile has no Bloom");

        bloom.intensity.overrideState = true;
        bloom.intensity.value = 0.6f;
        bloom.threshold.overrideState = true;
        bloom.threshold.value = 1f;
        EditorUtility.SetDirty(bloom);
        Debug.Log($"[Laser] {VolumeProfilePath}: Bloom intensity -> 0.6 (was 0.25)");
    }

    static void EnsureMaterial()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Simple Lit");
        if (shader == null) throw new InvalidOperationException("No usable unlit shader for the laser bolt");

        Material mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (mat == null)
        {
            mat = new Material(shader) { name = "BattleSkyLaserBolt" };
            AssetDatabase.CreateAsset(mat, MaterialPath);
        }

        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", BoltColor);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", BoltColor);
        EditorUtility.SetDirty(mat);
        Debug.Log($"[Laser] material {MaterialPath} color -> {BoltColor}");
    }

    static void EnsureClip()
    {
        if (AssetDatabase.LoadAssetAtPath<AudioClip>(ClipPath) != null) return;

        const int sampleRate = 44100;
        const float duration = 0.16f;
        int count = Mathf.CeilToInt(sampleRate * duration);
        var samples = new float[count];

        // Original descending "pew": exponential sweep 1500Hz -> 160Hz with a fast
        // exponential tail. No third-party audio is used here.
        float phase = 0f;
        for (int i = 0; i < count; i++)
        {
            float t = (float)i / sampleRate;
            float progress = t / duration;
            float frequency = 1500f * Mathf.Pow(160f / 1500f, progress);
            phase += 2f * Mathf.PI * frequency / sampleRate;

            float attack = Mathf.Min(1f, t / 0.004f);
            float envelope = attack * Mathf.Exp(-t / 0.05f);
            float wave = Mathf.Sin(phase)
                       + 0.45f * Mathf.Sin(phase * 2f)
                       + 0.2f * Mathf.Sin(phase * 3f);
            samples[i] = wave * envelope * 0.45f;
        }

        WriteWav(Path.GetFullPath(ClipPath), samples, sampleRate);
        AssetDatabase.ImportAsset(ClipPath);
        Debug.Log($"[Laser] generated {ClipPath} ({duration:0.00}s)");
    }

    static void WriteWav(string path, float[] samples, int sampleRate)
    {
        int dataLength = samples.Length * 2;
        using (var stream = new MemoryStream(44 + dataLength))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write("RIFF".ToCharArray());
            writer.Write(36 + dataLength);
            writer.Write("WAVE".ToCharArray());
            writer.Write("fmt ".ToCharArray());
            writer.Write(16);
            writer.Write((short)1);              // PCM
            writer.Write((short)1);              // mono
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);        // byte rate
            writer.Write((short)2);              // block align
            writer.Write((short)16);             // bits per sample
            writer.Write("data".ToCharArray());
            writer.Write(dataLength);
            foreach (float sample in samples)
                writer.Write((short)Mathf.RoundToInt(Mathf.Clamp(sample, -1f, 1f) * 32767f));

            File.WriteAllBytes(path, stream.ToArray());
        }
    }
}
