using UnityEngine;
using UnityEditor;
using UnityEngine.InputSystem;
using System.IO;

public class InputActionsGenerator : Editor
{
    [MenuItem("BattleSky/Generate Input Actions")]
    public static void GenerateInputActions()
    {
        string inputActionsPath = "Assets/Scripts/ShipInputActions.inputactions";
        string outputPath = "Assets/Scripts/ShipInputActions.cs";

        if (!File.Exists(inputActionsPath))
        {
            Debug.LogError("Input actions file not found at: " + inputActionsPath);
            return;
        }

        var inputActionsAsset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(inputActionsPath);
        if (inputActionsAsset == null)
        {
            Debug.LogError("Failed to load InputActionAsset. Make sure the .inputactions file is properly imported.");
            return;
        }

        string json = inputActionsAsset.ToJson();
        string classContent = GenerateWrapperClass(json);
        File.WriteAllText(outputPath, classContent);
        AssetDatabase.Refresh();

        Debug.Log("Generated ShipInputActions.cs at: " + outputPath);
    }

    private static string GenerateWrapperClass(string json)
    {
        return $@"using UnityEngine;
using UnityEngine.InputSystem;

public class ShipInputActions : MonoBehaviour
{{
    private InputActionAsset _asset;
    private InputActionMap _shipMap;

    public InputAction Thrust => _shipMap?.FindAction(""Thrust"");
    public InputAction Strafe => _shipMap?.FindAction(""Strafe"");
    public InputAction Look => _shipMap?.FindAction(""Look"");
    public InputAction Roll => _shipMap?.FindAction(""Roll"");
    public InputAction Fire => _shipMap?.FindAction(""Fire"");
    public InputAction Boost => _shipMap?.FindAction(""Boost"");
    public InputAction Shield => _shipMap?.FindAction(""Shield"");
    public InputAction FreeLook => _shipMap?.FindAction(""FreeLook"");
    public InputAction FlightAssist => _shipMap?.FindAction(""FlightAssist"");

    public void Initialize(InputActionAsset asset)
    {{
        _asset = asset;
        _shipMap = _asset?.FindActionMap(""Ship"");
    }}

    public void Enable() {{ _shipMap?.Enable(); }}
    public void Disable() {{ _shipMap?.Disable(); }}
}}";
    }
}