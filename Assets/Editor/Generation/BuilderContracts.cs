using UnityEngine;

namespace SparkCode.Generation
{
    /// <summary>Shared result type produced by the environment builder and consumed by lighting/camera/atmosphere.</summary>
    public struct EnvironmentInfo
    {
        public Transform Root;
        public Transform SparkAnchor;
        public Vector3 CameraTarget;
    }
}
