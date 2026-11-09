using UnityEngine;

namespace HoloTable.Simulation
{
    /// <summary>
    /// Immutable snapshot of an active expanding radial wave on the HoloTable.
    /// </summary>
    public readonly struct HoloTableWave
    {
        public readonly Vector2 OriginLocalXZ;
        public readonly float Age;
        public readonly float Lifetime;
        public readonly float Strength;
        public readonly float Speed;

        public HoloTableWave(Vector2 originLocalXZ, float age, float lifetime, float strength, float speed)
        {
            OriginLocalXZ = originLocalXZ;
            Age = age;
            Lifetime = lifetime;
            Strength = strength;
            Speed = speed;
        }

        public float FrontRadius => Speed * Age;

        public float NormalizedAge => Lifetime > 0f ? Mathf.Clamp01(Age / Lifetime) : 1f;
    }
}
