using System.Collections.Generic;
using HoloTable.Simulation;
using UnityEngine;
using UnityEngine.EventSystems;

namespace HoloTable.Runtime
{
    /// <summary>
    /// Canonical AppMana EventSystem pointer adapter for the HoloTable surface.
    /// Receives per-user pointer move, enter/exit, and press/tap events from
    /// InputSystemUIInputModule + PerUserPhysicsRaycaster (strictly EventSystem pointer events only)
    /// and forwards local table coordinates into the pure <see cref="HoloTableSimulation"/> data model.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HoloTablePointerInput : UIBehaviour,
        IPointerEnterHandler,
        IPointerMoveHandler,
        IPointerExitHandler,
        IPointerDownHandler
    {
        [SerializeField] private HoloTableVfxVisualizer m_Visualizer;
        [SerializeField] private Transform m_TableSpaceOrigin;
        [SerializeField] private Camera m_FallbackCamera;
        [SerializeField] private float m_ClickWaveStrength = 1.0f;

        private readonly HashSet<int> m_ActivePointers = new HashSet<int>();
        private HoloTableSimulation m_Simulation;
        private int m_PrimaryPointerId;
        private bool m_HasPrimaryScreenPos;
        private Vector2 m_LastScreenPosition;
        private Camera m_LastEventCamera;

        public HoloTableSimulation Simulation => m_Simulation ?? (m_Visualizer != null ? m_Visualizer.Simulation : null);

        public Transform TableSpaceOrigin => m_TableSpaceOrigin != null
            ? m_TableSpaceOrigin
            : (m_Visualizer != null ? m_Visualizer.transform : transform);

        public int ActiveHoverCount => m_ActivePointers.Count;

        public void BindSimulation(HoloTableSimulation simulation, Transform tableSpaceOrigin = null)
        {
            m_Simulation = simulation;
            if (tableSpaceOrigin != null)
            {
                m_TableSpaceOrigin = tableSpaceOrigin;
            }
        }

        protected override void Awake()
        {
            base.Awake();
            ResolveReferences();
        }

        protected override void Start()
        {
            base.Start();
            ResolveReferences();
        }

        protected override void OnDisable()
        {
            m_ActivePointers.Clear();
            m_HasPrimaryScreenPos = false;
            Simulation?.ClearPointer();
            base.OnDisable();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (eventData == null)
            {
                return;
            }

            m_ActivePointers.Add(eventData.pointerId);
            m_PrimaryPointerId = eventData.pointerId;
            CachePointerScreenRaySource(eventData, isPress: false);

            if (TryExtractTableLocalXZ(eventData, isPress: false, out var localXZ))
            {
                Simulation?.SetPointerLocalXZ(localXZ);
            }
        }

        public void OnPointerMove(PointerEventData eventData)
        {
            if (eventData == null)
            {
                return;
            }

            m_ActivePointers.Add(eventData.pointerId);
            m_PrimaryPointerId = eventData.pointerId;
            CachePointerScreenRaySource(eventData, isPress: false);

            if (TryExtractTableLocalXZ(eventData, isPress: false, out var localXZ))
            {
                Simulation?.SetPointerLocalXZ(localXZ);
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData == null)
            {
                return;
            }

            m_ActivePointers.Add(eventData.pointerId);
            m_PrimaryPointerId = eventData.pointerId;
            CachePointerScreenRaySource(eventData, isPress: true);

            var sim = Simulation;
            if (sim != null && TryExtractTableLocalXZ(eventData, isPress: true, out var localXZ))
            {
                sim.SetPointerLocalXZ(localXZ);
                sim.TriggerWaveAtLocalXZ(localXZ, m_ClickWaveStrength);
            }
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (eventData == null)
            {
                return;
            }

            m_ActivePointers.Remove(eventData.pointerId);
            if (m_ActivePointers.Count == 0)
            {
                m_HasPrimaryScreenPos = false;
                Simulation?.ClearPointer();
            }
        }

        /// <summary>
        /// Re-projects the active hovering pointer screen coordinate onto the table plane so
        /// camera deceleration or OrbitOnIdle keeps the mountain + targeter locked under a stationary cursor.
        /// </summary>
        public void RefreshStationaryHoverProjection()
        {
            if (m_ActivePointers.Count == 0 || !m_HasPrimaryScreenPos)
            {
                return;
            }

            var sim = Simulation;
            if (sim == null)
            {
                return;
            }

            var cam = m_LastEventCamera != null ? m_LastEventCamera : ResolveFallbackCamera();
            if (cam == null)
            {
                return;
            }

            if (TryProjectScreenPointToTableLocalXZ(cam, m_LastScreenPosition, out var localXZ))
            {
                sim.SetPointerLocalXZ(localXZ);
            }
        }

        public bool TryProjectScreenPointToTableLocalXZ(Camera eventCamera, Vector2 screenPosition, out Vector2 localXZ)
        {
            localXZ = Vector2.zero;
            if (eventCamera == null)
            {
                return false;
            }

            var originTransform = TableSpaceOrigin;
            if (originTransform == null)
            {
                return false;
            }

            var plane = new Plane(originTransform.up, transform.position);
            var ray = eventCamera.ScreenPointToRay(new Vector3(screenPosition.x, screenPosition.y, 0f));
            if (!plane.Raycast(ray, out float enter) || enter <= 0f || enter > 250f)
            {
                return false;
            }

            var worldHit = ray.GetPoint(enter);
            var localPos = originTransform.InverseTransformPoint(worldHit);
            localXZ = new Vector2(localPos.x, localPos.z);
            return true;
        }

        private void CachePointerScreenRaySource(PointerEventData eventData, bool isPress)
        {
            m_LastScreenPosition = eventData.position;
            m_HasPrimaryScreenPos = true;

            var eventCam = isPress && eventData.pressEventCamera != null
                ? eventData.pressEventCamera
                : eventData.enterEventCamera;
            if (eventCam != null)
            {
                m_LastEventCamera = eventCam;
            }
        }

        private bool TryExtractTableLocalXZ(PointerEventData eventData, bool isPress, out Vector2 localXZ)
        {
            var originTransform = TableSpaceOrigin;
            if (originTransform == null)
            {
                localXZ = Vector2.zero;
                return false;
            }

            var raycast = isPress && eventData.pointerPressRaycast.isValid
                ? eventData.pointerPressRaycast
                : eventData.pointerCurrentRaycast;

            if (raycast.isValid && (raycast.distance > 0f || raycast.worldPosition.sqrMagnitude > 1e-6f))
            {
                var localPos = originTransform.InverseTransformPoint(raycast.worldPosition);
                localXZ = new Vector2(localPos.x, localPos.z);
                return true;
            }

            var cam = (isPress ? eventData.pressEventCamera : null)
                      ?? eventData.enterEventCamera
                      ?? m_LastEventCamera
                      ?? ResolveFallbackCamera();
            return TryProjectScreenPointToTableLocalXZ(cam, eventData.position, out localXZ);
        }

        private void ResolveReferences()
        {
            if (m_Visualizer == null)
            {
                m_Visualizer = GetComponentInParent<HoloTableVfxVisualizer>();
            }

            if (m_TableSpaceOrigin == null && m_Visualizer != null)
            {
                m_TableSpaceOrigin = m_Visualizer.transform;
            }
        }

        private Camera ResolveFallbackCamera()
        {
            if (m_FallbackCamera == null)
            {
                m_FallbackCamera = Camera.main;
            }

            return m_FallbackCamera;
        }
    }
}
