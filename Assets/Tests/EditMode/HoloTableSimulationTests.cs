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
    }
}
