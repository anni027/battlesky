using Mirror;
using UnityEngine;
using UnityEngine.UI;

namespace BattleSky.Network
{
    /// <summary>
    /// Local-crew HUD: hull, shield and ammo readouts for the ship the local
    /// player actually controls. A gunner's proxy carries no real ship state,
    /// so it displays the shared crew ship instead.
    ///
    /// The widget tree is built in Awake, which keeps the scene free of a
    /// hand-kept uGUI hierarchy: it only needs this one component on the
    /// existing Canvas. The panel stays hidden until a local ship exists, so
    /// it never shows in the editor scene view or on a headless server.
    /// </summary>
    [DisallowMultipleComponent]
    public class NetworkHUD : MonoBehaviour
    {
        [Header("Layout (pixels)")]
        [Tooltip("Offset from the bottom-left screen corner")]
        public Vector2 panelOffset = new Vector2(24f, 26f);
        public float barWidth = 300f;
        public float barHeight = 16f;
        public float rowGap = 10f;
        public float labelWidth = 64f;
        public float valueWidth = 78f;
        public float reloadStripHeight = 5f;
        public float padding = 8f;
        public int fontSize = 14;

        [Header("Colors")]
        public Color panelColor = new Color(0f, 0f, 0f, 0.42f);
        public Color trackColor = new Color(0f, 0f, 0f, 0.6f);
        public Color hullColor = new Color(0.93f, 0.3f, 0.26f);
        public Color hullLowColor = new Color(1f, 0.64f, 0.16f);
        public Color shieldActiveColor = new Color(0.3f, 0.8f, 1f);
        public Color shieldIdleColor = new Color(0.33f, 0.46f, 0.58f);
        public Color ammoColor = new Color(1f, 0.82f, 0.35f);
        public Color reloadColor = new Color(0.55f, 0.95f, 0.55f);
        public Color textColor = new Color(0.92f, 0.95f, 1f);
        public Color deadColor = new Color(0.55f, 0.55f, 0.55f);

        [Header("Behavior")]
        [Tooltip("Seconds between bind attempts while no local ship exists yet")]
        public float rebindInterval = 0.4f;
        [Tooltip("Bar chase speed in 1/s; 0 snaps instantly")]
        public float smoothing = 14f;

        private const float ReloadStripGap = 4f;

        private RectTransform _root;
        private Image _hullFill;
        private Image _shieldFill;
        private Image _ammoFill;
        private Image _reloadFill;
        private Text _hullValue;
        private Text _shieldValue;
        private Text _ammoValue;
        private Sprite _square;
        private Texture2D _squareTexture;
        private Font _font;
        private NetworkPlayer _target;
        private float _nextBindTime;

        private void Awake()
        {
            Build();
        }

        private void OnDestroy()
        {
            // Edit-mode callers (editor tools, probes) exit the process, so
            // only a live player has runtime objects that need destroying.
            if (!Application.isPlaying) return;
            if (_square != null) Destroy(_square);
            if (_squareTexture != null) Destroy(_squareTexture);
        }

        private void Update()
        {
            if (_root == null) return;

            if (_target == null && Time.unscaledTime >= _nextBindTime)
                Bind(ResolveLocalTarget());

            bool visible = _target != null;
            if (_root.gameObject.activeSelf != visible)
                _root.gameObject.SetActive(visible);
            if (visible)
                UpdateReadouts();
        }

        /// <summary>
        /// The ship whose state the HUD shows: the local player's own ship, or
        /// the shared crew ship when the local player is a gunner proxy. Null
        /// before the local player spawns (menu, server, editor).
        /// </summary>
        public static NetworkPlayer ResolveLocalTarget()
        {
            NetworkPlayer local = NetworkClient.localPlayer != null
                ? NetworkClient.localPlayer.GetComponent<NetworkPlayer>()
                : null;
            if (local == null)
                return null;
            return local.IsGunnerProxy ? local.FindCrewShip() : local;
        }

        /// <summary>Bind a ship (null hides the panel) and snap the bars to its values.</summary>
        public void Bind(NetworkPlayer target)
        {
            if (_root == null)
                Build();
            _target = target;
            _nextBindTime = Time.unscaledTime + Mathf.Max(0.05f, rebindInterval);
            if (target != null)
                ApplyReadouts(1f);
        }

        /// <summary>Pull one frame of state from the bound ship.</summary>
        public void UpdateReadouts()
        {
            if (_target == null)
                return;
            float dt = Time.deltaTime;
            float k = smoothing <= 0f || dt <= 0f ? 1f : 1f - Mathf.Exp(-smoothing * dt);
            ApplyReadouts(k);
        }

        private void ApplyReadouts(float k)
        {
            float hull = Mathf.Clamp01(_target.HealthNormalized);
            _hullFill.fillAmount = Mathf.Lerp(_hullFill.fillAmount, hull, k);
            _hullFill.color = _target.IsDead
                ? deadColor
                : Color.Lerp(hullLowColor, hullColor, Mathf.InverseLerp(0.2f, 0.55f, hull));
            _hullValue.text = _target.IsDead ? "DOWN" : Mathf.RoundToInt(hull * 100f) + "%";
            _hullValue.color = _hullFill.color;

            float shield = Mathf.Clamp01(_target.ShieldNormalized);
            _shieldFill.fillAmount = Mathf.Lerp(_shieldFill.fillAmount, shield, k);
            _shieldFill.color = _target.IsShieldActive ? shieldActiveColor : shieldIdleColor;
            _shieldValue.text = Mathf.RoundToInt(shield * 100f) + "%";
            _shieldValue.color = _target.IsShieldActive ? shieldActiveColor : textColor;

            int ammo = _target.CurrentAmmo;
            int maxAmmo = Mathf.Max(1, _target.MaxAmmo);
            bool reloading = _target.IsReloading;
            _ammoFill.fillAmount = Mathf.Lerp(_ammoFill.fillAmount, Mathf.Clamp01((float)ammo / maxAmmo), k);
            _ammoFill.color = reloading ? reloadColor : ammoColor;
            _reloadFill.fillAmount = reloading
                ? Mathf.Clamp01(_target.ReloadProgress)
                : Mathf.Lerp(_reloadFill.fillAmount, 0f, k);
            _ammoValue.text = reloading ? "RELOAD" : ammo + " / " + maxAmmo;
            _ammoValue.color = reloading ? reloadColor : (ammo == 0 ? hullLowColor : textColor);
        }

        /// <summary>Create the widget tree once under this transform.</summary>
        public void Build()
        {
            if (_root != null)
                return;

            _font = LoadFont();
            _squareTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                name = "NetworkHudSquare",
                hideFlags = HideFlags.HideAndDontSave,
            };
            _squareTexture.SetPixel(0, 0, Color.white);
            _squareTexture.Apply();
            _square = Sprite.Create(_squareTexture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 100f);
            _square.name = "NetworkHudSquare";
            _square.hideFlags = HideFlags.HideAndDontSave;

            float contentWidth = labelWidth + barWidth + valueWidth;
            float contentHeight = reloadStripHeight + ReloadStripGap + barHeight * 3f + rowGap * 2f;

            var rootGo = new GameObject("HudPanel", typeof(RectTransform));
            _root = (RectTransform)rootGo.transform;
            _root.SetParent(transform, false);
            _root.anchorMin = Vector2.zero;
            _root.anchorMax = Vector2.zero;
            _root.pivot = Vector2.zero;
            _root.anchoredPosition = panelOffset;
            _root.sizeDelta = new Vector2(contentWidth + padding * 2f, contentHeight + padding * 2f);
            Image panel = rootGo.AddComponent<Image>();
            panel.sprite = _square;
            panel.color = panelColor;
            panel.raycastTarget = false;

            // Rows stack bottom-up: reload strip, ammo, shield, hull.
            _reloadFill = CreateReloadStrip(new Vector2(padding + labelWidth, padding));
            float y = padding + reloadStripHeight + ReloadStripGap;

            _ammoFill = CreateBar("Ammo", "AMMO", new Vector2(padding, y), contentWidth, out _ammoValue);
            y += barHeight + rowGap;
            _shieldFill = CreateBar("Shield", "SHIELD", new Vector2(padding, y), contentWidth, out _shieldValue);
            y += barHeight + rowGap;
            _hullFill = CreateBar("Hull", "HULL", new Vector2(padding, y), contentWidth, out _hullValue);
        }

        private Image CreateBar(string name, string caption, Vector2 position, float contentWidth, out Text value)
        {
            RectTransform row = CreateRect(_root, name + "Row");
            row.anchorMin = Vector2.zero;
            row.anchorMax = Vector2.zero;
            row.pivot = Vector2.zero;
            row.anchoredPosition = position;
            row.sizeDelta = new Vector2(contentWidth, barHeight);

            Text label = CreateText(row, name + "Label", 0f, labelWidth, TextAnchor.MiddleLeft);
            label.text = caption;

            RectTransform track = CreateRect(row, name + "Track");
            track.anchorMin = new Vector2(0f, 0f);
            track.anchorMax = new Vector2(0f, 1f);
            track.pivot = new Vector2(0f, 0.5f);
            track.anchoredPosition = new Vector2(labelWidth, 0f);
            track.sizeDelta = new Vector2(barWidth, 0f);
            Image trackImage = track.gameObject.AddComponent<Image>();
            trackImage.sprite = _square;
            trackImage.color = trackColor;
            trackImage.raycastTarget = false;

            Image fill = CreateFill(track, name + "Fill", true);
            value = CreateText(row, name + "Value", labelWidth + barWidth + 8f,
                Mathf.Max(0f, contentWidth - labelWidth - barWidth - 8f), TextAnchor.MiddleRight);
            return fill;
        }

        private Image CreateReloadStrip(Vector2 position)
        {
            RectTransform track = CreateRect(_root, "ReloadTrack");
            track.anchorMin = Vector2.zero;
            track.anchorMax = Vector2.zero;
            track.pivot = Vector2.zero;
            track.anchoredPosition = position;
            track.sizeDelta = new Vector2(barWidth, reloadStripHeight);
            Image trackImage = track.gameObject.AddComponent<Image>();
            trackImage.sprite = _square;
            trackImage.color = trackColor;
            trackImage.raycastTarget = false;

            Image fill = CreateFill(track, "ReloadFill", false);
            fill.fillAmount = 0f;
            return fill;
        }

        private Image CreateFill(RectTransform track, string name, bool inset)
        {
            RectTransform fill = CreateRect(track, name);
            fill.anchorMin = Vector2.zero;
            fill.anchorMax = Vector2.one;
            fill.offsetMin = inset ? new Vector2(1f, 1f) : Vector2.zero;
            fill.offsetMax = inset ? new Vector2(-1f, -1f) : Vector2.zero;
            Image image = fill.gameObject.AddComponent<Image>();
            image.sprite = _square;
            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Horizontal;
            image.fillOrigin = (int)Image.OriginHorizontal.Left;
            image.fillAmount = 1f;
            image.raycastTarget = false;
            return image;
        }

        private Text CreateText(RectTransform parent, string name, float x, float width, TextAnchor alignment)
        {
            RectTransform rect = CreateRect(parent, name);
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = new Vector2(x, 0f);
            rect.sizeDelta = new Vector2(width, 0f);
            Text text = rect.gameObject.AddComponent<Text>();
            text.font = _font;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = textColor;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            Shadow shadow = rect.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.65f);
            shadow.effectDistance = new Vector2(1f, -1f);
            return text;
        }

        private static RectTransform CreateRect(RectTransform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        private Font LoadFont()
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
                font = Font.CreateDynamicFontFromOSFont("Arial", fontSize);
            return font;
        }
    }
}
