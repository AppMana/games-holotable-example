using System;
using System.Reflection;
using HoloTable.Simulation;
using NUnit.Framework;
using UnityEngine;

namespace HoloTable.Tests.EditMode
{
    public sealed class HoloTableSimulationTests
    {
        [Test]
        public void SimulationAssembly_IsPureDataModel_WithZeroVisualOrEventDependencies()
        {
            var simAssembly = typeof(HoloTableSimulation).Assembly;

            foreach (var refAsm in simAssembly.GetReferencedAssemblies())
            {
                var name = refAsm.Name ?? string.Empty;
                Assert.That(name, Does.Not.Contain("VisualEffect"), "Simulation assembly must not reference VFX Graph.");
                Assert.That(name, Does.Not.Contain("InputSystem"), "Simulation assembly must not reference InputSystem.");
                Assert.That(name, Does.Not.Contain("UnityEngine.UI"), "Simulation assembly must not reference UI/EventSystem.");
                Assert.That(name, Does.Not.Contain("HighDefinition"), "Simulation assembly must not reference HDRP.");
                Assert.That(name, Does.Not.Contain("HoloTable.Runtime"), "Simulation assembly must not reference visualization runtime.");
            }

            foreach (var type in simAssembly.GetTypes())
            {
                Assert.IsFalse(typeof(MonoBehaviour).IsAssignableFrom(type),
                    $"Simulation type {type.FullName} must not inherit from MonoBehaviour.");
                Assert.IsFalse(typeof(ScriptableObject).IsAssignableFrom(type),
                    $"Simulation type {type.FullName} must not inherit from ScriptableObject.");

                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    Assert.IsFalse(typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType),
                        $"Simulation field {type.Name}.{field.Name} must not hold UnityEngine.Object ({field.FieldType.Name}).");
                }
            }
        }

        [Test]
        public void HexagonalGeometry_ContainmentClampingAndUVMapping_AreExactAndIdempotent()
        {
            var sim = new HoloTableSimulation();
            var cfg = sim.Config;

            Assert.IsTrue(sim.IsInsideHexagon(Vector2.zero));
            Assert.IsTrue(sim.IsInsideHexagon(new Vector2(cfg.HalfExtentX, 0f)));
            Assert.IsTrue(sim.IsInsideHexagon(new Vector2(-cfg.HalfExtentX, 0f)));
            Assert.IsTrue(sim.IsInsideHexagon(new Vector2(cfg.HalfExtentX * 0.5f, cfg.HalfExtentZ)));
            Assert.IsTrue(sim.IsInsideHexagon(new Vector2(-cfg.HalfExtentX * 0.5f, -cfg.HalfExtentZ)));

            Assert.IsFalse(sim.IsInsideHexagon(new Vector2(0f, cfg.HalfExtentZ + 0.05f)));
            Assert.IsFalse(sim.IsInsideHexagon(new Vector2(cfg.HalfExtentX * 0.8f, cfg.HalfExtentZ * 0.6f)));
            Assert.IsFalse(sim.IsInsideHexagon(new Vector2(float.NaN, 0f)));
            Assert.IsFalse(sim.IsInsideHexagon(new Vector2(0f, float.PositiveInfinity)));

            var interior = new Vector2(0.75f, -0.65f);
            Assert.That(Vector2.Distance(sim.ClampToHexagon(interior), interior), Is.LessThan(1e-6f));

            Vector2[] outsidePoints =
            {
                new Vector2(6.0f, 0.2f),
                new Vector2(-5.4f, 4.1f),
                new Vector2(0.3f, -8.0f),
                new Vector2(-3.2f, -3.5f),
                new Vector2(2.4f, 2.1f)
            };

            foreach (var outside in outsidePoints)
            {
                var clamped = sim.ClampToHexagon(outside);
                Assert.IsTrue(sim.IsInsideHexagon(clamped), $"Clamped point {clamped} from {outside} must lie inside hexagon.");
                var reclamped = sim.ClampToHexagon(clamped);
                Assert.That(Vector2.Distance(clamped, reclamped), Is.LessThan(1e-5f), "ClampToHexagon must be idempotent.");
            }

            Assert.AreEqual(Vector2.zero, sim.ClampToHexagon(new Vector2(float.NaN, 1f)));
            Assert.AreEqual(Vector2.zero, sim.ClampToHexagon(new Vector2(1f, float.NegativeInfinity)));

            var uv = sim.LocalXZToUV(interior);
            var roundTrip = sim.UVToLocalXZ(uv);
            Assert.That(Vector2.Distance(interior, roundTrip), Is.LessThan(1e-5f));
        }

        [Test]
        public void CursorMountainAndTargeter_TrackPointerInRealTimeAndFadeCleanly()
        {
            var sim = new HoloTableSimulation();

            Assert.IsFalse(sim.HasPointerInput);
            Assert.AreEqual(0f, sim.TargeterPresence, 1e-6f);
            var idleCenter = sim.SampleLocalXZ(0f, 0f);
            Assert.AreEqual(0f, idleCenter.TotalDisplacement, 1e-6f);
            Assert.AreEqual(0f, idleCenter.TargeterMask, 1e-6f);

            var posA = new Vector2(0.85f, -0.55f);
            sim.SetPointerLocalXZ(posA);
            Assert.IsTrue(sim.HasPointerInput);
            Assert.That(Vector2.Distance(sim.CurrentCursorLocalXZ, posA), Is.LessThan(1e-5f),
                "Initial pointer acquisition should snap immediately to cursor position without lag.");

            sim.Step(0.10f);
            Assert.AreEqual(1f, sim.TargeterPresence, 1e-4f);

            var summitA = sim.SampleLocalXZ(posA.x, posA.y);
            var farSample = sim.SampleLocalXZ(-1.55f, 1.25f);
            Assert.That(summitA.MountainHeight, Is.GreaterThan(0.28f));
            Assert.That(summitA.TotalDisplacement, Is.GreaterThan(0.28f));
            Assert.That(summitA.TargeterMask, Is.GreaterThan(0.90f));
            Assert.AreEqual(0f, farSample.TotalDisplacement, 1e-4f);

            // Verify outer targeter ring and cardinal crosshair ticks around cursor A
            var ringSample = sim.SampleLocalXZ(posA.x + sim.Config.TargeterRingRadius, posA.y);
            Assert.That(ringSample.TargeterMask, Is.GreaterThan(0.85f));

            float tickRadius = 0.28f;
            var cardinalTick = sim.SampleLocalXZ(posA.x + tickRadius, posA.y);
            var diagonalOffTick = sim.SampleLocalXZ(posA.x + tickRadius * 0.7071f, posA.y + tickRadius * 0.7071f);
            Assert.That(cardinalTick.TargeterMask, Is.GreaterThan(diagonalOffTick.TargeterMask + 0.35f));

            // Move pointer to a second position and verify mountain peak & targeter follow in real time
            var posB = new Vector2(-1.10f, 0.65f);
            sim.SetPointerLocalXZ(posB);
            for (int i = 0; i < 12; i++)
            {
                sim.Step(1f / 60f);
            }

            Assert.That(Vector2.Distance(sim.CurrentCursorLocalXZ, posB), Is.LessThan(0.01f));
            var summitB = sim.SampleLocalXZ(posB.x, posB.y);
            var oldA = sim.SampleLocalXZ(posA.x, posA.y);
            Assert.That(summitB.MountainHeight, Is.GreaterThan(0.28f));
            Assert.That(summitB.TargeterMask, Is.GreaterThan(0.90f));
            Assert.That(oldA.MountainHeight, Is.LessThan(0.01f));

            // Clear pointer and verify presence fades smoothly back to 0
            sim.ClearPointer();
            for (int i = 0; i < 20; i++)
            {
                sim.Step(1f / 60f);
            }

            Assert.AreEqual(0f, sim.TargeterPresence, 1e-4f);
            Assert.AreEqual(0f, sim.SampleLocalXZ(posB.x, posB.y).TotalDisplacement, 1e-5f);
        }

        [Test]
        public void RadialWaves_PropagateAtWaveSpeed_SuperposeAndExpireCleanly()
        {
            var sim = new HoloTableSimulation();
            var origin = new Vector2(-0.35f, 0.25f);
            sim.TriggerWaveAtLocalXZ(origin, 1.0f);
            Assert.AreEqual(1, sim.ActiveWaveCount);

            float[] checkpoints = { 0.40f, 0.85f, 1.30f };
            float elapsed = 0f;
            float previousPeakGlow = float.MaxValue;

            foreach (float targetTime in checkpoints)
            {
                while (elapsed + 1e-5f < targetTime)
                {
                    float step = Mathf.Min(0.05f, targetTime - elapsed);
                    sim.Step(step);
                    elapsed += step;
                }

                float expectedRadius = sim.Config.WaveSpeed * elapsed;
                float bestRadius = 0f;
                float bestGlow = -1f;
                float bestWaveHeight = 0f;

                for (int i = 0; i <= 300; i++)
                {
                    float r = i * 0.01f;
                    var sample = sim.SampleLocalXZ(origin.x + r, origin.y);
                    if (sample.WaveCrestGlow > bestGlow)
                    {
                        bestGlow = sample.WaveCrestGlow;
                        bestRadius = r;
                        bestWaveHeight = sample.WaveHeight;
                    }
                }

                Assert.That(bestRadius, Is.EqualTo(expectedRadius).Within(0.05f),
                    $"Wave crest radius at t={elapsed:F2}s should track WaveSpeed * t.");
                Assert.That(bestWaveHeight, Is.GreaterThan(0.02f));
                Assert.That(bestGlow, Is.LessThan(previousPeakGlow), "Wave crest glow should damp over time.");
                previousPeakGlow = bestGlow;
            }

            // Advance beyond lifetime -> wave expires cleanly
            for (int i = 0; i < 20; i++)
            {
                sim.Step(0.10f);
            }

            Assert.AreEqual(0, sim.ActiveWaveCount);
            Assert.AreEqual(0f, sim.SampleLocalXZ(origin.x, origin.y).WaveHeight, 1e-6f);
        }

        [Test]
        public void MultiWaveSuperposition_AndCapacityRecycling_AreDeterministic()
        {
            var singleSim = new HoloTableSimulation();
            var dualSim = new HoloTableSimulation();

            singleSim.TriggerWaveAtLocalXZ(new Vector2(-0.66f, 0f), 1.0f);
            dualSim.TriggerWaveAtLocalXZ(new Vector2(-0.66f, 0f), 1.0f);
            dualSim.TriggerWaveAtLocalXZ(new Vector2(0.66f, 0f), 1.0f);

            float meetTime = 0.66f / dualSim.Config.WaveSpeed;
            int steps = 8;
            for (int i = 0; i < steps; i++)
            {
                singleSim.Step(meetTime / steps);
                dualSim.Step(meetTime / steps);
            }

            var singleMid = singleSim.SampleLocalXZ(0f, 0f);
            var dualMid = dualSim.SampleLocalXZ(0f, 0f);
            Assert.That(dualMid.WaveHeight, Is.GreaterThan(singleMid.WaveHeight * 1.8f),
                "Overlapping wave crests should constructively superpose.");

            // Exceed MaxActiveWaves and confirm deterministic recycling without throwing
            var capSim = new HoloTableSimulation();
            int totalSpawned = capSim.Config.MaxActiveWaves + 8;
            for (int i = 0; i < totalSpawned; i++)
            {
                capSim.TriggerWaveAtLocalXZ(new Vector2((i % 5) * 0.2f, 0f), 1.0f);
                capSim.Step(0.02f);
            }

            Assert.AreEqual(capSim.Config.MaxActiveWaves, capSim.ActiveWaveCount);
            var newestCheck = capSim.GetActiveWave(0);
            Assert.That(newestCheck.Age, Is.LessThan(capSim.Config.WaveLifetime));
        }

        [Test]
        public void WriteInteractionFieldHalf4_MatchesSampleUV_AndStaysFiniteOver100kSteps()
        {
            var sim = new HoloTableSimulation();
            sim.SetPointerLocalXZ(new Vector2(0.55f, -0.40f));
            sim.TriggerWaveAtLocalXZ(new Vector2(-0.45f, 0.30f), 1.0f);
            sim.TriggerWaveAtLocalXZ(new Vector2(0.20f, 0.60f), 0.8f);
            sim.Step(0.20f);
            sim.Step(0.20f);

            const int res = 64;
            var buffer = new ushort[res * res * 4];
            sim.WriteInteractionFieldHalf4(buffer, res, res);

            for (int row = 0; row < res; row += 7)
            {
                for (int col = 0; col < res; col += 7)
                {
                    int idx = (row * res + col) * 4;
                    float r = Mathf.HalfToFloat(buffer[idx]);
                    float g = Mathf.HalfToFloat(buffer[idx + 1]);
                    float b = Mathf.HalfToFloat(buffer[idx + 2]);
                    float a = Mathf.HalfToFloat(buffer[idx + 3]);

                    var expected = sim.SampleUV((col + 0.5f) / res, (row + 0.5f) / res);
                    Assert.AreEqual(expected.TotalDisplacement, r, 2e-3f);
                    Assert.AreEqual(expected.TargeterMask, g, 2e-3f);
                    Assert.AreEqual(expected.WaveCrestGlow, b, 2e-3f);
                    Assert.AreEqual(expected.MountainMask, a, 2e-3f);
                }
            }

            // Adversarial step inputs + 100,000 steps long-run stability check
            sim.Step(0f);
            sim.Step(-2.5f);
            sim.Step(float.NaN);
            sim.Step(float.PositiveInfinity);
            sim.Step(15.0f);

            for (int i = 0; i < 100000; i++)
            {
                if (i % 2500 == 0)
                {
                    float angle = i * 0.01f;
                    sim.SetPointerLocalXZ(new Vector2(Mathf.Cos(angle) * 1.4f, Mathf.Sin(angle) * 1.1f));
                    sim.TriggerWaveAtLocalXZ(new Vector2(Mathf.Sin(angle) * 1.2f, Mathf.Cos(angle) * 0.9f), 1.0f);
                }

                sim.Step(1f / 60f);
            }

            Assert.That(sim.TargeterPhase, Is.GreaterThanOrEqualTo(0f).And.LessThan(Mathf.PI * 2f + 1e-4f));
            sim.WriteInteractionFieldHalf4(buffer, res, res);
            for (int i = 0; i < buffer.Length; i++)
            {
                float decoded = Mathf.HalfToFloat(buffer[i]);
                Assert.IsTrue(float.IsFinite(decoded), $"Buffer index {i} produced non-finite half value.");
            }
        }

        [Test]
        public void PackGpuUniforms_MatchesSampleLocalXZ_With8SimultaneousWaves_AndZeroAllocations()
        {
            var sim = new HoloTableSimulation();
            sim.SetPointerLocalXZ(new Vector2(-0.62f, 0.44f));

            Vector2[] waveOrigins =
            {
                new Vector2(-1.10f, -0.75f),
                new Vector2(0.85f, -0.50f),
                new Vector2(1.20f, 0.65f),
                new Vector2(-0.30f, 1.05f),
                new Vector2(0.15f, -1.15f),
                new Vector2(-0.95f, 0.20f),
                new Vector2(0.55f, 0.15f),
                new Vector2(0.00f, 0.00f)
            };

            for (int i = 0; i < waveOrigins.Length; i++)
            {
                sim.TriggerWaveAtLocalXZ(waveOrigins[i], 0.7f + 0.08f * i);
                sim.Step(0.09f);
            }

            Assert.AreEqual(8, sim.ActiveWaveCount);

            int packedCount = sim.PackGpuUniforms(
                out var cursorState,
                out var mountainParams,
                out var targeterParams,
                out var waveParams,
                out var waves0,
                out var waves1);
            Assert.AreEqual(8, packedCount);

            for (int ix = -8; ix <= 8; ix++)
            {
                for (int iz = -8; iz <= 8; iz++)
                {
                    float x = ix * 0.22f;
                    float z = iz * 0.20f;
                    if (!sim.IsInsideHexagon(new Vector2(x, z)))
                    {
                        continue;
                    }

                    var cpu = sim.SampleLocalXZ(x, z);
                    var gpu = EvaluatePackedGpuShaderMirror(
                        new Vector3(x, 0f, z),
                        cursorState,
                        mountainParams,
                        targeterParams,
                        waveParams,
                        waves0,
                        waves1);

                    Assert.AreEqual(cpu.TotalDisplacement, gpu.x, 1e-4f, $"Displacement mismatch at ({x},{z})");
                    Assert.AreEqual(cpu.TargeterMask, gpu.y, 1e-4f, $"TargeterMask mismatch at ({x},{z})");
                    Assert.AreEqual(cpu.WaveCrestGlow, gpu.z, 1e-4f, $"WaveCrestGlow mismatch at ({x},{z})");
                    Assert.AreEqual(cpu.MountainMask, gpu.w, 1e-4f, $"MountainMask mismatch at ({x},{z})");
                }
            }

            // Warmup JIT, then verify zero managed allocations and sub-0.05ms per frame across 1,000 frames
            sim.Step(1f / 60f);
            sim.PackGpuUniforms(out _, out _, out _, out _, out _, out _);

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++)
            {
                sim.Step(1f / 60f);
                sim.PackGpuUniforms(out _, out _, out _, out _, out _, out _);
            }
            sw.Stop();
            long allocAfter = GC.GetAllocatedBytesForCurrentThread();

            double avgMs = sw.Elapsed.TotalMilliseconds / 1000.0;
            Assert.AreEqual(allocBefore, allocAfter, "Step + PackGpuUniforms must allocate zero managed bytes.");
            Assert.That(avgMs, Is.LessThan(0.05), $"Step + PackGpuUniforms took {avgMs:F4} ms/frame (expected < 0.05 ms).");
        }

        private static Vector4 EvaluatePackedGpuShaderMirror(
            Vector3 targetPos,
            Vector4 cursorState,
            Vector4 mountainParams,
            Vector4 targeterParams,
            Vector4 waveParams,
            Matrix4x4 waves0,
            Matrix4x4 waves1)
        {
            float presence = cursorState.z;
            float mountainHeight = 0f;
            float targeterMask = 0f;
            float mountainMask = 0f;

            if (presence > 1e-4f)
            {
                float dx = targetPos.x - cursorState.x;
                float dz = targetPos.z - cursorState.y;
                float rSq = dx * dx + dz * dz;
                float r = Mathf.Sqrt(rSq);

                float expCore = rSq * mountainParams.y;
                float core = expCore < 12f ? Mathf.Exp(-expCore) : 0f;
                float expFoot = rSq * mountainParams.w;
                float foothill = expFoot < 12f ? Mathf.Exp(-expFoot) : 0f;

                float ringRadius = targeterParams.x;
                float invRing = targeterParams.y;
                float ringDist = Mathf.Abs(r - ringRadius);
                float expOuter = ringDist * ringDist * invRing;
                float outerRing = expOuter < 12f ? Mathf.Exp(-expOuter) : 0f;

                float innerRadius = ringRadius * (0.56f + 0.04f * cursorState.w);
                float innerDist = Mathf.Abs(r - innerRadius);
                float expInner = innerDist * innerDist * invRing * 1.8f;
                float innerRing = 0.65f * (expInner < 12f ? Mathf.Exp(-expInner) : 0f);

                float expBull = rSq * targeterParams.w;
                float bullseye = expBull < 12f ? Mathf.Exp(-expBull) : 0f;

                float t0 = Mathf.Clamp01((r - 0.16f) / 0.05f);
                float t1 = Mathf.Clamp01((r - 0.50f) / 0.06f);
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

            Vector2 w0 = EvaluateWaveMatrixMirror(targetPos, waves0, waveParams);
            Vector2 w1 = EvaluateWaveMatrixMirror(targetPos, waves1, waveParams);
            float totalDisp = Mathf.Clamp(mountainHeight + w0.x + w1.x, -0.14f, waveParams.w);
            float waveGlow = Mathf.Clamp01(w0.y + w1.y);
            return new Vector4(totalDisp, targeterMask, waveGlow, mountainMask);
        }

        private static Vector2 EvaluateWaveMatrixMirror(Vector3 targetPos, Matrix4x4 wavesMat, Vector4 waveParams)
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
                float dx = targetPos.x - w.x;
                float dz = targetPos.z - w.y;
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

                float r = Mathf.Sqrt(rSq);
                float delta = r - front;
                float e0 = delta * delta * invTwoWidthSq;
                float primary = e0 < 12f ? Mathf.Exp(-e0) : 0f;

                float d1 = delta + width * 1.55f;
                float e1 = d1 * d1 * invTwoWidthSq * 0.85f;
                float trough = (r <= front && e1 < 12f) ? 0.34f * Mathf.Exp(-e1) : 0f;

                float d2 = delta + width * 3.0f;
                float e2 = d2 * d2 * invTwoWidthSq * 0.75f;
                float secondary = (r <= front && e2 < 12f) ? 0.22f * Mathf.Exp(-e2) : 0f;

                sumH += baseAmp * env * (primary - trough + secondary);
                sumG += env * Mathf.Clamp01(primary + secondary * 0.45f);
            }

            return new Vector2(sumH, sumG);
        }
    }
}
