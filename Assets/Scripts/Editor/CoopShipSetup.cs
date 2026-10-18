using UnityEngine;
using UnityEditor;
using UnityEngine.InputSystem;

/// <summary>
/// One-click local (shared-screen) co-op wiring. Select the ship root, then
/// BattleSky / Setup Local Co-op Ship. Adds P1 Pilot + P2 Gunner handlers,
/// turret, crew manager and points flight/weapons/shields/HUD at them.
/// Pilot: W/S thrust, A/D yaw, mouse steer, Q/E roll, Shift boost.
/// Gunner: Arrows aim turret, Enter/RightClick fire, Space shield, R reload.
/// </summary>
public static class CoopShipSetup
{
    [MenuItem("BattleSky/Setup Local Co-op Ship")]
    public static void SetupSelectedShip()
    {
        GameObject ship = Selection.activeGameObject;
        if (ship == null)
        {
            Debug.LogError("[CoOp] Select the ship GameObject first, then BattleSky/Setup Local Co-op Ship.");
            return;
        }

        InputActionAsset asset = FindInputAsset();
        if (asset == null)
        {
            Debug.LogError("[CoOp] Could not find ShipInputActions asset. Assign Pilot/Gunner handlers manually.");
            return;
        }

        PilotInputHandler pilot = ship.GetComponent<PilotInputHandler>() ?? ship.AddComponent<PilotInputHandler>();
        GunnerInputHandler gunner = ship.GetComponent<GunnerInputHandler>() ?? ship.AddComponent<GunnerInputHandler>();
        ShipTurret turret = ship.GetComponent<ShipTurret>() ?? ship.AddComponent<ShipTurret>();
        CoopCrewManager crew = ship.GetComponent<CoopCrewManager>() ?? ship.AddComponent<CoopCrewManager>();

        SetAsset(pilot, asset);
        SetAsset(gunner, asset);

        // Remove legacy solo handler so roles are unambiguous (kept working if
        // the user prefers solo - this tool is explicitly the co-op path).
        ShipInputHandler legacy = ship.GetComponent<ShipInputHandler>();
        if (legacy != null)
        {
            Debug.Log("[CoOp] Removing legacy ShipInputHandler (solo) - Pilot/Gunner now own the inputs. Undo restores it.", legacy);
            Object.DestroyImmediate(legacy);
        }

        if (ship.GetComponent<ShipFlightModel>() == null)
            Debug.LogWarning("[CoOp] Ship has no ShipFlightModel - flight will not work. Add one to " + ship.name, ship);
        if (ship.GetComponent<ShipWeaponSystem>() == null)
            Debug.LogWarning("[CoOp] Ship has no ShipWeaponSystem - guns will not work. Add one to " + ship.name, ship);
        if (ship.GetComponent<ShieldSystem>() == null)
            Debug.LogWarning("[CoOp] Ship has no ShieldSystem - shields will not work. Add one to " + ship.name, ship);

        Debug.Log("[CoOp] Wired " + ship.name + ": P1 Pilot flies, P2 Gunner shoots+shields. Pilot map + Gunner map enabled.", ship);
    }

    private static InputActionAsset FindInputAsset()
    {
        string[] guids = AssetDatabase.FindAssets("ShipInputActions t:InputActionAsset");
        foreach (string g in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(g);
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(path);
            if (asset != null) return asset;
        }
        return null;
    }

    private static void SetAsset(Component c, InputActionAsset asset)
    {
        var so = new SerializedObject(c);
        SerializedProperty prop = so.FindProperty("_inputAsset");
        if (prop != null)
        {
            prop.objectReferenceValue = asset;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
