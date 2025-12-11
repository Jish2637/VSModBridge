using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR;
using VS.ModBridge;

namespace VS.ModBridgeMenu
{
    /// <summary>
    /// Camera-attached menu shell that behaves like the ObjectDropper menu,
    /// using direct XR input (same pattern as VRInput) for interaction.
    /// </summary>
    public class VSMenuAPI : MonoBehaviour
    {
        // ---------------- Singleton ----------------

        public static VSMenuAPI Instance { get; private set; }

        // ---------------- Camera attach ----------------

        [Header("Camera attach")]
        public Vector3 CamLocalPos = new Vector3(-0.35f, -0.22f, 1.20f);
        public float CamScale = 0.0005f;
        public Vector3 CamLocalEuler = Vector3.zero;

        // ---------------- UI state ----------------

        Canvas _canvas;
        CanvasGroup _cgContent;
        RectTransform _rt;
        RectTransform _listRoot;
        RectTransform _viewport;
        ScrollRect _scroll;
        VerticalLayoutGroup _vlg;
        RectTransform _selector;
        Text _title;

        GameObject _modalGO;
        bool _menuWasEnabledBeforeModal;

        readonly List<Button> _items = new List<Button>();

        readonly Color _idle = new Color(1f, 1f, 1f, 0.15f);
        readonly Color _focused = new Color(0.25f, 0.55f, 1f, 0.45f);
        readonly Color _panelCol = new Color(0.06f, 0.08f, 0.11f, 0.94f);
        readonly Color _headerCol = new Color(0.13f, 0.17f, 0.24f, 0.96f);
        readonly Color _accentCol = new Color(0.25f, 0.55f, 1f, 0.88f);
        readonly Color _btnIdleCol = new Color(1f, 1f, 1f, 0.06f);
        readonly Color _btnTextCol = new Color(0.95f, 0.97f, 1f, 0.95f);

        float _rowHeight = 64f;
        float _rowSpacing = 8f;
        float _selectorLerp = 1f;
        Vector2 _selectorTarget;

        int _sel = 0;

        float _navCooldown = 0f;
        const float FIRST_REPEAT_DELAY = 0.35f;
        const float REPEAT_DELAY = 0.20f;
        bool _stickWasNeutral = true;

        bool _built;
        float _ignoreSubmitUntil = 0f;

        string _currentPageKey;
        readonly Dictionary<string, int> _lastIndexByPageKey = new Dictionary<string, int>();
        Action _backShortcut;

        public static VSMenuAPI Ensure()
        {
            if (Instance != null)
                return Instance;

            var go = new GameObject("VSMenuAPI");
            return go.AddComponent<VSMenuAPI>();
        }


        // =========================================================
        // MOD REGISTRATION / HUB
        // =========================================================

        class RegisteredMenu
        {
            public string Id;
            public string Label;
            public Action ShowFunc;
        }

        static readonly List<RegisteredMenu> _registeredMenus = new List<RegisteredMenu>();

        const string HUB_PAGE_KEY = "VSMainHub";

        /// <summary>
        /// Register a menu entry in the central VS menu hub.
        /// id must be unique per mod; later registrations with the same id overwrite the earlier one.
        /// </summary>
        public static void RegisterMenu(string id, string label, Action showFunc)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("id must not be null or empty", nameof(id));
            if (string.IsNullOrEmpty(label)) throw new ArgumentException("label must not be null or empty", nameof(label));

            // Remove any existing with same id
            _registeredMenus.RemoveAll(m => m.Id == id);

            _registeredMenus.Add(new RegisteredMenu
            {
                Id = id,
                Label = label,
                ShowFunc = showFunc
            });

            Debug.Log($"[VSMenuAPI] Registered menu '{id}' as '{label}'.");
        }

        /// <summary>
        /// Unregister a previously registered menu (e.g. when mod is unloaded).
        /// </summary>
        public static void UnregisterMenu(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            _registeredMenus.RemoveAll(m => m.Id == id);
        }

        /// <summary>
        /// Show the global hub menu listing all registered mod menus.
        /// </summary>
        public static void ShowMainMenuHub()
        {
            Instance?.ShowHubInternal();
        }

        /// <summary>
        /// Internal hub builder: shows a list of all registered menus.
        /// </summary>
        void ShowHubInternal()
        {
            BuildIfNeeded();

            var entries = new List<(string label, Action onClick)>();

            if (_registeredMenus.Count == 0)
            {
                // No menus registered – just show an info item and a Close
                entries.Add(("No mods have registered a menu.", null));
            }
            else
            {
                foreach (var m in _registeredMenus)
                {
                    var menu = m; // local copy for closure
                    entries.Add((menu.Label, () =>
                    {
                        try
                        {
                            menu.ShowFunc?.Invoke();
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[VSMenuAPI] Error invoking menu '{menu.Id}': {ex}");
                            // If something fails, go back to hub so player isn't stuck
                            ShowHubInternal();
                        }
                    }
                    ));
                }
            }

            // Always add a close entry at the bottom
            entries.Add(("Close", () => Hide()));

            ShowPage(
                pageKey: HUB_PAGE_KEY,
                title: "Mods",
                entries: entries,
                explicitInitialIndex: null,
                onBackShortcut: () => Hide());
        }



        // =========================================================
        // XR INPUT – MIRRORING VRInput
        // =========================================================

        // XR devices
        InputDevice _left;
        InputDevice _right;
        readonly List<InputDevice> _devBuf = new List<InputDevice>();

        public static bool DominantRightHand = true;

        // Confirm / Cancel edges
        static bool _prevConfirm;
        static bool _prevCancel;

        // Menu gesture: hold dominant trigger + double-click dominant stick
        static float _menuLastClickTime;
        static int _menuClickCount;
        const float MENU_DOUBLE_CLICK_WINDOW = 0.35f;
        static bool _prevDomStickClick;

        void RefreshDevices()
        {
            _devBuf.Clear();
            InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, _devBuf);
            _left = _devBuf.Count > 0 ? _devBuf[0] : new InputDevice();

            _devBuf.Clear();
            InputDevices.GetDevicesAtXRNode(XRNode.RightHand, _devBuf);
            _right = _devBuf.Count > 0 ? _devBuf[0] : new InputDevice();
        }

        static InputDevice DominantDev()
        {
            if (Instance == null) return new InputDevice();
            return DominantRightHand ? Instance._right : Instance._left;
        }

        // Confirm: trigger or primary button on dominant hand (edge)
        static bool ReadConfirmRaw()
        {
            var dev = DominantDev();
            if (!dev.isValid) return UpdateAndReturnEdge(ref _prevConfirm, false);

            bool b;
            bool cur =
                (dev.TryGetFeatureValue(CommonUsages.triggerButton, out b) && b) ||
                (dev.TryGetFeatureValue(CommonUsages.primaryButton, out b) && b);

            return UpdateAndReturnEdge(ref _prevConfirm, cur);
        }

        // Cancel/back: grip or secondary button on dominant hand (edge)
        static bool ReadCancelRaw()
        {
            var dev = DominantDev();
            if (!dev.isValid) return UpdateAndReturnEdge(ref _prevCancel, false);

            bool b;
            bool cur =
                (dev.TryGetFeatureValue(CommonUsages.gripButton, out b) && b) ||
                (dev.TryGetFeatureValue(CommonUsages.secondaryButton, out b) && b);

            return UpdateAndReturnEdge(ref _prevCancel, cur);
        }

        static bool UpdateAndReturnEdge(ref bool prev, bool cur)
        {
            bool edge = cur && !prev;
            prev = cur;
            return edge;
        }

        public static bool ConfirmPressedThisFrame() => ReadConfirmRaw();
        public static bool CancelPressedThisFrame() => ReadCancelRaw();

        // Stick
        public static Vector2 Stick()
        {
            var dev = DominantDev(); Vector2 v;
            if (dev.isValid && dev.TryGetFeatureValue(CommonUsages.primary2DAxis, out v))
                return v;

            return Vector2.zero;
        }

        static bool DominantTriggerHeld()
        {
            var dev = DominantDev(); bool v;
            return dev.isValid && dev.TryGetFeatureValue(CommonUsages.triggerButton, out v) && v;
        }

        static bool DominantStickClick(bool edgeOnly, out bool isDownRaw)
        {
            var dev = DominantDev(); bool v = false;
            if (dev.isValid)
            {
                if (!dev.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out v))
                    dev.TryGetFeatureValue(CommonUsages.secondary2DAxisClick, out v);
            }

            isDownRaw = v;
            bool edge = v && !_prevDomStickClick;
            _prevDomStickClick = v;
            return edgeOnly ? edge : v;
        }

        public static bool MenuGestureDoubleClickThisFrame()
        {
            bool rawHeld;
            bool stickEdge = DominantStickClick(edgeOnly: true, out rawHeld);

            if (!DominantTriggerHeld())
            {
                _menuClickCount = 0;
                return false;
            }

            if (stickEdge)
            {
                float t = Time.unscaledTime;
                if (t - _menuLastClickTime <= MENU_DOUBLE_CLICK_WINDOW)
                    _menuClickCount++;
                else
                    _menuClickCount = 1;

                _menuLastClickTime = t;

                if (_menuClickCount >= 2)
                {
                    _menuClickCount = 0;
                    return true;
                }
            }

            if (_menuClickCount > 0 &&
                (Time.unscaledTime - _menuLastClickTime) > MENU_DOUBLE_CLICK_WINDOW)
            {
                _menuClickCount = 0;
            }

            return false;
        }

        public static bool MenuTogglePressedThisFrame()
        {
            if (MenuGestureDoubleClickThisFrame())
                return true;

            return false;
        }

        // =========================================================
        // LIFECYCLE
        // =========================================================

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            Debug.Log("[VSMenuAPI] Awake – instance created");

            RefreshDevices();
            InvokeRepeating(nameof(RefreshDevices), 0f, 1.0f);
        }

        void Start()
        {
            Debug.Log("[VSMenuAPI] Start – building UI");
            BuildIfNeeded();
            Hide();
        }

        void Update()
        {
            // Attach to camera (only mode we care about)
            var cam = Camera.main ? Camera.main.transform : null;
            if (cam && _rt)
            {
                _rt.SetParent(cam, false);
                _rt.localPosition = CamLocalPos;
                _rt.localRotation = Quaternion.Euler(CamLocalEuler);
                _rt.localScale = Vector3.one * CamScale;
            }

            // keep confirm/cancel XR edges in sync, like VRInput.Update()
            var _ = ReadConfirmRaw();
            _ = ReadCancelRaw();
            // IMPORTANT: do NOT call DominantStickClick here, or you will
            // consume the rising edge before the gesture code sees it.
            // bool __; DominantStickClick(edgeOnly: false, out __);

            // Global toggle (menu gesture): open/close the central hub
            if (MenuTogglePressedThisFrame())
            {
                Debug.Log("[VSMenuAPI] Menu gesture detected");
                if (_canvas && _canvas.enabled)
                {
                    Hide();
                }
                else
                {
                    ShowHubInternal();
                }
            }

            // If UI hidden and no modal, skip
            if ((_canvas == null || !_canvas.enabled) && !_modalGO) return;

            // Modal active: only confirm/back
            if (_modalGO)
            {
                HandleBackShortcut();
                HandleModalConfirm();
                return;
            }

            // Animate selector
            if (_selector)
            {
                _selectorLerp = Mathf.Clamp01(_selectorLerp + Time.unscaledTime * 10f);
                var cur = _selector.anchoredPosition;
                var next = Vector2.Lerp(cur, _selectorTarget, _selectorLerp);
                _selector.anchoredPosition = next;
            }

            HandleStickNavigation();
            HandleConfirm();
            HandleBackShortcut();
        }

        // =========================================================
        // PUBLIC MENU API
        // =========================================================

        public void Show()
        {
            if (_canvas) _canvas.enabled = true;

            _sel = Mathf.Clamp(_sel, 0, Mathf.Max(0, _items.Count - 1));
            _navCooldown = 0f;
            _stickWasNeutral = true;
            _ignoreSubmitUntil = 0f;
        }

        public void Hide()
        {
            if (_canvas) _canvas.enabled = false;
            CloseModal();
        }

        /// <summary>
        /// Show a page of menu entries.
        /// </summary>
        public void ShowPage(
            string pageKey,
            string title,
            IList<(string label, Action onClick)> entries,
            int? explicitInitialIndex = null,
            Action onBackShortcut = null)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            BuildIfNeeded();

            _currentPageKey = pageKey;
            _backShortcut = onBackShortcut;
            if (_title) _title.text = title ?? string.Empty;

            int initial;
            if (explicitInitialIndex.HasValue)
            {
                initial = Mathf.Clamp(explicitInitialIndex.Value, 0, Mathf.Max(0, entries.Count - 1));
            }
            else if (!string.IsNullOrEmpty(pageKey) &&
                     _lastIndexByPageKey.TryGetValue(pageKey, out var saved))
            {
                initial = Mathf.Clamp(saved, 0, Mathf.Max(0, entries.Count - 1));
            }
            else
            {
                initial = Mathf.Clamp(_sel, 0, Mathf.Max(0, entries.Count - 1));
            }

            RebuildList(entries, initial);
            Show();
        }

        public int CurrentIndex => _sel;

        public void RememberCurrentIndex(string pageKey)
        {
            if (string.IsNullOrEmpty(pageKey)) return;
            _lastIndexByPageKey[pageKey] =
                Mathf.Clamp(_sel, 0, Mathf.Max(0, _items.Count - 1));
        }

        // =========================================================
        // INPUT HANDLING (MENU SIDE)
        // =========================================================

        void HandleModalConfirm()
        {
            if (Time.unscaledTime < _ignoreSubmitUntil) return;
            if (ConfirmPressedThisFrame())
                CloseModal();
        }

        void HandleStickNavigation()
        {
            var stick = Stick();
            float y = stick.y;
            const float DEAD = 0.55f;

            _navCooldown = Mathf.Max(0f, _navCooldown - Time.unscaledDeltaTime);

            bool neutral = Mathf.Abs(y) < DEAD;
            if (neutral)
            {
                _stickWasNeutral = true;
                return;
            }

            if (_stickWasNeutral)
            {
                _stickWasNeutral = false;
                MoveSelection(y > 0f ? -1 : +1);
                _navCooldown = FIRST_REPEAT_DELAY;
                return;
            }

            if (_navCooldown <= 0f)
            {
                MoveSelection(y > 0f ? -1 : +1);
                _navCooldown = REPEAT_DELAY;
            }
        }

        void HandleConfirm()
        {
            if (Time.unscaledTime < _ignoreSubmitUntil) return;
            if (!ConfirmPressedThisFrame()) return;

            if (_items.Count == 0) return;
            var btn = _items[_sel];
            if (btn && btn.interactable)
                btn.onClick.Invoke();

            if (!string.IsNullOrEmpty(_currentPageKey))
                _lastIndexByPageKey[_currentPageKey] = _sel;
        }

        void HandleBackShortcut()
        {
            if (Time.unscaledTime < _ignoreSubmitUntil) return;
            if (!CancelPressedThisFrame()) return;

            if (_modalGO)
            {
                CloseModal();
                return;
            }

            if (_backShortcut != null)
                _backShortcut();
            else
                Hide();
        }

        void MoveSelection(int delta)
        {
            if (_items.Count == 0) return;

            _sel = (_sel + delta) % _items.Count;
            if (_sel < 0) _sel += _items.Count;

            ApplyFocusVisuals();
            SnapSelectorToCurrent();
        }

        void ApplyFocusVisuals()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                var img = _items[i].GetComponent<Image>();
                if (!img) continue;
                img.color = (i == _sel) ? _focused : _idle;
            }
        }

        // =========================================================
        // UI BUILD / LAYOUT
        // =========================================================

        void BuildIfNeeded()
        {
            if (_built) return;

            Debug.Log("[VSMenuAPI] BuildIfNeeded – creating world-space canvas");

            var root = new GameObject("VSMenuCanvas");
            DontDestroyOnLoad(root);

            _canvas = root.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.sortingOrder = 2000;
            _canvas.worldCamera = Camera.main;

            root.AddComponent<GraphicRaycaster>();
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            if (FindObjectOfType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
                DontDestroyOnLoad(es);
            }

            _rt = root.GetComponent<RectTransform>();
            _rt.sizeDelta = new Vector2(720, 540);

            // Panel
            var panelGO = new GameObject("Panel");
            panelGO.transform.SetParent(_rt, false);
            var panel = panelGO.AddComponent<Image>(); panel.color = _panelCol;
            var prt = panel.rectTransform;
            prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
            prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;

            var outline = panelGO.AddComponent<Outline>(); outline.effectColor = new Color(1, 1, 1, 0.05f);
            var shadow = panelGO.AddComponent<Shadow>(); shadow.effectColor = new Color(0, 0, 0, 0.5f);
            shadow.effectDistance = new Vector2(0f, -2f);

            // Header
            var headerGO = new GameObject("Header");
            headerGO.transform.SetParent(panelGO.transform, false);
            var headerImg = headerGO.AddComponent<Image>(); headerImg.color = _headerCol;
            var hrt = headerImg.rectTransform;
            hrt.anchorMin = new Vector2(0f, 1f); hrt.anchorMax = new Vector2(1f, 1f);
            hrt.pivot = new Vector2(0.5f, 1f);
            hrt.sizeDelta = new Vector2(0f, 72f);
            hrt.anchoredPosition = Vector2.zero;

            var titleGO = new GameObject("Title");
            titleGO.transform.SetParent(headerGO.transform, false);
            _title = titleGO.AddComponent<Text>();
            _title.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _title.fontSize = 32; _title.color = Color.white;
            _title.alignment = TextAnchor.MiddleCenter;
            var trt = _title.rectTransform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
            titleGO.AddComponent<Shadow>().effectColor = new Color(0, 0, 0, 0.6f);

            // Scroll rect
            var scrollGO = new GameObject("Scroll");
            scrollGO.transform.SetParent(panelGO.transform, false);
            _scroll = scrollGO.AddComponent<ScrollRect>();
            _scroll.horizontal = false;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            var srt = scrollGO.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(0f, 0f); srt.anchorMax = new Vector2(1f, 1f);
            srt.offsetMin = new Vector2(18f, 18f);
            srt.offsetMax = new Vector2(-18f, -90f);

            // Viewport
            var vpGO = new GameObject("Viewport");
            vpGO.transform.SetParent(scrollGO.transform, false);
            _viewport = vpGO.AddComponent<RectTransform>();
            _viewport.anchorMin = Vector2.zero;
            _viewport.anchorMax = Vector2.one;
            _viewport.offsetMin = Vector2.zero;
            _viewport.offsetMax = Vector2.zero;
            vpGO.AddComponent<RectMask2D>();
            _scroll.viewport = _viewport;

            // Content
            var contentGO = new GameObject("Content");
            contentGO.transform.SetParent(_viewport, false);
            _listRoot = contentGO.AddComponent<RectTransform>();
            _listRoot.anchorMin = new Vector2(0f, 1f);
            _listRoot.anchorMax = new Vector2(1f, 1f);
            _listRoot.pivot = new Vector2(0f, 1f);
            _listRoot.anchoredPosition = Vector2.zero;
            _listRoot.sizeDelta = new Vector2(0f, 0f);

            _vlg = contentGO.AddComponent<VerticalLayoutGroup>();
            _vlg.childControlHeight = true;
            _vlg.childForceExpandHeight = false;
            _vlg.childControlWidth = true;
            _vlg.childForceExpandWidth = true;
            _vlg.childAlignment = TextAnchor.UpperLeft;
            _vlg.spacing = _rowSpacing;
            _vlg.padding = new RectOffset(8, 8, 8, 8);

            contentGO.AddComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;
            _scroll.content = _listRoot;

            _cgContent = contentGO.AddComponent<CanvasGroup>();
            _cgContent.alpha = 1f;
            _cgContent.interactable = true;
            _cgContent.blocksRaycasts = true;

            // Selection bar
            var selGO = new GameObject("SelectionBar");
            selGO.transform.SetParent(_listRoot, false);
            _selector = selGO.AddComponent<RectTransform>();
            _selector.anchorMin = new Vector2(0f, 1f);
            _selector.anchorMax = new Vector2(1f, 1f);
            _selector.pivot = new Vector2(0.5f, 1f);
            _selector.sizeDelta = new Vector2(0f, _rowHeight);
            var selImg = selGO.AddComponent<Image>();
            selImg.color = new Color(_accentCol.r, _accentCol.g, _accentCol.b, 0.16f);
            selGO.AddComponent<LayoutElement>().ignoreLayout = true;
            _selector.SetAsFirstSibling();

            _built = true;
        }

        void RebuildList(IEnumerable<(string label, Action onClick)> entries, int initialIndex)
        {
            if (_cgContent)
            {
                _cgContent.alpha = 0f;
                _cgContent.blocksRaycasts = false;
                _cgContent.interactable = false;
            }

            foreach (var b in _items)
                if (b) Destroy(b.gameObject);
            _items.Clear();

            int idx = 0;
            foreach (var e in entries)
                _items.Add(CreateButton(_listRoot, e.label, e.onClick, idx++));

            _sel = Mathf.Clamp(initialIndex, 0, Math.Max(0, _items.Count - 1));
            ApplyFocusVisuals();

            LayoutRebuilder.ForceRebuildLayoutImmediate(_listRoot);
            SnapSelectorToCurrent(instant: true);

            if (_cgContent)
            {
                _cgContent.alpha = 1f;
                _cgContent.blocksRaycasts = true;
                _cgContent.interactable = true;
            }
        }

        Button CreateButton(Transform parent, string label, Action onClick, int idx)
        {
            var rowGO = new GameObject($"Row_{idx}_{label}");
            rowGO.transform.SetParent(parent, false);

            var rowRT = rowGO.AddComponent<RectTransform>();
            rowRT.anchorMin = new Vector2(0f, 1f);
            rowRT.anchorMax = new Vector2(1f, 1f);
            rowRT.pivot = new Vector2(0f, 1f);
            rowRT.sizeDelta = new Vector2(0f, _rowHeight);

            var img = rowGO.AddComponent<Image>(); img.color = _btnIdleCol;

            var le = rowGO.AddComponent<LayoutElement>();
            le.preferredHeight = _rowHeight;
            le.minHeight = _rowHeight;

            var btn = rowGO.AddComponent<Button>();
            var cb = btn.colors;
            cb.normalColor = img.color;
            cb.highlightedColor = new Color(1, 1, 1, 0.12f);
            cb.pressedColor = new Color(1, 1, 1, 0.18f);
            cb.selectedColor = cb.highlightedColor;
            cb.colorMultiplier = 1f;
            btn.colors = cb;

            var txtGO = new GameObject("Text");
            txtGO.transform.SetParent(rowGO.transform, false);
            var txt = txtGO.AddComponent<Text>();
            txt.text = label;
            txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            txt.alignment = TextAnchor.MiddleLeft;
            txt.color = _btnTextCol;
            txt.fontSize = 28;
            var tr = txt.rectTransform;
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = new Vector2(18f, 0f);
            tr.offsetMax = new Vector2(-18f, 0f);

            if (onClick != null)
                btn.onClick.AddListener(() => onClick());
            btn.name = label;

            var divider = new GameObject("Divider");
            divider.transform.SetParent(rowGO.transform, false);
            var dv = divider.AddComponent<Image>(); dv.color = new Color(1, 1, 1, 0.05f);
            var drt = dv.rectTransform;
            drt.anchorMin = new Vector2(0f, 0f);
            drt.anchorMax = new Vector2(1f, 0f);
            drt.sizeDelta = new Vector2(0f, 1f);
            drt.anchoredPosition = Vector2.zero;

            return btn;
        }

        void SnapSelectorToCurrent(bool instant = false)
        {
            if (_items.Count == 0 || _sel < 0 || _sel >= _items.Count || !_selector) return;

            float rowH = _rowHeight;
            float gap = _vlg ? _vlg.spacing : 0f;
            float padTop = _vlg ? _vlg.padding.top : 0f;

            float yTop = -(padTop + _sel * (rowH + gap));
            Vector2 pos = new Vector2(0f, yTop);

            _selector.sizeDelta = new Vector2(0f, rowH);

            if (instant)
            {
                _selector.anchoredPosition = pos;
                _selectorTarget = pos;
                _selectorLerp = 1f;
            }
            else
            {
                _selectorTarget = pos;
                _selectorLerp = 0f;
            }

            EnsureSelectedVisible();
        }

        void EnsureSelectedVisible()
        {
            if (!_viewport || !_scroll || !_scroll.content || _items.Count == 0) return;

            Canvas.ForceUpdateCanvases();

            float viewH = _viewport.rect.height;
            float contentH = _scroll.content.rect.height;
            if (contentH <= viewH) return;

            float rowH = _rowHeight;
            float gap = _vlg ? _vlg.spacing : 0f;
            float padTop = _vlg ? _vlg.padding.top : 0f;

            float yTop = padTop + _sel * (rowH + gap);
            float yBottom = yTop + rowH;

            float curY = _scroll.content.anchoredPosition.y;
            float viewTop = curY;
            float viewBottom = curY + viewH;

            float targetY = curY;

            if (yTop < viewTop)
                targetY = yTop;
            else if (yBottom > viewBottom)
                targetY = yBottom - viewH;

            targetY = Mathf.Clamp(targetY, 0f, Mathf.Max(0f, contentH - viewH));

            _scroll.content.anchoredPosition =
                new Vector2(_scroll.content.anchoredPosition.x, targetY);
        }

        // ---------------- Modal helpers (minimal) ----------------

        public void CloseModal()
        {
            if (_modalGO)
            {
                Destroy(_modalGO);
                _modalGO = null;
            }

            if (_canvas && _menuWasEnabledBeforeModal)
                _canvas.enabled = true;

            _ignoreSubmitUntil = Time.unscaledTime + 0.15f;
        }

    }

}
