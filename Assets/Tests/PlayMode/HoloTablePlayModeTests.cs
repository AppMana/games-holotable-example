using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AppMana.ComponentModel;
using AppMana.Multiplayer;
using Cinemachine;
using HoloTable.Runtime;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Profiling;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

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

            // Part B1 & Part C: 65,536 HoloTable pins alive and interactive VFX parameters exposed
            var visualizer = UnityEngine.Object.FindFirstObjectByType<HoloTableVfxVisualizer>();
            Assert.IsNotNull(visualizer, "HoloTableVfxVisualizer must be active on VFX:PinScreen.");
            var vfx = visualizer.VisualEffectComponent;
            Assert.IsNotNull(vfx);

            Assert.IsTrue(vfx.HasTexture("InteractionField"));
            Assert.IsTrue(vfx.HasVector4("InteractionTargetColor"));
            Assert.IsTrue(vfx.HasVector4("InteractionWaveColor"));
            Assert.IsTrue(vfx.HasVector3("LandingTargetPosition"));
            Assert.IsTrue(vfx.HasBool("DisplayTarget"));
            Assert.AreEqual(65536, vfx.aliveParticleCount, "All 65,536 holotable pins must spawn and compile cleanly.");
        }

        [UnityTest, Order(2)]
        public IEnumerator CanonicalEventSystemPointer_RealTimeCursorMountainTargeterAndClickWave_DriveVfxAndHeightfield()
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

            int fadeInGuard = 0;
            while (visualizer.Simulation.TargeterPresence < 0.99f && fadeInGuard++ < 40)
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

            SampleGpuUploadedFieldAtUV(
                visualizer,
                visualizer.Simulation.LocalXZToUV(visualizer.Simulation.CurrentCursorLocalXZ),
                out float heightAtA,
                out float targetMaskAtA,
                out _,
                out float mountainMaskAtA);
            Assert.That(heightAtA, Is.GreaterThan(0.24f), "Uploaded RGBAHalf field must show elevated mountain peak at cursor A.");
            Assert.That(targetMaskAtA, Is.GreaterThan(0.85f), "Uploaded RGBAHalf field must show targeter reticle bullseye at cursor A.");
            Assert.That(mountainMaskAtA, Is.GreaterThan(0.80f));

            // 2. Move pointer to table coordinate B and confirm mountain + targeter shift in real time
            var localPosB = new Vector2(0.95f, 0.45f);
            var screenPosB = ProjectTableLocalToScreen(mainCam, pointerInput, visualizer.transform, localPosB);
            var moveDataB = RaycastCanonicalPointer(eventSystem, screenPosB, pointerId: 1);
            Assert.AreEqual(pointerInput.gameObject, moveDataB.pointerCurrentRaycast.gameObject);
            ExecuteEvents.ExecuteHierarchy(pointerInput.gameObject, moveDataB, ExecuteEvents.pointerMoveHandler);

            int moveGuard = 0;
            while (Vector2.Distance(visualizer.Simulation.CurrentCursorLocalXZ, localPosB) > 0.08f && moveGuard++ < 50)
            {
                yield return null;
            }
            yield return null;

            Assert.That(Vector2.Distance(visualizer.Simulation.CurrentCursorLocalXZ, localPosB), Is.LessThan(0.12f));
            SampleGpuUploadedFieldAtUV(
                visualizer,
                visualizer.Simulation.LocalXZToUV(visualizer.Simulation.CurrentCursorLocalXZ),
                out float heightAtB,
                out float targetMaskAtB,
                out _,
                out _);
            SampleGpuUploadedFieldAtUV(
                visualizer,
                visualizer.Simulation.LocalXZToUV(localPosA),
                out float residualHeightAtA,
                out _,
                out _,
                out _);
            Assert.That(heightAtB, Is.GreaterThan(0.24f));
            Assert.That(targetMaskAtB, Is.GreaterThan(0.85f));
            Assert.That(residualHeightAtA, Is.LessThan(0.02f), "Mountain at previous cursor A must subside when cursor moves to B.");

            // 3. Dispatch canonical pointer click/tap (IPointerDownHandler) at B -> verify radial wave spawns & expands
            var pressDataB = RaycastCanonicalPointer(eventSystem, screenPosB, pointerId: 1);
            pressDataB.pointerPressRaycast = pressDataB.pointerCurrentRaycast;
            pressDataB.pressPosition = screenPosB;
            ExecuteEvents.ExecuteHierarchy(pointerInput.gameObject, pressDataB, ExecuteEvents.pointerDownHandler);

            Assert.AreEqual(1, visualizer.Simulation.ActiveWaveCount, "Pointer press/tap must spawn an active radial wave.");

            int guard = 0;
            while (visualizer.Simulation.ActiveWaveCount > 0 &&
                   visualizer.Simulation.GetActiveWave(0).FrontRadius < 0.85f &&
                   guard++ < 150)
            {
                yield return null;
            }

            Assert.AreEqual(1, visualizer.Simulation.ActiveWaveCount);
            var wave = visualizer.Simulation.GetActiveWave(0);
            var crestPoint = wave.OriginLocalXZ + new Vector2(-wave.FrontRadius, 0f);
            SampleGpuUploadedFieldAtUV(
                visualizer,
                visualizer.Simulation.LocalXZToUV(crestPoint),
                out float crestHeight,
                out _,
                out float crestGlow,
                out _);
            Assert.That(crestGlow, Is.GreaterThan(0.25f), "Uploaded RGBAHalf field must carry bright wave crest glow at FrontRadius.");
            Assert.That(crestHeight, Is.GreaterThan(0.04f), "Uploaded RGBAHalf field must carry positive wave displacement at FrontRadius.");

            // 4. Pointer exit -> targeter & subsurface light turn off cleanly
            ExecuteEvents.ExecuteHierarchy(pointerInput.gameObject, pressDataB, ExecuteEvents.pointerExitHandler);
            int fadeOutGuard = 0;
            while (visualizer.Simulation.TargeterPresence > 0.001f && fadeOutGuard++ < 80)
            {
                yield return null;
            }
            yield return null;

            Assert.IsFalse(visualizer.Simulation.HasPointerInput);
            Assert.IsFalse(vfx.GetBool("DisplayTarget"));
            Assert.IsFalse(visualizer.TargetSubsurfaceLight.gameObject.activeSelf);

            // Let remaining wave expire so next test starts from crisp idle baseline
            int waveClearGuard = 0;
            while (visualizer.Simulation.ActiveWaveCount > 0 && waveClearGuard++ < 250)
            {
                yield return null;
            }
        }

        [UnityTest, Order(3)]
        public IEnumerator EnduranceOrbitWrappingMemoryAndOptionalScreenshots_RemainHealthy()
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

            string screenshotDir = Environment.GetEnvironmentVariable("HOLOTABLE_SCREENSHOT_DIR");
            if (!string.IsNullOrEmpty(screenshotDir))
            {
                Directory.CreateDirectory(screenshotDir);
                var vfx = visualizer.VisualEffectComponent;
                var rt = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
                var prevTarget = mainCam.targetTexture;
                try
                {
                    mainCam.targetTexture = rt;
                    pov.m_HorizontalAxis.Value = -49.68f;
                    for (int i = 0; i < 12; i++)
                    {
                        visualizer.PushSimulationStateToVisuals();
                        vfx.Simulate(1f / 60f, 1);
                        yield return null;
                    }
                    SaveRenderTexturePng(mainCam, rt, Path.Combine(screenshotDir, "after_lighting_idle.png"));

                    // Hover cursor at foreground-left canyon to showcase live mountain + targeter reticle
                    var mountainSpot = new Vector2(-0.65f, -0.45f);
                    var mountainScreen = ProjectTableLocalToScreen(mainCam, pointerInput, visualizer.transform, mountainSpot);
                    var hoverEvt = RaycastCanonicalPointer(eventSystem, mountainScreen, pointerId: 1);
                    ExecuteEvents.ExecuteHierarchy(pointerInput.gameObject, hoverEvt, ExecuteEvents.pointerEnterHandler);
                    ExecuteEvents.ExecuteHierarchy(pointerInput.gameObject, hoverEvt, ExecuteEvents.pointerMoveHandler);

                    int snapGuard = 0;
                    while (visualizer.Simulation.TargeterPresence < 0.99f && snapGuard++ < 40)
                    {
                        visualizer.PushSimulationStateToVisuals();
                        vfx.Simulate(1f / 60f, 1);
                        yield return null;
                    }
                    for (int i = 0; i < 10; i++)
                    {
                        visualizer.PushSimulationStateToVisuals();
                        vfx.Simulate(1f / 60f, 1);
                        yield return null;
                    }
                    SaveRenderTexturePng(mainCam, rt, Path.Combine(screenshotDir, "after_cursor_mountain_targeter.png"));

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
                        visualizer.PushSimulationStateToVisuals();
                        vfx.Simulate(1f / 60f, 1);
                        yield return null;
                    }
                    SaveRenderTexturePng(mainCam, rt, Path.Combine(screenshotDir, "after_click_wave.png"));

                    ExecuteEvents.ExecuteHierarchy(pointerInput.gameObject, returnHoverEvt, ExecuteEvents.pointerExitHandler);
                }
                finally
                {
                    mainCam.targetTexture = prevTarget;
                    rt.Release();
                    UnityEngine.Object.Destroy(rt);
                }
            }

            // 180-frame continuous interaction endurance check
            for (int frame = 0; frame < 180; frame++)
            {
                float angle = frame * 0.07f;
                var localTarget = new Vector2(Mathf.Cos(angle) * 1.15f, Mathf.Sin(angle) * 0.85f);
                var screenPos = ProjectTableLocalToScreen(mainCam, pointerInput, visualizer.transform, localTarget);
                var evt = RaycastCanonicalPointer(eventSystem, screenPos, pointerId: 1);
                if (evt.pointerCurrentRaycast.isValid)
                {
                    if (frame == 0)
                    {
                        ExecuteEvents.ExecuteHierarchy(evt.pointerCurrentRaycast.gameObject, evt, ExecuteEvents.pointerEnterHandler);
                    }
                    ExecuteEvents.ExecuteHierarchy(evt.pointerCurrentRaycast.gameObject, evt, ExecuteEvents.pointerMoveHandler);
                    if (frame % 35 == 0)
                    {
                        evt.pointerPressRaycast = evt.pointerCurrentRaycast;
                        evt.pressPosition = screenPos;
                        ExecuteEvents.ExecuteHierarchy(evt.pointerCurrentRaycast.gameObject, evt, ExecuteEvents.pointerDownHandler);
                    }
                }

                yield return null;
            }

            long totalAllocated = Profiler.GetTotalAllocatedMemoryLong();
            long graphicsAllocated = Profiler.GetAllocatedMemoryForGraphicsDriver();
            Assert.That(totalAllocated, Is.LessThan(950L * 1024L * 1024L),
                $"Total Unity allocated memory ({totalAllocated / (1024 * 1024)} MiB) must stay comfortably within budget.");
            Assert.That(graphicsAllocated, Is.LessThan(950L * 1024L * 1024L),
                $"Graphics driver memory ({graphicsAllocated / (1024 * 1024)} MiB) must stay below 1 GiB pod VRAM limit.");

            // Destroy visualizer GameObject and verify HoloTableInteractionField Texture2D & NativeArray clean up with zero leaks
            UnityEngine.Object.Destroy(visualizer.gameObject);
            yield return null;
            yield return null;

            foreach (var tex in Resources.FindObjectsOfTypeAll<Texture2D>())
            {
                Assert.AreNotEqual("HoloTableInteractionField", tex.name,
                    "Dynamic HoloTableInteractionField Texture2D must be destroyed cleanly without leaking.");
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

        private static void SampleGpuUploadedFieldAtUV(
            HoloTableVfxVisualizer visualizer,
            Vector2 uv,
            out float r,
            out float g,
            out float b,
            out float a)
        {
            var tex = visualizer.InteractionTexture;
            int res = visualizer.FieldResolution;
            int col = Mathf.Clamp(Mathf.FloorToInt(uv.x * res), 0, res - 1);
            int row = Mathf.Clamp(Mathf.FloorToInt(uv.y * res), 0, res - 1);
            NativeArray<ushort> raw = tex.GetRawTextureData<ushort>();
            int idx = (row * res + col) * 4;
            r = Mathf.HalfToFloat(raw[idx]);
            g = Mathf.HalfToFloat(raw[idx + 1]);
            b = Mathf.HalfToFloat(raw[idx + 2]);
            a = Mathf.HalfToFloat(raw[idx + 3]);
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
