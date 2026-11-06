using UnityEngine;

namespace SparkCode
{
    /// <summary>
    /// Small showcase controller for the imported Blender temple scene.
    /// Keys 1-4 switch the authored cameras; the star slowly spins and bobs so the
    /// still-life reads as alive even before gameplay systems are wired up.
    /// </summary>
    public class TempleShowcaseController : MonoBehaviour
    {
        public Camera[] cameras;
        public Transform starPivot;
        public float starSpinSpeed = 5.0f;
        public float starBobAmplitude = 0.12f;
        public float starBobSpeed = 0.72f;

        Vector3 starBasePosition;
        int activeCamera;
        float verticalOffset;

        /// <summary>
        /// Additional vertical offset applied on top of the idle bob. The gameplay
        /// UI uses this for its proximity lowering so both effects can coexist.
        /// </summary>
        public void SetVerticalOffset(float offset)
        {
            verticalOffset = offset;
        }

        void Awake()
        {
            if (starPivot != null) starBasePosition = starPivot.localPosition;
            SelectCamera(0);
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Alpha1)) SelectCamera(0);
            if (Input.GetKeyDown(KeyCode.Alpha2)) SelectCamera(1);
            if (Input.GetKeyDown(KeyCode.Alpha3)) SelectCamera(2);
            if (Input.GetKeyDown(KeyCode.Alpha4)) SelectCamera(3);

            if (starPivot != null)
            {
                var p = starBasePosition;
                p.y += verticalOffset + Mathf.Sin(Time.time * starBobSpeed) * starBobAmplitude;
                starPivot.localPosition = p;
                starPivot.Rotate(Vector3.up, starSpinSpeed * Time.deltaTime, Space.Self);
            }
        }

        public void SelectCamera(int index)
        {
            if (cameras == null || cameras.Length == 0) return;
            activeCamera = Mathf.Clamp(index, 0, cameras.Length - 1);
            for (int i = 0; i < cameras.Length; i++)
            {
                if (cameras[i] == null) continue;
                cameras[i].enabled = (i == activeCamera);
                var listener = cameras[i].GetComponent<AudioListener>();
                if (listener != null) listener.enabled = (i == activeCamera);
            }

            if (starPivot != null && cameras[activeCamera] != null)
            {
                foreach (var billboard in starPivot.GetComponentsInChildren<Billboard>(true))
                    billboard.cameraTransform = cameras[activeCamera].transform;
            }
        }

        void OnGUI()
        {
            if (cameras == null || cameras.Length == 0) return;
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 17,
                normal = { textColor = new Color(1f, 0.86f, 0.58f, 0.82f) }
            };
            GUI.Label(new Rect(24f, Screen.height - 44f, 760f, 28f),
                "1 全景    2 穹顶    3 星火    4 侧景", style);
        }
    }
}
