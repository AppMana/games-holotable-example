using System;
using UnityEngine;

namespace HoloTable.Simulation
{
    /// <summary>
    /// Pure configuration parameters for the deterministic HoloTable terrain &amp; interaction simulation.
    /// Contains zero rendering, VFX, or input device dependencies.
    /// </summary>
    [Serializable]
    public struct HoloTableSimulationConfig
    {
        [Header("Hexagonal Table Geometry (VFX Local Space)")]
        public float HalfExtentX;
        public float HalfExtentZ;
        public float BaseLocalY;
        public float MaxDisplacement;

        [Header("Cursor & Targeter Dynamics")]
        public float CursorFollowSharpness;
        public float PresenceFadeInSpeed;
        public float PresenceFadeOutSpeed;
        public float TargeterPhaseSpeed;

        [Header("Real-Time Cursor Mountain")]
        public float MountainPeakHeight;
        public float MountainCoreSigma;
        public float MountainFoothillHeight;
        public float MountainFoothillSigma;

        [Header("Targeter Reticle Geometry")]
        public float TargeterRingRadius;
        public float TargeterRingWidth;
        public float TargeterRidgeHeight;
        public float TargeterCenterRadius;
        public float TargeterCrosshairWidth;
        public float TargeterCrosshairInnerRadius;
        public float TargeterCrosshairOuterRadius;

        [Header("Click / Touch Radial Waves")]
        public float WaveSpeed;
        public float WaveLifetime;
        public float WaveAmplitude;
        public float WaveRingWidth;
        public int MaxActiveWaves;

        public static HoloTableSimulationConfig Default => new HoloTableSimulationConfig
        {
            HalfExtentX = 2.525f,
            HalfExtentZ = 2.196f,
            BaseLocalY = -0.013f,
            MaxDisplacement = 0.44f,

            CursorFollowSharpness = 120f,
            PresenceFadeInSpeed = 40f,
            PresenceFadeOutSpeed = 5.5f,
            TargeterPhaseSpeed = 1.6f,

            MountainPeakHeight = 0.33f,
            MountainCoreSigma = 0.25f,
            MountainFoothillHeight = 0.065f,
            MountainFoothillSigma = 0.56f,

            TargeterRingRadius = 0.39f,
            TargeterRingWidth = 0.052f,
            TargeterRidgeHeight = 0.045f,
            TargeterCenterRadius = 0.088f,
            TargeterCrosshairWidth = 0.044f,
            TargeterCrosshairInnerRadius = 0.16f,
            TargeterCrosshairOuterRadius = 0.56f,

            WaveSpeed = 1.65f,
            WaveLifetime = 2.6f,
            WaveAmplitude = 0.24f,
            WaveRingWidth = 0.15f,
            MaxActiveWaves = 16
        };

        public HoloTableSimulationConfig Sanitized()
        {
            var c = this;
            c.HalfExtentX = SanitizePositive(c.HalfExtentX, 2.525f, 0.1f, 20f);
            c.HalfExtentZ = SanitizePositive(c.HalfExtentZ, 2.196f, 0.1f, 20f);
            if (!float.IsFinite(c.BaseLocalY))
            {
                c.BaseLocalY = -0.013f;
            }

            c.MaxDisplacement = SanitizePositive(c.MaxDisplacement, 0.44f, 0.05f, 2f);
            c.CursorFollowSharpness = SanitizePositive(c.CursorFollowSharpness, 120f, 1f, 300f);
            c.PresenceFadeInSpeed = SanitizePositive(c.PresenceFadeInSpeed, 40f, 0.5f, 200f);
            c.PresenceFadeOutSpeed = SanitizePositive(c.PresenceFadeOutSpeed, 5.5f, 0.5f, 100f);
            c.TargeterPhaseSpeed = float.IsFinite(c.TargeterPhaseSpeed) ? Mathf.Clamp(c.TargeterPhaseSpeed, -20f, 20f) : 1.6f;

            c.MountainPeakHeight = SanitizeNonNegative(c.MountainPeakHeight, 0.33f, 2f);
            c.MountainCoreSigma = SanitizePositive(c.MountainCoreSigma, 0.25f, 0.03f, 5f);
            c.MountainFoothillHeight = SanitizeNonNegative(c.MountainFoothillHeight, 0.065f, 2f);
            c.MountainFoothillSigma = SanitizePositive(c.MountainFoothillSigma, 0.56f, 0.05f, 5f);

            c.TargeterRingRadius = SanitizePositive(c.TargeterRingRadius, 0.39f, 0.05f, 5f);
            c.TargeterRingWidth = SanitizePositive(c.TargeterRingWidth, 0.052f, 0.01f, 1f);
            c.TargeterRidgeHeight = SanitizeNonNegative(c.TargeterRidgeHeight, 0.045f, 1f);
            c.TargeterCenterRadius = SanitizePositive(c.TargeterCenterRadius, 0.088f, 0.01f, 1f);
            c.TargeterCrosshairWidth = SanitizePositive(c.TargeterCrosshairWidth, 0.044f, 0.01f, 1f);
            c.TargeterCrosshairInnerRadius = SanitizeNonNegative(c.TargeterCrosshairInnerRadius, 0.16f, 5f);
            c.TargeterCrosshairOuterRadius = Mathf.Max(c.TargeterCrosshairInnerRadius + 0.02f,
                SanitizePositive(c.TargeterCrosshairOuterRadius, 0.56f, 0.05f, 5f));

            c.WaveSpeed = SanitizePositive(c.WaveSpeed, 1.65f, 0.05f, 20f);
            c.WaveLifetime = SanitizePositive(c.WaveLifetime, 2.6f, 0.1f, 30f);
            c.WaveAmplitude = SanitizeNonNegative(c.WaveAmplitude, 0.24f, 2f);
            c.WaveRingWidth = SanitizePositive(c.WaveRingWidth, 0.15f, 0.02f, 2f);
            c.MaxActiveWaves = Mathf.Clamp(c.MaxActiveWaves <= 0 ? 16 : c.MaxActiveWaves, 1, 64);
            return c;
        }

        private static float SanitizePositive(float value, float fallback, float min, float max)
        {
            if (!float.IsFinite(value) || value <= 0f)
            {
                return fallback;
            }

            return Mathf.Clamp(value, min, max);
        }

        private static float SanitizeNonNegative(float value, float fallback, float max)
        {
            if (!float.IsFinite(value) || value < 0f)
            {
                return fallback;
            }

            return Mathf.Min(value, max);
        }
    }
}
