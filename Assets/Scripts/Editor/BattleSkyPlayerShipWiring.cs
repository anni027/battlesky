using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Wires the four fire points and the four thrust mounts authored on the player
/// ship model into NetworkPlayer and ShipVisualEffects, and pushes the speed
/// line tuning that belongs with them.
///
/// The prefab is loaded as a whole, edited in memory and saved back, and every
/// field is written absolutely (the arrays are resized, never appended to), so
/// running the menu item again is a no-op instead of duplicating mounts. When a
/// required component or mount is missing the problem is logged and the prefab
/// is left untouched.
///
/// Run: BattleSky / Wire Player Ship Mounts
/// </summary>
public static class BattleSkyPlayerShipWiring
{
    const string PrefabPath = "Assets/models/Player.prefab";

    static readonly string[] FireMountNames =
    {
        "fire1", "fire1 (1)", "fire1 (2)", "fire1 (3)"
    };

    static readonly string[] ThrustMountNames =
    {
        "thrustp1", "thrustp1 (1)", "thrustp1 (2)", "thrustp1 (3)"
    };

    // Speed lines read as a few short dashes instead of laser beams.
    const float SpeedLineStartSpeed = 60f;
    const float SpeedLineFullSpeed = 140f;
    const float SpeedLineMaxRate = 10f;
    const int SpeedLineMaxParticles = 18;

    // The Death Star is ~2 km across, and it approaches from far outside a
    // default 1 km far clip, so every camera on the ship gets a longer one.
    const float CameraFarClipPlane = 12000f;

    [MenuItem("BattleSky/Wire Player Ship Mounts")]
    public static void Run()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError($"[PlayerWiring] could not load {PrefabPath}");
            return;
        }

        bool completed = false;
        try
        {
            var player = root.GetComponentInChildren<NetworkPlayer>(true);
            var vfx = root.GetComponentInChildren<ShipVisualEffects>(true);
            if (player == null || vfx == null)
            {
                string missing = player == null && vfx == null
                    ? "NetworkPlayer and ShipVisualEffects"
                    : player == null ? "NetworkPlayer" : "ShipVisualEffects";
                Debug.LogError($"[PlayerWiring] {PrefabPath} is missing {missing} - prefab left untouched");
                return;
            }

            Transform[] all = root.GetComponentsInChildren<Transform>(true);

            bool fireFallback;
            bool thrustFallback;
            Transform[] fireMounts = ResolveMounts(all, FireMountNames, "fire1", out fireFallback);
            Transform[] thrustMounts = ResolveMounts(all, ThrustMountNames, "thrustp1", out thrustFallback);

            if (fireMounts.Length == 0 || thrustMounts.Length == 0)
            {
                var missing = new List<string>();
                if (fireMounts.Length == 0) missing.Add("fire1");
                if (thrustMounts.Length == 0) missing.Add("thrustp1");
                Debug.LogError($"[PlayerWiring] no {string.Join("/", missing)} mounts found under {PrefabPath} - prefab left untouched");
                return;
            }

            // ------------------------------------------------ NetworkPlayer
            var playerSerialized = new SerializedObject(player);
            bool playerWired = SetObjectArray(playerSerialized, "firePoints", fireMounts);
            if (playerWired) playerSerialized.ApplyModifiedPropertiesWithoutUndo();

            // ------------------------------------------- ShipVisualEffects
            var vfxSerialized = new SerializedObject(vfx);
            bool vfxWired = SetObjectArray(vfxSerialized, "_thrustPoints", thrustMounts);
            vfxWired &= SetFloat(vfxSerialized, "_speedLineStartSpeed", SpeedLineStartSpeed);
            vfxWired &= SetFloat(vfxSerialized, "_speedLineFullSpeed", SpeedLineFullSpeed);
            vfxWired &= SetFloat(vfxSerialized, "_speedLineMaxRate", SpeedLineMaxRate);
            vfxWired &= SetInt(vfxSerialized, "_speedLineMaxParticles", SpeedLineMaxParticles);
            if (vfxWired) vfxSerialized.ApplyModifiedPropertiesWithoutUndo();

            if (!playerWired || !vfxWired)
            {
                Debug.LogError($"[PlayerWiring] field wiring failed on {PrefabPath} - prefab left untouched");
                return;
            }

            // --------------------------------------------------- far clips
            int cameras = 0;
            foreach (Camera camera in root.GetComponentsInChildren<Camera>(true))
            {
                camera.farClipPlane = CameraFarClipPlane;
                cameras++;
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            completed = true;

            Debug.Log($"[PlayerWiring] {PrefabPath}: fire points [{Describe(fireMounts)}], " +
                $"thrust mounts [{Describe(thrustMounts)}], far clip {CameraFarClipPlane} on {cameras} camera(s)");
            Debug.Log($"[PlayerWiring] speed lines start {SpeedLineStartSpeed} m/s, full {SpeedLineFullSpeed} m/s, " +
                $"rate {SpeedLineMaxRate}/s, max {SpeedLineMaxParticles} particles" +
                (fireFallback || thrustFallback ? " (mount names resolved by prefix fallback)" : string.Empty));
            Debug.Log("=== PlayerWiring done ===");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
            if (completed) AssetDatabase.SaveAssets();
        }
    }

    /// <summary>
    /// Resolves the mounts by exact name in the requested order. When any exact
    /// name is missing, every transform whose name starts with the prefix is
    /// used instead, sorted by name, so a renumbered model still wires.
    /// </summary>
    static Transform[] ResolveMounts(Transform[] all, string[] exactNames, string prefix, out bool usedFallback)
    {
        usedFallback = false;

        var exact = new Transform[exactNames.Length];
        bool allFound = true;
        for (int i = 0; i < exactNames.Length; i++)
        {
            exact[i] = FindExact(all, exactNames[i]);
            if (exact[i] == null) allFound = false;
        }

        if (allFound) return exact;

        usedFallback = true;

        var matches = new List<Transform>();
        foreach (Transform candidate in all)
        {
            if (candidate != null && candidate.name.StartsWith(prefix, StringComparison.Ordinal))
                matches.Add(candidate);
        }
        matches.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

        Debug.LogWarning($"[PlayerWiring] exact mount name missing for '{prefix}' - " +
            $"falling back to {matches.Count} prefix match(es): [{Describe(matches.ToArray())}]");

        return matches.ToArray();
    }

    static Transform FindExact(Transform[] all, string name)
    {
        foreach (Transform candidate in all)
        {
            if (candidate != null && candidate.name == name)
                return candidate;
        }
        return null;
    }

    static bool SetObjectArray(SerializedObject serialized, string fieldName, Transform[] values)
    {
        SerializedProperty property = serialized.FindProperty(fieldName);
        if (property == null || !property.isArray)
        {
            Debug.LogError($"[PlayerWiring] {fieldName} array not found on {serialized.targetObject.GetType().Name}");
            return false;
        }

        property.arraySize = values.Length;
        for (int i = 0; i < values.Length; i++)
            property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];

        return true;
    }

    static bool SetFloat(SerializedObject serialized, string fieldName, float value)
    {
        SerializedProperty property = serialized.FindProperty(fieldName);
        if (property == null)
        {
            Debug.LogError($"[PlayerWiring] {fieldName} not found on {serialized.targetObject.GetType().Name}");
            return false;
        }

        property.floatValue = value;
        return true;
    }

    static bool SetInt(SerializedObject serialized, string fieldName, int value)
    {
        SerializedProperty property = serialized.FindProperty(fieldName);
        if (property == null)
        {
            Debug.LogError($"[PlayerWiring] {fieldName} not found on {serialized.targetObject.GetType().Name}");
            return false;
        }

        property.intValue = value;
        return true;
    }

    static string Describe(Transform[] mounts)
    {
        var names = new string[mounts.Length];
        for (int i = 0; i < mounts.Length; i++)
            names[i] = mounts[i] != null ? mounts[i].name : "<null>";
        return string.Join(", ", names);
    }
}
