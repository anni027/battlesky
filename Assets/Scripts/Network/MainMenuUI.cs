using Mirror;
using UnityEngine;
using UnityEngine.UI;

namespace BattleSky.Network
{
    /// <summary>
    /// BattleSky main menu. Builds its whole widget tree at runtime so the
    /// scene only needs this one component on the existing Canvas.
    ///
    /// It replaces Mirror's stock NetworkManagerHUD (which used to show the
    /// raw transport port and a grey button column). The menu shows the
    /// product name, the mission structure, the host/join flow, the crew
    /// controls, and the transport port in a deliberate place.
    ///
    /// The same component owns the end-of-run overlays (mission complete /
    /// ship lost) so the whole flow lives in one place.
    /// </summary>
    [DisallowMultipleComponent]
    public class MainMenuUI : MonoBehaviour
    {
        [Header("Style")]
        public Color backdropColor = new Color(0.016f, 0.027f, 0.047f, 0.94f);
        public Color accentColor = new Color(0.35f, 0.85f, 1f, 1f);
        public Color warnColor = new Color(1f, 0.42f, 0.32f, 1f);
        public Color goldColor = new Color(1f, 0.82f, 0.35f, 1f);
        public Color textColor = new Color(0.88f, 0.93f, 1f, 1f);
        public Color dimTextColor = new Color(0.55f, 0.63f, 0.75f, 1f);
        public Color panelColor = new Color(0.04f, 0.07f, 0.11f, 0.82f);
        public int titleFontSize = 52;
        public int subtitleFontSize = 13;
        public int bodyFontSize = 13;
        public int buttonFontSize = 15;

        [Header("Layout")]
        [Tooltip("Width of the menu column")]
        public float columnWidth = 420f;
        [Tooltip("Distance of the menu column from the bottom of the screen")]
        public float columnBottomMargin = 40f;

        [Header("Network")]
        [Tooltip("Port shown in the corner; the host binds the same port")]
        public int port = 27015;

        private static MainMenuUI _instance;

        private BattleSkyNetworkManager _manager;
        private Font _font;
        private Sprite _pixel;
        private Texture2D _pixelTexture;
        private Canvas _canvas;

        private RectTransform _menuRoot;
        private CanvasGroup _menuGroup;
        private Text _statusText;
        private InputField _addressField;
        private RectTransform _lobbyActions;
        private RectTransform _pauseActions;

        private RectTransform _endRoot;
        private CanvasGroup _endGroup;
        private Text _endTitle;
        private Text _endBody;

        private float _fade = 1f;
        private bool _endShown;
        private bool _bossBeaten;
        private bool _paused;

        public static MainMenuUI Instance => _instance;

        private void Awake()
        {
            _instance = this;
            _manager = FindAnyObjectByType<BattleSkyNetworkManager>();
            _canvas = GetComponentInParent<Canvas>();
            _font = LoadFont();
            _pixel = CreatePixelSprite();
            port = ResolveTransportPort();
            Build();
        }

        private void OnEnable()
        {
            NetworkLevelManager.OnBossDefeated += HandleBossDefeated;
        }

        private void Start()
        {
            // The flight HUD builds its widgets in its own Awake, so make sure
            // the menu ends up on top of them once everything is built.
            if (_menuRoot != null) _menuRoot.SetAsLastSibling();
        }

        private void OnDisable()
        {
            NetworkLevelManager.OnBossDefeated -= HandleBossDefeated;
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            if (_pixel != null) Destroy(_pixel);
            if (_pixelTexture != null) Destroy(_pixelTexture);
        }

        private void Update()
        {
            bool session = NetworkClient.isConnected || NetworkServer.active;

            // Escape opens the in-game panel without dropping the session.
            if (session && !_endShown)
            {
                bool escape = UnityEngine.InputSystem.Keyboard.current != null
                    && UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame;
                if (escape) _paused = !_paused;
            }
            else
            {
                _paused = false;
            }

            // The menu owns the screen while the player is not in a session,
            // while the flight is paused, and while the mission end screen is
            // up (that one lives outside the faded group).
            float target = (session && !_paused && !_endShown) ? 0f : 1f;
            _fade = Mathf.MoveTowards(_fade, target, Time.unscaledDeltaTime * 4f);

            if (_menuGroup != null)
            {
                _menuGroup.alpha = _fade;
                _menuGroup.interactable = _fade > 0.5f;
                _menuGroup.blocksRaycasts = _fade > 0.5f;
                if (_menuRoot != null && !_menuRoot.gameObject.activeSelf)
                    _menuRoot.gameObject.SetActive(_fade > 0.001f);
            }

            // The flight hides and locks the mouse for steering, so the menu
            // has to give it back while it is open.
            if (_fade > 0.5f)
            {
                if (Cursor.lockState != CursorLockMode.None)
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
            }
            else if (session && Cursor.lockState != CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }

            // Swap the action set: lobby buttons in the menu, flight buttons
            // while the flight is paused.
            bool inFlight = session && _paused;
            if (_lobbyActions != null && _lobbyActions.gameObject.activeSelf == inFlight)
                _lobbyActions.gameObject.SetActive(!inFlight);
            if (_pauseActions != null && _pauseActions.gameObject.activeSelf != inFlight)
                _pauseActions.gameObject.SetActive(inFlight);

            if (_endGroup != null)
            {
                _endGroup.alpha = _endShown ? 1f : 0f;
                _endGroup.interactable = _endShown;
                _endGroup.blocksRaycasts = _endShown;
            }

            if (session && !_bossBeaten && !_endShown && !_paused)
            {
                // A downed crew should say so before the client disconnects.
                NetworkPlayer local = NetworkHUD.ResolveLocalPlayer();
                if (local != null && local.IsDead)
                    ShowDefeat();
            }
        }

        /// <summary>Reads the real transport port so the menu never lies.</summary>
        private int ResolveTransportPort()
        {
            var transport = _manager != null ? _manager.GetComponent<Transport>() : null;
            var portTransport = transport as PortTransport;
            if (portTransport != null && portTransport.Port > 0) return portTransport.Port;
            return port;
        }

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>Show the mission-complete overlay (called on every client).</summary>
        public void ShowVictory()
        {
            _bossBeaten = true;
            SetEndScreen("MISSION COMPLETE",
                "The Death Star is debris. The belt is clear and the crew is going home.",
                goldColor);
        }

        /// <summary>Show the crew-loss overlay.</summary>
        public void ShowDefeat()
        {
            SetEndScreen("CREW LOST",
                "Hull integrity gone. The belt keeps its own.",
                warnColor);
        }

        private void HandleBossDefeated()
        {
            ShowVictory();
        }

        private void SetEndScreen(string title, string body, Color color)
        {
            if (_endRoot == null) return;
            _endTitle.text = title;
            _endTitle.color = color;
            _endBody.text = body;
            _endShown = true;
        }

        // ------------------------------------------------------------------
        // Networking actions
        // ------------------------------------------------------------------

        public void StartHost()
        {
            if (_manager == null)
            {
                SetStatus("No network manager in the scene.", warnColor);
                return;
            }
            if (NetworkServer.active || NetworkClient.active)
            {
                // A previous session is still running (for example after the
                // crew was wiped out): tear it down before hosting again.
                _manager.StopHost();
                _manager.StopClient();
            }

            SetStatus("Hosting on port " + port + " \u00b7 waiting for a gunner\u2026", accentColor);
            _manager.StartHost();
        }

        public void StartClient()
        {
            if (_manager == null)
            {
                SetStatus("No network manager in the scene.", warnColor);
                return;
            }
            if (NetworkServer.active || NetworkClient.active)
            {
                _manager.StopHost();
                _manager.StopClient();
            }

            string address = _addressField != null ? _addressField.text : string.Empty;
            if (string.IsNullOrWhiteSpace(address))
            {
                SetStatus("Enter the host address first.", warnColor);
                return;
            }

            _manager.networkAddress = address.Trim();
            SetStatus("Connecting to " + address.Trim() + "\u2026", accentColor);
            _manager.StartClient();
        }

        public void LeaveSession()
        {
            if (_manager != null)
            {
                _manager.StopHost();
                _manager.StopClient();
                _manager.StopServer();
            }
            _endShown = false;
            _bossBeaten = false;
            _paused = false;
            SetStatus("Disconnected.", dimTextColor);
        }

        public void QuitGame()
        {
            Application.Quit();
        }

        private void SetStatus(string message, Color color)
        {
            if (_statusText == null) return;
            _statusText.text = message;
            _statusText.color = color;
        }

        // ------------------------------------------------------------------
        // UI construction
        // ------------------------------------------------------------------

        private void Build()
        {
            // --- backdrop -------------------------------------------------
            var backdropGo = new GameObject("MenuBackdrop", typeof(RectTransform));
            RectTransform backdrop = backdropGo.GetComponent<RectTransform>();
            backdrop.SetParent(transform, false);
            Stretch(backdrop);
            Image backdropImage = backdropGo.AddComponent<Image>();
            backdropImage.sprite = _pixel;
            backdropImage.color = backdropColor;
            backdropImage.raycastTarget = true;

            BuildStarfield(backdrop);
            BuildFrame(backdrop);

            // --- title -----------------------------------------------------
            var titleGo = new GameObject("TitleBlock", typeof(RectTransform));
            RectTransform titleRoot = titleGo.GetComponent<RectTransform>();
            titleRoot.SetParent(backdrop, false);
            Layout(titleRoot, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -30f), new Vector2(720f, 150f));

            Text title = CreateText(titleRoot, "Title", "BATTLESKY", titleFontSize, TextAnchor.UpperCenter);
            title.fontStyle = FontStyle.Bold;
            title.color = textColor;
            Layout(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f), Vector2.zero, new Vector2(720f, titleFontSize + 14f));

            Text subtitle = CreateText(titleRoot, "Subtitle",
                "TWO-PILOT CO-OP \u00b7 ASTEROID BELT \u00b7 DEATH STAR RUN",
                subtitleFontSize, TextAnchor.UpperCenter);
            subtitle.color = accentColor;
            Layout(subtitle.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -(titleFontSize + 16f)),
                new Vector2(720f, subtitleFontSize + 10f));

            RectTransform rule = CreateRect(titleRoot, "Rule");
            Layout(rule, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -(titleFontSize + subtitleFontSize + 30f)),
                new Vector2(320f, 1f));
            Image ruleImage = rule.gameObject.AddComponent<Image>();
            ruleImage.sprite = _pixel;
            ruleImage.color = new Color(accentColor.r, accentColor.g, accentColor.b, 0.45f);
            ruleImage.raycastTarget = false;

            // --- menu column ----------------------------------------------
            // Anchored bottom-right so it never collides with the controls
            // card in the bottom-left corner.
            var columnGo = new GameObject("MenuColumn", typeof(RectTransform));
            RectTransform column = columnGo.GetComponent<RectTransform>();
            column.SetParent(backdrop, false);
            Layout(column, new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(1f, 0f), new Vector2(-24f, columnBottomMargin),
                new Vector2(columnWidth, 236f));

            Image columnPanel = columnGo.AddComponent<Image>();
            columnPanel.sprite = _pixel;
            columnPanel.color = panelColor;
            columnPanel.raycastTarget = true;

            RectTransform accentBar = CreateRect(column, "AccentBar");
            Layout(accentBar, new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0.5f), Vector2.zero, new Vector2(3f, 0f));
            Image accentImage = accentBar.gameObject.AddComponent<Image>();
            accentImage.sprite = _pixel;
            accentImage.color = accentColor;
            accentImage.raycastTarget = false;

            // Two action sets share the column: the lobby set (host / join)
            // and the in-flight set (resume / leave).
            RectTransform mainActions = CreateRect(column, "LobbyActions");
            Layout(mainActions, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 0f), Vector2.zero, new Vector2(columnWidth, 236f));
            RectTransform pauseActions = CreateRect(column, "PauseActions");
            Layout(pauseActions, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 0f), Vector2.zero, new Vector2(columnWidth, 236f));

            BuildPauseActions(pauseActions);
            _pauseActions = pauseActions;
            _lobbyActions = mainActions;

            Button hostButton = CreateButton(mainActions, "HostButton", "HOST CREW", true, 0f, 190f);
            hostButton.onClick.AddListener(StartHost);

            Button joinButton = CreateButton(mainActions, "JoinButton", "JOIN CREW", false, 0f, 148f);
            joinButton.onClick.AddListener(StartClient);

            Text addressLabel = CreateText(mainActions, "AddressLabel", "SERVER ADDRESS", bodyFontSize - 2, TextAnchor.MiddleLeft);
            addressLabel.color = dimTextColor;
            Layout(addressLabel.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(24f, 112f),
                new Vector2(columnWidth - 48f, bodyFontSize + 4f));

            _addressField = CreateInputField(mainActions, "AddressField", "127.0.0.1");
            Layout(_addressField.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(24f, 78f), new Vector2(columnWidth - 48f, 26f));

            _statusText = CreateText(mainActions, "Status", "Ready. Host a crew or join one.", bodyFontSize, TextAnchor.MiddleLeft);
            _statusText.color = dimTextColor;
            Layout(_statusText.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(24f, 48f),
                new Vector2(columnWidth - 48f, 22f));

            Text portText = CreateText(mainActions, "Port", "TRANSPORT PORT " + port, bodyFontSize - 2, TextAnchor.MiddleLeft);
            portText.color = new Color(dimTextColor.r, dimTextColor.g, dimTextColor.b, 0.75f);
            Layout(portText.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(24f, 28f),
                new Vector2(columnWidth - 48f, 20f));

            Button quitButton = CreateButton(mainActions, "QuitButton", "QUIT", false, 0f, 0f);
            Layout(quitButton.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(24f, 0f), new Vector2(110f, 22f));
            quitButton.onClick.AddListener(QuitGame);

            // --- controls card --------------------------------------------
            var controlsGo = new GameObject("ControlsCard", typeof(RectTransform));
            RectTransform controls = controlsGo.GetComponent<RectTransform>();
            controls.SetParent(backdrop, false);
            Layout(controls, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(24f, 20f), new Vector2(440f, 98f));

            Image controlsPanel = controlsGo.AddComponent<Image>();
            controlsPanel.sprite = _pixel;
            controlsPanel.color = new Color(0.04f, 0.07f, 0.11f, 0.7f);
            controlsPanel.raycastTarget = false;

            Text controlsText = CreateText(controls, "ControlsText",
                "PILOT   W/S thrust \u00b7 A/D yaw \u00b7 mouse steer \u00b7 Q/E roll\n" +
                "            LShift boost \u00b7 X brake \u00b7 LCtrl air brake \u00b7 Alt look\n" +
                "            G flight assist \u00b7 Esc pause\n" +
                "GUNNER  mouse aim \u00b7 RMB fire \u00b7 Space shield \u00b7 R reload",
                bodyFontSize - 1, TextAnchor.UpperLeft);
            controlsText.color = new Color(textColor.r, textColor.g, textColor.b, 0.78f);
            Layout(controlsText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(16f, -10f), new Vector2(410f, 88f));

            // --- end-of-run overlay ---------------------------------------
            // The end screen is parented to the Canvas, not to the menu
            // backdrop: the backdrop is faded out during a flight, and the
            // mission result must stay readable while the crew is still
            // connected.
            BuildEndScreen((RectTransform)transform);

            // --- group that fades with the menu ---------------------------
            _menuGroup = backdropGo.AddComponent<CanvasGroup>();
            _menuGroup.alpha = 1f;
            _menuRoot = backdrop;

            // The menu must sit above the flight HUD widgets.
            backdrop.SetAsLastSibling();

            // ... and the mission result above the menu.
            if (_endRoot != null) _endRoot.SetAsLastSibling();
        }

        /// <summary>
        /// The in-flight action set: resume or leave the crew. Only shown
        /// while the flight is paused with Escape.
        /// </summary>
        private void BuildPauseActions(RectTransform parent)
        {
            Text heading = CreateText(parent, "PauseHeading", "FLIGHT PAUSED", buttonFontSize + 3, TextAnchor.MiddleLeft);
            heading.fontStyle = FontStyle.Bold;
            heading.color = accentColor;
            Layout(heading.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(24f, 196f),
                new Vector2(columnWidth - 48f, buttonFontSize + 8f));

            Text note = CreateText(parent, "PauseNote",
                "The server keeps flying. Esc resumes the flight.",
                bodyFontSize, TextAnchor.MiddleLeft);
            note.color = dimTextColor;
            Layout(note.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(24f, 166f),
                new Vector2(columnWidth - 48f, 22f));

            Button resume = CreateButton(parent, "ResumeButton", "RESUME", true, 0f, 116f);
            resume.onClick.AddListener(ResumeFlight);

            Button leave = CreateButton(parent, "LeaveButton", "LEAVE CREW", false, 0f, 74f);
            leave.onClick.AddListener(LeaveSession);

            Text portText = CreateText(parent, "PausePort", "TRANSPORT PORT " + port, bodyFontSize - 2, TextAnchor.MiddleLeft);
            portText.color = new Color(dimTextColor.r, dimTextColor.g, dimTextColor.b, 0.75f);
            Layout(portText.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(24f, 40f),
                new Vector2(columnWidth - 48f, 20f));

            parent.gameObject.SetActive(false);
        }

        /// <summary>Closes the in-flight panel and hands control back.</summary>
        public void ResumeFlight()
        {
            _paused = false;
        }

        private void BuildEndScreen(RectTransform parent)
        {
            var endGo = new GameObject("EndScreen", typeof(RectTransform));
            _endRoot = endGo.GetComponent<RectTransform>();
            _endRoot.SetParent(parent, false);
            Stretch(_endRoot);

            Image shade = endGo.AddComponent<Image>();
            shade.sprite = _pixel;
            shade.color = new Color(0.01f, 0.02f, 0.04f, 0.85f);
            shade.raycastTarget = true;

            Text headline = CreateText(_endRoot, "EndTitle", "MISSION COMPLETE", 40, TextAnchor.MiddleCenter);
            headline.fontStyle = FontStyle.Bold;
            headline.color = goldColor;
            Layout(headline.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(0f, 60f), new Vector2(640f, 56f));
            _endTitle = headline;

            _endBody = CreateText(_endRoot, "EndBody", "", bodyFontSize + 1, TextAnchor.UpperCenter);
            _endBody.color = textColor;
            Layout(_endBody.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(0f, -14f), new Vector2(600f, 60f));

            Button back = CreateButton(_endRoot, "EndBackButton", "RETURN TO MENU", false, 0f, 0f);
            Layout(back.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(0f, -50f), new Vector2(240f, 32f));
            back.onClick.AddListener(LeaveSession);

            _endGroup = endGo.AddComponent<CanvasGroup>();
            _endGroup.alpha = 0f;
            _endGroup.interactable = false;
            _endGroup.blocksRaycasts = false;
        }

        private void BuildStarfield(RectTransform parent)
        {
            var starGo = new GameObject("Starfield", typeof(RectTransform));
            RectTransform stars = starGo.GetComponent<RectTransform>();
            stars.SetParent(parent, false);
            Stretch(stars);

            var random = new System.Random(20261020);
            // Spread the stars over the whole canvas in canvas units (not
            // physical pixels), so wide displays are covered as well.
            float scale = _canvas != null ? Mathf.Max(0.01f, _canvas.scaleFactor) : 1f;
            float width = Mathf.Max(800f, Screen.width / scale);
            float height = Mathf.Max(600f, Screen.height / scale);
            for (int i = 0; i < 110; i++)
            {
                float x = (float)random.NextDouble() * width;
                float y = (float)random.NextDouble() * height;
                float size = random.NextDouble() < 0.15f ? 2f : 1f;
                float alpha = 0.12f + (float)random.NextDouble() * 0.45f;

                var dotGo = new GameObject("Star", typeof(RectTransform));
                RectTransform dot = dotGo.GetComponent<RectTransform>();
                dot.SetParent(stars, false);
                Layout(dot, new Vector2(0f, 0f), new Vector2(0f, 0f),
                    new Vector2(0.5f, 0.5f), new Vector2(x, y), new Vector2(size, size));

                Image image = dotGo.AddComponent<Image>();
                image.sprite = _pixel;
                image.color = new Color(0.75f, 0.87f, 1f, alpha);
                image.raycastTarget = false;
            }
        }

        private void BuildFrame(RectTransform parent)
        {
            var frameGo = new GameObject("Frame", typeof(RectTransform));
            RectTransform frame = frameGo.GetComponent<RectTransform>();
            frame.SetParent(parent, false);
            Stretch(frame);

            const float inset = 14f;
            const float thickness = 1f;
            const float bracket = 46f;

            CreateLine(frame, "TopLine", new Vector2(inset, 1f - inset), new Vector2(1f - inset, 1f - inset), thickness, false);
            CreateLine(frame, "BottomLine", new Vector2(inset, inset), new Vector2(1f - inset, inset), thickness, false);
            CreateLine(frame, "LeftLine", new Vector2(inset, inset), new Vector2(inset, 1f - inset), thickness, true);
            CreateLine(frame, "RightLine", new Vector2(1f - inset, inset), new Vector2(1f - inset, 1f - inset), thickness, true);

            // Corner brackets for a targeting-reticle feel.
            Color bracketColor = new Color(accentColor.r, accentColor.g, accentColor.b, 0.55f);
            CreateLine(frame, "CornerTL", new Vector2(inset, 1f - inset - bracket), new Vector2(inset + bracket, 1f - inset), 2f, false, bracketColor);
            CreateLine(frame, "CornerTR", new Vector2(1f - inset - bracket, 1f - inset), new Vector2(1f - inset, 1f - inset), 2f, false, bracketColor);
            CreateLine(frame, "CornerBL", new Vector2(inset, inset), new Vector2(inset + bracket, inset), 2f, false, bracketColor);
            CreateLine(frame, "CornerBR", new Vector2(1f - inset - bracket, inset), new Vector2(1f - inset, inset), 2f, false, bracketColor);
        }

        private void CreateLine(RectTransform parent, string name, Vector2 min, Vector2 max, float thickness, bool vertical, Color? color = null)
        {
            RectTransform rect = CreateRect(parent, name);
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            if (vertical)
                rect.sizeDelta = new Vector2(thickness, 0f);
            else
                rect.sizeDelta = new Vector2(0f, thickness);

            Image image = rect.gameObject.AddComponent<Image>();
            image.sprite = _pixel;
            image.color = color ?? new Color(accentColor.r, accentColor.g, accentColor.b, 0.2f);
            image.raycastTarget = false;
        }

        private Button CreateButton(RectTransform parent, string name, string label, bool primary, float x, float y)
        {
            var go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            Layout(rect, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(x, y), new Vector2(columnWidth - 48f, 34f));

            Image image = go.AddComponent<Image>();
            image.sprite = _pixel;
            image.color = primary ? accentColor : new Color(0.08f, 0.12f, 0.18f, 0.9f);
            image.raycastTarget = true;

            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.2f, 1.2f, 1.2f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            Text text = CreateText(rect, "Label", label, buttonFontSize, TextAnchor.MiddleCenter);
            text.fontStyle = FontStyle.Bold;
            text.color = primary ? new Color(0.02f, 0.05f, 0.09f, 1f) : accentColor;
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = Vector2.zero;
            text.rectTransform.offsetMax = Vector2.zero;
            text.raycastTarget = false;

            return button;
        }

        private InputField CreateInputField(RectTransform parent, string name, string initial)
        {
            var go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);

            Image background = go.AddComponent<Image>();
            background.sprite = _pixel;
            background.color = new Color(0.02f, 0.04f, 0.07f, 0.95f);
            background.raycastTarget = true;

            Text placeholder = CreateText(rect, "Placeholder", initial, bodyFontSize, TextAnchor.MiddleLeft);
            placeholder.color = new Color(dimTextColor.r, dimTextColor.g, dimTextColor.b, 0.55f);
            placeholder.rectTransform.anchorMin = Vector2.zero;
            placeholder.rectTransform.anchorMax = Vector2.one;
            placeholder.rectTransform.offsetMin = new Vector2(10f, 0f);
            placeholder.rectTransform.offsetMax = new Vector2(-10f, 0f);
            placeholder.raycastTarget = false;

            Text value = CreateText(rect, "Text", initial, bodyFontSize, TextAnchor.MiddleLeft);
            value.color = textColor;
            value.rectTransform.anchorMin = Vector2.zero;
            value.rectTransform.anchorMax = Vector2.one;
            value.rectTransform.offsetMin = new Vector2(10f, 0f);
            value.rectTransform.offsetMax = new Vector2(-10f, 0f);
            value.raycastTarget = false;

            InputField field = go.AddComponent<InputField>();
            field.textComponent = value;
            field.placeholder = placeholder;
            field.text = initial;
            field.lineType = InputField.LineType.SingleLine;
            field.caretColor = accentColor;
            field.customCaretColor = true;
            field.selectionColor = new Color(accentColor.r, accentColor.g, accentColor.b, 0.35f);
            return field;
        }

        private Text CreateText(RectTransform parent, string name, string content, int size, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(200f, size + 8f);

            Text text = go.AddComponent<Text>();
            text.font = _font;
            text.fontSize = size;
            text.text = content;
            text.alignment = alignment;
            text.color = textColor;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            Shadow shadow = go.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.6f);
            shadow.effectDistance = new Vector2(1f, -1f);
            return text;
        }

        private static RectTransform CreateRect(RectTransform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        private static void Layout(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 pivot, Vector2 position, Vector2 size)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

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
                font = Font.CreateDynamicFontFromOSFont("Arial", 14);
            return font;
        }

        private Sprite CreatePixelSprite()
        {
            _pixelTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                name = "BattleSkyUiPixel",
                hideFlags = HideFlags.HideAndDontSave,
            };
            _pixelTexture.SetPixel(0, 0, Color.white);
            _pixelTexture.Apply();
            Sprite sprite = Sprite.Create(_pixelTexture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = "BattleSkyUiPixel";
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }
    }
}
