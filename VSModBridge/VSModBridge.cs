// VSModBridge.cs (ultra-verbose)
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.XR;
//using VS.ModBridgeMenu;

[assembly: MelonInfo(typeof(VS.ModBridge.MelonEntry), "VSModBridge", "1.0.0", "Josh2367")]
// [assembly: MelonGame("FlipAxisStudios", "VirtualSkate")]

namespace VS.ModBridge
{

    public class MelonEntry : MelonMod
    {
        static MelonLogger.Instance _log = new MelonLogger.Instance("VSModBridge");

        public override void OnInitializeMelon()
        {
            _log.Msg("[VSModBridge] OnInitializeMelon");
            VSBridge.Ensure();
            //VSMenuAPI.Ensure();
        }

        public override void OnLateInitializeMelon()
        {
            _log.Msg("[VSModBridge] OnLateInitializeMelon");
            VSBridge.Ensure();
            //VSMenuAPI.Ensure();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            _log.Msg($"[VSModBridge] Loaded: idx={buildIndex} name={sceneName}");
            VSBridge.Ensure();
            //VSMenuAPI.Ensure();
        }

    }

    // === PUBLIC CONTRACTS (unchanged) ===
    public enum VSPlayerState { Unknown = 0, OffBoard = 1, Riding = 2, Bailed = 3 }
    [Serializable] public struct PoseData { public Vector3 position; public Quaternion rotation; public static PoseData From(Transform t) { if (!t) return default; return new PoseData { position = t.position, rotation = t.rotation }; } }

    [Serializable]
    public struct PlayerFlags
    {
        public bool IsGrinding;
        public bool IsManual;
        public bool IsSwitch;
    }

    [Serializable]
    public struct ButtonState
    {
        // Digital face buttons
        public bool A, B, X, Y;

        // Digital “grasp” abstraction
        public bool GraspLeft, GraspRight;

        // Raw analogs
        public float GripLeft, GripRight;
        public float TriggerLeft, TriggerRight;

        public bool StickClickLeft, StickClickRight;

        // Game-defined thresholds (mirrored)
        public float PinchThreshold;
        public float GraspThreshold;
        public float TriggerGripThreshold;

        // Platform metadata
        public string Platform;
        public bool IsQuest;
    }

    [Serializable]
    public struct BoardTargets
    {
        public PoseData RawTarget, Target, TargetOffset, TargetCorrection;
        public PoseData PopRotation, ShuvRotation, FinalTarget;
        public PoseData HeightTarget, HeightTargetLerped;
        public PoseData PivotAnimParent, PivotAnimTarget, PivotAnimPosParent, PivotAnimPosTarget;
        public PoseData BackTruckPivot, FrontTruckPivot;
        public PoseData FlipRotationParent, FlipRotationTarget, ShuvAxisTarget;
    }

    // High-level replay view mode, derived from ReplayManager fields.
    public enum VSReplayViewMode
    {
        None = 0,          // Not in replay or view not meaningful
        HeadsetFollow = 1, // Controller/headset view following the skater
        ReplayCamera = 2,  // Free replay camera view
    }

    [Serializable]
    public struct ReplayInfo
    {
        // Raw fields taken from ReplayManager
        public bool IsReplayMode;                   // replayMode
        public bool IsRunningOrExiting;             // replayModeIsRunningOrIsExiting
        public bool ShowCamera;                     // showCamera
        public bool PauseReplayPlaybackMenuOpen;    // pauseReplayPlaybackMenuOpen

        // Derived “friendly” view mode
        public VSReplayViewMode ViewMode;

        // Optional raw text field if ReplayManager exposes a state string
        public string RawReplayState;
    }

    [Serializable]
    public struct ReplayRig
    {
        public bool IsValid;

        public PoseData ControllerRoot;   // replayController
        public PoseData Headset;          // headset
        public PoseData LeftController;   // leftController
        public PoseData RightController;  // rightController
        public PoseData TrackingSpace;    // trackingSpace
    }

    [Serializable]
    public struct VSSnapshot
    {
        // optional but recommended
        public int ApiVersion;

        public double Timestamp;
        public int Frame;
        public VSPlayerState PlayerState;

        public PoseData Headset, LeftHand, RightHand, LeftHandTarget, RightHandTarget;
        public PoseData LeftController, RightController, LeftControllerCorrected, RightControllerCorrected;
        public PoseData LeftControllerOffset, RightControllerOffset;
        public PoseData TrackingSpace;
        public BoardTargets Board;
        public ButtonState Buttons;

        public PlayerFlags Flags;

        public ReplayInfo Replay;
        public ReplayRig ReplayRig;

    }

    public delegate void SnapshotHandler(in VSSnapshot snapshot);
    public delegate void PlayerStateChangedHandler(VSPlayerState prev, VSPlayerState next);

    public class VSBridge : MonoBehaviour
    {
        // Public contract version for consumer mods
        public const int ApiVersion = 1;

        public static VSBridge Instance { get; private set; }
        public static event SnapshotHandler OnSnapshot;
        public static event PlayerStateChangedHandler OnPlayerStateChanged;

        [Header("Sampling")][Range(1, 120)] public int sampleHz = 5;
        public float posEpsilon = 0.0025f, rotEpsilon = 1.0f;

        [Tooltip("Write to Mods/VSBridge.log")] public bool fileLog = false;
        [Tooltip("Spam the Melon console")] public bool consoleLog = false;

        // control log growth
        [Tooltip("Clear Mods/VSBridge.log on startup")] public bool clearFileOnStart = true;
        [Tooltip("Enable verbose startup probes (reflection dumps, resolve spam)")] public bool verboseProbeLogs = false;
        [Tooltip("Enable verbose per-tick logs (state/build/no-emit/switch debug)")] public bool verboseTickLogs = false;

        string _logPath;
        float _nextTick;
        VSSnapshot _last;
        bool _hasLast;
        VSPlayerState _lastState = VSPlayerState.Unknown;

        object _inputMgr;
        object _playerMgr;
        object _replayMgr;

        readonly Dictionary<string, Transform> _tfCache = new Dictionary<string, Transform>(128);
        static MelonLogger.Instance _log = new MelonLogger.Instance("VSModBridge");

        // cached types
        System.Type _cachedImType, _cachedPmType, _cachedRmType;

        // cached ReplayManager field infos (resolved when we first find ReplayManager)
        FieldInfo _rmReplayMode;
        FieldInfo _rmReplayModeRunning;
        FieldInfo _rmShowCamera;
        FieldInfo _rmPauseReplayPlaybackMenuOpen;
        FieldInfo _rmCurrentState; // optional string, if present

        FieldInfo _rmReplayControllerTf;
        FieldInfo _rmHeadsetTf;
        FieldInfo _rmLeftControllerTf;
        FieldInfo _rmRightControllerTf;
        FieldInfo _rmTrackingSpaceTf;

        FieldInfo _rmLeftHandRenderer;
        FieldInfo _rmRightHandRenderer;

        void Awake()
        {
            if (Instance && Instance != this) { Destroy(gameObject); return; }
            Instance = this; DontDestroyOnLoad(gameObject);

            _logPath = System.IO.Path.Combine(Application.persistentDataPath, "Mods", "VSBridge.log");

            // New: start each run with a clean log file (prevents multi-session growth)
            if (fileLog && clearFileOnStart)
            {
                try
                {
                    var dir = System.IO.Path.GetDirectoryName(_logPath);
                    if (!string.IsNullOrEmpty(dir))
                        System.IO.Directory.CreateDirectory(dir);

                    System.IO.File.WriteAllText(_logPath, string.Empty);
                }
                catch { }
            }

            Log($"[Awake] Unity {Application.unityVersion} dataPath={Application.dataPath} persistent={Application.persistentDataPath}");
        }


        void OnEnable()
        {
            Log("[OnEnable] starting resolve loop");
            StartCoroutine(ResolveLoop());

            // New: only dump InputManager fields when you explicitly want the probe spam
            if (verboseProbeLogs)
                DebugListInputManagerFields();

            _nextTick = 0f; _hasLast = false;
        }

        System.Collections.IEnumerator ResolveLoop()
        {
            // give scene a moment to spawn managers
            yield return new WaitForEndOfFrame();
            int tries = 0;

            while ((_inputMgr == null || _playerMgr == null) && tries < 120) // ~60s @ 0.5s
            {
                TryResolveGameRefs();
                if (_inputMgr != null && _playerMgr != null) break;
                tries++;
                if ((tries % 4) == 0) Log($"[Resolve] still waiting... tries={tries}");
                yield return new WaitForSeconds(0.5f);
            }

            Log($"[Resolve] done InputManager={(_inputMgr != null)} PlayerManager={(_playerMgr != null)}");
        }

        void Update()
        {
            if (_inputMgr == null || _playerMgr == null)
                return;

            float now = Time.realtimeSinceStartup;
            if (now < _nextTick) return;
            _nextTick = now + 1f / Mathf.Max(1, sampleHz);

            var snap = BuildSnapshot();
            var reason = ShouldEmitReason(_last, snap);
            if (!_hasLast || reason != EmitReason.None)
            {
                EmitSnapshot(ref snap, reason);
                _last = snap; _hasLast = true;
            }
            else
            {
                if (verboseTickLogs)
                    LogOncePer("no-emit", 2f, "[Tick] no emit (no movement/no edges)");
            }
        }

        enum EmitReason { None, StateChange, PoseMoved, ButtonEdge }

        EmitReason ShouldEmitReason(in VSSnapshot prev, in VSSnapshot cur)
        {
            if (!_hasLast) return EmitReason.PoseMoved;

            // Suppress transitions INTO Unknown (treat as "no state change")
            var prevState = prev.PlayerState;
            var curState = cur.PlayerState;
            if (prevState != curState && curState != VSPlayerState.Unknown)
                return EmitReason.StateChange;

            // Pose movement
            if (Moved(prev.Headset, cur.Headset) ||
                Moved(prev.LeftHand, cur.LeftHand) ||
                Moved(prev.RightHand, cur.RightHand) ||
                Moved(prev.LeftControllerCorrected, cur.LeftControllerCorrected) ||
                Moved(prev.RightControllerCorrected, cur.RightControllerCorrected))
                return EmitReason.PoseMoved;

            // Button edge
            if (prev.Buttons.A != cur.Buttons.A || prev.Buttons.B != cur.Buttons.B ||
                prev.Buttons.X != cur.Buttons.X || prev.Buttons.Y != cur.Buttons.Y ||
                prev.Buttons.GraspLeft != cur.Buttons.GraspLeft || prev.Buttons.GraspRight != cur.Buttons.GraspRight)
                return EmitReason.ButtonEdge;

            return EmitReason.None;
        }

        void EmitSnapshot(ref VSSnapshot snap, EmitReason reason)
        {
            // Stabilize player-state: ignore "Unknown" as a real transition
            if (_lastState != snap.PlayerState && snap.PlayerState != VSPlayerState.Unknown)
            {
                var prev = _lastState; _lastState = snap.PlayerState;
                Log($"[Emit] PlayerState {prev} -> {snap.PlayerState}");
                OnPlayerStateChanged?.Invoke(prev, snap.PlayerState);
            }

            OnSnapshot?.Invoke(snap);

            // compact line
            if (fileLog || consoleLog)
            {
                string line = $"{snap.Timestamp:F3} f={snap.Frame} reason={reason} state={snap.PlayerState} LH={Vec(snap.LeftHand.position)} RH={Vec(snap.RightHand.position)} A={snap.Buttons.A}B={snap.Buttons.B}X={snap.Buttons.X}Y={snap.Buttons.Y} GL={snap.Buttons.GraspLeft} GR={snap.Buttons.GraspRight}";
                WriteFile(line);
                Log(line);
            }

            // verbose block (rate-limited)
            LogOncePer("emit-verbose", 0.5f, BuildVerboseSnapshotDump(snap));
        }

        string BuildVerboseSnapshotDump(in VSSnapshot s)
        {
            var sb = new StringBuilder(1024);
            sb.AppendLine("[Snapshot]");
            sb.AppendLine($"  Frame={s.Frame} Time={s.Timestamp:F3} State={s.PlayerState}");
            sb.AppendLine($"  Headset    pos={Vec(s.Headset.position)} rotEul={Eul(s.Headset.rotation)}");
            sb.AppendLine($"  LHand      pos={Vec(s.LeftHand.position)} rotEul={Eul(s.LeftHand.rotation)}");
            sb.AppendLine($"  RHand      pos={Vec(s.RightHand.position)} rotEul={Eul(s.RightHand.rotation)}");
            sb.AppendLine($"  LCtrl(C)   pos={Vec(s.LeftControllerCorrected.position)}  RCtrl(C) pos={Vec(s.RightControllerCorrected.position)}");
            sb.AppendLine($"  Buttons    A={s.Buttons.A} B={s.Buttons.B} X={s.Buttons.X} Y={s.Buttons.Y} GL={s.Buttons.GraspLeft} GR={s.Buttons.GraspRight}");
            sb.AppendLine($"  Thresholds pinch={s.Buttons.PinchThreshold} grasp={s.Buttons.GraspThreshold} trigGrip={s.Buttons.TriggerGripThreshold} platform={s.Buttons.Platform} isQuest={s.Buttons.IsQuest}");
            // replay section
            sb.AppendLine($"  Replay     mode={s.Replay.IsReplayMode} runningOrExit={s.Replay.IsRunningOrExiting} " +
                          $"showCam={s.Replay.ShowCamera} view={s.Replay.ViewMode} " +
                          $"pauseMenu={s.Replay.PauseReplayPlaybackMenuOpen}");
            if (!string.IsNullOrEmpty(s.Replay.RawReplayState))
                sb.AppendLine($"  ReplayRaw  state='{s.Replay.RawReplayState}'");
            return sb.ToString();
        }

        bool Moved(PoseData a, PoseData b)
        {
            if ((a.position - b.position).sqrMagnitude > posEpsilon * posEpsilon) return true;
            if (Quaternion.Angle(a.rotation, b.rotation) > rotEpsilon) return true;
            return false;
        }

        VSSnapshot BuildSnapshot()
        {
            if (verboseTickLogs)
                LogOncePer("build-start", 1f, "[Build] snapshot.");


            string rawStateText;
            var state = ReadPlayerState(out rawStateText);

            var snap = new VSSnapshot
            {
                ApiVersion = VSBridge.ApiVersion,

                Timestamp = Time.realtimeSinceStartup,
                Frame = Time.frameCount,
                PlayerState = state,
                Headset = PoseData.From(FindPM("headset")),
                LeftHand = PoseData.From(FindPM("leftHand")),
                RightHand = PoseData.From(FindPM("rightHand")),
                LeftHandTarget = PoseData.From(FindPM("leftHandTarget")),
                RightHandTarget = PoseData.From(FindPM("rightHandTarget")),
                LeftController = PoseData.From(FindPM("leftController")),
                RightController = PoseData.From(FindPM("rightController")),
                LeftControllerCorrected = PoseData.From(FindPM("leftControllerCorrected")),
                RightControllerCorrected = PoseData.From(FindPM("rightControllerCorrected")),
                LeftControllerOffset = PoseData.From(FindIM("leftControllerOffset")),
                RightControllerOffset = PoseData.From(FindIM("rightControllerOffset")),
                TrackingSpace = PoseData.From(FindPM("trackingSpace")),
                Board = ReadBoardTargets(),
                Buttons = ReadButtons(),
                Replay = ReadReplayInfo(),
                ReplayRig = ReadReplayRig()
            };

            // Fill explicit flags from CurrentState string
            snap.Flags = ReadPlayerFlags(rawStateText);

            // Derive “switch” from board movement vs board forward
            snap.Flags.IsSwitch = DeriveSwitchFlag(_hasLast, _last, snap, snap.Flags.IsSwitch);

            return snap;
        }


        VSPlayerState ReadPlayerState()
        {
            string _;
            return ReadPlayerState(out _);
        }

        VSPlayerState ReadPlayerState(out string rawStateText)
        {
            rawStateText = string.Empty;

            if (_playerMgr == null)
            {
                Log("[State] PlayerManager null");
                return VSPlayerState.Unknown;
            }

            VSPlayerState Map(string s)
            {
                if (string.IsNullOrEmpty(s)) return VSPlayerState.Unknown;

                var lower = s.ToLowerInvariant();

                // Your logger showed e.g. "Grinding", "Manualling"
                if (lower.Contains("offboard"))
                    return VSPlayerState.OffBoard;

                if (lower.Contains("bail"))
                    return VSPlayerState.Bailed;

                if (lower.Contains("riding") || lower.Contains("grind") || lower.Contains("manual"))
                    return VSPlayerState.Riding;

                return VSPlayerState.Unknown;
            }

            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                // Try field: CurrentState
                var f = _playerMgr.GetType().GetField("CurrentState", flags);
                if (f != null)
                {
                    var raw = f.GetValue(_playerMgr);
                    rawStateText = raw?.ToString() ?? string.Empty;
                    if (verboseTickLogs) Log($"[State] CurrentState(field)='{rawStateText}'");
                    return Map(rawStateText);
                }

                // Try property: CurrentState
                var p = _playerMgr.GetType().GetProperty("CurrentState", flags);
                if (p != null)
                {
                    var raw = p.GetValue(_playerMgr, null);
                    rawStateText = raw?.ToString() ?? string.Empty;
                    if (verboseTickLogs) Log($"[State] CurrentState(field)='{rawStateText}'");
                    return Map(rawStateText);
                }

                // Try method: GetCurrentState()
                var m = _playerMgr.GetType().GetMethod("GetCurrentState", flags, null, Type.EmptyTypes, null);
                if (m != null)
                {
                    var raw = m.Invoke(_playerMgr, null);
                    rawStateText = raw?.ToString() ?? string.Empty;
                    if (verboseTickLogs) Log($"[State] CurrentState(field)='{rawStateText}'");
                    return Map(rawStateText);
                }

                Log("[State] CurrentState not found (field/prop/method)");
            }
            catch (Exception ex)
            {
                Log("[State] err " + ex.Message);
            }

            return VSPlayerState.Unknown;
        }

        PlayerFlags ReadPlayerFlags(string rawStateText)
        {
            var flags = new PlayerFlags();

            if (!string.IsNullOrEmpty(rawStateText))
            {
                // Use the exact strings you observed in logs
                if (rawStateText.IndexOf("Grinding", StringComparison.OrdinalIgnoreCase) >= 0)
                    flags.IsGrinding = true;

                if (rawStateText.IndexOf("Manualling", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    rawStateText.IndexOf("Manual", StringComparison.OrdinalIgnoreCase) >= 0)
                    flags.IsManual = true;
            }

            // IsSwitch is filled separately (derived), see below
            return flags;
        }

        bool DeriveSwitchFlag(bool hasPrev, in VSSnapshot prev, in VSSnapshot cur, bool existing)
        {
            // If already set from some future explicit field, keep it.
            if (existing) return true;

            if (!hasPrev)
                return false;

            // Use board final target for position + forward
            Vector3 prevPos = prev.Board.FinalTarget.position;
            Vector3 curPos = cur.Board.FinalTarget.position;

            Vector3 delta = curPos - prevPos;
            if (delta.sqrMagnitude < 0.0001f)
                return false; // not moving → no stance inference

            // Board forward from rotation
            Vector3 fwd = cur.Board.FinalTarget.rotation * Vector3.forward;

            // Work in horizontal plane
            delta.y = 0f;
            fwd.y = 0f;

            float vLen2 = delta.sqrMagnitude;
            float fLen2 = fwd.sqrMagnitude;
            if (vLen2 < 0.0001f || fLen2 < 0.0001f)
                return false;

            delta /= Mathf.Sqrt(vLen2);
            fwd /= Mathf.Sqrt(fLen2);

            float dot = Vector3.Dot(fwd, delta);

            // dot < 0 means moving roughly opposite to board forward.
            // Use a small tolerance so slight angles still count as "backwards".
            bool isBackwards = dot < -0.35f;

            if (verboseTickLogs)
            {
                LogOncePer("switch-debug", 0.5f,
                    $"[Switch] dot={dot:F2} isBackwards={isBackwards}");
            }

            return isBackwards;
        }

        BoardTargets ReadBoardTargets()
        {
            string[] names = {
                "rawBoardTarget","boardTarget","boardTargetOffset","boardTargetCorrection",
                "boardTargetPopRotation","boardTargetShuvRotation","finalBoardTarget",
                "boardHeightTarget","boardHeightTargetLerped",
                "boardPivotAnimationParent","boardPivotAnimationTarget","boardPivotAnimationPositionParent","boardPivotAnimationPositionTarget",
                "backPivotPosition","frontPivotPosition","flipRotationParent","flipRotationTarget","shuvAxisTarget"
            };
            foreach (var n in names)
            {
                var tr = FindPM(n);
                LogOncePer("pm-" + n, 2f, tr ? $"[BoardRef] {n} => {tr.name} pos={Vec(tr.position)}" : $"[BoardRef] {n} => NULL");
            }

            return new BoardTargets
            {
                RawTarget = PoseData.From(FindPM("rawBoardTarget")),
                Target = PoseData.From(FindPM("boardTarget")),
                TargetOffset = PoseData.From(FindPM("boardTargetOffset")),
                TargetCorrection = PoseData.From(FindPM("boardTargetCorrection")),
                PopRotation = PoseData.From(FindPM("boardTargetPopRotation")),
                ShuvRotation = PoseData.From(FindPM("boardTargetShuvRotation")),
                FinalTarget = PoseData.From(FindPM("finalBoardTarget")),
                HeightTarget = PoseData.From(FindPM("boardHeightTarget")),
                HeightTargetLerped = PoseData.From(FindPM("boardHeightTargetLerped")),
                PivotAnimParent = PoseData.From(FindPM("boardPivotAnimationParent")),
                PivotAnimTarget = PoseData.From(FindPM("boardPivotAnimationTarget")),
                PivotAnimPosParent = PoseData.From(FindPM("boardPivotAnimationPositionParent")),
                PivotAnimPosTarget = PoseData.From(FindPM("boardPivotAnimationPositionTarget")),
                BackTruckPivot = PoseData.From(FindPM("backPivotPosition")),
                FrontTruckPivot = PoseData.From(FindPM("frontPivotPosition")),
                FlipRotationParent = PoseData.From(FindPM("flipRotationParent")),
                FlipRotationTarget = PoseData.From(FindPM("flipRotationTarget")),
                ShuvAxisTarget = PoseData.From(FindPM("shuvAxisTarget")),
            };
        }
        ButtonState ReadButtons()
        {
            var bs = new ButtonState();

            // thresholds
            float pinchTh = 0.5f, graspTh = 0.5f, trigTh = 0.5f;

            if (_inputMgr != null)
            {
                var t = _cachedImType ?? _inputMgr.GetType();

                pinchTh = GetFieldFloat(t, _inputMgr, "pinchThreshold", 0.5f, "[Buttons] pinchThreshold");
                graspTh = GetFieldFloat(t, _inputMgr, "graspThreshold", 0.5f, "[Buttons] graspThreshold");
                trigTh = GetFieldFloat(t, _inputMgr, "triggerGripThreshold", 0.5f, "[Buttons] triggerGripThreshold");

                bs.A = GetFieldBool(t, _inputMgr, "currA", "[Buttons] currA");
                bs.B = GetFieldBool(t, _inputMgr, "currB", "[Buttons] currB");
                bs.X = GetFieldBool(t, _inputMgr, "currX", "[Buttons] currX");
                bs.Y = GetFieldBool(t, _inputMgr, "currY", "[Buttons] currY");

                var pf = t.GetField("platform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pf != null) bs.Platform = pf.GetValue(_inputMgr)?.ToString() ?? "";

                bs.IsQuest |= GetFieldBool(t, _inputMgr, "isQuest", "[Buttons] isQuest");

                // digital overrides if game exposes them
                bs.GraspLeft |= GetFieldBool(t, _inputMgr, "leftGraspActivated", "[Buttons] leftGraspActivated");
                bs.GraspRight |= GetFieldBool(t, _inputMgr, "rightGraspActivated", "[Buttons] rightGraspActivated");
            }

            // XR ANALOG INPUT
            if (TryReadXRAnalog(out float gL, out float gR,
                                out float tL, out float tR,
                                out bool gbL, out bool gbR,
                                out bool stickL, out bool stickR,
                                out bool primL, out bool secL,
                                out bool primR, out bool secR))
            {
                bs.GripLeft = gL;
                bs.GripRight = gR;
                bs.TriggerLeft = tL;
                bs.TriggerRight = tR;

                bs.StickClickLeft = stickL;
                bs.StickClickRight = stickR;

                // Face buttons (typical XR mapping):
                // Left:  primary=X, secondary=Y
                // Right: primary=A, secondary=B
                bs.X = primL;
                bs.Y = secL;
                bs.A = primR;
                bs.B = secR;

                // Do NOT combine unless needed
                bs.GraspLeft |= (gL >= graspTh) || gbL;
                bs.GraspRight |= (gR >= graspTh) || gbR;
            }

            bs.PinchThreshold = pinchTh;
            bs.GraspThreshold = graspTh;
            bs.TriggerGripThreshold = trigTh;

            return bs;
        }

        bool TryReadXRAnalog(out float gL, out float gR,
                             out float tL, out float tR,
                             out bool gbL, out bool gbR,
                             out bool stickL, out bool stickR,
                             out bool primL, out bool secL,
                             out bool primR, out bool secR)
        {
            gL = gR = tL = tR = 0f;
            gbL = gbR = false;
            stickL = stickR = false;
            primL = secL = primR = secR = false;

            try
            {
                _xrBuf.Clear();
                InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, _xrBuf);
                if (_xrBuf.Count > 0 && _xrBuf[0].isValid)
                {
                    var L = _xrBuf[0];
                    L.TryGetFeatureValue(CommonUsages.grip, out gL);
                    L.TryGetFeatureValue(CommonUsages.trigger, out tL);
                    L.TryGetFeatureValue(CommonUsages.gripButton, out gbL);
                    L.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out stickL);
                    L.TryGetFeatureValue(CommonUsages.primaryButton, out primL);
                    L.TryGetFeatureValue(CommonUsages.secondaryButton, out secL);
                }

                _xrBuf.Clear();
                InputDevices.GetDevicesAtXRNode(XRNode.RightHand, _xrBuf);
                if (_xrBuf.Count > 0 && _xrBuf[0].isValid)
                {
                    var R = _xrBuf[0];
                    R.TryGetFeatureValue(CommonUsages.grip, out gR);
                    R.TryGetFeatureValue(CommonUsages.trigger, out tR);
                    R.TryGetFeatureValue(CommonUsages.gripButton, out gbR);
                    R.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out stickR);
                    R.TryGetFeatureValue(CommonUsages.primaryButton, out primR);
                    R.TryGetFeatureValue(CommonUsages.secondaryButton, out secR);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        void DebugListInputManagerFields()
        {
            if (_inputMgr == null)
            {
                Log("[DebugList] _inputMgr null");
                return;
            }

            var t = _inputMgr.GetType();
            Log($"[DebugList] Listing fields on {_inputMgr.GetType().FullName}:");
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance |
                                           System.Reflection.BindingFlags.Public |
                                           System.Reflection.BindingFlags.NonPublic))
            {
                try
                {
                    var val = f.GetValue(_inputMgr);
                    Log($"[IM] {f.FieldType.Name} {f.Name} = {val}");
                }
                catch (Exception ex)
                {
                    Log($"[IM] {f.Name} err {ex.Message}");
                }
            }
        }

        float GetFieldFloat(Type t, object o, string name, float dflt, string tag)
        {
            try
            {
                var f = t.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f != null) { var v = Convert.ToSingle(f.GetValue(o)); Log($"{tag} = {v}"); return v; }
                Log($"{tag} MISSING");
            }
            catch (Exception ex) { Log($"{tag} err {ex.Message}"); }
            return dflt;
        }
        
        bool GetFieldBool(Type t, object o, string name, string tag)
        {
            try
            {
                var f = t.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f != null) { var v = Convert.ToBoolean(f.GetValue(o)); Log($"{tag} = {v}"); return v; }
                Log($"{tag} MISSING");
            }
            catch (Exception ex) { Log($"{tag} err {ex.Message}"); }
            return false;
        }

        // XR grip/trigger read using buffered device lookup (matches VRInput.cs style)
        static readonly List<InputDevice> _xrBuf = new List<InputDevice>(2);

        float GetAnimParam(Animator anim, params string[] names)
        {
            if (!anim) return 0f;
            for (int i = 0; i < names.Length; i++)
            {
                if (HasFloatParam(anim, names[i])) return anim.GetFloat(names[i]);
            }
            return 0f;
        }
        bool HasFloatParam(Animator anim, string name)
        {
            if (!anim) return false;
            var ps = anim.parameters;
            for (int i = 0; i < ps.Length; i++)
                if (ps[i].type == AnimatorControllerParameterType.Float && ps[i].name == name) return true;
            return false;
        }

        ReplayInfo ReadReplayInfo()
        {
            var info = new ReplayInfo
            {
                ViewMode = VSReplayViewMode.None,
                RawReplayState = string.Empty
            };

            if (_replayMgr == null || _cachedRmType == null)
                return info;

            try
            {
                if (_rmReplayMode != null)
                {
                    var v = _rmReplayMode.GetValue(_replayMgr);
                    if (v is bool b) info.IsReplayMode = b;
                }

                if (_rmReplayModeRunning != null)
                {
                    var v = _rmReplayModeRunning.GetValue(_replayMgr);
                    if (v is bool b) info.IsRunningOrExiting = b;
                }

                if (_rmShowCamera != null)
                {
                    var v = _rmShowCamera.GetValue(_replayMgr);
                    if (v is bool b) info.ShowCamera = b;
                }

                if (_rmPauseReplayPlaybackMenuOpen != null)
                {
                    var v = _rmPauseReplayPlaybackMenuOpen.GetValue(_replayMgr);
                    if (v is bool b) info.PauseReplayPlaybackMenuOpen = b;
                }

                if (_rmCurrentState != null)
                {
                    var v = _rmCurrentState.GetValue(_replayMgr);
                    info.RawReplayState = v?.ToString() ?? string.Empty;
                }

                // Derive a simple view mode:
                // From your logs:
                //   replayMode = true, showCamera = false  => headset/VR follow view
                //   replayMode = true, showCamera = true   => camera view
                if (info.IsReplayMode)
                {
                    info.ViewMode = info.ShowCamera
                        ? VSReplayViewMode.ReplayCamera
                        : VSReplayViewMode.HeadsetFollow;
                }
                else
                {
                    info.ViewMode = VSReplayViewMode.None;
                }
            }
            catch (Exception ex)
            {
                Log("[ReplayRead] err " + ex.Message);
            }

            return info;
        }

        ReplayRig ReadReplayRig()
        {
            var rig = new ReplayRig
            {
                IsValid = false
            };

            if (_replayMgr == null || _cachedRmType == null)
                return rig;

            try
            {
                Transform tr;

                if (_rmReplayControllerTf != null)
                {
                    tr = _rmReplayControllerTf.GetValue(_replayMgr) as Transform;
                    rig.ControllerRoot = PoseData.From(tr);
                    if (tr) rig.IsValid = true;
                }

                if (_rmHeadsetTf != null)
                {
                    tr = _rmHeadsetTf.GetValue(_replayMgr) as Transform;
                    rig.Headset = PoseData.From(tr);
                    if (tr) rig.IsValid = true;
                }

                if (_rmLeftControllerTf != null)
                {
                    tr = _rmLeftControllerTf.GetValue(_replayMgr) as Transform;
                    rig.LeftController = PoseData.From(tr);
                    if (tr) rig.IsValid = true;
                }

                if (_rmRightControllerTf != null)
                {
                    tr = _rmRightControllerTf.GetValue(_replayMgr) as Transform;
                    rig.RightController = PoseData.From(tr);
                    if (tr) rig.IsValid = true;
                }

                if (_rmTrackingSpaceTf != null)
                {
                    tr = _rmTrackingSpaceTf.GetValue(_replayMgr) as Transform;
                    rig.TrackingSpace = PoseData.From(tr);
                    if (tr) rig.IsValid = true;
                }
            }
            catch (Exception ex)
            {
                Log("[ReplayRig] err " + ex.Message);
            }

            return rig;
        }

        // === RESOLUTION ===
        void TryResolveGameRefs()
        {
            // already good?
            if (_inputMgr != null && _playerMgr != null && _replayMgr != null) return;

            // --- Resolve InputManager ---
            if (_inputMgr == null)
            {
                var imType = FindTypeAnywhere("InputManager");
                if (imType != null)
                {
                    // try any existing instance (includes inactive)
                    var all = Resources.FindObjectsOfTypeAll(imType);
                    if (all != null && all.Length > 0) _inputMgr = all[0];

                    // fallback: from "Game Manager" GO (matches your probe log)
                    if (_inputMgr == null)
                    {
                        var gm = GameObject.Find("Game Manager");
                        if (gm != null) _inputMgr = gm.GetComponent(imType);
                    }
                }
                Log($"[Resolve] InputManager type={(imType != null)} inst={(_inputMgr != null)}");

                if (_inputMgr != null && _cachedImType == null)
                    _cachedImType = _inputMgr.GetType();
            }

            // --- Resolve PlayerManager ---
            if (_playerMgr == null)
            {
                var pmType = FindTypeAnywhere("PlayerManager");
                if (pmType != null)
                {
                    var all = Resources.FindObjectsOfTypeAll(pmType);
                    if (all != null && all.Length > 0) _playerMgr = all[0];

                    // fallback: from "Skater" GO (matches your probe log)
                    if (_playerMgr == null)
                    {
                        var skater = GameObject.Find("Skater");
                        if (skater != null) _playerMgr = skater.GetComponent(pmType);
                    }
                }
                Log($"[Resolve] PlayerManager type={(pmType != null)} inst={(_playerMgr != null)}");

                if (_playerMgr != null && _cachedPmType == null)
                    _cachedPmType = _playerMgr.GetType();
            }

            // --- Resolve ReplayManager (NEW) ---
            if (_replayMgr == null)
            {
                var rmType = FindTypeAnywhere("ReplayManager");
                if (rmType != null)
                {
                    var all = Resources.FindObjectsOfTypeAll(rmType);
                    if (all != null && all.Length > 0) _replayMgr = all[0];

                    // fallback: from "Replay Manager" GO
                    if (_replayMgr == null)
                    {
                        var rmGo = GameObject.Find("Replay Manager");
                        if (rmGo != null) _replayMgr = rmGo.GetComponent(rmType);
                    }
                }

                Log($"[Resolve] ReplayManager type={(rmType != null)} inst={(_replayMgr != null)}");

                if (_replayMgr != null && _cachedRmType == null)
                {
                    _cachedRmType = _replayMgr.GetType();
                    CacheReplayManagerFields(_cachedRmType);
                }
            }
        }

        void CacheReplayManagerFields(Type t)
        {
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                _rmReplayMode = t.GetField("replayMode", flags);
                _rmReplayModeRunning = t.GetField("replayModeIsRunningOrIsExiting", flags);
                _rmShowCamera = t.GetField("showCamera", flags);
                _rmPauseReplayPlaybackMenuOpen = t.GetField("pauseReplayPlaybackMenuOpen", flags);

                // optional state string, name may be "CurrentState" or similar; we try both
                _rmCurrentState = t.GetField("CurrentState", flags) ??
                                  t.GetField("currentState", flags);

                // replay rig transforms
                _rmReplayControllerTf = t.GetField("replayController", flags);
                _rmHeadsetTf = t.GetField("headset", flags);
                _rmLeftControllerTf = t.GetField("leftController", flags);
                _rmRightControllerTf = t.GetField("rightController", flags);
                _rmTrackingSpaceTf = t.GetField("trackingSpace", flags);

                // NEW: hand renderers
                _rmLeftHandRenderer = t.GetField("leftHandRenderer", flags);
                _rmRightHandRenderer = t.GetField("rightHandRenderer", flags);

                Log($"[ReplayCache] fields replayMode={(_rmReplayMode != null)} " +
                    $"running={(_rmReplayModeRunning != null)} showCamera={(_rmShowCamera != null)} " +
                    $"pausePlaybackMenu={(_rmPauseReplayPlaybackMenuOpen != null)} rawState={(_rmCurrentState != null)} " +
                    $"replayRig={(_rmHeadsetTf != null || _rmReplayControllerTf != null)} " +
                    $"handsL={(_rmLeftHandRenderer != null)} handsR={(_rmRightHandRenderer != null)}");
            }
            catch (Exception ex)
            {
                Log("[ReplayCache] err " + ex.Message);
            }
        }


        // helper (like your logger)
        static System.Type FindTypeAnywhere(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try
                {
                    var t = asms[i].GetType(name, false, true);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        object TryGetSingleton(string typeName)
        {
            var t = FindTypeByName(typeName);
            if (t == null) { Log($"[FindType] '{typeName}' NOT FOUND"); return null; }
            try
            {
                var prop = t.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                if (prop != null)
                {
                    var inst = prop.GetValue(null, null);
                    Log($"[Singleton] {typeName}.Instance {(inst != null ? "OK" : "NULL")}");
                    return inst;
                }
            }
            catch (Exception ex) { Log($"[Singleton] {typeName}.Instance err {ex.Message}"); }
            return null;
        }

        object TryFindComponentOn(string goName, string typeName)
        {
            try
            {
                var t = FindTypeByName(typeName);
                if (t == null) { Log($"[FindComp] type '{typeName}' not found"); return null; }
                var all = GameObject.FindObjectsOfType(typeof(Transform), true);
                for (int i = 0; i < all.Length; i++)
                {
                    var tr = all[i] as Transform;
                    if (tr && tr.name == goName)
                    {
                        var comp = tr.gameObject.GetComponent(t);
                        Log($"[FindComp] GO='{goName}' has {typeName} => {(comp != null)}");
                        if (comp != null) return comp;
                    }
                }
                Log($"[FindComp] GO='{goName}' not found or missing {typeName}");
            }
            catch (Exception ex) { Log($"[FindComp] err {ex.Message}"); }
            return null;
        }

        Type FindTypeByName(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(name, false);
                    if (t != null) { LogOncePer("type-" + name, 3f, $"[FindType] Found '{name}' in {asm.GetName().Name}"); return t; }
                    foreach (var tt in asm.GetTypes())
                        if (tt.Name == name) { LogOncePer("type-" + name, 3f, $"[FindType] Found by short-name '{name}' in {asm.GetName().Name}"); return tt; }
                }
                catch { }
            }
            Log($"[FindType] '{name}' not present in any loaded assembly");
            return null;
        }

        Transform FindPM(string fieldName) => FindFieldTransform(_playerMgr, "PM", fieldName);
        Transform FindIM(string fieldName) => FindFieldTransform(_inputMgr, "IM", fieldName);

        Transform FindFieldTransform(object owner, string prefix, string fieldName)
        {
            if (owner == null) return null;
            string key = prefix + "." + fieldName;
            if (_tfCache.TryGetValue(key, out var cached) && cached)
            {
                LogOncePer("cache-" + key, 5f, $"[Cache] {key} => {cached.name}");
                return cached;
            }
            try
            {
                var t = owner.GetType();
                var f = t.GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f == null) { Log($"[{prefix}] field '{fieldName}' MISSING on {t.FullName}"); return null; }
                var tr = f.GetValue(owner) as Transform;
                if (tr)
                {
                    _tfCache[key] = tr;
                    Log($"[{prefix}] field '{fieldName}' => {tr.name} pos={Vec(tr.position)}");
                    return tr;
                }
                else
                {
                    Log($"[{prefix}] field '{fieldName}' present but NULL or not a Transform");
                }
            }
            catch (Exception ex) { Log($"[{prefix}] field '{fieldName}' err {ex.Message}"); }
            return null;
        }

        // === API ===
        public static bool TryGetLatest(out VSSnapshot snapshot)
        {
            if (Instance == null) { snapshot = default; return false; }
            snapshot = Instance._last; return Instance._hasLast;
        }
        public static void Configure(int sampleRateHz = 20, float posEps = 0.0025f, float rotEps = 1.0f)
        {
            if (Instance == null) return;
            Instance.sampleHz = Mathf.Clamp(sampleRateHz, 1, 120);
            Instance.posEpsilon = Mathf.Max(1e-5f, posEps);
            Instance.rotEpsilon = Mathf.Max(0.1f, rotEps);
            Instance.Log($"[Configure] Hz={Instance.sampleHz} posEps={Instance.posEpsilon} rotEps={Instance.rotEpsilon}");
        }
        public static VSBridge Ensure()
        {
            if (Instance) return Instance;
            var go = new GameObject("VSModBridge");
            return go.AddComponent<VSBridge>();
        }

        public static bool TryGetReplayHands(out SkinnedMeshRenderer left, out SkinnedMeshRenderer right)
        {
            left = null;
            right = null;

            if (Instance == null || Instance._replayMgr == null || Instance._cachedRmType == null)
                return false;

            try
            {
                if (Instance._rmLeftHandRenderer != null)
                {
                    left = Instance._rmLeftHandRenderer.GetValue(Instance._replayMgr) as SkinnedMeshRenderer;
                }

                if (Instance._rmRightHandRenderer != null)
                {
                    right = Instance._rmRightHandRenderer.GetValue(Instance._replayMgr) as SkinnedMeshRenderer;
                }

                if (left != null || right != null)
                {
                    Instance.LogOncePer("replay-hands-ok", 5f,
                        "[ReplayHands] Resolved replay hand SkinnedMeshRenderers.");
                    return true;
                }

                Instance.LogOncePer("replay-hands-null", 5f,
                    "[ReplayHands] Replay hand renderers are null.");
                return false;
            }
            catch (Exception ex)
            {
                Instance.LogOncePer("replay-hands-err", 5f,
                    "[ReplayHands] Reflection error: " + ex.Message);
                return false;
            }
        }

        // === LOGGING UTILS ===
        readonly Dictionary<string, (float t, string last)> _rate = new Dictionary<string, (float, string)>();
        void LogOncePer(string key, float secs, string msg)
        {
            float now = Time.realtimeSinceStartup;
            if (_rate.TryGetValue(key, out var v) && now - v.t < secs) return;
            _rate[key] = (now, msg);
            Log(msg);
        }
        void Log(string msg)
        {
            if (consoleLog) _log.Msg(msg);
            if (fileLog) WriteFile(msg);
        }
        void WriteFile(string line)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_logPath));
                using (var sw = new System.IO.StreamWriter(_logPath, true)) sw.WriteLine(line);
            }
            catch { }
        }
        static string Vec(Vector3 v) => $"({v.x:F3},{v.y:F3},{v.z:F3})";
        static string Eul(Quaternion q) { var e = q.eulerAngles; return $"({e.x:F1},{e.y:F1},{e.z:F1})"; }
    }

    public static class VSBridgeBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            // Ensure the bridge is present
            VSBridge.Ensure();

            // Ensure the camera-attached menu shell is present
            //VSMenuAPI.Ensure();
        }
    }
}
