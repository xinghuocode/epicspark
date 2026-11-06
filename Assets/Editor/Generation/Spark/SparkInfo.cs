using UnityEngine;

namespace SparkCode.Generation
{
    /// <summary>Result of <see cref="SparkBuilder.Build"/>; consumed by the atmosphere builder and the Lead's scene builder.</summary>
    public struct SparkInfo
    {
        public GameObject Root;
        public Light CoreLight;
        public ParticleSystem MoteParticles;
        public Transform Anchor;
    }
}
