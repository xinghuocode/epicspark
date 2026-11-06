using UnityEngine;

namespace SparkCode.Generation
{
    /// <summary>Shared dimensions for the temple. Meters, Y up, origin at the centre of the pool island.</summary>
    public static class TempleLayout
    {
        public const float PlazaRadius = 22f;
        public const float FloorY = 0f;

        public const float PoolOuterRadius = 7.2f;
        public const float PoolInnerRadius = 3.3f; // island shore; water annulus 3.3 .. 7.2
        public const float WaterY = -0.35f;
        public const float BasinFloorY = -1.7f;
        public const float RimTopY = 0.28f;
        public const float RimInnerRadius = 6.55f;
        public const float RimOuterRadius = 7.35f;

        public const float IslandRadius = 2.7f;
        public const float IslandTopY = 0.55f;

        public const int InnerColumnCount = 16;
        public const float InnerColumnRing = 10.4f;
        public const float InnerColumnHeight = 10.6f;
        public const float InnerEntablatureBottom = 10.6f;
        public const float InnerEntablatureTop = 11.9f;

        public const int DomeStepCount = 12;
        public const float DomeBaseY = 11.9f;
        public const float DomeBaseRadius = 10.1f;
        public const float OculusRadius = 2.35f;
        public const float OculusY = 24.5f;

        public const float CeilingY = 16.2f;
        public const float CeilingOuterRadius = 21.4f;
        /// <summary>Approximate inner radius of the flat coffered ceiling where it meets the dome.</summary>
        public const float CeilingInnerRadius = 7.6f;

        public const int OuterColumnCount = 24;
        public const float OuterColumnRing = 18.0f;
        public const float OuterColumnHeight = 16.2f;
        public const float OuterEntablatureTop = 17.4f;

        public const float WallRadius = 21.4f;
        public const float WallThickness = 0.9f;
        public const float WallHeight = 17.4f;
        public const float DoorHalfAngle = 6.5f;   // degrees
        public const float DoorHeight = 9.5f;

        public static readonly Vector3 SparkAnchor = new Vector3(0f, 7.4f, 0f);
        public static readonly Vector3 CameraTarget = new Vector3(0f, 0.6f, 0f);

        public const float CameraOrbitRadius = 14.5f;
        public const float CameraHeight = 3.0f;
        public const float CameraLookHeight = 5.0f;
        public const float CameraFov = 46f;

        // UI hooks
        public const string StartScenePath = "Assets/Scenes/StartScene.unity";
        public const string StartSceneName = "StartScene";
    }
}
