using System.Globalization;
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

        [Header("Role Block (top-left)")]
        [Tooltip("Top-left offset of the local role label + control hints")]
        public Vector2 roleBlockOffset = new Vector2(24f, -22f);
        [Tooltip("Width of the hint area; the solo pilot line needs room for brake, air brake and free-look")]
        public float roleBlockWidth = 660f;
        public int roleFontSize = 16;
        public int hintFontSize = 12;

        [Header("Flight Status (bottom-centre)")]
        [Tooltip("Offset from the bottom-centre screen edge")]
        public Vector2 statusOffset = new Vector2(0f, 28f);
        public float statusWidth = 320f;
        public int statusFontSize = 15;

        [Header("Gunner Reticle")]
        [Tooltip("Gap between the screen centre and each crosshair tick (px)")]
        public float reticleGap = 9f;
        [Tooltip("Length of each crosshair tick (px)")]
        public float reticleArmLength = 7f;
        public float reticleThickness = 2f;
        public Color reticleColor = new Color(1f, 1f, 1f, 0.65f);

        [Header("Mission Banner (top-centre)")]
        [Tooltip("Offset from the top-centre screen edge")]
        public Vector2 objectiveOffset = new Vector2(0f, -16f);
        public float objectiveWidth = 640f;
        public int objectiveFontSize = 15;
        [Tooltip("Width of the boss integrity bar")]
        public float bossBarWidth = 420f;
        public float bossBarHeight = 12f;
        public Color objectiveColor = new Color(0.86f, 0.92f, 1f, 1f);
        public Color bossBarColor = new Color(1f, 0.36f, 0.3f, 1f);
        public Color warningColor = new Color(1f, 0.72f, 0.2f, 1f);

        private const float ReloadStripGap = 4f;
        // The boss label carries two lines (the stage title plus the
        // sub-label that says what to do), so the bar and the superlaser
        // charge strip both sit below the pair of them.
        private const float BossLabelHeight = 30f;
        private const float BossTrackTop = 32f;

        private RectTransform _root;
        private Image _hullFill;
        private Image _shieldFill;
        private Image _ammoFill;
        private Image _reloadFill;
        private Text _hullValue;
        private Text _shieldValue;
        private Text _ammoValue;
        private RectTransform _roleRoot;
        private Text _roleLabel;
        private Text _roleHints;
        private Text _roleStatus;
        private RectTransform _statusRoot;
        private Text _statusText;
        private RectTransform _reticleRoot;
        private RectTransform _objectiveRoot;
        private Text _objectiveText;
        private RectTransform _bossRoot;
        private Image _bossFill;
        private Text _bossLabel;
        private Text _bossWarning;
        private Image _bossChargeFill;
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

            // The role block and the reticle follow the local player, not the
            // bound ship: a gunner proxy still needs to see GUNNER even while
            // the crew ship has not replicated yet.
            NetworkPlayer local = ResolveLocalPlayer();
            UpdateRoleBlock(local);
            UpdateReticle(local);
            UpdateStatusReadout();
            UpdateMissionBanner();
            UpdateBossBar();

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
            NetworkPlayer local = ResolveLocalPlayer();
            if (local == null)
                return null;
            return local.IsGunnerProxy ? local.FindCrewShip() : local;
        }

        /// <summary>
        /// The local crew member's own NetworkPlayer, gunner proxy included.
        /// Null before spawn (menu, server, editor).
        /// </summary>
        public static NetworkPlayer ResolveLocalPlayer()
        {
            NetworkIdentity identity = NetworkClient.localPlayer;
            return identity != null ? identity.GetComponent<NetworkPlayer>() : null;
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

            BuildRoleBlock();
            BuildReticle();
            BuildStatusReadout();
            BuildMissionBanner();
            BuildBossBar();
        }

        /// <summary>
        /// Small top-left block with the local crew member's seat and the keys
        /// that seat actually owns. The hints follow the real bindings in
        /// ShipInputActions: Ship map (W/S thrust, A/D yaw, mouse steer,
        /// Q/E roll, Fire, leftShift boost, X brake, leftCtrl air brake,
        /// Space shield, R reload,
        /// LeftAlt free-look, G flight assist) and Gunner map (mouse-aimed
        /// camera, RMB fire, Space shield, R reload). Pilot fire is ignored
        /// while a gunner is aboard, so the pilot hint drops the weapon keys in
        /// that case.
        /// </summary>
        private void BuildRoleBlock()
        {
            var roleGo = new GameObject("HudRoleBlock", typeof(RectTransform));
            _roleRoot = (RectTransform)roleGo.transform;
            _roleRoot.SetParent(transform, false);
            _roleRoot.anchorMin = new Vector2(0f, 1f);
            _roleRoot.anchorMax = new Vector2(0f, 1f);
            _roleRoot.pivot = new Vector2(0f, 1f);
            _roleRoot.anchoredPosition = roleBlockOffset;

            // The hint line is authored with one explicit break, so reserve two
            // wrapped lines before the crew status line.
            float hintY = -(roleFontSize + 4f);
            float hintLine = hintFontSize + 4f;
            float statusY = hintY - hintLine * 2f - 2f;
            _roleRoot.sizeDelta = new Vector2(roleBlockWidth, -statusY + hintFontSize + 6f);

            _roleLabel = CreateTopLeftText(_roleRoot, "RoleLabel", 0f, roleFontSize);
            _roleHints = CreateTopLeftText(_roleRoot, "RoleHints", hintY, hintFontSize);
            _roleStatus = CreateTopLeftText(_roleRoot, "RoleStatus", statusY, hintFontSize - 1);
            _roleRoot.gameObject.SetActive(false);
        }

        /// <summary>
        /// Four short ticks around the screen centre. The gunner's camera
        /// forward is the aim direction sent to the crew ship, so screen
        /// centre is the aim point. Shown only for the local gunner: the pilot
        /// steers the ship itself and needs no reticle.
        /// </summary>
        private void BuildReticle()
        {
            var reticleGo = new GameObject("GunnerReticle", typeof(RectTransform));
            _reticleRoot = (RectTransform)reticleGo.transform;
            _reticleRoot.SetParent(transform, false);
            _reticleRoot.anchorMin = new Vector2(0.5f, 0.5f);
            _reticleRoot.anchorMax = new Vector2(0.5f, 0.5f);
            _reticleRoot.pivot = new Vector2(0.5f, 0.5f);
            _reticleRoot.anchoredPosition = Vector2.zero;
            _reticleRoot.sizeDelta = Vector2.zero;

            float inner = reticleGap + reticleArmLength * 0.5f;
            CreateReticleTick("ReticleTop", new Vector2(0f, inner), new Vector2(reticleThickness, reticleArmLength));
            CreateReticleTick("ReticleBottom", new Vector2(0f, -inner), new Vector2(reticleThickness, reticleArmLength));
            CreateReticleTick("ReticleLeft", new Vector2(-inner, 0f), new Vector2(reticleArmLength, reticleThickness));
            CreateReticleTick("ReticleRight", new Vector2(inner, 0f), new Vector2(reticleArmLength, reticleThickness));
            _reticleRoot.gameObject.SetActive(false);
        }

        /// <summary>
        /// Small bottom-centre strip with the shared crew ship's speed and,
        /// while level 1 is still running, the distance to the checkpoint
        /// gate. Pilot and gunner both see it because both read the same
        /// crew ship. An AIR BRAKE tag is appended while the air brake is
        /// held.
        /// </summary>
        private void BuildStatusReadout()
        {
            var statusGo = new GameObject("HudStatus", typeof(RectTransform));
            _statusRoot = (RectTransform)statusGo.transform;
            _statusRoot.SetParent(transform, false);
            _statusRoot.anchorMin = new Vector2(0.5f, 0f);
            _statusRoot.anchorMax = new Vector2(0.5f, 0f);
            _statusRoot.pivot = new Vector2(0.5f, 0f);
            _statusRoot.anchoredPosition = statusOffset;
            _statusRoot.sizeDelta = new Vector2(statusWidth, statusFontSize + padding * 2f);

            Image panel = statusGo.AddComponent<Image>();
            panel.sprite = _square;
            panel.color = panelColor;
            panel.raycastTarget = false;

            _statusText = CreateStatusText(_statusRoot);
            _statusRoot.gameObject.SetActive(false);
        }

        private Text CreateStatusText(RectTransform parent)
        {
            RectTransform rect = CreateRect(parent, "StatusText");
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(padding, 0f);
            rect.offsetMax = new Vector2(-padding, 0f);

            Text text = rect.gameObject.AddComponent<Text>();
            text.font = _font;
            text.fontSize = statusFontSize;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = textColor;
            text.raycastTarget = false;
            text.supportRichText = true;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            Shadow shadow = rect.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.65f);
            shadow.effectDistance = new Vector2(1f, -1f);
            return text;
        }

        /// <summary>
        /// Top-centre mission line. The level is replicated by
        /// NetworkLevelManager, so pilot and gunner see the same objective
        /// and the text changes when the crew advances.
        /// </summary>
        private void BuildMissionBanner()
        {
            var go = new GameObject("HudObjective", typeof(RectTransform));
            _objectiveRoot = (RectTransform)go.transform;
            _objectiveRoot.SetParent(transform, false);
            _objectiveRoot.anchorMin = new Vector2(0.5f, 1f);
            _objectiveRoot.anchorMax = new Vector2(0.5f, 1f);
            _objectiveRoot.pivot = new Vector2(0.5f, 1f);
            _objectiveRoot.anchoredPosition = objectiveOffset;
            _objectiveRoot.sizeDelta = new Vector2(objectiveWidth, objectiveFontSize + 8f);

            _objectiveText = go.AddComponent<Text>();
            _objectiveText.font = _font;
            _objectiveText.fontSize = objectiveFontSize;
            _objectiveText.alignment = TextAnchor.MiddleCenter;
            _objectiveText.color = objectiveColor;
            _objectiveText.raycastTarget = false;
            _objectiveText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _objectiveText.verticalOverflow = VerticalWrapMode.Overflow;
            Shadow shadow = go.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.7f);
            shadow.effectDistance = new Vector2(1f, -1f);

            _objectiveRoot.gameObject.SetActive(false);
        }

        /// <summary>
        /// Boss integrity bar plus the superlaser warning. It only appears
        /// while the Death Star is alive, so level 1 and 2 stay clean.
        /// </summary>
        private void BuildBossBar()
        {
            var go = new GameObject("HudBossBar", typeof(RectTransform));
            _bossRoot = (RectTransform)go.transform;
            _bossRoot.SetParent(transform, false);
            _bossRoot.anchorMin = new Vector2(0.5f, 1f);
            _bossRoot.anchorMax = new Vector2(0.5f, 1f);
            _bossRoot.pivot = new Vector2(0.5f, 1f);
            _bossRoot.anchoredPosition = objectiveOffset + new Vector2(0f, -(objectiveFontSize + 16f));
            _bossRoot.sizeDelta = new Vector2(bossBarWidth, BossTrackTop + bossBarHeight + 30f);

            // The label holds the stage title and, on the second line, the
            // instruction or the fighter count for that stage.
            _bossLabel = CreateHudText(_bossRoot, "BossLabel", 12, TextAnchor.UpperLeft);
            _bossLabel.fontStyle = FontStyle.Bold;
            _bossLabel.color = bossBarColor;
            _bossLabel.rectTransform.anchorMin = new Vector2(0f, 1f);
            _bossLabel.rectTransform.anchorMax = new Vector2(1f, 1f);
            _bossLabel.rectTransform.pivot = new Vector2(0f, 1f);
            _bossLabel.rectTransform.anchoredPosition = Vector2.zero;
            _bossLabel.rectTransform.sizeDelta = new Vector2(0f, BossLabelHeight);

            RectTransform track = CreateHudRect(_bossRoot, "BossTrack");
            track.anchorMin = new Vector2(0f, 1f);
            track.anchorMax = new Vector2(1f, 1f);
            track.pivot = new Vector2(0f, 1f);
            track.anchoredPosition = new Vector2(0f, -BossTrackTop);
            track.sizeDelta = new Vector2(0f, bossBarHeight);
            Image trackImage = track.gameObject.AddComponent<Image>();
            trackImage.sprite = _square;
            trackImage.color = trackColor;
            trackImage.raycastTarget = false;

            Image fill = CreateFill(track, "BossFill", true);
            fill.color = bossBarColor;
            _bossFill = fill;

            RectTransform charge = CreateHudRect(_bossRoot, "BossCharge");
            charge.anchorMin = new Vector2(0f, 1f);
            charge.anchorMax = new Vector2(1f, 1f);
            charge.pivot = new Vector2(0f, 1f);
            charge.anchoredPosition = new Vector2(0f, -(BossTrackTop + bossBarHeight + 3f));
            charge.sizeDelta = new Vector2(0f, 4f);
            Image chargeImage = charge.gameObject.AddComponent<Image>();
            chargeImage.sprite = _square;
            chargeImage.color = new Color(0f, 0f, 0f, 0.6f);
            chargeImage.raycastTarget = false;

            Image chargeFill = CreateFill(charge, "BossChargeFill", false);
            chargeFill.color = warningColor;
            _bossChargeFill = chargeFill;

            _bossWarning = CreateHudText(_bossRoot, "BossWarning", 12, TextAnchor.UpperCenter);
            _bossWarning.fontStyle = FontStyle.Bold;
            _bossWarning.color = warningColor;
            _bossWarning.rectTransform.anchorMin = new Vector2(0f, 1f);
            _bossWarning.rectTransform.anchorMax = new Vector2(1f, 1f);
            _bossWarning.rectTransform.pivot = new Vector2(0.5f, 1f);
            _bossWarning.rectTransform.anchoredPosition = new Vector2(0f, -(BossTrackTop + bossBarHeight + 10f));
            _bossWarning.rectTransform.sizeDelta = new Vector2(0f, 16f);

            _bossRoot.gameObject.SetActive(false);
        }

        private Text CreateHudText(RectTransform parent, string name, int size, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            Text text = go.AddComponent<Text>();
            text.font = _font;
            text.fontSize = size;
            text.alignment = alignment;
            text.color = textColor;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            Shadow shadow = go.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.7f);
            shadow.effectDistance = new Vector2(1f, -1f);
            return text;
        }

        private static RectTransform CreateHudRect(RectTransform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        private void UpdateMissionBanner()
        {
            if (_objectiveRoot == null) return;

            NetworkPlayer local = ResolveLocalPlayer();
            bool visible = local != null;
            if (_objectiveRoot.gameObject.activeSelf != visible)
                _objectiveRoot.gameObject.SetActive(visible);
            if (!visible) return;

            int level = NetworkLevelManager.CurrentLevel;
            string line;
            if (level <= 1)
            {
                line = "OBJECTIVE  //  RUN THE BELT \u2014 REACH THE CHECKPOINT GATE";
            }
            else if (level == 2)
            {
                int down = Mathf.Clamp(NetworkLevelManager.EnemiesDestroyed, 0, NetworkLevelManager.Level3KillThreshold);
                int remaining = Mathf.Max(0, NetworkLevelManager.Level3KillThreshold - down);
                line = "OBJECTIVE  //  CLEAR THE BLOCKADE \u2014 "
                    + down + "/" + NetworkLevelManager.Level3KillThreshold + " HOSTILES DOWN  //  "
                    + (remaining > 0 ? remaining + " TO GO" : "DEATH STAR INBOUND");
            }
            else
            {
                line = DeathStarObjective();
            }
            _objectiveText.text = line;
        }

        /// <summary>
        /// Objective line for the Death Star fight. The station fights in
        /// stages, so the line names the stage the crew is in right now:
        /// the force field, then the fighter wave, then the heavy wave and
        /// the hull.
        /// </summary>
        private static string DeathStarObjective()
        {
            DeathStar boss = DeathStar.Active;
            if (boss == null) return "OBJECTIVE  //  DEATH STAR";
            switch (boss.Stage)
            {
                case DeathStarStage.ForceField:
                    return "OBJECTIVE  //  DEATH STAR \u2014 BREAK THE WHITE FORCE FIELD";
                case DeathStarStage.Fighters:
                    return "OBJECTIVE  //  DEATH STAR \u2014 DESTROY THE FIGHTER WAVE";
                case DeathStarStage.HeavyFighters:
                    return "OBJECTIVE  //  DEATH STAR \u2014 DESTROY THE HEAVY WAVE, THEN BREAK THE HULL";
                default:
                    return "OBJECTIVE  //  DEATH STAR";
            }
        }

        /// <summary>
        /// Boss bar for the Death Star. The station fights in stages, so the
        /// bar always shows the number the crew has to beat right now: the
        /// force field while it is up, the surface integrity once it is
        /// gone. The fighter stages add a live count of the wave that is
        /// still in the air.
        /// </summary>
        private void UpdateBossBar()
        {
            if (_bossRoot == null) return;

            DeathStar boss = DeathStar.Active;
            bool visible = boss != null && !boss.IsDestroyed;
            if (_bossRoot.gameObject.activeSelf != visible)
                _bossRoot.gameObject.SetActive(visible);
            if (!visible) return;

            string title;
            string sub;
            float fill;
            Color fillColor = bossBarColor;

            switch (boss.Stage)
            {
                case DeathStarStage.ForceField:
                {
                    // Stage 1: the force field is the only thing that can be
                    // hit, so the bar belongs to the field, not to the hull.
                    float field = Mathf.Clamp01(boss.ForceFieldNormalized);
                    fill = field;
                    title = "DEATH STAR  //  FORCE FIELD  " + Mathf.RoundToInt(field * 100f) + "%";
                    sub = boss.IsForceFieldActive ? "BREAK THE FORCE FIELD" : "FORCE FIELD DOWN";
                    fillColor = shieldActiveColor;
                    break;
                }
                case DeathStarStage.Fighters:
                {
                    fill = Mathf.Clamp01(boss.HealthNormalized);
                    title = "DEATH STAR  //  STAGE 2/3";
                    sub = FighterCounter();
                    break;
                }
                case DeathStarStage.HeavyFighters:
                {
                    // The last stage: the swarm never stops and the hull is
                    // the only thing left to break.
                    fill = Mathf.Clamp01(boss.HealthNormalized);
                    title = "DEATH STAR  //  STAGE 3/3";
                    sub = FighterCounter();
                    break;
                }
                default:
                {
                    fill = Mathf.Clamp01(boss.HealthNormalized);
                    title = "DEATH STAR  //  HULL BREACH";
                    sub = "SURFACE INTEGRITY  " + Mathf.RoundToInt(fill * 100f) + "%";
                    break;
                }
            }

            _bossFill.fillAmount = fill;
            _bossFill.color = fillColor;
            _bossLabel.text = title + "\n" + SubLabelText(sub);

            bool charging = boss.IsChargingSuperlaser;
            if (_bossWarning.gameObject.activeSelf != charging)
                _bossWarning.gameObject.SetActive(charging);

            _bossChargeFill.fillAmount = charging ? Mathf.Clamp01(boss.SuperlaserChargeNormalized) : 0f;

            if (charging)
            {
                // Pulse the warning so it reads as urgent at a glance.
                float pulse = 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 7f));
                Color c = warningColor;
                c.a = pulse;
                _bossWarning.color = c;
            }
        }

        /// <summary>How many fighters of the current wave are still in the air.</summary>
        private static string FighterCounter()
        {
            int total = NetworkEnemySpawner.WaveTotal;
            if (total <= 0) return "CLEAR THE FIGHTERS";
            int remaining = Mathf.Clamp(NetworkEnemySpawner.WaveRemaining, 0, total);
            return "FIGHTERS " + remaining + "/" + total;
        }

        /// <summary>Sub-label in a dimmer shade, so the title keeps the eye.</summary>
        private string SubLabelText(string text)
        {
            Color c = new Color(textColor.r, textColor.g, textColor.b, 0.72f);
            return "<color=#" + ColorUtility.ToHtmlStringRGBA(c) + ">" + text + "</color>";
        }

        private void UpdateStatusReadout()
        {            if (_statusRoot == null) return;

            // Without a bound crew ship there is no speed to show and no
            // origin for the gate distance, so the whole strip stays hidden.
            bool visible = _target != null;
            if (_statusRoot.gameObject.activeSelf != visible)
                _statusRoot.gameObject.SetActive(visible);
            if (!visible) return;

            int speed = Mathf.Clamp(Mathf.RoundToInt(_target.ShipVelocity.magnitude), 0, 999);
            string line = "SPEED " + speed.ToString("000", CultureInfo.InvariantCulture) + " m/s";

            Level1Checkpoint gate = Level1Checkpoint.Instance;
            if (NetworkLevelManager.CurrentLevel == 1 && gate != null && !gate.Reached)
                line += "   \u00b7   " + FormatGateDistance(gate.DistanceTo(_target.transform.position));

            // Air brake readout. It only shows while the brake is actually
            // held, so the strip stays quiet during normal flight. The rich
            // text tag keeps the speed and the gate distance in their normal
            // colour and tints just the new words.
            if (_target.IsAirBraking)
                line += "   \u00b7   <color=#" + ColorUtility.ToHtmlStringRGBA(warningColor) + ">AIR BRAKE</color>";

            _statusText.text = line;
        }

        private static string FormatGateDistance(float metres)
        {
            if (metres >= 1000f)
                return "GATE " + (metres / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " km";
            return "GATE " + Mathf.Max(0, Mathf.RoundToInt(metres)).ToString(CultureInfo.InvariantCulture) + " m";
        }

        private void UpdateRoleBlock(NetworkPlayer local)
        {
            if (_roleRoot == null) return;

            bool visible = local != null;
            if (_roleRoot.gameObject.activeSelf != visible)
                _roleRoot.gameObject.SetActive(visible);
            if (!visible) return;

            string role;
            string hints;
            string status;
            Color accent;

            if (local.IsGunnerProxy)
            {
                role = "GUNNER";
                hints = "Mouse aim \u00b7 RMB fire \u00b7 Space shield \u00b7 R reload";
                status = "Crew ship: pilot flies, you shoot + shield";
                accent = ammoColor;
            }
            else if (local.crewRole == CrewRole.Pilot)
            {
                role = "PILOT";
                if (local.hasGunner)
                {
                    hints = "W/S thrust \u00b7 A/D yaw \u00b7 mouse steer \u00b7 Q/E roll\nLShift boost \u00b7 X brake \u00b7 LCtrl air brake \u00b7 Alt look \u00b7 G flight assist";
                    status = "P2 gunner aboard: guns + shields are theirs";
                }
                else
                {
                    hints = "W/S thrust \u00b7 A/D yaw \u00b7 mouse steer \u00b7 Q/E roll\nLMB fire \u00b7 Space shield \u00b7 R reload \u00b7 LShift boost \u00b7 X brake \u00b7 LCtrl air brake \u00b7 Alt look \u00b7 G assist";
                    status = "Solo flight: a 2nd player can join as gunner";
                }
                accent = shieldActiveColor;
            }
            else
            {
                role = "SOLO";
                hints = "W/S thrust \u00b7 A/D yaw \u00b7 mouse steer \u00b7 Q/E roll\nLMB fire \u00b7 Space shield \u00b7 R reload \u00b7 LShift boost \u00b7 X brake \u00b7 LCtrl air brake \u00b7 Alt look \u00b7 G assist";
                status = "Single-seat ship: you fly and shoot";
                accent = reloadColor;
            }

            _roleLabel.text = role;
            _roleLabel.color = accent;
            _roleHints.text = hints;
            _roleHints.color = textColor;
            _roleStatus.text = status;
            _roleStatus.color = new Color(textColor.r, textColor.g, textColor.b, 0.6f);
        }

        private void UpdateReticle(NetworkPlayer local)
        {
            if (_reticleRoot == null) return;

            // _target is the shared crew ship for a gunner; hide the reticle
            // once that ship is down.
            bool show = local != null && local.IsGunnerProxy && _target != null && !_target.IsDead;
            if (_reticleRoot.gameObject.activeSelf != show)
                _reticleRoot.gameObject.SetActive(show);
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

        /// <summary>Text that hangs from the top edge of its parent, wrapping inside the parent width.</summary>
        private Text CreateTopLeftText(RectTransform parent, string name, float y, int size)
        {
            RectTransform rect = CreateRect(parent, name);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, y);
            rect.sizeDelta = new Vector2(0f, size + 6f);
            Text text = rect.gameObject.AddComponent<Text>();
            text.font = _font;
            text.fontSize = size;
            text.alignment = TextAnchor.UpperLeft;
            text.color = textColor;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            Shadow shadow = rect.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.65f);
            shadow.effectDistance = new Vector2(1f, -1f);
            return text;
        }

        private void CreateReticleTick(string name, Vector2 position, Vector2 size)
        {
            RectTransform rect = CreateRect(_reticleRoot, name);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            Image image = rect.gameObject.AddComponent<Image>();
            image.sprite = _square;
            image.color = reticleColor;
            image.raycastTarget = false;
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
