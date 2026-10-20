using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;
using Mirror;

/// <summary>
/// Top-right minimap of the local crew ship (blue, dead centre), asteroids
/// (small grey dots), enemy fighters (red), capital ships (larger orange dots)
/// and the Death Star (large bright-red blip wrapped in a target ring).
/// World offsets on the XZ plane are projected onto the panel. Capital ships,
/// the boss and the level 1 objective gate clamp to the map edge when they
/// are out of range and get a small outward tick.
/// The panel also carries a compass ring (N/E/S/W ticks), a heading chevron
/// that orbits the ring with the crew ship's heading and a range/objective
/// readout, so it reads like a nav display instead of a dot cloud.
/// Requires a Canvas ancestor in the scene.
/// </summary>
[RequireComponent(typeof(Canvas))]
public class Minimap : MonoBehaviour
{
    [Header("Layout")]
    [SerializeField] private Vector2 _panelSize = new Vector2(220f, 220f);
    [SerializeField] private Vector2 _panelOffset = new Vector2(-20f, -20f);
    [SerializeField] private float _mapRadius = 1000f;
    [SerializeField] private float _borderSize = 4f;
    [Tooltip("Seconds between blip refreshes; the panel itself follows the ship every frame")]
    [SerializeField] private float _updateInterval = 0.1f;

    [Header("Colors")]
    [SerializeField] private Color _panelColor = new Color(0f, 0f, 0f, 0.45f);
    [SerializeField] private Color _borderColor = new Color(0.5f, 0.5f, 0.5f, 0.8f);
    [SerializeField] private Color _playerColor = new Color(0.3f, 0.8f, 1f, 1f);
    [SerializeField] private Color _enemyColor = new Color(1f, 0.3f, 0.3f, 1f);
    [SerializeField] private Color _asteroidColor = new Color(0.6f, 0.6f, 0.6f, 0.7f);
    [SerializeField] private Color _capitalShipColor = new Color(1f, 0.6f, 0f, 1f);
    [SerializeField] private Color _bossColor = new Color(1f, 0.15f, 0.1f, 1f);
    [Tooltip("Objective / checkpoint marker colour")]
    [SerializeField] private Color _objectiveColor = new Color(0.2f, 1f, 1f, 1f);
    [Tooltip("Compass ring and cardinal labels")]
    [SerializeField] private Color _compassColor = new Color(0.75f, 0.85f, 1f, 0.7f);
    [Tooltip("Heading chevron")]
    [SerializeField] private Color _headingColor = new Color(1f, 0.95f, 0.6f, 1f);
    [Tooltip("Range / objective readout text")]
    [SerializeField] private Color _readoutColor = new Color(0.8f, 0.92f, 1f, 0.95f);

    [Header("Dot sizes")]
    [SerializeField] private float _playerDotSize = 8f;
    [SerializeField] private float _enemyDotSize = 5f;
    [SerializeField] private float _asteroidDotSize = 3f;
    [SerializeField] private float _capitalShipDotSize = 8f;
    [SerializeField] private float _bossDotSize = 16f;
    [Tooltip("Length of the outward tick drawn on blips clamped to the map edge")]
    [SerializeField] private float _edgeTickLength = 10f;
    [Tooltip("Size of the diamond that marks the level 1 objective")]
    [SerializeField] private float _objectiveDotSize = 10f;
    [Tooltip("Diameter of the ring drawn around the level 1 objective marker")]
    [SerializeField] private float _objectiveRingSize = 24f;
    [Tooltip("Diameter of the ring drawn around the Death Star blip")]
    [SerializeField] private float _bossRingSize = 22f;

    [Header("Text")]
    [SerializeField] private int _readoutFontSize = 11;
    [SerializeField] private int _compassFontSize = 9;

    /// <summary>One pooled map marker: a dot plus an optional edge tick and ring.</summary>
    private sealed class Blip
    {
        public RectTransform Rect;
        public RectTransform Tick;
        public RectTransform Ring;
        /// <summary>Full diameter of the ring, kept so it can be shrunk when clamped.</summary>
        public float RingSize;
        /// <summary>Hide the ring while the blip is clamped to the map edge.</summary>
        public bool HideRingWhenClamped;
    }

    private RectTransform _root;
    private Texture2D _dotTexture;
    private Sprite _dotSprite;
    private Texture2D _ringTexture;
    private Sprite _ringSprite;
    private Font _font;
    private Canvas _canvas;
    private float _mapPixelRadius;
    private float _refreshTimer;

    private readonly List<Blip> _enemyBlips = new();
    private readonly List<Blip> _asteroidBlips = new();
    private readonly List<Blip> _capitalBlips = new();
    private readonly List<Blip> _bossBlips = new();
    private readonly List<Blip> _objectiveBlips = new();

    private RectTransform _overlay;
    private RectTransform _headingChevron;
    private Text _readoutText;
    private string _cachedReadout = string.Empty;
    private string _rangeLine = string.Empty;
    private string _gateLine = string.Empty;
    private int _cachedGateMetres = -1;

    private void Awake()
    {
        _canvas = GetComponent<Canvas>();
        if (_canvas == null) _canvas = gameObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 100;

        var rt = transform as RectTransform;
        if (rt == null)
        {
            Debug.LogError("[Minimap] Must be placed on a RectTransform.");
            return;
        }

        BuildUI();
    }

    private void OnDestroy()
    {
        if (_dotSprite != null) Destroy(_dotSprite);
        if (_dotTexture != null) Destroy(_dotTexture);
        if (_ringSprite != null) Destroy(_ringSprite);
        if (_ringTexture != null) Destroy(_ringTexture);
    }

    private void BuildUI()
    {
        _dotTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
        {
            name = "MinimapDot",
            hideFlags = HideFlags.HideAndDontSave
        };
        _dotTexture.SetPixel(0, 0, Color.white);
        _dotTexture.Apply();
        _dotSprite = Sprite.Create(_dotTexture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 100f);
        _dotSprite.name = "MinimapDot";
        _dotSprite.hideFlags = HideFlags.HideAndDontSave;

        CreateRingSprite();

        _font = LoadFont();

        var rootGo = new GameObject("MinimapRoot", typeof(RectTransform));
        _root = rootGo.GetComponent<RectTransform>();
        _root.SetParent(transform, false);
        _root.anchorMin = Vector2.one;
        _root.anchorMax = Vector2.one;
        _root.pivot = Vector2.one;
        _root.anchoredPosition = _panelOffset;
        _root.sizeDelta = _panelSize;

        var panel = rootGo.AddComponent<Image>();
        panel.sprite = _dotSprite;
        panel.color = _panelColor;
        panel.raycastTarget = false;

        // Distance from the map centre to the drawable edge, in UI pixels.
        // Blips are projected inside this radius so nothing spills over the border.
        _mapPixelRadius = Mathf.Min(_panelSize.x, _panelSize.y) * 0.5f - _borderSize - 10f;

        // Four thin strips make a hollow frame. A single stretched Image would
        // paint over the whole panel and hide the blips underneath it.
        CreateEdge(_root, "BorderTop", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, _borderSize));
        CreateEdge(_root, "BorderBottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, _borderSize));
        CreateEdge(_root, "BorderLeft", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(_borderSize, 0f));
        CreateEdge(_root, "BorderRight", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(_borderSize, 0f));

        // Player blip: always dead centre (the map is north-up, no rotation).
        var haloGo = new GameObject("PlayerHalo", typeof(RectTransform));
        var haloRect = haloGo.GetComponent<RectTransform>();
        haloRect.SetParent(_root, false);
        haloRect.anchorMin = Vector2.one * 0.5f;
        haloRect.anchorMax = Vector2.one * 0.5f;
        haloRect.pivot = Vector2.one * 0.5f;
        haloRect.sizeDelta = Vector2.one * (_playerDotSize + 4f);
        var haloImg = haloGo.AddComponent<Image>();
        haloImg.sprite = _dotSprite;
        haloImg.color = new Color(1f, 1f, 1f, 0.5f);
        haloImg.raycastTarget = false;

        var playerGo = new GameObject("PlayerDot", typeof(RectTransform));
        var playerRect = playerGo.GetComponent<RectTransform>();
        playerRect.SetParent(_root, false);
        playerRect.anchorMin = Vector2.one * 0.5f;
        playerRect.anchorMax = Vector2.one * 0.5f;
        playerRect.pivot = Vector2.one * 0.5f;
        playerRect.sizeDelta = Vector2.one * _playerDotSize;
        var playerImg = playerGo.AddComponent<Image>();
        playerImg.sprite = _dotSprite;
        playerImg.color = _playerColor;
        playerImg.raycastTarget = false;

        BuildOverlay();
    }

    /// <summary>
    /// Builds the ring texture used for the boss and objective target rings.
    /// It is a soft annulus on a transparent background, so a plain Image can
    /// draw a circle around any blip without a prefab.
    /// </summary>
    private void CreateRingSprite()
    {
        const int size = 64;
        _ringTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "MinimapRing",
            hideFlags = HideFlags.HideAndDontSave
        };

        float half = size * 0.5f;
        float outer = half - 1f;
        float inner = outer - 6f;
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - half;
                float dy = y + 0.5f - half;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                // Signed distance to the band, softened over one pixel.
                float alpha = Mathf.Clamp01(Mathf.Min(outer - d, d - inner) + 0.5f);
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }
        _ringTexture.SetPixels(pixels);
        _ringTexture.Apply();
        _ringSprite = Sprite.Create(_ringTexture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        _ringSprite.name = "MinimapRing";
        _ringSprite.hideFlags = HideFlags.HideAndDontSave;
    }

    /// <summary>
    /// Compass ring, heading chevron and the range readout. They live in an
    /// overlay that is kept on top of the blips.
    /// </summary>
    private void BuildOverlay()
    {
        var overlayGo = new GameObject("MinimapOverlay", typeof(RectTransform));
        _overlay = overlayGo.GetComponent<RectTransform>();
        _overlay.SetParent(_root, false);
        _overlay.anchorMin = Vector2.zero;
        _overlay.anchorMax = Vector2.one;
        _overlay.pivot = Vector2.one * 0.5f;
        _overlay.offsetMin = Vector2.zero;
        _overlay.offsetMax = Vector2.zero;

        BuildCompassRing(_overlay, _mapPixelRadius);

        _headingChevron = CreateChevron(_overlay, _headingColor);

        var readoutGo = new GameObject("MinimapReadout", typeof(RectTransform));
        var readoutRect = readoutGo.GetComponent<RectTransform>();
        readoutRect.SetParent(_overlay, false);
        readoutRect.anchorMin = Vector2.zero;
        readoutRect.anchorMax = Vector2.zero;
        readoutRect.pivot = Vector2.zero;
        readoutRect.anchoredPosition = new Vector2(8f, 6f);
        readoutRect.sizeDelta = new Vector2(150f, 32f);
        _readoutText = readoutGo.AddComponent<Text>();
        _readoutText.font = _font;
        _readoutText.fontSize = _readoutFontSize;
        _readoutText.alignment = TextAnchor.LowerLeft;
        _readoutText.color = _readoutColor;
        _readoutText.raycastTarget = false;
        _readoutText.horizontalOverflow = HorizontalWrapMode.Overflow;
        _readoutText.verticalOverflow = VerticalWrapMode.Overflow;
        _rangeLine = "RANGE " + FormatMapRadius();
        _readoutText.text = _rangeLine;
        _cachedReadout = _rangeLine;
        var readoutShadow = readoutGo.AddComponent<Shadow>();
        readoutShadow.effectColor = new Color(0f, 0f, 0f, 0.7f);
        readoutShadow.effectDistance = new Vector2(1f, -1f);
    }

    /// <summary>
    /// Cardinal ticks and N/E/S/W labels around the map ring. The map is
    /// north-up, so north is straight up on the panel.
    /// </summary>
    private void BuildCompassRing(RectTransform parent, float radius)
    {
        string[] labels = { "N", "E", "S", "W" };
        for (int i = 0; i < 4; i++)
        {
            float angle = i * 90f;
            float rad = angle * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));

            var tickGo = new GameObject("CompassTick", typeof(RectTransform));
            var tickRect = tickGo.GetComponent<RectTransform>();
            tickRect.SetParent(parent, false);
            tickRect.anchorMin = Vector2.one * 0.5f;
            tickRect.anchorMax = Vector2.one * 0.5f;
            tickRect.pivot = Vector2.one * 0.5f;
            tickRect.sizeDelta = new Vector2(2f, 6f);
            tickRect.anchoredPosition = dir * (radius + 1f);
            tickRect.localEulerAngles = new Vector3(0f, 0f, -angle);
            var tickImg = tickGo.AddComponent<Image>();
            tickImg.sprite = _dotSprite;
            tickImg.color = _compassColor;
            tickImg.raycastTarget = false;

            var labelGo = new GameObject("CompassLabel", typeof(RectTransform));
            var labelRect = labelGo.GetComponent<RectTransform>();
            labelRect.SetParent(parent, false);
            labelRect.anchorMin = Vector2.one * 0.5f;
            labelRect.anchorMax = Vector2.one * 0.5f;
            labelRect.pivot = Vector2.one * 0.5f;
            labelRect.sizeDelta = new Vector2(16f, 12f);
            labelRect.anchoredPosition = dir * (radius - 16f);
            var labelText = labelGo.AddComponent<Text>();
            labelText.font = _font;
            labelText.fontSize = _compassFontSize;
            labelText.alignment = TextAnchor.MiddleCenter;
            labelText.color = _compassColor;
            labelText.raycastTarget = false;
            labelText.horizontalOverflow = HorizontalWrapMode.Overflow;
            labelText.verticalOverflow = VerticalWrapMode.Overflow;
            labelText.text = labels[i];
        }
    }

    /// <summary>
    /// A chevron (two bars meeting at an apex) that points along local +Y.
    /// It is rotated to show the crew ship's heading.
    /// </summary>
    private RectTransform CreateChevron(RectTransform parent, Color color)
    {
        var go = new GameObject("HeadingChevron", typeof(RectTransform));
        var rect = go.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = Vector2.one * 0.5f;
        rect.anchorMax = Vector2.one * 0.5f;
        rect.pivot = Vector2.one * 0.5f;
        rect.sizeDelta = new Vector2(14f, 14f);

        const float armLength = 9f;
        const float armAngle = 38f;
        const float apexHeight = 5f;

        float rad = armAngle * Mathf.Deg2Rad;
        Vector2 axis = new Vector2(-Mathf.Sin(rad), Mathf.Cos(rad));
        Vector2 leftCentre = new Vector2(0f, apexHeight) - axis * (armLength * 0.5f);
        CreateChevronArm(rect, color, leftCentre, armAngle, armLength);
        CreateChevronArm(rect, color, new Vector2(-leftCentre.x, leftCentre.y), -armAngle, armLength);
        return rect;
    }

    private void CreateChevronArm(RectTransform parent, Color color, Vector2 centre, float angle, float armLength)
    {
        var go = new GameObject("ChevronArm", typeof(RectTransform));
        var rect = go.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = Vector2.one * 0.5f;
        rect.anchorMax = Vector2.one * 0.5f;
        rect.pivot = Vector2.one * 0.5f;
        rect.sizeDelta = new Vector2(2f, armLength);
        rect.anchoredPosition = centre;
        rect.localEulerAngles = new Vector3(0f, 0f, angle);
        var img = go.AddComponent<Image>();
        img.sprite = _dotSprite;
        img.color = color;
        img.raycastTarget = false;
    }

    private void CreateEdge(RectTransform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rect = go.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = pivot;
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = size;
        var img = go.AddComponent<Image>();
        img.sprite = _dotSprite;
        img.color = _borderColor;
        img.raycastTarget = false;
    }

    private void Update()
    {
        if (_root == null) return;

        Transform center = ResolveMapCenter();
        if (center == null)
        {
            _root.gameObject.SetActive(false);
            return;
        }
        _root.gameObject.SetActive(true);

        // The panel is fixed to the HUD corner and the player blip is anchored
        // at its centre, so only the blips need a periodic world-space refresh.
        UpdateHeading(center);

        _refreshTimer -= Time.deltaTime;
        if (_refreshTimer > 0f) return;
        _refreshTimer = Mathf.Max(0.01f, _updateInterval);

        Vector3 centerPos = center.position;
        UpdateBlips<BattleSky.Network.EnemyShip>(_enemyBlips, centerPos, _enemyColor, _enemyDotSize, false);
        UpdateBlips<BattleSky.Network.CapitalShip>(_capitalBlips, centerPos, _capitalShipColor, _capitalShipDotSize, true);
        UpdateBlips<BattleSky.Network.NetworkAsteroid>(_asteroidBlips, centerPos, _asteroidColor, _asteroidDotSize, false);
        UpdateBlips<BattleSky.Network.DeathStar>(_bossBlips, centerPos, _bossColor, _bossDotSize, true, true, _bossRingSize);
        UpdateObjectiveBlip(centerPos);
        UpdateReadout(centerPos);

        // Blips are created after the overlay, so push the compass back on top.
        if (_overlay != null) _overlay.SetAsLastSibling();
    }

    /// <summary>
    /// Pools one dot per live entity of <typeparamref name="T"/> and positions
    /// it from the map centre. With <paramref name="clampToEdge"/> the dot is
    /// never hidden: out-of-range entities sit on the map ring with a tick.
    /// </summary>
    private void UpdateBlips<T>(List<Blip> blips, Vector3 centerPos, Color color, float size, bool clampToEdge, bool withRing = false, float ringSize = 0f) where T : Component
    {
        var entities = Object.FindObjectsByType<T>(FindObjectsSortMode.None);
        int count = entities.Length;

        while (blips.Count > count)
        {
            int last = blips.Count - 1;
            Destroy(blips[last].Rect.gameObject);
            blips.RemoveAt(last);
        }
        while (blips.Count < count)
            blips.Add(CreateBlip(color, size, clampToEdge, withRing, ringSize));

        float worldToMap = _mapPixelRadius / Mathf.Max(1f, _mapRadius);
        float sqrRadius = _mapRadius * _mapRadius;

        for (int i = 0; i < count; i++)
        {
            Vector3 offset = entities[i].transform.position - centerPos;
            Vector2 mapPos = new Vector2(offset.x, offset.z);
            PositionBlip(blips[i], mapPos, worldToMap, sqrRadius, size, clampToEdge);
        }
    }

    /// <summary>
    /// The level 1 objective gate. It is pooled exactly like the other blips:
    /// one marker is kept alive while the gate is live and released once the
    /// gate has been reached.
    /// </summary>
    private void UpdateObjectiveBlip(Vector3 centerPos)
    {
        BattleSky.Network.Level1Checkpoint checkpoint = BattleSky.Network.Level1Checkpoint.Instance;
        bool visible = checkpoint != null && !checkpoint.Reached;

        int count = visible ? 1 : 0;
        while (_objectiveBlips.Count > count)
        {
            int last = _objectiveBlips.Count - 1;
            Destroy(_objectiveBlips[last].Rect.gameObject);
            _objectiveBlips.RemoveAt(last);
        }
        while (_objectiveBlips.Count < count)
            _objectiveBlips.Add(CreateObjectiveBlip());

        if (!visible) return;

        Vector3 offset = checkpoint.transform.position - centerPos;
        Vector2 mapPos = new Vector2(offset.x, offset.z);
        float worldToMap = _mapPixelRadius / Mathf.Max(1f, _mapRadius);
        float sqrRadius = _mapRadius * _mapRadius;
        PositionBlip(_objectiveBlips[0], mapPos, worldToMap, sqrRadius, _objectiveDotSize, true);
    }

    /// <summary>
    /// Places one blip from its map-space offset. Off-map blips are hidden
    /// unless <paramref name="clampToEdge"/> is set, in which case they sit on
    /// the map ring with an outward tick pointing at them.
    /// </summary>
    private void PositionBlip(Blip blip, Vector2 mapPos, float worldToMap, float sqrRadius, float size, bool clampToEdge)
    {
        bool offMap = mapPos.sqrMagnitude > sqrRadius;
        if (offMap && !clampToEdge)
        {
            blip.Rect.gameObject.SetActive(false);
            return;
        }

        blip.Rect.gameObject.SetActive(true);
        if (!offMap)
        {
            blip.Rect.anchoredPosition = mapPos * worldToMap;
            if (blip.Tick != null)
                blip.Tick.gameObject.SetActive(false);
            if (blip.Ring != null)
            {
                blip.Ring.gameObject.SetActive(true);
                blip.Ring.anchoredPosition = Vector2.zero;
                blip.Ring.sizeDelta = Vector2.one * blip.RingSize;
            }
            return;
        }

        // Clamp onto the map ring and point a tick outward so it reads as
        // "this thing is off the map, in that direction".
        Vector2 dir = mapPos.sqrMagnitude > 0.0001f ? mapPos.normalized : Vector2.up;
        Vector2 edgePos = dir * _mapPixelRadius;
        blip.Rect.anchoredPosition = edgePos;
        if (blip.Tick != null)
        {
            blip.Tick.gameObject.SetActive(true);
            blip.Tick.sizeDelta = new Vector2(2f, _edgeTickLength);
            // The tick hangs off the dot, so the offset is relative to the dot
            // centre: it sits just inside the clamped blip and points outwards.
            blip.Tick.anchoredPosition = -dir * (size * 0.5f + _edgeTickLength * 0.5f);
            blip.Tick.localEulerAngles = new Vector3(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f);
        }
        if (blip.Ring != null)
        {
            blip.Ring.gameObject.SetActive(!blip.HideRingWhenClamped);
            if (!blip.HideRingWhenClamped)
            {
                // Pull the ring inwards so it does not spill over the map border.
                blip.Ring.anchoredPosition = -dir * (blip.RingSize - size) * 0.5f;
                blip.Ring.sizeDelta = Vector2.one * blip.RingSize;
            }
        }
    }

    private Blip CreateBlip(Color color, float size, bool withTick, bool withRing = false, float ringSize = 0f)
    {
        var go = new GameObject("Dot", typeof(RectTransform));
        var rect = go.GetComponent<RectTransform>();
        rect.SetParent(_root, false);
        rect.anchorMin = Vector2.one * 0.5f;
        rect.anchorMax = Vector2.one * 0.5f;
        rect.pivot = Vector2.one * 0.5f;
        rect.sizeDelta = Vector2.one * size;
        var img = go.AddComponent<Image>();
        img.sprite = _dotSprite;
        img.color = color;
        img.raycastTarget = false;

        var blip = new Blip { Rect = rect };
        if (withTick)
            blip.Tick = CreateTickChild(rect, color);
        if (withRing)
        {
            float diameter = ringSize > 0f ? ringSize : size * 1.5f;
            blip.RingSize = diameter;
            blip.Ring = CreateRingChild(rect, diameter, color);
        }
        return blip;
    }

    /// <summary>
    /// The objective marker: a cyan diamond (a square turned on its corner)
    /// inside a cyan ring, so it can never be confused with a hostile blip.
    /// </summary>
    private Blip CreateObjectiveBlip()
    {
        var go = new GameObject("ObjectiveBlip", typeof(RectTransform));
        var rect = go.GetComponent<RectTransform>();
        rect.SetParent(_root, false);
        rect.anchorMin = Vector2.one * 0.5f;
        rect.anchorMax = Vector2.one * 0.5f;
        rect.pivot = Vector2.one * 0.5f;
        rect.sizeDelta = Vector2.zero;

        var blip = new Blip { Rect = rect };

        var diamondGo = new GameObject("ObjectiveDiamond", typeof(RectTransform));
        var diamondRect = diamondGo.GetComponent<RectTransform>();
        diamondRect.SetParent(rect, false);
        diamondRect.anchorMin = Vector2.one * 0.5f;
        diamondRect.anchorMax = Vector2.one * 0.5f;
        diamondRect.pivot = Vector2.one * 0.5f;
        diamondRect.sizeDelta = Vector2.one * _objectiveDotSize;
        diamondRect.localEulerAngles = new Vector3(0f, 0f, 45f);
        var diamondImg = diamondGo.AddComponent<Image>();
        diamondImg.sprite = _dotSprite;
        diamondImg.color = _objectiveColor;
        diamondImg.raycastTarget = false;

        blip.RingSize = _objectiveRingSize;
        blip.HideRingWhenClamped = true;
        blip.Ring = CreateRingChild(rect, _objectiveRingSize, _objectiveColor);
        blip.Tick = CreateTickChild(rect, _objectiveColor);
        return blip;
    }

    private RectTransform CreateTickChild(RectTransform parent, Color color)
    {
        var tickGo = new GameObject("EdgeTick", typeof(RectTransform));
        var tickRect = tickGo.GetComponent<RectTransform>();
        tickRect.SetParent(parent, false);
        tickRect.anchorMin = Vector2.one * 0.5f;
        tickRect.anchorMax = Vector2.one * 0.5f;
        tickRect.pivot = Vector2.one * 0.5f;
        tickRect.sizeDelta = new Vector2(2f, _edgeTickLength);
        var tickImg = tickGo.AddComponent<Image>();
        tickImg.sprite = _dotSprite;
        tickImg.color = color;
        tickImg.raycastTarget = false;
        return tickRect;
    }

    private RectTransform CreateRingChild(RectTransform parent, float diameter, Color color)
    {
        var ringGo = new GameObject("TargetRing", typeof(RectTransform));
        var ringRect = ringGo.GetComponent<RectTransform>();
        ringRect.SetParent(parent, false);
        ringRect.anchorMin = Vector2.one * 0.5f;
        ringRect.anchorMax = Vector2.one * 0.5f;
        ringRect.pivot = Vector2.one * 0.5f;
        ringRect.sizeDelta = Vector2.one * diameter;
        var ringImg = ringGo.AddComponent<Image>();
        ringImg.sprite = _ringSprite;
        ringImg.color = color;
        ringImg.raycastTarget = false;
        return ringRect;
    }

    /// <summary>
    /// Moves the heading chevron around the compass ring so the crew can see
    /// which way the ship points. The map itself stays north-up.
    /// </summary>
    private void UpdateHeading(Transform center)
    {
        if (_headingChevron == null) return;

        Vector3 forward = center.forward;
        Vector2 flat = new Vector2(forward.x, forward.z);
        if (flat.sqrMagnitude < 0.0001f) return;

        // 0 degrees is north (+Z on the map), the angle grows clockwise.
        float angle = Mathf.Atan2(flat.x, flat.y) * Mathf.Rad2Deg;
        float rad = angle * Mathf.Deg2Rad;
        Vector2 dir = new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));

        _headingChevron.anchoredPosition = dir * (_mapPixelRadius - 6f);
        _headingChevron.localEulerAngles = new Vector3(0f, 0f, -angle);
    }

    /// <summary>
    /// Range of the map plus, while the level 1 gate is live, the distance to
    /// it. The strings are only rebuilt when the rounded distance changes.
    /// </summary>
    private void UpdateReadout(Vector3 centerPos)
    {
        if (_readoutText == null) return;

        BattleSky.Network.Level1Checkpoint checkpoint = BattleSky.Network.Level1Checkpoint.Instance;
        bool hasGate = checkpoint != null && !checkpoint.Reached;

        if (!hasGate)
        {
            _cachedGateMetres = -1;
            _gateLine = string.Empty;
            ApplyReadout(_rangeLine);
            return;
        }

        float metres = checkpoint.DistanceTo(centerPos);
        int rounded = Mathf.Max(0, Mathf.RoundToInt(metres));
        if (rounded != _cachedGateMetres)
        {
            _cachedGateMetres = rounded;
            _gateLine = _rangeLine + "\nGATE " + FormatGateDistance(metres);
        }
        ApplyReadout(_gateLine);
    }

    private void ApplyReadout(string line)
    {
        if (line == _cachedReadout) return;
        _cachedReadout = line;
        _readoutText.text = line;
    }

    /// <summary>Map radius as shown on the readout, in metres or kilometres.</summary>
    private string FormatMapRadius()
    {
        if (_mapRadius > 1000f)
            return (_mapRadius / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " km";
        return Mathf.Max(0, Mathf.RoundToInt(_mapRadius)).ToString(CultureInfo.InvariantCulture) + "m";
    }

    /// <summary>Gate distance as shown on the readout, in metres or kilometres.</summary>
    private static string FormatGateDistance(float metres)
    {
        if (metres > 1000f)
            return (metres / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " km";
        return Mathf.Max(0, Mathf.RoundToInt(metres)).ToString(CultureInfo.InvariantCulture) + " m";
    }

    /// <summary>
    /// Ship the map is centred on. The gunner's NetworkPlayer is an invisible
    /// control proxy that only mirrors the crew ship pose, so a gunner centres
    /// on the real (non-proxy) ship of the same crew instead.
    /// </summary>
    private Transform ResolveMapCenter()
    {
        NetworkPlayer local = LocalPlayerExtensions.FindLocalPlayerSafe();
        if (local == null) return null;
        if (!local.IsGunnerProxy) return local.transform;

        NetworkPlayer crewShip = FindCrewShip(local);
        return crewShip != null ? crewShip.transform : local.transform;
    }

    /// <summary>
    /// Client-side crew ship lookup for a gunner proxy: the non-proxy
    /// NetworkPlayer sharing its crewId. crewId is 0 until the server assigns
    /// a crew, so never match on it before then. Kept here so NetworkPlayer.cs
    /// does not need to change.
    /// </summary>
    private static NetworkPlayer FindCrewShip(NetworkPlayer proxy)
    {
        if (proxy == null || proxy.crewId == 0) return null;

        foreach (NetworkPlayer ship in Object.FindObjectsByType<NetworkPlayer>(FindObjectsSortMode.None))
        {
            if (ship == null || ship == proxy) continue;
            if (ship.crewId == proxy.crewId && !ship.IsGunnerProxy)
                return ship;
        }
        return null;
    }

    /// <summary>
    /// Builtin UI font with an OS fallback, matching the HUD font loading.
    /// </summary>
    private static Font LoadFont()
    {
        Font font = null;
        try
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }
        catch
        {
            // Older editors only ship Arial.ttf; the OS fallback covers both.
        }
        if (font == null)
            font = CreateOsFont("Arial");
        if (font == null)
            font = CreateOsFont("Helvetica");
        if (font == null)
            font = CreateOsFont("Segoe UI");
        return font;
    }

    private static Font CreateOsFont(string name)
    {
        try
        {
            return Font.CreateDynamicFontFromOSFont(name, 12);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Small helper to find the local player NetworkPlayer without requiring
/// NetworkClient.localPlayer to be set (handles offline/solo scenes).
/// </summary>
public static class LocalPlayerExtensions
{
    public static NetworkPlayer FindLocalPlayerSafe()
    {
        if (Mirror.NetworkClient.localPlayer != null)
        {
            var p = Mirror.NetworkClient.localPlayer.GetComponent<NetworkPlayer>();
            if (p != null) return p;
        }
        foreach (var p in Object.FindObjectsByType<NetworkPlayer>(FindObjectsSortMode.None))
        {
            if (p.isLocalPlayer) return p;
        }
        return null;
    }
}
