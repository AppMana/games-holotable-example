using System;
using UnityEngine;

namespace HoloTable.Simulation
{
    /// <summary>
    /// Pure, deterministic CPU data model for real-time HoloTable interaction:
    /// - Tracks normalized &amp; local-space pointer coordinates clamped to the hexagonal pin bed.
    /// - Evaluates the live cursor mountain + sculpted targeter reticle around the pointer.
    /// - Propagates &amp; superposes expanding radial waves spawned on pointer click/touch.
    /// - Serializes the 2D interaction height/mask field into half-precision texels with zero GC allocations.
    /// </summary>
    public sealed class HoloTableSimulation
    {
        private struct WaveSlot
        {
            public Vector2 OriginLocalXZ;
            public float Age;
            public float Strength;
        }

        private const float TwoPi = Mathf.PI * 2f;
        private const float MinTotalDisplacement = -0.06f;

        private readonly HoloTableSimulationConfig m_Config;
        private readonly WaveSlot[] m_Waves;

        private int m_ActiveWaveCount;
        private bool m_HasPointerInput;
        private Vector2 m_TargetCursorLocalXZ;
        private Vector2 m_CurrentCursorLocalXZ;
        private float m_TargeterPresence;
        private float m_TargeterPhase;

        public HoloTableSimulation()
            : this(HoloTableSimulationConfig.Default)
        {
        }

        public HoloTableSimulation(HoloTableSimulationConfig config)
        {
            m_Config = config.Sanitized();
            m_Waves = new WaveSlot[m_Config.MaxActiveWaves];
        }

        public HoloTableSimulationConfig Config => m_Config;

        public bool HasPointerInput => m_HasPointerInput;

        public Vector2 TargetCursorLocalXZ => m_TargetCursorLocalXZ;

        public Vector2 CurrentCursorLocalXZ => m_CurrentCursorLocalXZ;

        public Vector2 CurrentCursorUV => LocalXZToUV(m_CurrentCursorLocalXZ);

        public float TargeterPresence => m_TargeterPresence;

        public float TargeterPhase => m_TargeterPhase;

        public int ActiveWaveCount => m_ActiveWaveCount;

        public Vector3 CurrentTargetLocalPosition
        {
            get
            {
                var sample = SampleLocalXZ(m_CurrentCursorLocalXZ.x, m_CurrentCursorLocalXZ.y);
                return new Vector3(
                    m_CurrentCursorLocalXZ.x,
                    m_Config.BaseLocalY + sample.TotalDisplacement,
                    m_CurrentCursorLocalXZ.y);
            }
        }

        public Vector2 LocalXZToUV(Vector2 localXZ)
        {
            float spanX = m_Config.HalfExtentX * 2f;
            float spanZ = m_Config.HalfExtentZ * 2f;
            return new Vector2(
                Mathf.Clamp01(localXZ.x / spanX + 0.5f),
                Mathf.Clamp01(localXZ.y / spanZ + 0.5f));
        }

        public Vector2 UVToLocalXZ(Vector2 uv)
        {
            float u = float.IsFinite(uv.x) ? Mathf.Clamp01(uv.x) : 0.5f;
            float v = float.IsFinite(uv.y) ? Mathf.Clamp01(uv.y) : 0.5f;
            return new Vector2(
                (u - 0.5f) * (m_Config.HalfExtentX * 2f),
                (v - 0.5f) * (m_Config.HalfExtentZ * 2f));
        }

        /// <summary>
        /// Exact containment test for the regular hexagonal 65,536-pin bed with vertices
        /// at (±HalfExtentX, 0) and (±HalfExtentX/2, ±HalfExtentZ).
        /// </summary>
        public bool IsInsideHexagon(Vector2 localXZ)
        {
            if (!float.IsFinite(localXZ.x) || !float.IsFinite(localXZ.y))
            {
                return false;
            }

            float ax = Mathf.Abs(localXZ.x);
            float az = Mathf.Abs(localXZ.y);
            float rx = m_Config.HalfExtentX;
            float rz = m_Config.HalfExtentZ;

            if (az > rz + 1e-5f)
            {
                return false;
            }

            return (ax / rx + az / (2f * rz)) <= 1f + 1e-5f;
        }

        /// <summary>
        /// Closest-point Euclidean projection onto the hexagonal HoloTable pin bed.
        /// </summary>
        public Vector2 ClampToHexagon(Vector2 localXZ)
        {
            if (!float.IsFinite(localXZ.x) || !float.IsFinite(localXZ.y))
            {
                return Vector2.zero;
            }

            float rx = m_Config.HalfExtentX;
            float rz = m_Config.HalfExtentZ;
            float ax = Mathf.Abs(localXZ.x);
            float az = Mathf.Abs(localXZ.y);

            if (az <= rz && (ax / rx + az / (2f * rz)) <= 1f)
            {
                return localXZ;
            }

            var p = new Vector2(ax, az);

            // Top flat edge segment: (0, rz) -> (rx * 0.5, rz)
            float topX = Mathf.Clamp(ax, 0f, rx * 0.5f);
            var closestTop = new Vector2(topX, rz);

            // Slanted edge segment: a = (rx * 0.5, rz) -> b = (rx, 0)
            var segA = new Vector2(rx * 0.5f, rz);
            var segB = new Vector2(rx, 0f);
            var ab = segB - segA;
            float abLenSq = Vector2.Dot(ab, ab);
            float t = abLenSq > 1e-8f ? Mathf.Clamp01(Vector2.Dot(p - segA, ab) / abLenSq) : 0f;
            var closestSlant = segA + ab * t;

            float distTopSq = (p - closestTop).sqrMagnitude;
            float distSlantSq = (p - closestSlant).sqrMagnitude;
            var clampedAbs = distTopSq <= distSlantSq ? closestTop : closestSlant;

            return new Vector2(
                localXZ.x < 0f ? -clampedAbs.x : clampedAbs.x,
                localXZ.y < 0f ? -clampedAbs.y : clampedAbs.y);
        }

        public void SetPointerLocalXZ(Vector2 rawLocalXZ)
        {
            var clamped = ClampToHexagon(rawLocalXZ);
            bool snapImmediately = !m_HasPointerInput && m_TargeterPresence <= 0.01f;
            m_TargetCursorLocalXZ = clamped;
            if (snapImmediately)
            {
                m_CurrentCursorLocalXZ = clamped;
            }

            m_HasPointerInput = true;
        }

        public void SetPointerUV(Vector2 uv)
        {
            SetPointerLocalXZ(UVToLocalXZ(uv));
        }

        public void ClearPointer()
        {
            m_HasPointerInput = false;
        }

        public HoloTableWave TriggerWaveAtLocalXZ(Vector2 rawLocalXZ, float strength = 1f)
        {
            var origin = ClampToHexagon(rawLocalXZ);
            float cleanStrength = float.IsFinite(strength) ? Mathf.Clamp(strength, 0.05f, 3f) : 1f;

            int slotIndex;
            if (m_ActiveWaveCount < m_Waves.Length)
            {
                slotIndex = m_ActiveWaveCount;
                m_ActiveWaveCount++;
            }
            else
            {
                // Recycle the oldest wave deterministically when at capacity.
                slotIndex = 0;
                float maxAge = m_Waves[0].Age;
                for (int i = 1; i < m_Waves.Length; i++)
                {
                    if (m_Waves[i].Age > maxAge)
                    {
                        maxAge = m_Waves[i].Age;
                        slotIndex = i;
                    }
                }
            }

            m_Waves[slotIndex] = new WaveSlot
            {
                OriginLocalXZ = origin,
                Age = 0f,
                Strength = cleanStrength
            };

            return new HoloTableWave(origin, 0f, m_Config.WaveLifetime, cleanStrength, m_Config.WaveSpeed);
        }

        public HoloTableWave TriggerWaveAtUV(Vector2 uv, float strength = 1f)
        {
            return TriggerWaveAtLocalXZ(UVToLocalXZ(uv), strength);
        }

        public HoloTableWave GetActiveWave(int index)
        {
            if (index < 0 || index >= m_ActiveWaveCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            var w = m_Waves[index];
            return new HoloTableWave(w.OriginLocalXZ, w.Age, m_Config.WaveLifetime, w.Strength, m_Config.WaveSpeed);
        }

        public void Step(float deltaTime)
        {
            if (!float.IsFinite(deltaTime) || deltaTime <= 0f)
            {
                return;
            }

            float dt = Mathf.Min(deltaTime, 0.25f);

            if (m_HasPointerInput)
            {
                m_TargeterPresence = Mathf.MoveTowards(m_TargeterPresence, 1f, m_Config.PresenceFadeInSpeed * dt);
            }
            else
            {
                m_TargeterPresence = Mathf.MoveTowards(m_TargeterPresence, 0f, m_Config.PresenceFadeOutSpeed * dt);
            }

            float followBlend = 1f - Mathf.Exp(-m_Config.CursorFollowSharpness * dt);
            m_CurrentCursorLocalXZ = Vector2.Lerp(m_CurrentCursorLocalXZ, m_TargetCursorLocalXZ, followBlend);
            if (!float.IsFinite(m_CurrentCursorLocalXZ.x) || !float.IsFinite(m_CurrentCursorLocalXZ.y))
            {
                m_CurrentCursorLocalXZ = Vector2.zero;
            }

            m_TargeterPhase = Mathf.Repeat(m_TargeterPhase + m_Config.TargeterPhaseSpeed * dt, TwoPi);

            float lifetime = m_Config.WaveLifetime;
            int writeIdx = 0;
            for (int i = 0; i < m_ActiveWaveCount; i++)
            {
                var wave = m_Waves[i];
                wave.Age += dt;
                if (wave.Age < lifetime && float.IsFinite(wave.Age))
                {
                    m_Waves[writeIdx++] = wave;
                }
            }

            m_ActiveWaveCount = writeIdx;
        }

        public HoloTableSample SampleUV(float u, float v)
        {
            var local = UVToLocalXZ(new Vector2(u, v));
            return SampleLocalXZ(local.x, local.y);
        }

        public HoloTableSample SampleLocalXZ(float x, float z)
        {
            if (!float.IsFinite(x) || !float.IsFinite(z))
            {
                return default;
            }

            float mountainHeight = 0f;
            float targeterMask = 0f;
            float mountainMask = 0f;

            if (m_TargeterPresence > 1e-4f)
            {
                EvaluateCursorMountainAndTargeter(
                    x,
                    z,
                    Mathf.Sin(m_TargeterPhase),
                    out mountainHeight,
                    out targeterMask,
                    out mountainMask);
            }

            float waveHeight = 0f;
            float waveCrestGlow = 0f;

            if (m_ActiveWaveCount > 0)
            {
                EvaluateWaves(x, z, out waveHeight, out waveCrestGlow);
            }

            float total = Mathf.Clamp(mountainHeight + waveHeight, MinTotalDisplacement, m_Config.MaxDisplacement);

            return new HoloTableSample(
                mountainHeight,
                waveHeight,
                total,
                targeterMask,
                waveCrestGlow,
                mountainMask);
        }

        public const int MaxGpuPackedWaves = 8;

        /// <summary>
        /// Packs the live simulation state into compact uniform vectors and two 4x4 matrices
        /// (up to 8 simultaneous overlapping waves) for direct zero-texture GPU compute evaluation.
        /// Performs O(ActiveWaveCount) scalar work with zero heap allocations.
        /// </summary>
        public int PackGpuUniforms(
            out Vector4 cursorState,
            out Vector4 mountainParams,
            out Vector4 targeterParams,
            out Vector4 waveParams,
            out Matrix4x4 waves0,
            out Matrix4x4 waves1)
        {
            cursorState = new Vector4(
                m_CurrentCursorLocalXZ.x,
                m_CurrentCursorLocalXZ.y,
                m_TargeterPresence,
                Mathf.Sin(m_TargeterPhase));

            float invCoreTwoSigmaSq = 1f / (2f * m_Config.MountainCoreSigma * m_Config.MountainCoreSigma);
            float invFoothillTwoSigmaSq = 1f / (2f * m_Config.MountainFoothillSigma * m_Config.MountainFoothillSigma);
            mountainParams = new Vector4(
                m_Config.MountainPeakHeight,
                invCoreTwoSigmaSq,
                m_Config.MountainFoothillHeight,
                invFoothillTwoSigmaSq);

            float invRingTwoSigmaSq = 1f / (2f * m_Config.TargeterRingWidth * m_Config.TargeterRingWidth);
            float invCenterTwoSigmaSq = 1f / (2f * m_Config.TargeterCenterRadius * m_Config.TargeterCenterRadius);
            targeterParams = new Vector4(
                m_Config.TargeterRingRadius,
                invRingTwoSigmaSq,
                m_Config.TargeterRidgeHeight,
                invCenterTwoSigmaSq);

            float invWaveTwoWidthSq = 1f / (2f * m_Config.WaveRingWidth * m_Config.WaveRingWidth);
            waveParams = new Vector4(
                m_Config.WaveRingWidth,
                invWaveTwoWidthSq,
                m_Config.WaveAmplitude,
                m_Config.MaxDisplacement);

            waves0 = default;
            waves1 = default;

            if (m_ActiveWaveCount <= 0)
            {
                return 0;
            }

            float speed = m_Config.WaveSpeed;
            float lifetime = m_Config.WaveLifetime;
            int startIdx = Mathf.Max(0, m_ActiveWaveCount - MaxGpuPackedWaves);
            int packedCount = 0;

            for (int i = startIdx; i < m_ActiveWaveCount && packedCount < MaxGpuPackedWaves; i++)
            {
                var wave = m_Waves[i];
                float front = speed * wave.Age;
                float normalizedAge = Mathf.Clamp01(wave.Age / lifetime);
                float attack = SmoothStep(0f, 0.045f, wave.Age);
                float decay = (1f - normalizedAge) * Mathf.Sqrt(1f - normalizedAge);
                float envelope = attack * decay * wave.Strength;

                var packedRow = new Vector4(wave.OriginLocalXZ.x, wave.OriginLocalXZ.y, front, envelope);
                if (packedCount < 4)
                {
                    waves0.SetRow(packedCount, packedRow);
                }
                else
                {
                    waves1.SetRow(packedCount - 4, packedRow);
                }

                packedCount++;
            }

            return packedCount;
        }

        public void WriteInteractionFieldHalf4(ushort[] destination, int width, int height)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            WriteInteractionFieldHalf4(destination.AsSpan(), width, height);
        }

        /// <summary>
        /// Writes a width x height RGBAHalf texture buffer (R=TotalDisplacement, G=TargeterMask,
        /// B=WaveCrestGlow, A=MountainMask) with zero heap allocations.
        /// </summary>
        public void WriteInteractionFieldHalf4(Span<ushort> destination, int width, int height)
        {
            if (width <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            if (height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height));
            }

            int requiredLength = width * height * 4;
            if (destination.Length < requiredLength)
            {
                throw new ArgumentException($"Destination length {destination.Length} is smaller than required {requiredLength}.", nameof(destination));
            }

            ushort zeroHalf = Mathf.FloatToHalf(0f);
            if (m_TargeterPresence <= 1e-4f && m_ActiveWaveCount == 0)
            {
                destination.Slice(0, requiredLength).Fill(zeroHalf);
                return;
            }

            float minX = -m_Config.HalfExtentX;
            float spanX = m_Config.HalfExtentX * 2f;
            float minZ = -m_Config.HalfExtentZ;
            float spanZ = m_Config.HalfExtentZ * 2f;

            float invWidth = 1f / width;
            float invHeight = 1f / height;
            float phasePulse = Mathf.Sin(m_TargeterPhase);

            float mountainMaxRadius = Mathf.Max(
                m_Config.MountainFoothillSigma * 5.0f,
                m_Config.TargeterCrosshairOuterRadius + m_Config.TargeterRingWidth * 5.0f);
            float mountainMaxRadiusSq = mountainMaxRadius * mountainMaxRadius;
            bool hasCursor = m_TargeterPresence > 1e-4f;
            float cx = m_CurrentCursorLocalXZ.x;
            float cz = m_CurrentCursorLocalXZ.y;

            int idx = 0;
            for (int row = 0; row < height; row++)
            {
                float v = (row + 0.5f) * invHeight;
                float z = minZ + v * spanZ;
                float dzCursor = z - cz;
                bool rowNearCursor = hasCursor && Mathf.Abs(dzCursor) <= mountainMaxRadius;

                for (int col = 0; col < width; col++)
                {
                    float u = (col + 0.5f) * invWidth;
                    float x = minX + u * spanX;

                    float mountainHeight = 0f;
                    float targeterMask = 0f;
                    float mountainMask = 0f;

                    if (rowNearCursor)
                    {
                        float dxCursor = x - cx;
                        if (dxCursor * dxCursor + dzCursor * dzCursor <= mountainMaxRadiusSq)
                        {
                            EvaluateCursorMountainAndTargeter(
                                x,
                                z,
                                phasePulse,
                                out mountainHeight,
                                out targeterMask,
                                out mountainMask);
                        }
                    }

                    float waveHeight = 0f;
                    float waveGlow = 0f;
                    if (m_ActiveWaveCount > 0)
                    {
                        EvaluateWaves(x, z, out waveHeight, out waveGlow);
                    }

                    if (mountainHeight == 0f && waveHeight == 0f && targeterMask == 0f && waveGlow == 0f && mountainMask == 0f)
                    {
                        destination[idx] = zeroHalf;
                        destination[idx + 1] = zeroHalf;
                        destination[idx + 2] = zeroHalf;
                        destination[idx + 3] = zeroHalf;
                    }
                    else
                    {
                        float total = Mathf.Clamp(mountainHeight + waveHeight, MinTotalDisplacement, m_Config.MaxDisplacement);
                        destination[idx] = Mathf.FloatToHalf(total);
                        destination[idx + 1] = Mathf.FloatToHalf(targeterMask);
                        destination[idx + 2] = Mathf.FloatToHalf(waveGlow);
                        destination[idx + 3] = Mathf.FloatToHalf(mountainMask);
                    }

                    idx += 4;
                }
            }
        }

        private void EvaluateCursorMountainAndTargeter(
            float x,
            float z,
            float phasePulse,
            out float mountainHeight,
            out float targeterMask,
            out float mountainMask)
        {
            float dx = x - m_CurrentCursorLocalXZ.x;
            float dz = z - m_CurrentCursorLocalXZ.y;
            float rSq = dx * dx + dz * dz;
            float r = Mathf.Sqrt(rSq);

            float invCoreTwoSigmaSq = 1f / (2f * m_Config.MountainCoreSigma * m_Config.MountainCoreSigma);
            float invFoothillTwoSigmaSq = 1f / (2f * m_Config.MountainFoothillSigma * m_Config.MountainFoothillSigma);

            float core = FastGaussianFromR2(rSq, invCoreTwoSigmaSq);
            float foothill = FastGaussianFromR2(rSq, invFoothillTwoSigmaSq);

            // Outer targeter ring + pulsing inner lock ring + cardinal HUD crosshair ticks + summit bullseye
            float ringDist = Mathf.Abs(r - m_Config.TargeterRingRadius);
            float invRingTwoSigmaSq = 1f / (2f * m_Config.TargeterRingWidth * m_Config.TargeterRingWidth);
            float outerRing = FastGaussianFromR2(ringDist * ringDist, invRingTwoSigmaSq);

            float innerRingRadius = m_Config.TargeterRingRadius * (0.56f + 0.04f * phasePulse);
            float innerRingDist = Mathf.Abs(r - innerRingRadius);
            float innerRing = 0.65f * FastGaussianFromR2(innerRingDist * innerRingDist, invRingTwoSigmaSq * 1.8f);

            float bullseye = FastGaussianFromR2(rSq, 1f / (2f * m_Config.TargeterCenterRadius * m_Config.TargeterCenterRadius));

            float radialWindow = SmoothStep(m_Config.TargeterCrosshairInnerRadius, m_Config.TargeterCrosshairInnerRadius + 0.05f, r)
                               * (1f - SmoothStep(m_Config.TargeterCrosshairOuterRadius - 0.06f, m_Config.TargeterCrosshairOuterRadius, r));

            float crosshair = 0f;
            if (radialWindow > 1e-4f)
            {
                float invTickTwoSigmaSq = 1f / (2f * m_Config.TargeterCrosshairWidth * m_Config.TargeterCrosshairWidth);
                float tickX = FastGaussianFromR2(dx * dx, invTickTwoSigmaSq);
                float tickZ = FastGaussianFromR2(dz * dz, invTickTwoSigmaSq);
                crosshair = radialWindow * Mathf.Max(tickX, tickZ) * 0.92f;
            }

            float presence = m_TargeterPresence;
            float rawMountain = m_Config.MountainPeakHeight * core
                              + m_Config.MountainFoothillHeight * foothill
                              + m_Config.TargeterRidgeHeight * outerRing;

            mountainHeight = rawMountain * presence;
            targeterMask = Mathf.Clamp01(Mathf.Max(Mathf.Max(outerRing, innerRing), Mathf.Max(bullseye * 0.95f, crosshair)) * presence);
            mountainMask = Mathf.Clamp01(Mathf.Max(foothill, outerRing * 0.85f) * presence);
        }

        private void EvaluateWaves(float x, float z, out float waveHeight, out float waveCrestGlow)
        {
            float sumHeight = 0f;
            float sumGlow = 0f;

            float speed = m_Config.WaveSpeed;
            float lifetime = m_Config.WaveLifetime;
            float width = m_Config.WaveRingWidth;
            float invTwoWidthSq = 1f / (2f * width * width);
            float maxBandOffset = width * 4.0f;
            float baseAmp = m_Config.WaveAmplitude;

            for (int i = 0; i < m_ActiveWaveCount; i++)
            {
                var wave = m_Waves[i];
                float front = speed * wave.Age;
                float maxReach = front + maxBandOffset;

                float dx = x - wave.OriginLocalXZ.x;
                if (Mathf.Abs(dx) > maxReach)
                {
                    continue;
                }

                float dz = z - wave.OriginLocalXZ.y;
                if (Mathf.Abs(dz) > maxReach)
                {
                    continue;
                }

                float rSq = dx * dx + dz * dz;
                float minReach = Mathf.Max(0f, front - maxBandOffset * 1.4f);
                if (rSq > maxReach * maxReach || rSq < minReach * minReach)
                {
                    continue;
                }

                float r = Mathf.Sqrt(rSq);
                float normalizedAge = Mathf.Clamp01(wave.Age / lifetime);
                float attack = SmoothStep(0f, 0.045f, wave.Age);
                float decay = (1f - normalizedAge) * Mathf.Sqrt(1f - normalizedAge);
                float envelope = attack * decay * wave.Strength;

                float delta = r - front;
                float primaryCrest = FastGaussianFromR2(delta * delta, invTwoWidthSq);

                // Trailing trough + secondary harmonic ripple behind the expanding front
                float trailTroughDelta = delta + width * 1.55f;
                float trailingTrough = r <= front ? 0.34f * FastGaussianFromR2(trailTroughDelta * trailTroughDelta, invTwoWidthSq * 0.85f) : 0f;

                float secondaryDelta = delta + width * 3.0f;
                float secondaryCrest = r <= front ? 0.22f * FastGaussianFromR2(secondaryDelta * secondaryDelta, invTwoWidthSq * 0.75f) : 0f;

                sumHeight += baseAmp * envelope * (primaryCrest - trailingTrough + secondaryCrest);
                sumGlow += envelope * Mathf.Clamp01(primaryCrest + secondaryCrest * 0.45f);
            }

            waveHeight = sumHeight;
            waveCrestGlow = Mathf.Clamp01(sumGlow);
        }

        private static float FastGaussianFromR2(float distSq, float invTwoSigmaSq)
        {
            float exponent = distSq * invTwoSigmaSq;
            if (exponent >= 12f)
            {
                return 0f;
            }

            return Mathf.Exp(-exponent);
        }

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            if (edge1 <= edge0)
            {
                return x >= edge1 ? 1f : 0f;
            }

            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }
    }
}
