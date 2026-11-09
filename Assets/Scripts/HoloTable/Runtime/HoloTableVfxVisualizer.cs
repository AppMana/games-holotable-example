using HoloTable.Simulation;
using UnityEngine;
using UnityEngine.VFX;
using UnityEngine.VFX.Utility;

namespace HoloTable.Runtime
{
    /// <summary>
    /// Visualization layer driving the Spaceship HoloTable VFX Graph and subsurface targeter light
    /// from the pure <see cref="HoloTableSimulation"/> data model.
    /// Uploads compact Vector4 and Matrix4x4 simulation uniforms directly to VFX Graph GPU compute
    /// with zero dynamic textures, zero D3D12 texture uploads, and zero GC allocations.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HoloTableVfxVisualizer : MonoBehaviour
    {
        private static readonly int s_InteractionCursorId = Shader.PropertyToID("InteractionCursor");
        private static readonly int s_InteractionMountainParamsId = Shader.PropertyToID("InteractionMountainParams");
        private static readonly int s_InteractionTargeterParamsId = Shader.PropertyToID("InteractionTargeterParams");
        private static readonly int s_InteractionWaveParamsId = Shader.PropertyToID("InteractionWaveParams");
        private static readonly int s_InteractionWaves0Id = Shader.PropertyToID("InteractionWaves0");
        private static readonly int s_InteractionWaves1Id = Shader.PropertyToID("InteractionWaves1");
        private static readonly int s_InteractionTargetColorId = Shader.PropertyToID("InteractionTargetColor");
        private static readonly int s_InteractionWaveColorId = Shader.PropertyToID("InteractionWaveColor");
        private static readonly int s_LandingTargetPositionId = Shader.PropertyToID("LandingTargetPosition");
        private static readonly int s_DisplayTargetId = Shader.PropertyToID("DisplayTarget");
        private static readonly int s_AABoxCenterId = Shader.PropertyToID("AABoxControl_center");
        private static readonly int s_AABoxSizeId = Shader.PropertyToID("AABoxControl_size");

        [Header("Simulation Configuration")]
        [SerializeField] private HoloTableSimulationConfig m_Config = HoloTableSimulationConfig.Default;

        [Header("Scene & VFX References")]
        [SerializeField] private VisualEffect m_VisualEffect;
        [SerializeField] private HoloTablePointerInput m_PointerInput;
        [SerializeField] private Transform m_TargetAnchorTransform;
        [SerializeField] private Light m_TargetSubsurfaceLight;

        [Header("Holographic Palette (Linear HDR)")]
        [SerializeField, ColorUsage(true, true)]
        private Color m_TargeterColor = new Color(0.42f, 0.045f, 0.004f, 1.0f);

        [SerializeField, ColorUsage(true, true)]
        private Color m_WaveColor = new Color(0.005f, 0.22f, 0.65f, 1.0f);

        [SerializeField] private Vector3 m_AmbientTerrainCenter = new Vector3(-0.35f, 0.11f, -0.25f);
        [SerializeField] private Vector3 m_AmbientTerrainSize = new Vector3(8.5f, 1.35f, 8.5f);
        [SerializeField] private float m_TargetLightPeakIntensity = 4.5f;

        private HoloTableSimulation m_Simulation;
        private Vector4 m_LastPackedCursorState;
        private Matrix4x4 m_LastPackedWaves0;
        private Matrix4x4 m_LastPackedWaves1;
        private int m_LastPackedWaveCount;

        private bool m_HasCursorProp;
        private bool m_HasMountainParamsProp;
        private bool m_HasTargeterParamsProp;
        private bool m_HasWaveParamsProp;
        private bool m_HasWaves0Prop;
        private bool m_HasWaves1Prop;
        private bool m_HasTargetColorProp;
        private bool m_HasWaveColorProp;
        private bool m_HasLandingTargetProp;
        private bool m_HasDisplayTargetProp;
        private bool m_HasAABoxCenterProp;
        private bool m_HasAABoxSizeProp;

        public HoloTableSimulation Simulation => m_Simulation;

        public VisualEffect VisualEffectComponent => m_VisualEffect;

        public HoloTablePointerInput PointerInput => m_PointerInput;

        public Light TargetSubsurfaceLight => m_TargetSubsurfaceLight;

        public Vector4 LastPackedCursorState => m_LastPackedCursorState;

        public Matrix4x4 LastPackedWaves0 => m_LastPackedWaves0;

        public Matrix4x4 LastPackedWaves1 => m_LastPackedWaves1;

        public int LastPackedWaveCount => m_LastPackedWaveCount;

        private void Awake()
        {
            m_Simulation = new HoloTableSimulation(m_Config);

            ResolveSceneReferences();
            DisableLegacyRandomAnimationRig();
            CacheVfxPropertyAvailability();

            if (m_PointerInput != null)
            {
                m_PointerInput.BindSimulation(m_Simulation, transform);
            }

            PushStaticVfxParameters();
            PushSimulationStateToVisuals();
        }

        private void OnEnable()
        {
            if (m_PointerInput != null && m_Simulation != null)
            {
                m_PointerInput.BindSimulation(m_Simulation, transform);
            }
        }

        private void Update()
        {
            if (m_Simulation == null)
            {
                return;
            }

            if (m_PointerInput != null)
            {
                m_PointerInput.RefreshStationaryHoverProjection();
            }

            m_Simulation.Step(Time.deltaTime);
            PushSimulationStateToVisuals();
        }

        /// <summary>
        /// Immediately synchronizes the CPU simulation state into compact GPU VFX uniform properties.
        /// Public so integration tests can deterministically step and verify GPU-bound state.
        /// </summary>
        public void PushSimulationStateToVisuals()
        {
            if (m_Simulation == null)
            {
                return;
            }

            m_LastPackedWaveCount = m_Simulation.PackGpuUniforms(
                out m_LastPackedCursorState,
                out var mountainParams,
                out var targeterParams,
                out var waveParams,
                out m_LastPackedWaves0,
                out m_LastPackedWaves1);

            var localTargetPos = m_Simulation.CurrentTargetLocalPosition;
            bool showTarget = m_Simulation.TargeterPresence > 0.005f;

            if (m_VisualEffect != null)
            {
                if (m_HasCursorProp)
                {
                    m_VisualEffect.SetVector4(s_InteractionCursorId, m_LastPackedCursorState);
                }

                if (m_HasMountainParamsProp)
                {
                    m_VisualEffect.SetVector4(s_InteractionMountainParamsId, mountainParams);
                }

                if (m_HasTargeterParamsProp)
                {
                    m_VisualEffect.SetVector4(s_InteractionTargeterParamsId, targeterParams);
                }

                if (m_HasWaveParamsProp)
                {
                    m_VisualEffect.SetVector4(s_InteractionWaveParamsId, waveParams);
                }

                if (m_HasWaves0Prop)
                {
                    m_VisualEffect.SetMatrix4x4(s_InteractionWaves0Id, m_LastPackedWaves0);
                }

                if (m_HasWaves1Prop)
                {
                    m_VisualEffect.SetMatrix4x4(s_InteractionWaves1Id, m_LastPackedWaves1);
                }

                if (m_HasLandingTargetProp)
                {
                    m_VisualEffect.SetVector3(s_LandingTargetPositionId, localTargetPos);
                }

                if (m_HasDisplayTargetProp)
                {
                    m_VisualEffect.SetBool(s_DisplayTargetId, showTarget);
                }
            }

            UpdateTargetSubsurfaceLight(localTargetPos, showTarget);
        }

        private void PushStaticVfxParameters()
        {
            if (m_VisualEffect == null)
            {
                return;
            }

            if (m_HasTargetColorProp)
            {
                m_VisualEffect.SetVector4(s_InteractionTargetColorId, (Vector4)m_TargeterColor);
            }

            if (m_HasWaveColorProp)
            {
                m_VisualEffect.SetVector4(s_InteractionWaveColorId, (Vector4)m_WaveColor);
            }

            if (m_HasAABoxCenterProp)
            {
                m_VisualEffect.SetVector3(s_AABoxCenterId, m_AmbientTerrainCenter);
            }

            if (m_HasAABoxSizeProp)
            {
                m_VisualEffect.SetVector3(s_AABoxSizeId, m_AmbientTerrainSize);
            }
        }

        private void UpdateTargetSubsurfaceLight(Vector3 localTargetPos, bool showTarget)
        {
            if (m_TargetAnchorTransform != null)
            {
                m_TargetAnchorTransform.position = transform.TransformPoint(new Vector3(localTargetPos.x, 0.11f, localTargetPos.z));
            }

            if (m_TargetSubsurfaceLight != null)
            {
                var lightGo = m_TargetSubsurfaceLight.gameObject;
                if (lightGo.activeSelf != showTarget)
                {
                    lightGo.SetActive(showTarget);
                }

                if (showTarget)
                {
                    m_TargetSubsurfaceLight.intensity = m_TargetLightPeakIntensity * m_Simulation.TargeterPresence;
                }
            }
        }

        private void ResolveSceneReferences()
        {
            if (m_VisualEffect == null)
            {
                m_VisualEffect = GetComponent<VisualEffect>();
            }

            if (m_PointerInput == null)
            {
                m_PointerInput = GetComponentInChildren<HoloTablePointerInput>(true);
            }

            if (m_TargetAnchorTransform == null)
            {
                var mapController = transform.Find("MapPosition-Controller/User Position")
                                    ?? transform.Find("MapPosition-Controller");
                if (mapController != null)
                {
                    m_TargetAnchorTransform = mapController;
                }
            }

            if (m_TargetSubsurfaceLight == null && m_TargetAnchorTransform != null)
            {
                m_TargetSubsurfaceLight = m_TargetAnchorTransform.GetComponentInChildren<Light>(true);
            }
        }

        private void DisableLegacyRandomAnimationRig()
        {
            var anim = GetComponent<Animator>();
            if (anim != null)
            {
                anim.enabled = false;
            }

            var binder = GetComponent<VFXPropertyBinder>();
            if (binder != null)
            {
                binder.enabled = false;
            }

            if (m_TargetAnchorTransform != null)
            {
                var legacyCollider = m_TargetAnchorTransform.GetComponent<Collider>();
                if (legacyCollider != null && (m_PointerInput == null || legacyCollider.gameObject != m_PointerInput.gameObject))
                {
                    legacyCollider.enabled = false;
                }
            }
        }

        private void CacheVfxPropertyAvailability()
        {
            if (m_VisualEffect == null)
            {
                return;
            }

            m_HasCursorProp = m_VisualEffect.HasVector4(s_InteractionCursorId);
            m_HasMountainParamsProp = m_VisualEffect.HasVector4(s_InteractionMountainParamsId);
            m_HasTargeterParamsProp = m_VisualEffect.HasVector4(s_InteractionTargeterParamsId);
            m_HasWaveParamsProp = m_VisualEffect.HasVector4(s_InteractionWaveParamsId);
            m_HasWaves0Prop = m_VisualEffect.HasMatrix4x4(s_InteractionWaves0Id);
            m_HasWaves1Prop = m_VisualEffect.HasMatrix4x4(s_InteractionWaves1Id);
            m_HasTargetColorProp = m_VisualEffect.HasVector4(s_InteractionTargetColorId);
            m_HasWaveColorProp = m_VisualEffect.HasVector4(s_InteractionWaveColorId);
            m_HasLandingTargetProp = m_VisualEffect.HasVector3(s_LandingTargetPositionId);
            m_HasDisplayTargetProp = m_VisualEffect.HasBool(s_DisplayTargetId);
            m_HasAABoxCenterProp = m_VisualEffect.HasVector3(s_AABoxCenterId);
            m_HasAABoxSizeProp = m_VisualEffect.HasVector3(s_AABoxSizeId);
        }
    }
}
