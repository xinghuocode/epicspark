using UnityEngine;

namespace SparkCode
{
    public class OrbitCamera : MonoBehaviour
    {
        public Transform target;
        public float radius = 15f;
        public float height = 2.8f;
        public float lookHeight = 3.5f;
        public float fieldOfView = 50f;
        public float introRadius = 18.5f;
        public float introDuration = 7f;
        public float swayDegrees = 4f;
        public float swaySpeed = 0.035f;
        public float bobAmplitude = 0.07f;
        public float bobSpeed = 0.09f;
        public float orbitSpeed = 0f;
        public float focusRadius = 7.4f;
        public float focusLookHeight = 4f;
        public float focusBlendSpeed = 0.55f;

        float _time;
        float _focusTarget;
        float _focusBlend;
        float _paused;

        public float CurrentAzimuth { get; private set; }

        void Start()
        {
            _time = 0f;
            Camera cam = GetComponent<Camera>();
            if (cam != null)
            {
                cam.fieldOfView = fieldOfView;
                cam.nearClipPlane = 0.08f;
                cam.farClipPlane = 140f;
            }
        }

        public void SetPaused(bool paused)
        {
            _paused = paused ? 1f : 0f;
        }

        public void FocusOnSpark()
        {
            _focusTarget = 1f;
        }

        public void ResetView()
        {
            _focusTarget = 0f;
        }

        void LateUpdate()
        {
            if (target == null) return;

            _time += Time.deltaTime;
            _focusBlend = Mathf.MoveTowards(_focusBlend, _focusTarget, Time.deltaTime * focusBlendSpeed);

            float intro = introDuration > 0f ? Mathf.Clamp01(_time / introDuration) : 1f;
            float eased = intro * intro * (3f - 2f * intro);
            float currentRadius = Mathf.Lerp(introRadius, radius, eased);
            float currentLook = lookHeight;
            currentRadius = Mathf.Lerp(currentRadius, focusRadius, _focusBlend);
            currentLook = Mathf.Lerp(currentLook, focusLookHeight, _focusBlend);

            float sway = Mathf.Sin(_time * swaySpeed * Mathf.PI * 2f) * swayDegrees;
            float drift = orbitSpeed * _time * (1f - _paused);
            CurrentAzimuth = sway + drift;
            float bob = Mathf.Sin(_time * bobSpeed * Mathf.PI * 2f) * bobAmplitude;

            Vector3 offset = Quaternion.Euler(0f, CurrentAzimuth, 0f) * new Vector3(0f, height + bob, currentRadius);
            Vector3 position = target.position + offset;
            Quaternion rotation = Quaternion.LookRotation(target.position + Vector3.up * currentLook - position, Vector3.up);

            transform.SetPositionAndRotation(position, rotation);
        }
    }
}
