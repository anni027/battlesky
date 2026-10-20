using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Wires the fire points authored on the enemy ship model into EnemyShip so
/// the guns shoot out of the markers instead of out of the middle of the
/// hull, and pushes the tuning that belongs with them.
///
/// The prefab is loaded as a whole, edited in memory and saved back, and the
/// array is resized (never appended to), so running the menu item again is a
/// no-op instead of duplicating guns. When a marker is missing the problem is
/// logged and the prefab is left untouched.
///
/// Run: BattleSky / Wire Enemy Ship Guns
/// </summary>
public static class BattleSkyEnemyShipWiring
{
    const string PrefabPath = "Assets/models/EnemyShip.prefab";

    static readonly string[] FirePointNames =
    {
        "firepoint1", "firepoint2"
    };

    const string FirePointPrefix = "firepoint";

    // Bolts leave the gun a little ahead of it so they never start inside the
    // hull mesh.
    const float MuzzleClearance = 4f;

    [MenuItem("BattleSky/Wire Enemy Ship Guns")]
    public static void Run()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError($"[EnemyWiring] could not load {PrefabPath}");
            return;
        }

        bool completed = false;
        try
        {
            var enemy = root.GetComponentInChildren<BattleSky.Network.EnemyShip>(true);
            if (enemy == null)
            {
                Debug.LogError($"[EnemyWiring] {PrefabPath} has no EnemyShip component - prefab left untouched");
                return;
            }

            Transform[] all = root.GetComponentsInChildren<Transform>(true);

            bool usedFallback;
            Transform[] firePoints = ResolveFirePoints(all, out usedFallback);
            if (firePoints.Length == 0)
            {
                Debug.LogError($"[EnemyWiring] no fire points found under {PrefabPath} - prefab left untouched");
                return;
            }

            var serialized = new SerializedObject(enemy);
            if (!SetObjectArray(serialized, "_firePoints", firePoints))
            {
                Debug.LogError($"[EnemyWiring] field wiring failed on {PrefabPath} - prefab left untouched");
                return;
            }
            if (!SetFloat(serialized, "_muzzleClearance", MuzzleClearance))
            {
                Debug.LogError($"[EnemyWiring] field wiring failed on {PrefabPath} - prefab left untouched");
                return;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            completed = true;

            Debug.Log($"[EnemyWiring] {PrefabPath}: fire points [{Describe(firePoints)}], " +
                $"muzzle clearance {MuzzleClearance} m" +
                (usedFallback ? " (fire points resolved by prefix fallback)" : string.Empty));

            ReportGeometry(root, firePoints);

            Debug.Log("=== EnemyWiring done ===");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
            if (completed) AssetDatabase.SaveAssets();
        }
    }

    /// <summary>
    /// Resolves the fire points by exact name in the requested order. When an
    /// exact name is missing, every transform whose name starts with the
    /// prefix is used instead, sorted by name, so a renumbered model still
    /// wires.
    /// </summary>
    static Transform[] ResolveFirePoints(Transform[] all, out bool usedFallback)
    {
        usedFallback = false;

        var exact = new Transform[FirePointNames.Length];
        bool allFound = true;
        for (int i = 0; i < FirePointNames.Length; i++)
        {
            exact[i] = FindExact(all, FirePointNames[i]);
            if (exact[i] == null) allFound = false;
        }

        if (allFound) return exact;

        usedFallback = true;

        var matches = new List<Transform>();
        foreach (Transform candidate in all)
        {
            if (candidate != null && candidate.name.StartsWith(FirePointPrefix, StringComparison.OrdinalIgnoreCase))
                matches.Add(candidate);
        }
        matches.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

        Debug.LogWarning($"[EnemyWiring] exact fire point name missing - " +
            $"falling back to {matches.Count} prefix match(es): [{Describe(matches.ToArray())}]");

        return matches.ToArray();
    }

    static Transform FindExact(Transform[] all, string name)
    {
        foreach (Transform candidate in all)
        {
            if (candidate != null && candidate.name == name) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Prints where the guns sit relative to the ship, so a mis-placed marker
    /// is obvious without opening the prefab by hand.
    /// </summary>
    static void ReportGeometry(GameObject root, Transform[] firePoints)
    {
        Transform ship = root.transform;
        Debug.Log($"[EnemyWiring] ship root scale {ship.localScale}, forward {ship.forward}, position {ship.position}");

        foreach (Transform gun in firePoints)
        {
            if (gun == null) continue;

            Vector3 local = ship.InverseTransformPoint(gun.position);
            float along = Vector3.Dot(local, ship.forward);
            float sideways = Vector3.Dot(local, ship.right);
            float up = Vector3.Dot(local, ship.up);
            float alignment = Vector3.Dot(gun.forward, ship.forward);

            Debug.Log($"[EnemyWiring] gun '{gun.name}' world {gun.position}, " +
                $"ship-local ({local.x:F3}, {local.y:F3}, {local.z:F3}), " +
                $"forward {along:F2} m, sideways {sideways:F2} m, up {up:F2} m, " +
                $"distance {Vector3.Distance(gun.position, ship.position):F2} m, " +
                $"forward alignment with ship {alignment:F2}");
        }

        foreach (Collider hull in root.GetComponentsInChildren<Collider>())
        {
            if (hull == null) continue;
            Bounds bounds = hull.bounds;
            Debug.Log($"[EnemyWiring] collider '{hull.name}' bounds size {bounds.size}, " +
                $"center {bounds.center}, extent along ship forward " +
                $"{bounds.extents.x * Mathf.Abs(ship.forward.x) + bounds.extents.y * Mathf.Abs(ship.forward.y) + bounds.extents.z * Mathf.Abs(ship.forward.z):F2} m");
        }
    }

    static bool SetObjectArray(SerializedObject serialized, string fieldName, Transform[] values)
    {
        SerializedProperty property = serialized.FindProperty(fieldName);
        if (property == null || !property.isArray)
        {
            Debug.LogError($"[EnemyWiring] {fieldName} array not found on {serialized.targetObject.GetType().Name}");
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
            Debug.LogError($"[EnemyWiring] {fieldName} not found on {serialized.targetObject.GetType().Name}");
            return false;
        }

        property.floatValue = value;
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
