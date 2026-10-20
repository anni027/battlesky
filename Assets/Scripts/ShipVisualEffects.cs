using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Runtime-built visual effects for the crew ship: engine glow spheres with
/// point lights, engine exhaust particle plumes, camera speed lines and a
/// procedurally generated engine hum.
///
/// Everything is created in code, so the ship needs no new prefabs and no new
/// art assets. The component is a plain MonoBehaviour (not a NetworkBehaviour)
/// and only reads state from NetworkPlayer, so it is safe to use on a ship
/// that has no NetworkPlayer at all (editor / test scene).
///
/// The ship flies with its nose along +Z and the chase camera sits at the
/// transform origin looking down +Z, so the exhaust sits at the -Z end of the
/// hull and the speed lines stream backwards past the camera.
/// </summary>
[DisallowMultipleComponent]
public class ShipVisualEffects : MonoBehaviour
{
    [Header("Engine Glow")]
    [SerializeField] private float _glowMinScale = 0.55f;
    [SerializeField] private float _glowMaxScale = 1.7f;
    [SerializeField] private float _glowBoostScale = 2.3f;
    [SerializeField] private float _lightMinIntensity = 2.5f;
    [SerializeField] private float _lightMaxIntensity = 15f;
    [SerializeField] private float _lightBoostMultiplier = 2.2f;
    [SerializeField] private float _lightRange = 90f;
    [SerializeField] private Color _glowColor = new Color(0.25f, 1.6f, 3.4f, 1f);
    [SerializeField] private Color _boostGlowColor = new Color(1.6f, 3.6f, 4.2f, 1f);
    [Tooltip("Thrust/engine mount transforms; when empty the component falls back to the two computed rear positions")]
    [SerializeField] private Transform[] _thrustPoints = new Transform[0];

    [Header("Speed Lines")]
    [SerializeField] private float _speedLineStartSpeed = 60f;
    [SerializeField] private float _speedLineFullSpeed = 140f;
    [SerializeField] private float _speedLineMaxRate = 10f;
    [SerializeField] private float _speedLineBaseVelocity = 25f;
    [SerializeField] private float _speedLineVelocityPerSpeed = 0.95f;
    [SerializeField] private int _speedLineMaxParticles = 18;

    [Header("Engine Audio")]
    [SerializeField] private float _engineVolume = 0.12f;
    [SerializeField] private float _engineMinPitch = 0.85f;
    [SerializeField] private float _engineMaxPitch = 1.35f;

    [Header("Reference")]
    [Tooltip("Speed that counts as 100% for glow, light and pitch scaling (m/s)")]
    [SerializeField] private float _referenceSpeed = 140f;

    // Exhaust plume tuning. These are plain constants instead of serialized
    // fields so the prefab does not have to grow new entries for them.
    private const float ExhaustIdleSpeed = 10f;
    private const float ExhaustCruiseSpeed = 40f;
    private const float ExhaustBoostSpeed = 60f;
    private const float ExhaustMaxEmissionRate = 120f;
    private const int ExhaustMaxParticles = 120;
    private static readonly Color ExhaustColor = new Color(0.2f, 1.3f, 2.9f, 1f);
    private static readonly Color ExhaustBoostColor = new Color(3.2f, 3.4f, 3.6f, 1f);

    /// <summary>Current speed of the ship in m/s.</summary>
    public float CurrentSpeed { get; private set; }

    /// <summary>0..1 estimate of how hard the main engine is pushing.</summary>
    public float Throttle { get; private set; }

    /// <summary>True while the engines are running at boost brightness.</summary>
    public bool IsBoosting { get; private set; }

    /// <summary>True when this ship belongs to the local player.</summary>
    public bool IsLocalPlayer { get; private set; }

    private NetworkPlayer _netPlayer;
    private Rigidbody _rb;

    private Transform _vfxRoot;
    private Transform[] _engineGlows = new Transform[0];
    private Light[] _engineLights = new Light[0];
    private ParticleSystem[] _engineExhausts = new ParticleSystem[0];

    private Material _glowMaterial;
    private Material _particleMaterial;
    private bool _glowUsesEmission;

    private AudioSource _audioSource;
    private AudioClip _engineClip;
    private bool _audioBuilt;

    private ParticleSystem _speedLines;
    private GameObject _speedLinesObject;
    private bool _speedLinesBuilt;

    private bool _lastBoost;
    private bool _wasLocal;
    private bool _wasProxy;
    private bool _boostLatch;
    private float _previousSpeed;
    private bool _speedInitialized;
    private float _lastSpeedLineRate = -1f;
    private float _lastSpeedLineVelocity = -1f;
    private float _lastExhaustRate = -1f;
    private float _lastExhaustSpeed = -1f;

    private void Awake()
    {
        _netPlayer = GetComponent<NetworkPlayer>();
        _rb = GetComponent<Rigidbody>();

        // A headless / batch-mode server has no use for any of this.
        if (Application.isBatchMode)
        {
            enabled = false;
            return;
        }

        BuildVisuals();
        ResolveState(true);
    }

    private void OnDestroy()
    {
        DestroyRuntimeObject(_glowMaterial);
        DestroyRuntimeObject(_particleMaterial);
        DestroyRuntimeObject(_engineClip);
    }

    private void Update()
    {
        if (_vfxRoot == null) return;

        float dt = Time.deltaTime;
        if (dt <= 0f) dt = 1f / 60f;

        ResolveState(false);

        float speed = 0f;
        if (_netPlayer != null) speed = _netPlayer.ShipVelocity.magnitude;
        else if (_rb != null) speed = _rb.linearVelocity.magnitude;

        CurrentSpeed = speed;
        float speed01 = Mathf.Clamp01(speed / Mathf.Max(1f, _referenceSpeed));

        // Throttle is estimated from the speed delta so it also works for
        // remote ships, whose boost flag is never replicated.
        if (!_speedInitialized)
        {
            // First frame: there is no previous speed to compare against, so
            // a huge fake acceleration would otherwise fake a boost.
            _speedInitialized = true;
            _previousSpeed = speed;
        }

        float accel = (speed - _previousSpeed) / dt;
        _previousSpeed = speed;
        float rawThrottle = Mathf.Clamp01(accel / 12f);
        Throttle = Mathf.Lerp(Throttle, rawThrottle, 1f - Mathf.Exp(-6f * dt));

        UpdateBoostState();
        UpdateGlow(speed01);
        UpdateExhaust(speed01);
        UpdateSpeedLines(speed, speed01);
        UpdateAudio(speed01);
    }

    /// <summary>
    /// Turns the whole effect set on or off (for cutscenes, death states or
    /// a gunner proxy that must stay invisible).
    /// </summary>
    public void SetEffectsEnabled(bool value)
    {
        if (_vfxRoot != null) _vfxRoot.gameObject.SetActive(value);

        if (_audioSource != null)
        {
            if (value && _audioBuilt && IsLocalPlayer) _audioSource.Play();
            else _audioSource.Stop();
        }
    }

    // ---------- state ----------

    private void ResolveState(bool force)
    {
        bool proxy = _netPlayer != null && _netPlayer.IsGunnerProxy;
        if (proxy != _wasProxy || force)
        {
            _wasProxy = proxy;
            if (_vfxRoot != null) _vfxRoot.gameObject.SetActive(!proxy);
        }

        // Without a NetworkPlayer (editor, test scene) the ship is treated as
        // the local one so the effects can be tested without a session.
        bool local = IsLocalNetworkPlayer();
        if (local != _wasLocal || force)
        {
            _wasLocal = local;
            IsLocalPlayer = local;

            if (local)
            {
                BuildAudio();
                BuildSpeedLines();
            }

            if (_audioSource != null)
            {
                // The gunner's own ship object is an invisible proxy, but it
                // is still the local player object, so it is the one that
                // plays the engine hum (its transform follows the crew ship).
                if (local) _audioSource.Play();
                else _audioSource.Stop();
            }

            if (_speedLinesObject != null)
                _speedLinesObject.SetActive(local && !proxy);
        }
    }

    /// <summary>
    /// True when this ship belongs to the local player. Mirror only wires the
    /// NetworkIdentity into the NetworkBehaviour during its own Awake, so the
    /// identity is checked for null instead of trusting isLocalPlayer blindly.
    /// </summary>
    private bool IsLocalNetworkPlayer()
    {
        if (_netPlayer == null) return true;

        var identity = _netPlayer.netIdentity;
        return identity != null && identity.isLocalPlayer;
    }

    private void UpdateBoostState()
    {
        bool boost;
        if (_netPlayer != null && IsLocalNetworkPlayer())
        {
            // The local pilot knows the real boost state.
            boost = _netPlayer.IsBoosting;
        }
        else
        {
            // Remote ships never replicate the boost flag, so infer it from
            // a hard acceleration (with hysteresis so it cannot flicker).
            if (Throttle > 0.85f) _boostLatch = true;
            else if (Throttle < 0.55f) _boostLatch = false;
            boost = _boostLatch;
        }

        IsBoosting = boost;
    }

    // ---------- engine glow ----------

    private void UpdateGlow(float speed01)
    {
        float scale = Mathf.Lerp(_glowMinScale, _glowMaxScale, speed01);
        if (IsBoosting) scale *= _glowBoostScale;

        float intensity = Mathf.Lerp(_lightMinIntensity, _lightMaxIntensity, speed01);
        if (IsBoosting) intensity *= _lightBoostMultiplier;

        for (int i = 0; i < _engineGlows.Length; i++)
        {
            Transform glow = _engineGlows[i];
            if (glow != null)
                glow.localScale = new Vector3(scale, scale, scale);

            Light light = _engineLights[i];
            if (light != null)
                light.intensity = intensity;
        }

        if (_glowMaterial == null) return;

        Color color = IsBoosting ? _boostGlowColor : _glowColor;
        color *= Mathf.Lerp(0.6f, 1.4f, speed01);
        color.a = 1f;

        if (_glowUsesEmission)
        {
            if (_glowMaterial.HasProperty("_EmissionColor"))
                _glowMaterial.SetColor("_EmissionColor", color);
        }
        else if (_glowMaterial.HasProperty("_BaseColor"))
        {
            _glowMaterial.SetColor("_BaseColor", color);
        }
        else if (_glowMaterial.HasProperty("_Color"))
        {
            _glowMaterial.SetColor("_Color", color);
        }
    }

    // ---------- exhaust ----------

    /// <summary>
    /// Drives the two engine exhaust particle systems: the emission rate and
    /// the stream speed follow how hard the engine is pushing and the colour
    /// switches to the hot boost colour while boosting.
    ///
    /// The modules are only re-pushed when the value actually changed, so a
    /// steady cruise does not rebuild the curves every frame.
    /// </summary>
    private void UpdateExhaust(float speed01)
    {
        // How hard the engine appears to push, 0..1. The throttle estimate
        // comes from the speed delta, so a small floor from the ship speed
        // keeps a faint plume alive while cruising at a constant speed.
        float drive = Mathf.Clamp01(Mathf.Max(Throttle, speed01 * 0.35f));

        float rate = drive * ExhaustMaxEmissionRate;
        if (IsBoosting) rate = Mathf.Max(rate, ExhaustMaxEmissionRate * 0.75f);

        if (Mathf.Abs(rate - _lastExhaustRate) > 0.5f)
        {
            _lastExhaustRate = rate;
            for (int i = 0; i < _engineExhausts.Length; i++)
            {
                ParticleSystem system = _engineExhausts[i];
                if (system == null) continue;
                ParticleSystem.EmissionModule emission = system.emission;
                emission.rateOverTime = rate;
            }
        }

        // The stream accelerates away from the nozzle: about 10 m/s when the
        // engine is idle up to about 60 m/s while boosting.
        float speed = Mathf.Lerp(ExhaustIdleSpeed, ExhaustCruiseSpeed, drive);
        if (IsBoosting) speed = ExhaustBoostSpeed;

        if (Mathf.Abs(speed - _lastExhaustSpeed) > 0.5f)
        {
            _lastExhaustSpeed = speed;
            for (int i = 0; i < _engineExhausts.Length; i++)
            {
                ParticleSystem system = _engineExhausts[i];
                if (system == null) continue;
                ParticleSystem.VelocityOverLifetimeModule velocity = system.velocityOverLifetime;
                // Negative Z: the plume travels away from the nose, along the
                // tail of the ship. Two constants (both the same) keep z in
                // the same curve mode as the random x/y spread - mixing a
                // constant with the random axes makes Unity reject the module.
                velocity.z = new ParticleSystem.MinMaxCurve(-speed, -speed);
            }
        }

        if (IsBoosting == _lastBoost) return;
        _lastBoost = IsBoosting;

        Color color = IsBoosting ? ExhaustBoostColor : ExhaustColor;
        for (int i = 0; i < _engineExhausts.Length; i++)
        {
            ParticleSystem system = _engineExhausts[i];
            if (system == null) continue;
            ParticleSystem.ColorOverLifetimeModule colorOverLife = system.colorOverLifetime;
            colorOverLife.enabled = true;
            colorOverLife.color = new ParticleSystem.MinMaxGradient(CreateExhaustGradient(color));
        }
    }

    /// <summary>
    /// Gradient of the exhaust plume: the given colour at the nozzle, fading
    /// out over the particle lifetime so the stream dissipates instead of
    /// popping out of existence.
    /// </summary>
    private static Gradient CreateExhaustGradient(Color color)
    {
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(color, 0f), new GradientColorKey(color * 0.5f, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.1f), new GradientAlphaKey(0f, 1f) });
        return gradient;
    }

    // ---------- speed lines ----------

    private void UpdateSpeedLines(float speed, float speed01)
    {
        if (_speedLines == null) return;

        float amount = Mathf.InverseLerp(_speedLineStartSpeed, _speedLineFullSpeed, speed);
        float rate = amount * _speedLineMaxRate;
        float backwards = _speedLineBaseVelocity + _speedLineVelocityPerSpeed * speed01 * _referenceSpeed;

        // Only push new curves to the particle system when they actually
        // changed, so a steady cruise does not rebuild them every frame.
        if (Mathf.Abs(rate - _lastSpeedLineRate) > 0.5f)
        {
            _lastSpeedLineRate = rate;
            ParticleSystem.EmissionModule emission = _speedLines.emission;
            emission.rateOverTime = rate;
        }

        if (Mathf.Abs(backwards - _lastSpeedLineVelocity) > 0.5f)
        {
            _lastSpeedLineVelocity = backwards;
            ParticleSystem.VelocityOverLifetimeModule velocity = _speedLines.velocityOverLifetime;
            velocity.z = new ParticleSystem.MinMaxCurve(-backwards);
        }
    }

    // ---------- engine audio ----------

    private void UpdateAudio(float speed01)
    {
        if (_audioSource == null) return;

        _audioSource.pitch = Mathf.Lerp(_engineMinPitch, _engineMaxPitch, speed01);

        float thrust = Mathf.Clamp01(Throttle + (IsBoosting ? 0.75f : 0f));
        _audioSource.volume = _engineVolume * Mathf.Lerp(0.45f, 1f, thrust);
    }

    // ---------- construction ----------

    private void BuildVisuals()
    {
        _glowMaterial = CreateGlowMaterial(out _glowUsesEmission);
        _particleMaterial = CreateUnlitMaterial("ShipSpeedLines", new Color(0.7f, 0.95f, 1f, 1f));

        GameObject root = new GameObject("ShipVfx");
        root.transform.SetParent(transform, false);
        _vfxRoot = root.transform;

        // Authored thrust mounts win: each one gets its own glow, light and
        // plume, so a four-engine hull really shows four plumes.
        int count = CountThrustPoints();
        if (count > 0)
        {
            _engineGlows = new Transform[count];
            _engineLights = new Light[count];
            _engineExhausts = new ParticleSystem[count];

            int index = 0;
            for (int i = 0; i < _thrustPoints.Length; i++)
            {
                Transform point = _thrustPoints[i];
                if (point == null) continue;

                // The engine node is parented to the VFX root, so the mount has
                // to be expressed in that space. The plume then leaves along the
                // mount's own -Z, which is right for a rotated exhaust nozzle.
                Vector3 localPos = _vfxRoot.InverseTransformPoint(point.position);
                Quaternion localRot = Quaternion.Inverse(_vfxRoot.rotation) * point.rotation;
                BuildEngine(index, "Thrust" + index, localPos, localRot);
                index++;
            }

            return;
        }

        // No authored mounts: fall back to the two computed rear positions.
        _engineGlows = new Transform[2];
        _engineLights = new Light[2];
        _engineExhausts = new ParticleSystem[2];

        Vector3 left;
        Vector3 right;
        ComputeEnginePositions(out left, out right);

        for (int i = 0; i < 2; i++)
        {
            Vector3 position = i == 0 ? left : right;
            BuildEngine(i, i == 0 ? "EngineLeft" : "EngineRight", position, Quaternion.identity);
        }
    }

    /// <summary>
    /// Number of valid authored thrust mounts. Empty inspector slots are
    /// ignored so a half-filled array cannot create a null engine.
    /// </summary>
    private int CountThrustPoints()
    {
        if (_thrustPoints == null) return 0;

        int count = 0;
        for (int i = 0; i < _thrustPoints.Length; i++)
        {
            if (_thrustPoints[i] != null) count++;
        }
        return count;
    }

    private void BuildEngine(int index, string name, Vector3 localPosition, Quaternion localRotation)
    {
        GameObject engine = new GameObject(name);
        engine.transform.SetParent(_vfxRoot, false);
        engine.transform.localPosition = localPosition;
        engine.transform.localRotation = localRotation;

        // Emissive glow ball.
        GameObject glow = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        glow.name = "EngineGlow";
        glow.transform.SetParent(engine.transform, false);
        glow.transform.localPosition = Vector3.zero;
        glow.transform.localScale = Vector3.one * _glowMinScale;

        Collider sphereCollider = glow.GetComponent<Collider>();
        if (sphereCollider != null)
        {
            // Never let the glow ball take part in physics.
            sphereCollider.enabled = false;
            Destroy(sphereCollider);
        }

        MeshRenderer meshRenderer = glow.GetComponent<MeshRenderer>();
        if (meshRenderer != null)
        {
            if (_glowMaterial != null) meshRenderer.sharedMaterial = _glowMaterial;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.lightProbeUsage = LightProbeUsage.Off;
            meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        Light light = glow.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(0.35f, 0.75f, 1f);
        light.range = _lightRange;
        light.intensity = _lightMinIntensity;
        light.shadows = LightShadows.None;

        _engineGlows[index] = glow.transform;
        _engineLights[index] = light;

        BuildExhaust(engine.transform, index);
    }

    /// <summary>
    /// Builds the exhaust plume for one engine. The system sits on the
    /// engine node (whose local rotation matches its thrust mount, nose along
    /// +Z), so the plume has to travel along -Z to leave the ship behind.
    ///
    /// The travel direction is taken from the velocity over lifetime module
    /// instead of the shape, because that sign is guaranteed: the shape only
    /// decides where the particles are born (a small cone at the nozzle),
    /// while the negative Z velocity always pushes them away from the nose.
    /// </summary>
    private void BuildExhaust(Transform engine, int index)
    {
        GameObject exhaust = new GameObject("Exhaust");
        exhaust.transform.SetParent(engine, false);
        exhaust.transform.localPosition = Vector3.zero;
        exhaust.transform.localRotation = Quaternion.identity;
        exhaust.transform.localScale = Vector3.one;

        ParticleSystem system = exhaust.AddComponent<ParticleSystem>();
        system.Stop();
        _engineExhausts[index] = system;

        ParticleSystem.MainModule main = system.main;
        main.loop = true;
        main.playOnAwake = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.8f);
        main.startSpeed = 0f;
        main.startSize = new ParticleSystem.MinMaxCurve(0.5f, 2f);
        main.startColor = Color.white;
        main.maxParticles = ExhaustMaxParticles;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.scalingMode = ParticleSystemScalingMode.Local;
        main.gravityModifier = 0f;

        ParticleSystem.EmissionModule emission = system.emission;
        emission.enabled = true;
        emission.rateOverTime = 0f;

        ParticleSystem.ShapeModule shape = system.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 40f;
        shape.radius = 0.15f;
        shape.radiusThickness = 0f;
        shape.position = Vector3.zero;
        // The cone points backwards, along -Z of the engine node.
        shape.rotation = new Vector3(0f, 180f, 0f);
        shape.randomDirectionAmount = 0f;
        shape.sphericalDirectionAmount = 0f;
        shape.randomPositionAmount = 0.2f;
        shape.alignToDirection = false;

        ParticleSystem.VelocityOverLifetimeModule velocity = system.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.Local;
        // Unity requires all three velocity axes to share one curve mode.
        // They are all random-between-two-constants; z just uses the same
        // value twice until the throttle drives it.
        velocity.x = new ParticleSystem.MinMaxCurve(-1.2f, 1.2f);
        velocity.y = new ParticleSystem.MinMaxCurve(-1.2f, 1.2f);
        velocity.z = new ParticleSystem.MinMaxCurve(-ExhaustIdleSpeed, -ExhaustIdleSpeed);

        ParticleSystem.ColorOverLifetimeModule colorOverLife = system.colorOverLifetime;
        colorOverLife.enabled = true;
        colorOverLife.color = new ParticleSystem.MinMaxGradient(CreateExhaustGradient(ExhaustColor));

        ParticleSystem.RotationOverLifetimeModule rotation = system.rotationOverLifetime;
        rotation.enabled = true;
        // Same rule as velocity: every rotation axis must share one curve
        // mode, so x and y are explicit zero two-constant curves rather than
        // the default constant ones.
        rotation.x = new ParticleSystem.MinMaxCurve(0f, 0f);
        rotation.y = new ParticleSystem.MinMaxCurve(0f, 0f);
        rotation.z = new ParticleSystem.MinMaxCurve(-45f, 45f);

        ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Stretch;
        renderer.alignment = ParticleSystemRenderSpace.View;
        renderer.velocityScale = 0.03f;
        renderer.lengthScale = 2f;
        // Behind the speed lines and behind any screen space HUD.
        renderer.sortingOrder = -100;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderer.maxParticleSize = 0.25f;
        if (_particleMaterial != null) renderer.material = _particleMaterial;

        system.Play();
    }

    private void BuildSpeedLines()
    {
        if (_speedLinesBuilt) return;
        _speedLinesBuilt = true;

        GameObject go = new GameObject("SpeedLines");
        go.transform.SetParent(_vfxRoot, false);
        go.transform.localPosition = new Vector3(0f, 0f, 6f);
        _speedLinesObject = go;

        ParticleSystem system = go.AddComponent<ParticleSystem>();
        system.Stop();
        _speedLines = system;

        ParticleSystem.MainModule main = system.main;
        main.loop = true;
        main.playOnAwake = false;
        // Short dashes instead of long beams: a 0.22 s lifetime with a low
        // stream speed turns every particle into a brief streak that flicks
        // past the canopy, not a laser held across the screen.
        main.startLifetime = 0.22f;
        main.startSpeed = 0f;
        main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.05f);
        main.startColor = new Color(0.7f, 0.95f, 1f, 1f);
        main.maxParticles = Mathf.Max(1, _speedLineMaxParticles);
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.scalingMode = ParticleSystemScalingMode.Local;
        main.gravityModifier = 0f;

        ParticleSystem.EmissionModule emission = system.emission;
        emission.enabled = true;
        emission.rateOverTime = 0f;

        ParticleSystem.ShapeModule shape = system.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(16f, 10f, 1f);
        shape.position = new Vector3(0f, 0f, 32f);
        shape.rotation = Vector3.zero;
        shape.randomDirectionAmount = 0f;

        ParticleSystem.VelocityOverLifetimeModule velocity = system.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.Local;
        velocity.x = 0f;
        velocity.y = 0f;
        velocity.z = 0f;

        ParticleSystem.ColorOverLifetimeModule colorOverLife = system.colorOverLifetime;
        colorOverLife.enabled = true;
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(new Color(0.7f, 0.95f, 1f), 0f), new GradientColorKey(new Color(0.95f, 1f, 1f), 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.5f, 0.15f), new GradientAlphaKey(0f, 1f) });
        colorOverLife.color = new ParticleSystem.MinMaxGradient(gradient);

        ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Stretch;
        renderer.alignment = ParticleSystemRenderSpace.View;
        renderer.velocityScale = 0.02f;
        renderer.lengthScale = 0.3f;
        renderer.sortingOrder = -100;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderer.maxParticleSize = 0.3f;
        if (_particleMaterial != null) renderer.material = _particleMaterial;

        go.SetActive(IsLocalPlayer);
        system.Play();
    }

    private void BuildAudio()
    {
        if (_audioBuilt) return;
        _audioBuilt = true;

        _engineClip = CreateEngineClip();
        if (_engineClip == null) return;

        _audioSource = gameObject.AddComponent<AudioSource>();
        _audioSource.clip = _engineClip;
        _audioSource.loop = true;
        _audioSource.playOnAwake = false;
        _audioSource.volume = _engineVolume;
        _audioSource.pitch = _engineMinPitch;
        _audioSource.spatialBlend = 1f;
        _audioSource.rolloffMode = AudioRolloffMode.Linear;
        _audioSource.minDistance = 5f;
        _audioSource.maxDistance = 150f;
        _audioSource.dopplerLevel = 0f;

        if (IsLocalPlayer) _audioSource.Play();
    }

    // ---------- geometry ----------

    private void ComputeEnginePositions(out Vector3 left, out Vector3 right)
    {
        left = Vector3.zero;
        right = Vector3.zero;

        Bounds bounds;
        bool found = TryComputeLocalBounds(out bounds);
        bool degenerate = !found
            || bounds.size.sqrMagnitude < 0.01f
            || bounds.extents.z < 0.5f;

        if (degenerate)
        {
            // Fall back to fixed local offsets behind the hull.
            left = new Vector3(-1f, 0f, -6f);
            right = new Vector3(1f, 0f, -6f);
            return;
        }

        float rearZ = bounds.min.z - 0.6f;
        float y = bounds.center.y;
        float halfWidth = Mathf.Clamp(bounds.extents.x * 0.3f, 0.6f, 8f);

        left = new Vector3(-halfWidth, y, rearZ);
        right = new Vector3(halfWidth, y, rearZ);
    }

    /// <summary>
    /// Combined bounds of every renderer under the ship, expressed in this
    /// transform's local space. Mesh bounds are transformed exactly, so the
    /// result does not change while the ship rolls and pitches.
    /// </summary>
    private bool TryComputeLocalBounds(out Bounds localBounds)
    {
        localBounds = new Bounds(Vector3.zero, Vector3.zero);
        bool any = false;

        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        if (renderers == null || renderers.Length == 0) return false;

        Matrix4x4 toLocal = transform.worldToLocalMatrix;

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null) continue;

            MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = meshFilter != null ? meshFilter.sharedMesh : null;

            if (mesh != null)
            {
                Matrix4x4 matrix = toLocal * meshFilter.transform.localToWorldMatrix;
                EncapsulateCorners(ref localBounds, mesh.bounds, matrix, ref any);
            }
            else
            {
                EncapsulateCorners(ref localBounds, renderer.bounds, toLocal, ref any);
            }
        }

        return any;
    }

    private static void EncapsulateCorners(ref Bounds target, Bounds box, Matrix4x4 matrix, ref bool any)
    {
        Vector3 min = box.min;
        Vector3 max = box.max;

        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 point = new Vector3(
                (corner & 1) == 0 ? min.x : max.x,
                (corner & 2) == 0 ? min.y : max.y,
                (corner & 4) == 0 ? min.z : max.z);

            Vector3 local = matrix.MultiplyPoint3x4(point);

            if (!any)
            {
                target = new Bounds(local, Vector3.zero);
                any = true;
            }
            else
            {
                target.Encapsulate(local);
            }
        }
    }

    // ---------- materials ----------

    private static Material CreateGlowMaterial(out bool usesEmission)
    {
        usesEmission = false;

        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit != null)
        {
            Material material = new Material(lit) { name = "ShipEngineGlow" };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", new Color(0.03f, 0.08f, 0.12f, 1f));
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.75f);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            if (material.HasProperty("_EmissionColor"))
            {
                material.SetColor("_EmissionColor", Color.black);
                material.EnableKeyword("_EMISSION");
                usesEmission = true;
            }
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            return material;
        }

        Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
        if (unlit != null)
        {
            Material material = new Material(unlit) { name = "ShipEngineGlow" };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.black);
            else if (material.HasProperty("_Color")) material.SetColor("_Color", Color.black);
            return material;
        }

        return CreateUnlitMaterial("ShipEngineGlow", Color.black);
    }

    private static Material CreateUnlitMaterial(string name, Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        bool urpParticles = shader != null;
        if (!urpParticles) shader = Shader.Find("Sprites/Default");
        if (shader == null) return null;

        Material material = new Material(shader) { name = name };

        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        else if (material.HasProperty("_Color")) material.SetColor("_Color", color);

        material.renderQueue = (int)RenderQueue.Transparent;

        if (urpParticles)
        {
            // Transparent, premultiplied, no depth write: the glow blends over
            // the hull instead of cutting a hole into it.
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 1f);
            material.SetFloat("_SrcBlend", (float)BlendMode.One);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_Cull", (float)CullMode.Off);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.SetShaderPassEnabled("DepthOnly", false);
            material.SetShaderPassEnabled("DepthNormalsOnly", false);
        }

        return material;
    }

    // ---------- audio ----------

    /// <summary>
    /// Builds a one second looping engine hum out of layered sines, a saw and
    /// a band of pseudo noise. Every partial uses a whole number of hertz, so
    /// the waveform is perfectly periodic and the loop has no click.
    /// </summary>
    private static AudioClip CreateEngineClip()
    {
        const int sampleRate = 44100;
        const int sampleCount = sampleRate;

        float[] data = new float[sampleCount];
        uint seed = 0x9E3779B9u;

        float phaseA = NextPhase(ref seed);
        float phaseB = NextPhase(ref seed);
        float phaseC = NextPhase(ref seed);
        float phaseD = NextPhase(ref seed);
        float phaseE = NextPhase(ref seed);
        float phaseF = NextPhase(ref seed);
        float phaseG = NextPhase(ref seed);
        float phaseH = NextPhase(ref seed);

        const float twoPi = Mathf.PI * 2f;

        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)sampleRate;

            float body = 0.55f * Mathf.Sin(twoPi * 55f * t + phaseA);
            body += 0.30f * Mathf.Sin(twoPi * 110f * t + phaseB);
            body += 0.16f * Mathf.Sin(twoPi * 220f * t + phaseC);

            float sawPhase = 55f * t;
            body += 0.12f * (2f * (sawPhase - Mathf.Floor(sawPhase + 0.5f)));

            float noise = 0.10f * Mathf.Sin(twoPi * 311f * t + phaseD);
            noise += 0.08f * Mathf.Sin(twoPi * 517f * t + phaseE);
            noise += 0.06f * Mathf.Sin(twoPi * 733f * t + phaseF);
            noise += 0.05f * Mathf.Sin(twoPi * 1103f * t + phaseG);

            float lfo = 0.72f + 0.28f * Mathf.Sin(twoPi * 7f * t + phaseH);

            data[i] = (body * lfo + noise) * 0.5f;
        }

        float peak = 0f;
        for (int i = 0; i < sampleCount; i++)
        {
            float magnitude = Mathf.Abs(data[i]);
            if (magnitude > peak) peak = magnitude;
        }
        if (peak > 0.0001f)
        {
            float gain = 0.9f / peak;
            for (int i = 0; i < sampleCount; i++) data[i] *= gain;
        }

        AudioClip clip = AudioClip.Create("ShipEngineHum", sampleCount, 1, sampleRate, false);
        if (clip == null) return null;
        clip.SetData(data, 0);
        return clip;
    }

    private static float NextPhase(ref uint seed)
    {
        seed = seed * 1664525u + 1013904223u;
        return (seed >> 8) * (1f / 16777216f);
    }

    private static void DestroyRuntimeObject(Object target)
    {
        if (target == null) return;
        if (Application.isPlaying) Destroy(target);
        else DestroyImmediate(target);
    }
}
