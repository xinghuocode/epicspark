using UnityEngine;

namespace SparkCode
{
    public class Billboard : MonoBehaviour
    {
        public Transform cameraTransform;
        public float rollSpeed;
        public float baseRoll;
        public bool faceCamera = true;

        float _roll;

        void LateUpdate()
        {
            if (!faceCamera) return;
            if (cameraTransform == null)
            {
                Camera cam = Camera.main;
                if (cam == null) return;
                cameraTransform = cam.transform;
            }

            _roll += rollSpeed * Time.deltaTime;
            transform.rotation = cameraTransform.rotation * Quaternion.Euler(0f, 0f, baseRoll + _roll);
        }
    }
}
