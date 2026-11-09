using HoloTable.Simulation;
using Unity.Collections;
using UnityEngine;
using UnityEngine.VFX;
using UnityEngine.VFX.Utility;

namespace HoloTable.Runtime
{
    /// <summary>
    /// Visualization layer driving the Spaceship HoloTable VFX Graph and subsurface targeter light
    /// from the pure <see cref="HoloTableSimulation"/> data model.
    /// Uploads a pre-allocated RGBAHalf interaction height/mask texture each frame with zero GC allocations.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HoloTableVfxVisualizer : MonoBehaviour
    {
        private static readonly int s_InteractionFieldId = Shader.PropertyToID("InteractionField");
        private static readonly int s_InteractionTargetColorId = Shader.PropertyToID("InteractionTargetColor");
        private static readonly int s_InteractionWaveColorId = Shader.PropertyToID("InteractionWaveColor");
        private static readonly int s_LandingTargetPositionId = Shader.PropertyToID("LandingTargetPosition");
        private static readonly int s_DisplayTargetId = Shader.PropertyToID("DisplayTarget");
        private static readonly int s_AABoxCenterId = Shader.PropertyToID("AABoxControl_center");
        private static readonly int s_AABoxSizeId = Shader.PropertyToID("AABoxControl_size");

        [Header("Simulation Configuration")]
        [SerializeField] private HoloTableSimulationConfig m_Config = HoloTableSimulationConfig.Default;
        [SerializeField, Range(64, 256)] private int m_FieldResolution = 192;

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
        private Texture2D m_InteractionTexture;
        private NativeArray<ushort> m_PixelBuffer;
        private bool m_HasInteractionFieldProp;
        private bool m_HasTargetColorProp;
        private bool m_HasWaveColorProp;
        private bool m_HasLandingTargetProp;
        private bool m_HasDisplayTargetProp;
        private bool m_HasAABoxCenterProp;
        private bool m_HasAABoxSizeProp;

        public HoloTableSimulation Simulation => m_Simulation;

        public Texture2D InteractionTexture => m_InteractionTexture;

        public int FieldResolution => m_FieldResolution;

        public VisualEffect VisualEffectComponent => m_VisualEffect;

        public HoloTablePointerInput PointerInput => m_PointerInput;

        public Light TargetSubsurfaceLight => m_TargetSubsurfaceLight;

        private void Awake()
        {
            m_FieldResolution = Mathf.Clamp(m_FieldResolution, 64, 256);
            m_Simulation = new HoloTableSimulation(m_Config);

            ResolveSceneReferences();
            DisableLegacyRandomAnimationRig();
            AllocateInteractionTexture();
            CacheVfxPropertyAvailability();

            if (m_PointerInput != null)
            {
                m_PointerInput.BindSimulation(m_Simulation, transform);
            }

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

        private void OnDestroy()
        {
            if (m_PixelBuffer.IsCreated)
            {
                m_PixelBuffer.Dispose();
            }

            if (m_InteractionTexture != null)
            {
                Destroy(m_InteractionTexture);
                m_InteractionTexture = null;
            }
        }

        /// <summary>
        /// Immediately synchronizes the CPU simulation state into the GPU texture and VFX properties.
        /// Public so integration tests can deterministically step and verify GPU-bound state.
        /// </summary>
        public void PushSimulationStateToVisuals()
        {
            if (m_Simulation == null || !m_PixelBuffer.IsCreated || m_InteractionTexture == null)
            {
                return;
            }

            m_Simulation.WriteInteractionFieldHalf4(m_PixelBuffer.AsSpan(), m_FieldResolution, m_FieldResolution);
            m_InteractionTexture.SetPixelData(m_PixelBuffer, 0);
            m_InteractionTexture.Apply(updateMipmaps: false, makeNoLongerReadable: false);

            var localTargetPos = m_Simulation.CurrentTargetLocalPosition;
            bool showTarget = m_Simulation.TargeterPresence > 0.005f;

            if (m_VisualEffect != null)
            {
                if (m_HasInteractionFieldProp)
                {
                    m_VisualEffect.SetTexture(s_InteractionFieldId, m_InteractionTexture);
                }

                if (m_HasTargetColorProp)
                {
                    m_VisualEffect.SetVector4(s_InteractionTargetColorId, (Vector4)m_TargeterColor);
                }

                if (m_HasWaveColorProp)
                {
                    m_VisualEffect.SetVector4(s_InteractionWaveColorId, (Vector4)m_WaveColor);
                }

                if (m_HasLandingTargetProp)
                {
                    m_VisualEffect.SetVector3(s_LandingTargetPositionId, localTargetPos);
                }

                if (m_HasDisplayTargetProp)
                {
                    m_VisualEffect.SetBool(s_DisplayTargetId, showTarget);
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

            UpdateTargetSubsurfaceLight(localTargetPos, showTarget);
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

        private void AllocateInteractionTexture()
        {
            int elementCount = m_FieldResolution * m_FieldResolution * 4;
            m_PixelBuffer = new NativeArray<ushort>(elementCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            m_InteractionTexture = new Texture2D(
                m_FieldResolution,
                m_FieldResolution,
                TextureFormat.RGBAHalf,
                mipChain: false,
                linear: true)
            {
                name = "HoloTableInteractionField",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
        }

        private void CacheVfxPropertyAvailability()
        {
            if (m_VisualEffect == null)
            {
                return;
            }

            m_HasInteractionFieldProp = m_VisualEffect.HasTexture(s_InteractionFieldId);
            m_HasTargetColorProp = m_VisualEffect.HasVector4(s_InteractionTargetColorId);
            m_HasWaveColorProp = m_VisualEffect.HasVector4(s_InteractionWaveColorId);
            m_HasLandingTargetProp = m_VisualEffect.HasVector3(s_LandingTargetPositionId);
            m_HasDisplayTargetProp = m_VisualEffect.HasBool(s_DisplayTargetId);
            m_HasAABoxCenterProp = m_VisualEffect.HasVector3(s_AABoxCenterId);
            m_HasAABoxSizeProp = m_VisualEffect.HasVector3(s_AABoxSizeId);
        }
    }
}
