using AppMana.ComponentModel;
using AppManaPublic.Configuration;
using Cinemachine;
using UniRx;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace AppMana.InteractionToolkit
{
    public class OrbitOnIdle : UIBehaviour, IHasInputActionReferences
    {
        [SerializeField] private InputActionReference m_CancelsIdle;
        [SerializeField] private CinemachineVirtualCameraBase m_Camera;
        [SerializeField] private float m_HorizontalOrbitSpeed = 1f;
        [SerializeField] private float m_Timeout = 3f;
        [SerializeField] private float m_Acceleration = 1f;
        [SerializeField] private float m_MinimumInteractionTime = 1f;

        public IHasInputActionReferences.InputActionReferenceProperty[] inputActionReferenceProperties => new[]
        {
            new IHasInputActionReferences.InputActionReferenceProperty(
                () => m_CancelsIdle,
                value => m_CancelsIdle = value)
        };

        protected override void Start()
        {
            base.Start();

            if (m_Camera == null)
            {
                m_Camera = GetComponent<CinemachineVirtualCameraBase>();
            }

            if (m_Camera == null)
            {
                return;
            }

            var pov = m_Camera.GetComponentInChildren<CinemachinePOV>();
            if (pov == null)
            {
                return;
            }

            var speed = m_HorizontalOrbitSpeed;
            var cancelAction = ResolveCancelAction();

            if (cancelAction != null)
            {
                cancelAction.OnPerformedAsObservable()
                    .Where(ctx => ctx.time > m_MinimumInteractionTime)
                    .Subscribe(ctx =>
                    {
                        if (ctx.control is ButtonControl { wasReleasedThisFrame: true })
                        {
                            return;
                        }

                        speed = -m_Acceleration * m_Timeout;
                    })
                    .AddTo(this);
            }

            Observable.EveryUpdate()
                .Subscribe(_ =>
                {
                    if (pov == null)
                    {
                        return;
                    }

                    if (speed < m_HorizontalOrbitSpeed)
                    {
                        speed += m_Acceleration * Time.smoothDeltaTime;
                    }

                    var delta = Mathf.Clamp(speed, 0f, m_HorizontalOrbitSpeed) * Time.deltaTime;
                    var wrapped = Mathf.Repeat(pov.m_HorizontalAxis.Value + delta + 180f, 360f) - 180f;
                    if (float.IsFinite(wrapped))
                    {
                        pov.m_HorizontalAxis.Value = wrapped;
                    }
                })
                .AddTo(this);
        }

        private InputAction ResolveCancelAction()
        {
            if (m_CancelsIdle == null)
            {
                return null;
            }

            var config = GetComponentInParent<RemotePlayableConfiguration>() ??
                         FindFirstObjectByType<RemotePlayableConfiguration>();
            if (config != null && config.actions != null)
            {
                var sourceAction = m_CancelsIdle.action;
                if (sourceAction != null)
                {
                    var mapped = config.actions.FindAction(sourceAction.id) ??
                                 config.actions.FindAction(sourceAction.name, false);
                    if (mapped != null)
                    {
                        return mapped;
                    }
                }
            }

            return m_CancelsIdle.action;
        }
    }
}
