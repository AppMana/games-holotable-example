namespace HoloTable.Simulation
{
    /// <summary>
    /// Sampled height &amp; holographic mask state at a point on the HoloTable.
    /// </summary>
    public readonly struct HoloTableSample
    {
        /// <summary>Vertical elevation (meters) contributed by the live cursor mountain and targeter ridge.</summary>
        public readonly float MountainHeight;

        /// <summary>Signed vertical displacement (meters) contributed by superposed click/touch waves.</summary>
        public readonly float WaveHeight;

        /// <summary>Clamped total displacement (meters) written to channel R of the interaction field.</summary>
        public readonly float TotalDisplacement;

        /// <summary>Normalized [0..1] targeter reticle mask (ring + crosshairs + bullseye) written to channel G.</summary>
        public readonly float TargeterMask;

        /// <summary>Normalized [0..1] wave crest glow written to channel B.</summary>
        public readonly float WaveCrestGlow;

        /// <summary>Normalized [0..1] cursor mountain footprint mask written to channel A.</summary>
        public readonly float MountainMask;

        public HoloTableSample(
            float mountainHeight,
            float waveHeight,
            float totalDisplacement,
            float targeterMask,
            float waveCrestGlow,
            float mountainMask)
        {
            MountainHeight = mountainHeight;
            WaveHeight = waveHeight;
            TotalDisplacement = totalDisplacement;
            TargeterMask = targeterMask;
            WaveCrestGlow = waveCrestGlow;
            MountainMask = mountainMask;
        }
    }
}
