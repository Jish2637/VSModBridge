VS Mod Bridge – Developer Documentation
1. INTRODUCTION
VS Mod Bridge is a lightweight data-access layer for Virtual Skate.
It exposes a consistent, game-agnostic stream of real-time information about:
•	Player state
•	Headset and controller poses
•	Hand targets used by IK
•	Board target transforms and trick-related axes
•	Controller, button, grip, and trigger inputs
•	Replay mode state and replay rig data
It is designed to be the single integration point for mods that need motion, input, replay, or state data — removing the need for reflection, patching, or reverse-engineering.
Mods may listen for live snapshots or query the most recent snapshot at any time.________________________________________
2. CORE CONCEPT: THE SNAPSHOT STREAM
The API outputs a structured “snapshot” at a configurable sampling rate.
A snapshot represents a single moment in gameplay and includes:
•	Timestamp (seconds since game start)
•	Frame index
•	Full set of transforms (VR, board, targets)
•	Input state
•	Player state flags
•	Replay system data
Snapshots are emitted only when meaningful changes occur (position change, rotation change, button edge, or game-state transition). This keeps the system lightweight while remaining highly responsive.________________________________________
3. DATA CATEGORIES EXPOSED BY THE BRIDGE
3.1 PLAYER STATE & FLAGS
The bridge reads the internal player state and exposes:
•	Riding
•	Off-board
•	Bailed
•	Grinding
•	Manualling
•	Switch state (derived from board direction vs actual movement)
These are intended to help trick detection, gameplay analytics, or coaching mods________________________________________
3.2 VR POSE DATA
Available at all times (if applicable):
•	Headset world pose
•	Left and right hand poses
•	Left and right controller poses (raw + corrected)
•	Controller offsets
•	Tracking space transform
This supports camera mods, gesture mods, analytics, and motion capture.
________________________________________
3.3 BOARD TARGETS AND TRICK AXES
The bridge exposes every board-related transform normally internal to the game:
•	Raw target
•	Main board target
•	Target offset and correction
•	Pop rotation
•	Shuv rotation
•	Flip rotation (parent + target)
•	Final board target
•	Height targets (raw + lerped)
•	Pivot animation positions
•	Front and back truck pivot points
•	Shuv axis target
This is essential for building:
•	Trick analyzers
•	Replay visualizers
•	Line-review tools
•	Physics-tuning mods
•	Motion capture pipelines
________________________________________
3.4 INPUT & BUTTONS
The bridge merges game-level and XR-level input:
•	A/B/X/Y buttons
•	Grip and trigger analog values
•	Grasp states
•	Stick click
•	Pinch/Grasp/TriggerGrip thresholds
•	Platform metadata (PC/Quest)
This allows for:
•	Gesture-based interfaces
•	Interaction mods
•	Training/coaching input displays
•	In-replay overlays
________________________________________










3.5 REPLAY SYSTEM DATA
If the player enters the replay mode, the bridge reports:
•	Whether replay mode is active
•	Replay camera state (headset follow or replay camera)
•	Whether the playback menu is open
•	Replay mode transitions
•	Raw replay-state string (if available)
•	Replay rig transforms:
o	Headset
o	Left controller
o	Right controller
o	Controller root
o	Tracking space
This makes advanced replay systems significantly easier to build.
________________________________________
4. USAGE MODELS
The API supports two integration paths depending on your mod’s needs.
4.1 EVENT-DRIVEN (RECOMMENDED)
You subscribe to the snapshot stream and react only when there is new data.
Ideal for replay tools, trick analysis, camera mods, or anything reactive.
4.2 ON-DEMAND (POLLING)
Your mod queries for the most recent snapshot whenever it needs it.
Best for UI tools, debug overlays, or systems updating on their own tick.
________________________________________
5. SAMPLING & PERFORMANCE
•	Default sampling: 5 Hz
•	Adjustable up to 120 Hz
•	Snapshots are only emitted when:
o	Player state changes
o	A tracked transform moves or rotates beyond tolerance
o	A button changes state (pressed/released)
This ensures the bridge is extremely lightweight and suitable for use in multiple mods simultaneously.
________________________________________













6. RESOLUTION & INTERNAL MECHANICS
6.1 AUTOMATIC MANAGER DISCOVERY
The bridge automatically discovers:
•	InputManager
•	PlayerManager
•	ReplayManager
It retries discovery for up to ~60 seconds to ensure compatibility with scene loads.
6.2 TRANSFORM CACHING
To eliminate repeated reflection overhead:
•	All field lookups for transforms are cached.
•	Subsequent accesses are near-zero cost.
6.3 REFLECTION MINIMISATION
Reflection is used once during setup.
All runtime access uses cached references or direct transform reads.
________________________________________
7. REPLAY RIG SUPPORT
When replay mode is active, the bridge exposes:
•	Replay headset transform
•	Replay camera root
•	Left/right replay controllers
•	Replay tracking space
Mods may use this for:
•	Cinematic replay systems
•	Camera path builders
•	Tactical replays
•	Side-by-side recording tools
Additionally, if available, the bridge can expose SkinnedMeshRenderer references for replay hands.
________________________________________
8. ERROR HANDLING & ROBUSTNESS
The bridge is designed to:
•	Fail gracefully if game systems are absent
•	Suppress transient "Unknown" state transitions
•	Only emit stable state changes
•	Tolerate missing fields or renamed fields
•	Avoid throwing exceptions during gameplay
This ensures mods depending on the bridge are stable and resilient.
________________________________________








9. TYPICAL USE CASES FOR MODDERS
Below is a conceptual breakdown of how modders typically use the bridge.
CAMERA / CINEMATIC MODS
•	Smooth follow cams
•	Replay director tools
•	Spot-aware angle selectors
ANALYTICS & TRICK DETECTION
•	Detect manual/grind/flip transitions
•	Measure speed, angle, axis alignment
•	Score cleanliness or consistency
TRAINING TOOLS
•	Ghost skater playback
•	Line repetition comparisons
•	VR coaching gestures
REPLAY EXTENSIONS
•	Keyframed camera paths
•	Multi-angle exports
•	Timeline editors
MOTION CAPTURE / DATA EXPORT
•	Export full frame-by-frame pose data
•	Pose reconstruction in external tools
•	Syncing with Blender/VRChat pipelines
INTERACTION / INPUT MODS
•	Custom VR gestures
•	Radial menus triggered by pose or grip
•	Trick-triggered effects
________________________________________
10. COMPATIBILITY & REQUIREMENTS
•	Requires MelonLoader
•	Must be installed separately from your mod
•	Safe to include as a dependency
•	Zero gameplay modification
•	No direct control over physics or actions (read-only API)
________________________________________
11. PHILOSOPHY & DESIGN GOALS
VS Mod Bridge is designed to:
•	Lower the barrier for modders
•	Provide a standardised API instead of fragmented reflection hacks
•	Keep performance overhead near zero
•	Support realistic, skate-driven tooling
•	Enable an entire ecosystem of replay, analysis, and camera mods
Its purpose is not to modify gameplay — but to reveal clean, structured data that modders can use creatively.




VS Mod Bridge – Developer Guide & Examples
0. PREREQUISITES
•	Loader: MelonLoader
•	Namespace:
•	using VS.ModBridge;
•	Runtime: Unity (Virtual Skate)
Players should install VS Mod Bridge as a separate dependency. Your mod should never ship its own copy. 
________________________________________
1. INITIALISING THE BRIDGE
Call VSBridge.Ensure() once during your mod’s startup. It is idempotent.
using MelonLoader;
using VS.ModBridge;

public class MyMod : MelonMod
{
    public override void OnInitializeMelon()
    {
        // Make sure the bridge GameObject exists
        VSBridge.Ensure();

        // Subscribe to events (see next sections)
        VSBridge.OnSnapshot += OnSnapshot;
        VSBridge.OnPlayerStateChanged += OnPlayerStateChanged;
    }

    private void OnSnapshot(in VSSnapshot snap) { /* ... */ }

    private void OnPlayerStateChanged(VSPlayerState prev, VSPlayerState next)
    {
        MelonLogger.Msg($"Player state: {prev} -> {next}");
    }
}
________________________________________











2. WORKING WITH SNAPSHOTS
2.1 EVENT-DRIVEN (RECOMMENDED)
Use the OnSnapshot event to react when a new snapshot is emitted.
private void OnSnapshot(in VSSnapshot snap)
{
    // Example: log frame + state
    MelonLogger.Msg($"[{snap.Frame}] {snap.PlayerState} at {snap.Timestamp:F2}s");
}
Snapshots are emitted when:
•	Player state changes
•	Key poses move/rotate beyond thresholds
•	Button edges occur (pressed/released)
2.2 POLLING THE LATEST SNAPSHOT
If your system has its own tick (e.g. per-frame camera):
public override void OnUpdate()
{
    if (!VSBridge.TryGetLatest(out var snap))
        return; // bridge not ready yet

    // Use snapshot
    var headPos = snap.Headset.position;
}
2.3 ADJUSTING SAMPLING RATE AND TOLERANCES
public override void OnInitializeMelon()
{
    VSBridge.Ensure();

    // 30 Hz sampling, tighter position epsilon, slightly looser rotation epsilon
    VSBridge.Configure(
        sampleRateHz: 30,
        posEps: 0.0015f,
        rotEps: 1.5f
    );
}
________________________________________










3. PLAYER STATE & FLAGS
3.1 RESPONDING TO PLAYER STATE CHANGES
Use OnPlayerStateChanged to track transitions like Riding → Bailed.
private int _bailCount;
private float _lastRideStartTime;
private void OnPlayerStateChanged(VSPlayerState prev, VSPlayerState next)
{
    if (next == VSPlayerState.Bailed)
    {
        _bailCount++;
        MelonLogger.Msg($"Bail #{_bailCount}");
    }

    if (next == VSPlayerState.Riding)
    {
        _lastRideStartTime = Time.realtimeSinceStartup;
    }

    if (prev == VSPlayerState.Riding && next != VSPlayerState.Riding)
    {
        float duration = Time.realtimeSinceStartup - _lastRideStartTime;
        MelonLogger.Msg($"Ride segment lasted {duration:F1}s");
    }
}
3.2 DETECTING GRIND/MANUAL/SWITCH IN SNAPSHOTS
private void OnSnapshot(in VSSnapshot snap)
{
    var f = snap.Flags;

    if (f.IsGrinding)
    {
        // Perfect for grind-tracking, sparks, or grind indicators
    }

    if (f.IsManual)
    {
        // Manual practice tools, balance meters
    }

    if (f.IsSwitch)
    {
        // Switch-only challenges, style scoring etc.
    }
}
________________________________________
4. VR POSE DATA (HEADSET, HANDS, CONTROLLERS)
4.1 BASIC CAMERA FOLLOW USING HEADSET
public class HeadFollowCamera : MonoBehaviour
{
    public Transform cameraTransform; // Assign your camera

    void LateUpdate()
    {
        if (!VSBridge.TryGetLatest(out var snap))
            return;

        var head = snap.Headset;
        cameraTransform.position = head.position;
        cameraTransform.rotation = head.rotation;
    }
}
4.2 USING CORRECTED CONTROLLERS (FOR ACCURATE HANDS)
private void OnSnapshot(in VSSnapshot snap)
{
    var left = snap.LeftControllerCorrected;
    var right = snap.RightControllerCorrected;

    // Example: draw a debug line between hands
    Debug.DrawLine(left.position, right.position, Color.cyan);
}
4.3 USING HAND TARGETS (IK DATA)
private void OnSnapshot(in VSSnapshot snap)
{
    var lTarget = snap.LeftHandTarget.position;
    var rTarget = snap.RightHandTarget.position;

    // Example: visualize IK targets with small spheres
    DebugDrawSphere(lTarget, 0.05f, Color.green);
    DebugDrawSphere(rTarget, 0.05f, Color.green);
}
(Implementation of DebugDrawSphere is up to your mod; you can use Gizmos, custom meshes, etc.)
________________________________________







5. BOARD TARGETS & TRICK AXES
5.1 VISUALISING THE BOARD’S PATH (FINALTARGET)
private Vector3 _lastFinalBoardPos;
private bool _hasLast;

private void OnSnapshot(in VSSnapshot snap)
{
    var bt = snap.Board;
    var cur = bt.FinalTarget.position;

    if (_hasLast)
    {
        Debug.DrawLine(_lastFinalBoardPos, cur, Color.yellow, 0.1f, false);
    }

    _lastFinalBoardPos = cur;
    _hasLast = true;
}
5.2 VISUALISING FLIP & SHUV AXES
private void OnSnapshot(in VSSnapshot snap)
{
    var bt = snap.Board;

    // Flip axis: forward of FlipRotationTarget
    var flipPos = bt.FlipRotationTarget.position;
    var flipRot = bt.FlipRotationTarget.rotation;
    var flipAxis = flipRot * Vector3.forward;

    Debug.DrawRay(flipPos, flipAxis * 0.5f, Color.magenta);

    // Shuv axis: from shuvAxisTarget pose
    var shuvPos = bt.ShuvAxisTarget.position;
    var shuvRot = bt.ShuvAxisTarget.rotation;
    var shuvAxis = shuvRot * Vector3.up; // typical assumption

    Debug.DrawRay(shuvPos, shuvAxis * 0.5f, Color.blue);
}







5.3 TRUCK PIVOT DEBUG
private void OnSnapshot(in VSSnapshot snap)
{
    var bt = snap.Board;

    DebugDrawSphere(bt.FrontTruckPivot.position, 0.04f, Color.red);
    DebugDrawSphere(bt.BackTruckPivot.position, 0.04f, Color.red);
}
________________________________________
6. INPUT & BUTTONS
6.1 TOGGLING A UI OVERLAY USING A BUTTON
private bool _overlayVisible;
private bool _lastA;

private void OnSnapshot(in VSSnapshot snap)
{
    var a = snap.Buttons.A;

    // Edge detection: A just pressed
    if (a && !_lastA)
    {
        _overlayVisible = !_overlayVisible;
        SetOverlayVisible(_overlayVisible);
    }

    _lastA = a;
}
6.2 GRIP-BASED “GRASP” DETECTION
private void OnSnapshot(in VSSnapshot snap)
{
    var b = snap.Buttons;

    // Game-level grasp (bool) plus XR analog
    if (b.GraspLeft || b.GripLeft >= b.GraspThreshold)
    {
        // Left hand is grasping (e.g. grab UI, tweak camera)
    }

    if (b.GraspRight || b.GripRight >= b.GraspThreshold)
    {
        // Right hand grasping
    }
}


6.3 STICK CLICKS FOR CAMERA PRESETS
private bool _lastStickL, _lastStickR;

private void OnSnapshot(in VSSnapshot snap)
{
    bool l = snap.Buttons.StickClickLeft;
    bool r = snap.Buttons.StickClickRight;

    if (l && !_lastStickL)
        CycleCameraPreset(-1);

    if (r && !_lastStickR)
        CycleCameraPreset(+1);

    _lastStickL = l;
    _lastStickR = r;
}
________________________________________
7. REPLAY MODE DATA
7.1 DETECTING WHEN REPLAY MODE IS ACTIVE
private void OnSnapshot(in VSSnapshot snap)
{
    var rp = snap.Replay;

    if (!rp.IsReplayMode)
        return;

    // Only run in replay
    if (rp.ViewMode == VSReplayViewMode.ReplayCamera)
    {
        // Free camera mode
    }
    else if (rp.ViewMode == VSReplayViewMode.HeadsetFollow)
    {
        // VR follow mode
    }
}







7.2 PAUSING BEHAVIOUR WHEN REPLAY MENU IS OPEN
private void OnSnapshot(in VSSnapshot snap)
{
    if (!snap.Replay.IsReplayMode)
        return;

    if (snap.Replay.PauseReplayPlaybackMenuOpen)
    {
        // Stop advancing a timeline, freeze overlays etc.
    }
}
________________________________________
8. REPLAY RIG & HANDS
8.1 USING REPLAYRIG TO ATTACH CUSTOM CAMERAS
private Transform _replayCam;

public override void OnInitializeMelon()
{
    VSBridge.Ensure();
    VSBridge.OnSnapshot += OnSnapshot;

    // Create a camera object or find one
    var go = new GameObject("MyReplayCamera");
    _replayCam = go.transform;
}

private void OnSnapshot(in VSSnapshot snap)
{
    if (!snap.Replay.IsReplayMode || !snap.ReplayRig.IsValid)
        return;

    var rig = snap.ReplayRig;

    // Example: parent your camera to replay controller root
    _replayCam.position = rig.ControllerRoot.position;
    _replayCam.rotation = rig.ControllerRoot.rotation;
}







8.2 HIDING REPLAY HANDS
public override void OnInitializeMelon()
{
    VSBridge.Ensure();
    MelonCoroutines.Start(WaitAndHideHands());
}

private System.Collections.IEnumerator WaitAndHideHands()
{
    // wait a bit for ReplayManager to exist
    yield return new WaitForSeconds(2f);

    if (VSBridge.TryGetReplayHands(out var left, out var right))
    {
        if (left != null) left.enabled = false;
        if (right != null) right.enabled = false;
    }
}
________________________________________
9. ERROR HANDLING & READINESS
9.1 HANDLING “BRIDGE NOT READY YET”
public override void OnUpdate()
{
    if (!VSBridge.TryGetLatest(out var snap))
    {
        // Optional: show a small “bridge warming up…” message once
        return;
    }

    // Safe to use snapshot here
}
9.2 DEFENSIVE CODING IN EVENT HANDLERS
Because the bridge itself handles exceptions internally, your main concern is your own code:
private void OnSnapshot(in VSSnapshot snap)
{
    try
    {
        // your logic here
    }
    catch (Exception ex)
    {
        MelonLogger.Error($"MyMod OnSnapshot error: {ex}");
    }
}
