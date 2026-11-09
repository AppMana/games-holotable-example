using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AppMana.ComponentModel;
using AppMana.Multiplayer;
using Cinemachine;
using HoloTable.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Profiling;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.VFX;
using Debug = UnityEngine.Debug;

namespace HoloTable.Tests.PlayMode
{
    public sealed class HoloTablePlayModeTests
    {
        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = false;
        }

        [UnityTest, Order(1)]
        public IEnumerator GameScene_RestoresBakedLightingPostProcessingAndVfxParticles_WithZeroErrors()
        {
            yield return EnsureGameSceneLoaded();

            for (int i = 0; i < 20; i++)
            {
                yield return null;
            }

            // Part B2: Baked lightmaps & reflection probes restored via LightingData.asset
            Assert.IsNotNull(LightmapSettings.lightmaps);
            Assert.AreEqual(10, LightmapSettings.lightmaps.Length, "Game scene should load all 10 baked lightmap sets.");

            var probes = UnityEngine.Object.FindObjectsByType<HDProbe>(FindObjectsSortMode.None);
            Assert.That(probes.Length, Is.GreaterThanOrEqualTo(4));
            int validBakedProbes = 0;
            foreach (var probe in probes)
            {
                if (probe.bakedTexture != null)
                {
                    validBakedProbes++;
                }
            }
            Assert.That(validBakedProbes, Is.GreaterThanOrEqualTo(4), "All baked HDProbe reflection probes must resolve non-null textures.");

            // Part B3: Main Camera volumeLayerMask includes Layer 8 (PostProcessing)
            var mainCam = Camera.main;
            Assert.IsNotNull(mainCam, "Main Camera must exist in Game scene.");
            var hdCam = mainCam.GetComponent<HDAdditionalCameraData>();
            Assert.IsNotNull(hdCam, "Main Camera must have HDAdditionalCameraData.");
            Assert.AreNotEqual(0, hdCam.volumeLayerMask.value & (1 << 8),
                "Main Camera volumeLayerMask must include Layer 8 (PostProcessing).");

            // Part A: Mipmap streaming active & PerUserGraphicRaycaster wired on Canvas
            Assert.IsTrue(QualitySettings.streamingMipmapsActive, "Mipmap streaming should be enabled.");
            var canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
            Assert.IsNotNull(canvas);
            Assert.IsNotNull(canvas.GetComponent<PerUserGraphicRaycaster>(),
                "Scene Canvas must use PerUserGraphicRaycaster directly.");
            var plainRaycaster = canvas.GetComponent<GraphicRaycaster>();
            Assert.IsTrue(plainRaycaster is PerUserGraphicRaycaster);

            // Part B1 & Part C: 65,536 HoloTable pins alive and zero-texture GPU uniform parameters exposed
            var visualizer = UnityEngine.Object.FindFirstObjectByType<HoloTableVfxVisualizer>();
            Assert.IsNotNull(visualizer, "HoloTableVfxVisualizer must be active on VFX:PinScreen.");
            var vfx = visualizer.VisualEffectComponent;
            Assert.IsNotNull(vfx);

            Assert.IsFalse(vfx.HasTexture("InteractionField"),
                "Dynamic CPU Texture2D InteractionField must be eliminated in favor of GPU uniform vectors/matrices.");
            Assert.IsTrue(vfx.HasVector4("InteractionCursor"));
            Assert.IsTrue(vfx.HasVector4("InteractionMountainParams"));
            Assert.IsTrue(vfx.HasVector4("InteractionTargeterParams"));
            Assert.IsTrue(vfx.HasVector4("InteractionWaveParams"));
            Assert.IsTrue(vfx.HasMatrix4x4("InteractionWaves0"));
            Assert.IsTrue(vfx.HasMatrix4x4("InteractionWaves1"));
            Assert.IsTrue(vfx.HasVector4("InteractionTargetColor"));
            Assert.IsTrue(vfx.HasVector4("InteractionWaveColor"));
            Assert.IsTrue(vfx.HasVector3("LandingTargetPosition"));
            Assert.IsTrue(vfx.HasBool("DisplayTarget"));
            Assert.AreEqual(50f, vfx.GetFloat("BlendSpeed"), 1e-3f,
                "PinScreen VFX BlendSpeed must default to crisp 50/s low-latency tracking.");
            Assert.AreEqual(65536, vfx.aliveParticleCount, "All 65,536 holotable pins must spawn and compile cleanly.");
        }

        [UnityTest, Order(2)]
        public IEnumerator CanonicalEventSystemPointer_RealTimeCursorMountainTargeterAndClickWave_DriveVfxUniforms()
        {
            yield return EnsureGameSceneLoaded();

            for (int i = 0; i < 12; i++)
            {
                yield return null;
            }

            var mainCam = Camera.main;
            var eventSystem = EventSystem.current;
            var visualizer = UnityEngine.Object.FindFirstObjectByType<HoloTableVfxVisualizer>();
            var pointerInput = visualizer.PointerInput;
            var vfx = visualizer.VisualEffectComponent;

            Assert.IsNotNull(mainCam);
            Assert.IsNotNull(eventSystem);
            Assert.IsNotNull(visualizer);
            Assert.IsNotNull(pointerInput);
            Assert.IsNotNull(mainCam.GetComponent<PerUserPhysicsRaycaster>(),
                "Main Camera must use canonical PerUserPhysicsRaycaster.");

            // Idle before pointer enters
            Assert.IsFalse(visualizer.Simulation.HasPointerInput);
            Assert.IsFalse(vfx.GetBool("DisplayTarget"));
            Assert.IsFalse(visualizer.TargetSubsurfaceLight.gameObject.activeSelf);

            // 1. Hover pointer at table coordinate A through EventSystem.RaycastAll -> ExecuteEvents
            var localPosA = new Vector2(-0.85f, -0.40f);
            var screenPosA = ProjectTableLocalToScreen(mainCam, pointerInput, visualizer.transform, localPosA);

            var eventData = RaycastCanonicalPointer(eventSystem, screenPosA, pointerId: 1);
            Assert.IsTrue(eventData.pointerCurrentRaycast.isValid,
                "EventSystem + PerUserPhysicsRaycaster must hit HoloTablePointerSurface.");
            var hitTarget = eventData.pointerCurrentRaycast.gameObject;
            Assert.AreEqual(pointerInput.gameObject, hitTarget);

            ExecuteEvents.ExecuteHierarchy(hitTarget, eventData, ExecuteEvents.pointerEnterHandler);
            ExecuteEvents.ExecuteHierarchy(hitTarget, eventData, ExecuteEvents.pointerMoveHandler);

            // Verify zero-frame-delay LateUpdate push: before the next frame's Update even runs,
            // the current frame's LateUpdate must already push InteractionCursor & DisplayTarget to VFX!
            yield return null;
            var firstFrameCursor = vfx.GetVector4("InteractionCursor");
            Assert.That(Vector2.Distance(new Vector2(firstFrameCursor.x, firstFrameCursor.y), localPosA), Is.LessThan(0.12f),
                "HoloTableVfxVisualizer.LateUpdate must push updated cursor XZ to VFX uniforms on the exact same frame.");
            Assert.That(firstFrameCursor.z, Is.GreaterThan(0f),
                "HoloTableVfxVisualizer.LateUpdate must advance TargeterPresence > 0 on the exact same frame.");

            float fadeInDeadline = Time.realtimeSinceStartup + 2.0f;
            while (visualizer.Simulation.TargeterPresence < 0.99f && Time.realtimeSinceStartup < fadeInDeadline)
            {
                yield return null;
            }
            yield return null;

            Assert.IsTrue(visualizer.Simulation.HasPointerInput);
            Assert.That(Vector2.Distance(visualizer.Simulation.CurrentCursorLocalXZ, localPosA), Is.LessThan(0.12f));
            Assert.IsTrue(vfx.GetBool("DisplayTarget"));
            Assert.IsTrue(visualizer.TargetSubsurfaceLight.gameObject.activeSelf);
            Assert.That(visualizer.TargetSubsurfaceLight.intensity, Is.GreaterThan(3.5f));
            Assert.That(Vector3.Distance(vfx.GetVector3("LandingTargetPosition"), visualizer.Simulation.CurrentTargetLocalPosition),
                Is.LessThan(1e-4f));

            SampleGpuUniformsAtLocalXZ(
                vfx,
                visualizer.Simulation.CurrentCursorLocalXZ,
                out float heightAtA,
                out float targetMaskAtA,
                out _,
                out float mountainMaskAtA);
            Assert.That(heightAtA, Is.GreaterThan(0.28f), "GPU VFX uniforms must evaluate elevated mountain peak at cursor A.");
            Assert.That(targetMaskAtA, Is.GreaterThan(0.90f), "GPU VFX uniforms must evaluate targeter reticle bullseye at cursor A.");
            Assert.That(mountainMaskAtA, Is.GreaterThan(0.85f));

            // 2. Move pointer to table coordinate B and confirm mountain + targeter shift in real time
            var localPosB = new Vector2(0.95f, 0.45f);
            var screenPosB = ProjectTableLocalToScreen(mainCam, pointerInput, visualizer.transform, localPosB);
            var moveDataB = RaycastCanonicalPointer(eventSystem, screenPosB, pointerId: 1);
            Assert.AreEqual(pointerInput.gameObject, moveDataB.pointerCurrentRaycast.gameObject);
            ExecuteEvents.ExecuteHierarchy(pointerInput.gameObject, moveDataB, ExecuteEvents.pointerMoveHandler);

            float moveDeadline = Time.realtimeSinceStartup + 2.0f;
            while (Vector2.Distance(visualizer.Simulation.CurrentCursorLocalXZ, localPosB) > 0.08f &&
                   Time.realtimeSinceStartup < moveDeadline)
            {
                yield return null;
            }
            yield return null;

            Assert.That(Vector2.Distance(visualizer.Simulation.CurrentCursorLocalXZ, localPosB), Is.LessThan(0.12f));
            SampleGpuUniformsAtLocalXZ(
                vfx,
                visualizer.Simulation.CurrentCursorLocalXZ,
                out float heightAtB,
                out float targetMaskAtB,
                out _,
                out _);
            SampleGpuUniformsAtLocalXZ(
                vfx,
                localPosA,
                out float residualHeightAtA,
                out _,
                out _,
                out _);
            Assert.That(heightAtB, Is.GreaterThan(0.28f));
            Assert.That(targetMaskAtB, Is.GreaterThan(0.90f));
            Assert.That(residualHeightAtA, Is.LessThan(0.02f), "Mountain at previous cursor A must subside when cursor moves to B.");

            // 3. Dispatch canonical pointer click/tap (IPointerDownHandler) at B -> verify radial wave spawns & expands
            var pressDataB = RaycastCanonicalPointer(eventSystem, screenPosB, pointerId: 1);
            pressDataB.pointerPressRaycast = pressDataB.pointerCurrentRaycast;
            pressDataB.pressPosition = screenPosB;
            ExecuteEvents.ExecuteHierarchy(pointerInput.gameObject, pressDataB, ExecuteEvents.pointerDownHandler);

            Assert.AreEqual(1, visualizer.Simulation.ActiveWaveCount, "Pointer press/tap must spawn an active radial wave.");

            float waveDeadline = Time.realtimeSinceStartup + 2.5f;
            while (visualizer.Simulation.ActiveWaveCount > 0 &&
                   visualizer.Simulation.GetActiveWave(0).FrontRadius < 0.85f &&
                   Time.realtimeSinceStartup < waveDeadline)
            {
                yield return null;
            }

            Assert.AreEqual(1, visualizer.Simulation.ActiveWaveCount);
            var wave = visualizer.Simulation.GetActiveWave(0);
            var crestPoint = wave.OriginLocalXZ + new Vector2(-wave.FrontRadius, 0f);
            SampleGpuUniformsAtLocalXZ(
                vfx,
                crestPoint,
                out float crestHeight,
                out _,
                out float crestGlow,
                out _);
            Assert.That(crestGlow, Is.GreaterThan(0.25f), "Bound VFX wave matrices must carry bright wave crest glow at FrontRadius.");
            Assert.That(crestHeight, Is.GreaterThan(0.04f), "Bound VFX wave matrices must carry positive wave displacement at FrontRadius.");

            // 4. Pointer exit -> targeter & subsurface light turn off cleanly
            ExecuteEvents.ExecuteHierarchy(pointerInput.gameObject, pressDataB, ExecuteEvents.pointerExitHandler);
            float fadeOutDeadline = Time.realtimeSinceStartup + 2.5f;
            while (visualizer.Simulation.TargeterPresence > 0.001f && Time.realtimeSinceStartup < fadeOutDeadline)
            {
                yield return null;
            }
            yield return null;

            Assert.IsFalse(visualizer.Simulation.HasPointerInput);
            Assert.IsFalse(vfx.GetBool("DisplayTarget"));
            Assert.IsFalse(visualizer.TargetSubsurfaceLight.gameObject.activeSelf);

            float waveClearDeadline = Time.realtimeSinceStartup + 3.5f;
            while (visualizer.Simulation.ActiveWaveCount > 0 && Time.realtimeSinceStartup < waveClearDeadline)
            {
                yield return null;
            }
        }

        [UnityTest, Order(3)]
        public IEnumerator DesktopRetina2048x1324_EightWaveCpuFrameTimeAndVramBudget_RemainWellBelowLimits()
        {
            yield return EnsureGameSceneLoaded();

            for (int i = 0; i < 20; i++)
            {
                yield return null;
            }

            var mainCam = Camera.main;
            var eventSystem = EventSystem.current;
            var visualizer = UnityEngine.Object.FindFirstObjectByType<HoloTableVfxVisualizer>();
            var pointerInput = visualizer.PointerInput;
            var vfx = visualizer.VisualEffectComponent;

            // Verify OrbitOnIdle implements IHasInputActionReferences with wired cancel action and wraps CinemachinePOV axis
            var vcam = UnityEngine.Object.FindFirstObjectByType<CinemachineVirtualCamera>();
            Assert.IsNotNull(vcam);
            var orbitActionHolder = vcam.GetComponents<IHasInputActionReferences>()
                .FirstOrDefault(c => c.GetType().Name == "OrbitOnIdle");
            Assert.IsNotNull(orbitActionHolder, "OrbitOnIdle must implement IHasInputActionReferences.");
            Assert.AreEqual(1, orbitActionHolder.inputActionReferenceProperties.Length);
            Assert.IsNotNull(orbitActionHolder.inputActionReferenceProperties[0].get(),
                "OrbitOnIdle.m_CancelsIdle must be wired in Game.unity.");

            var pov = vcam.GetCinemachineComponent<CinemachinePOV>();
            Assert.IsNotNull(pov);
            pov.m_HorizontalAxis.Value = 179.98f;
            for (int i = 0; i < 15; i++)
            {
                yield return null;
            }
            Assert.That(pov.m_HorizontalAxis.Value, Is.InRange(-180f, 180f),
                "OrbitOnIdle must wrap CinemachinePOV horizontal axis within [-180, 180].");

            // Render at exact 2048x1324 desktop Retina streaming viewport resolution
            string screenshotDir = Environment.GetEnvironmentVariable("HOLOTABLE_SCREENSHOT_DIR");
            var retinaRt = new RenderTexture(2048, 1324, 24, RenderTextureFormat.ARGB32);
            var prevTarget = mainCam.targetTexture;
            try
            {
                mainCam.targetTexture = retinaRt;
                pov.m_HorizontalAxis.Value = -49.68f;
                for (int i = 0; i < 12; i++)
                {
                    visualizer.PushSimulationStateToVisuals();
                    vfx.Simulate(1f / 60f, 1);
                    yield return null;
                }

                if (!string.IsNullOrEmpty(screenshotDir))
                {
                    Directory.CreateDirectory(screenshotDir);
                    SaveRenderTexturePng(mainCam, retinaRt, Path.Combine(screenshotDir, "after_lighting_idle.png"));
                }

                // Hover cursor at foreground-left canyon to showcase live mountain + targeter reticle
                var mountainSpot = new Vector2(-0.65f, -0.45f);
                var mountainScreen = ProjectTableLocalToScreen(mainCam, pointerInput, visualizer.transform, mountainSpot);
                var hoverEvt = RaycastCanonicalPointer(eventSystem, mountainScreen, pointerId: 1);
                ExecuteEvents.ExecuteHierarchy(pointerInput.gameObject, hoverEvt, ExecuteEvents.pointerEnterHandler);
                ExecuteEvents.ExecuteHierarchy(pointerInput.gameObject, hoverEvt, ExecuteEvents.pointerMoveHandler);

                int snapGuard = 0;
                while (visualizer.Simulation.TargeterPresence < 0.99f && snapGuard++ < 40)
                {
                    visualizer.Simulation.Step(1f / 60f);
                    visualizer.PushSimulationStateToVisuals();
                    vfx.Simulate(1f / 60f, 1);
                    yield return null;
                }
                for (int i = 0; i < 8; i++)
                {
                    visualizer.Simulation.Step(1f / 60f);
                    visualizer.PushSimulationStateToVisuals();
                    vfx.Simulate(1f / 60f, 1);
                    yield return null;
                }

                if (!string.IsNullOrEmpty(screenshotDir))
                {
                    SaveRenderTexturePng(mainCam, retinaRt, Path.Combine(screenshotDir, "after_cursor_mountain_targeter.png"));
                }

                // Trigger click wave on right side while keeping cursor mountain + targeter active on left
                var waveOrigin = new Vector2(0.75f, 0.25f);
                var waveScreen = ProjectTableLocalToScreen(mainCam, pointerInput, visualizer.transform, waveOrigin);
                var clickEvt = RaycastCanonicalPointer(eventSystem, waveScreen, pointerId: 1);
                clickEvt.pointerPressRaycast = clickEvt.pointerCurrentRaycast;
                clickEvt.pressPosition = waveScreen;
                ExecuteEvents.ExecuteHierarchy(pointerInput.gameObject, clickEvt, ExecuteEvents.pointerDownHandler);

                var returnHoverEvt = RaycastCanonicalPointer(eventSystem, mountainScreen, pointerId: 1);
                ExecuteEvents.ExecuteHierarchy(pointerInput.gameObject, returnHoverEvt, ExecuteEvents.pointerMoveHandler);

                int waveFrames = 0;
                while (visualizer.Simulation.ActiveWaveCount > 0 &&
                       visualizer.Simulation.GetActiveWave(0).FrontRadius < 1.15f &&
                       waveFrames++ < 150)
                {
                    visualizer.Simulation.Step(1f / 60f);
                    visualizer.PushSimulationStateToVisuals();
                    vfx.Simulate(1f / 60f, 1);
                    yield return null;
                }

                if (!string.IsNullOrEmpty(screenshotDir))
                {
                    SaveRenderTexturePng(mainCam, retinaRt, Path.Combine(screenshotDir, "after_click_wave.png"));
                }

                // Populate 8 simultaneous overlapping waves + live cursor mountain and benchmark CPU update time (< 0.5 ms)
                for (int w = 0; w < 8; w++)
                {
                    float ang = w * (Mathf.PI * 2f / 8f);
                    visualizer.Simulation.TriggerWaveAtLocalXZ(new Vector2(Mathf.Cos(ang) * 0.9f, Mathf.Sin(ang) * 0.7f), 1.0f);
                    visualizer.Simulation.Step(0.04f);
                }
                Assert.That(visualizer.Simulation.ActiveWaveCount, Is.GreaterThanOrEqualTo(8));

                // Warmup then time 120 simulation + VFX uniform upload steps with all 8 waves active
                visualizer.Simulation.Step(0.004f);
                visualizer.PushSimulationStateToVisuals();
                Assert.AreEqual(8, visualizer.LastPackedWaveCount, "All 8 active waves must pack into InteractionWaves0 + InteractionWaves1.");

                const int benchIterations = 120;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < benchIterations; i++)
                {
                    visualizer.Simulation.Step(0.004f);
                    visualizer.PushSimulationStateToVisuals();
                }
                sw.Stop();
                double avgCpuMs = sw.Elapsed.TotalMilliseconds / benchIterations;

                // Render 30 full frames at 2048x1324 with 8 active waves + cursor mountain to materialize all HDRP RTHandles
                for (int frame = 0; frame < 30; frame++)
                {
                    float angle = frame * 0.09f;
                    var localTarget = new Vector2(Mathf.Cos(angle) * 1.15f, Mathf.Sin(angle) * 0.85f);
                    var screenPos = ProjectTableLocalToScreen(mainCam, pointerInput, visualizer.transform, localTarget);
                    var evt = RaycastCanonicalPointer(eventSystem, screenPos, pointerId: 1);
                    if (evt.pointerCurrentRaycast.isValid)
                    {
                        ExecuteEvents.ExecuteHierarchy(evt.pointerCurrentRaycast.gameObject, evt, ExecuteEvents.pointerMoveHandler);
                    }

                    vfx.Simulate(1f / 60f, 1);
                    mainCam.Render();
                    yield return null;
                }

                long totalAllocated = Profiler.GetTotalAllocatedMemoryLong();
                long graphicsAllocated = Profiler.GetAllocatedMemoryForGraphicsDriver();
                Debug.Log($"[HoloTablePerf2048x1324] avgCpuUpdateMs={avgCpuMs:F4} ms, totalAllocatedMiB={totalAllocated / (1024.0 * 1024.0):F1}, graphicsAllocatedMiB={graphicsAllocated / (1024.0 * 1024.0):F1}, packedWaves={visualizer.LastPackedWaveCount}");

                Assert.That(avgCpuMs, Is.LessThan(0.5),
                    $"8-wave CPU simulation + VFX uniform upload ({avgCpuMs:F4} ms) must stay well under 0.5 ms.");
                Assert.That(totalAllocated, Is.LessThan(850L * 1024L * 1024L),
                    $"Total Unity allocated memory at 2048x1324 ({totalAllocated / (1024 * 1024)} MiB) must stay comfortably within budget.");
                Assert.That(graphicsAllocated, Is.LessThan(850L * 1024L * 1024L),
                    $"Graphics driver memory at 2048x1324 ({graphicsAllocated / (1024 * 1024)} MiB) must stay well below 1 GiB pod VRAM limit.");
            }
            finally
            {
                mainCam.targetTexture = prevTarget;
                retinaRt.Release();
                UnityEngine.Object.Destroy(retinaRt);
            }

            // Confirm zero dynamic HoloTableInteractionField Texture2D exists anywhere
            foreach (var tex in Resources.FindObjectsOfTypeAll<Texture2D>())
            {
                Assert.AreNotEqual("HoloTableInteractionField", tex.name,
                    "No dynamic HoloTableInteractionField Texture2D may exist.");
            }
        }

        private static IEnumerator EnsureGameSceneLoaded()
        {
            var existing = SceneManager.GetSceneByName("Game");
            if (existing.IsValid() && existing.isLoaded && UnityEngine.Object.FindFirstObjectByType<HoloTableVfxVisualizer>() != null)
            {
                yield break;
            }

            var op = SceneManager.LoadSceneAsync("Assets/Scenes/Game.unity", LoadSceneMode.Single);
            Assert.IsNotNull(op, "Failed to start loading Assets/Scenes/Game.unity");
            while (!op.isDone)
            {
                yield return null;
            }
        }

        private static Vector2 ProjectTableLocalToScreen(
            Camera cam,
            HoloTablePointerInput pointerInput,
            Transform tableOrigin,
            Vector2 localXZ)
        {
            float surfaceLocalY = tableOrigin.InverseTransformPoint(pointerInput.transform.position).y;
            var worldPoint = tableOrigin.TransformPoint(new Vector3(localXZ.x, surfaceLocalY, localXZ.y));
            var sp = cam.WorldToScreenPoint(worldPoint);
            return new Vector2(sp.x, sp.y);
        }

        private static PointerEventData RaycastCanonicalPointer(
            EventSystem eventSystem,
            Vector2 screenPosition,
            int pointerId)
        {
            var eventData = new PointerEventData(eventSystem)
            {
                pointerId = pointerId,
                position = screenPosition,
                button = PointerEventData.InputButton.Left
            };

            var results = new List<RaycastResult>();
            eventSystem.RaycastAll(eventData, results);
            eventData.pointerCurrentRaycast = FindFirstValidRaycast(results);
            return eventData;
        }

        private static RaycastResult FindFirstValidRaycast(List<RaycastResult> candidates)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].gameObject != null)
                {
                    return candidates[i];
                }
            }

            return default;
        }

        private static void SampleGpuUniformsAtLocalXZ(
            VisualEffect vfx,
            Vector2 localXZ,
            out float r,
            out float g,
            out float b,
            out float a)
        {
            Vector4 cursorState = vfx.GetVector4("InteractionCursor");
            Vector4 mountainParams = vfx.GetVector4("InteractionMountainParams");
            Vector4 targeterParams = vfx.GetVector4("InteractionTargeterParams");
            Vector4 waveParams = vfx.GetVector4("InteractionWaveParams");
            Matrix4x4 waves0 = vfx.GetMatrix4x4("InteractionWaves0");
            Matrix4x4 waves1 = vfx.GetMatrix4x4("InteractionWaves1");

            float presence = cursorState.z;
            float mountainHeight = 0f;
            float targeterMask = 0f;
            float mountainMask = 0f;

            if (presence > 1e-4f)
            {
                float dx = localXZ.x - cursorState.x;
                float dz = localXZ.y - cursorState.y;
                float rSq = dx * dx + dz * dz;
                float dist = Mathf.Sqrt(rSq);

                float expCore = rSq * mountainParams.y;
                float core = expCore < 12f ? Mathf.Exp(-expCore) : 0f;
                float expFoot = rSq * mountainParams.w;
                float foothill = expFoot < 12f ? Mathf.Exp(-expFoot) : 0f;

                float ringRadius = targeterParams.x;
                float invRing = targeterParams.y;
                float ringDist = Mathf.Abs(dist - ringRadius);
                float expOuter = ringDist * ringDist * invRing;
                float outerRing = expOuter < 12f ? Mathf.Exp(-expOuter) : 0f;

                float innerRadius = ringRadius * (0.56f + 0.04f * cursorState.w);
                float innerDist = Mathf.Abs(dist - innerRadius);
                float expInner = innerDist * innerDist * invRing * 1.8f;
                float innerRing = 0.65f * (expInner < 12f ? Mathf.Exp(-expInner) : 0f);

                float expBull = rSq * targeterParams.w;
                float bullseye = expBull < 12f ? Mathf.Exp(-expBull) : 0f;

                float t0 = Mathf.Clamp01((dist - 0.16f) / 0.05f);
                float t1 = Mathf.Clamp01((dist - 0.50f) / 0.06f);
                float window = (t0 * t0 * (3f - 2f * t0)) * (1f - t1 * t1 * (3f - 2f * t1));
                float crosshair = 0f;
                if (window > 1e-4f)
                {
                    const float invTick = 1f / (2f * 0.044f * 0.044f);
                    float ex = dx * dx * invTick;
                    float ez = dz * dz * invTick;
                    float tickX = ex < 12f ? Mathf.Exp(-ex) : 0f;
                    float tickZ = ez < 12f ? Mathf.Exp(-ez) : 0f;
                    crosshair = window * Mathf.Max(tickX, tickZ) * 0.92f;
                }

                float rawMountain = mountainParams.x * core + mountainParams.z * foothill + targeterParams.z * outerRing;
                mountainHeight = rawMountain * presence;
                targeterMask = Mathf.Clamp01(Mathf.Max(Mathf.Max(outerRing, innerRing), Mathf.Max(bullseye * 0.95f, crosshair)) * presence);
                mountainMask = Mathf.Clamp01(Mathf.Max(foothill, outerRing * 0.85f) * presence);
            }

            Vector2 w0 = EvalWaveMatrix(localXZ, waves0, waveParams);
            Vector2 w1 = EvalWaveMatrix(localXZ, waves1, waveParams);
            r = Mathf.Clamp(mountainHeight + w0.x + w1.x, -0.14f, waveParams.w);
            g = targeterMask;
            b = Mathf.Clamp01(w0.y + w1.y);
            a = mountainMask;
        }

        private static Vector2 EvalWaveMatrix(Vector2 localXZ, Matrix4x4 wavesMat, Vector4 waveParams)
        {
            float width = waveParams.x;
            float invTwoWidthSq = waveParams.y;
            float baseAmp = waveParams.z;
            float maxBand = width * 4f;
            float sumH = 0f;
            float sumG = 0f;

            for (int i = 0; i < 4; i++)
            {
                Vector4 w = wavesMat.GetRow(i);
                float env = w.w;
                if (env <= 1e-5f)
                {
                    continue;
                }

                float front = w.z;
                float maxReach = front + maxBand;
                float dx = localXZ.x - w.x;
                float dz = localXZ.y - w.y;
                if (Mathf.Abs(dx) > maxReach || Mathf.Abs(dz) > maxReach)
                {
                    continue;
                }

                float rSq = dx * dx + dz * dz;
                float minReach = Mathf.Max(0f, front - maxBand * 1.4f);
                if (rSq > maxReach * maxReach || rSq < minReach * minReach)
                {
                    continue;
                }

                float dist = Mathf.Sqrt(rSq);
                float delta = dist - front;
                float e0 = delta * delta * invTwoWidthSq;
                float primary = e0 < 12f ? Mathf.Exp(-e0) : 0f;

                float d1 = delta + width * 1.55f;
                float e1 = d1 * d1 * invTwoWidthSq * 0.85f;
                float trough = (dist <= front && e1 < 12f) ? 0.34f * Mathf.Exp(-e1) : 0f;

                float d2 = delta + width * 3.0f;
                float e2 = d2 * d2 * invTwoWidthSq * 0.75f;
                float secondary = (dist <= front && e2 < 12f) ? 0.22f * Mathf.Exp(-e2) : 0f;

                sumH += baseAmp * env * (primary - trough + secondary);
                sumG += env * Mathf.Clamp01(primary + secondary * 0.45f);
            }

            return new Vector2(sumH, sumG);
        }

        private static void SaveRenderTexturePng(Camera cam, RenderTexture rt, string outputPath)
        {
            var prevActive = RenderTexture.active;
            try
            {
                cam.Render();
                RenderTexture.active = rt;
                var readback = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, mipChain: false);
                try
                {
                    readback.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    readback.Apply();
                    File.WriteAllBytes(outputPath, readback.EncodeToPNG());
                }
                finally
                {
                    UnityEngine.Object.Destroy(readback);
                }
            }
            finally
            {
                RenderTexture.active = prevActive;
            }
        }
    }
}
