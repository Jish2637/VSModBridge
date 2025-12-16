// VSGrindFXMod.cs
//
// Example grind sparks/flames mod using VSModBridge.
//
// Requirements:
// - MelonLoader
// - VSModBridge.dll (with PlayerFlags + BoardTargets as in your API)
// - Compiled against the same Unity version as Virtual Skate

//Just a fun little thing i tried.

using System;
using MelonLoader;
using UnityEngine;
using VS.ModBridge; // <- from VSModBridge.dll

[assembly: MelonInfo(typeof(VSGrindFXMod.GrindFXModMain), "VSGrindFX", "1.0.0", "josh2367")]
//[assembly: MelonGame("FlipAxisStudios", "VirtualSkate")]

namespace VSGrindFXMod
{
    public class GrindFXModMain : MelonMod
    {
        public override void OnInitializeMelon()
        {
            MelonLogger.Msg("[GrindFX] Initializing");

            // Ensure the bridge is alive (usually already spawned by its own Melon)
            VSBridge.Ensure();

            // Subscribe to snapshots
            VSBridge.OnSnapshot += OnSnapshot;
        }

        public override void OnDeinitializeMelon()
        {
            VSBridge.OnSnapshot -= OnSnapshot;
        }

        private void OnSnapshot(in VSSnapshot snap)
        {
            // Basic safety checks
            if (snap.ApiVersion < 1)
                return;

            if (snap.PlayerState != VSPlayerState.Riding)
                return;

            // Are we grinding?
            bool isGrinding = snap.Flags.IsGrinding;

            // Get singleton controller that actually manages the VFX
            var ctrl = GrindFXController.Instance;
            if (!ctrl)
                return;

            // Update controller with the latest info
            ctrl.UpdateFromSnapshot(in snap, isGrinding);
        }
    }

    /// <summary>
    /// Handles creation and updating of grind sparks/flames.
    /// Uses particle systems at trucks and hands and reuses them.
    /// </summary>
    public class GrindFXController : MonoBehaviour
    {
        private static GrindFXController _instance;
        public static GrindFXController Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("GrindFXController");
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<GrindFXController>();
                }
                return _instance;
            }
        }

        // ==============================
        // Configurable fields
        // ==============================
        [Header("Truck FX Settings")]
        public bool enableFrontTruck = true;
        public bool enableBackTruck = true;
        public bool truckUseFlames = true; // false = sparks(broken idk why... yet...), true = flames (lets gooooo fireeeeeee)
        public float truckSizeMultiplier = 1.0f;
        public float truckEmissionRate = 60f;

        [Header("Hand FX Settings")]
        public bool enableHandFlames = true;
        public float handSizeMultiplier = 0.7f;
        public float handEmissionRate = 40f;

        // Internal particle systems
        private ParticleSystem _backFX;
        private ParticleSystem _frontFX;
        private ParticleSystem _leftHandFX;
        private ParticleSystem _rightHandFX;

        // Track last grind state to detect start/stop events
        private bool _wasGrinding;

        void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);

            CreateOrConfigureSystems();
        }

        void OnDestroy()
        {
            _instance = null;
        }

        /// <summary>
        /// Called by the mod when a new snapshot comes in.
        /// </summary>
        public void UpdateFromSnapshot(in VSSnapshot snap, bool isGrinding)
        {
            if (_backFX == null || _frontFX == null || _leftHandFX == null || _rightHandFX == null)
                CreateOrConfigureSystems();

            // ==============================
            // Position trucks
            // ==============================
            var back = snap.Board.BackTruckPivot;
            var front = snap.Board.FrontTruckPivot;

            if (enableBackTruck && _backFX != null)
            {
                _backFX.transform.position = back.position;
                _backFX.transform.rotation = back.rotation;
            }

            if (enableFrontTruck && _frontFX != null)
            {
                _frontFX.transform.position = front.position;
                _frontFX.transform.rotation = front.rotation;
            }

            // ==============================
            // Position hands (use hand poses)
            // ==============================
            if (enableHandFlames)
            {
                var leftHand = snap.LeftHand;
                var rightHand = snap.RightHand;

                if (_leftHandFX != null)
                {
                    _leftHandFX.transform.position = leftHand.position;
                    _leftHandFX.transform.rotation = leftHand.rotation;
                }

                if (_rightHandFX != null)
                {
                    _rightHandFX.transform.position = rightHand.position;
                    _rightHandFX.transform.rotation = rightHand.rotation;
                }
            }

            // ==============================
            // Grind start / end
            // ==============================
            if (isGrinding && !_wasGrinding)
            {
                OnGrindStart();
            }
            else if (!isGrinding && _wasGrinding)
            {
                OnGrindEnd();
            }

            _wasGrinding = isGrinding;

            // While grinding, keep emission active; when not, emission is off
            SetEmission(_backFX, isGrinding && enableBackTruck);
            SetEmission(_frontFX, isGrinding && enableFrontTruck);

            SetEmission(_leftHandFX, isGrinding && enableHandFlames);
            SetEmission(_rightHandFX, isGrinding && enableHandFlames);
        }

        // =========================================================
        // VFX creation & configuration
        // =========================================================

        private void CreateOrConfigureSystems()
        {
            // Trucks
            if (_backFX == null)
                _backFX = CreateFXObject("GrindFX_BackTruck");

            if (_frontFX == null)
                _frontFX = CreateFXObject("GrindFX_FrontTruck");

            ConfigureTruckFX(_backFX);
            ConfigureTruckFX(_frontFX);

            // Hands
            if (_leftHandFX == null)
                _leftHandFX = CreateFXObject("GrindFX_LeftHand");

            if (_rightHandFX == null)
                _rightHandFX = CreateFXObject("GrindFX_RightHand");

            ConfigureHandFX(_leftHandFX);
            ConfigureHandFX(_rightHandFX);
        }

        private ParticleSystem CreateFXObject(string name)
        {
            var go = new GameObject(name);
            go.transform.parent = transform;

            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = true;

            main.startLifetime = 0.25f;
            main.startSpeed = 3.0f;
            main.startSize = 0.06f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 256;

            var emission = ps.emission;
            emission.enabled = false;   // start disabled
            emission.rateOverTime = 0f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 15f;
            shape.radius = 0.02f;
            shape.rotation = new Vector3(0f, 0f, 0f);

            // Renderer
            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                renderer.material = GetDefaultParticleMaterial();
                renderer.sortingOrder = 3000;
            }

            return ps;
        }

        private void ConfigureTruckFX(ParticleSystem ps)
        {
            if (ps == null) return;

            var main = ps.main;

            if (!truckUseFlames)
            {
                // Sparks-style
                main.startLifetime = 0.15f;
                main.startSpeed = 4.5f;
                main.startSize = 0.05f * truckSizeMultiplier;
                main.startColor = new Color(1.0f, 0.9f, 0.6f); // warm spark
                main.gravityModifier = 0.4f;
            }
            else
            {
                // Flames-style
                main.startLifetime = 0.35f;
                main.startSpeed = 2.0f;
                main.startSize = 0.12f * truckSizeMultiplier;
                main.startColor = new Color(1.0f, 0.5f, 0.1f); // orange flame
                main.gravityModifier = 0.0f;
            }

            var emission = ps.emission;
            emission.rateOverTime = truckEmissionRate;
        }

        private void ConfigureHandFX(ParticleSystem ps)
        {
            if (ps == null) return;

            var main = ps.main;

            // Always flames for hands (on fire)
            main.startLifetime = 0.35f;
            main.startSpeed = 1.5f;
            main.startSize = 0.10f * handSizeMultiplier;
            main.startColor = new Color(1.0f, 0.5f, 0.1f); // orange flame
            main.gravityModifier = 0.0f;

            var emission = ps.emission;
            emission.rateOverTime = handEmissionRate;
        }

        private void SetEmission(ParticleSystem ps, bool enabled)
        {
            if (ps == null) return;
            var emission = ps.emission;
            emission.enabled = enabled;

            if (enabled && !ps.isPlaying)
                ps.Play();
            else if (!enabled && ps.isPlaying)
                ps.Stop();
        }

        private void OnGrindStart()
        {
            MelonLogger.Msg("[GrindFX] Grind start");
            Burst(_backFX, 10);
            Burst(_frontFX, 10);
            Burst(_leftHandFX, 8);
            Burst(_rightHandFX, 8);
        }

        private void OnGrindEnd()
        {
            MelonLogger.Msg("[GrindFX] Grind end");
        }

        private void Burst(ParticleSystem ps, int count)
        {
            if (ps == null) return;
            ps.Emit(count);
        }

        private Material GetDefaultParticleMaterial()
        {
            var shader = Shader.Find("Particles/Standard Unlit");
            if (shader != null)
            {
                var mat = new Material(shader);
                mat.SetFloat("_Mode", 2f); // Fade
                return mat;
            }

            return new Material(Shader.Find("Legacy Shaders/Particles/Additive"));
        }
    }
}
