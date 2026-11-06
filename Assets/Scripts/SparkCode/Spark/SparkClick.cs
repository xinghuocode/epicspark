using UnityEngine;
using UnityEngine.EventSystems;

namespace SparkCode.Spark
{
    /// <summary>
    /// Lets the player click the Spark to start the game. The menu reference is resolved
    /// at build time when available and falls back to a scene lookup so the component works
    /// even when the builder had no menu yet.
    /// <para>StartMenuController lives in the parent namespace <c>SparkCode</c>, so it resolves
    /// without an extra import from here.</para>
    /// </summary>
    public class SparkClick : MonoBehaviour
    {
        public StartMenuController menu;
        public SparkVisual visual;
        public float maxRayDistance = 400f;

        Camera _camera;

        void Awake()
        {
            Resolve();
        }

        void Resolve()
        {
            if (menu == null) menu = Object.FindObjectOfType<StartMenuController>();
            if (visual == null) visual = GetComponentInChildren<SparkVisual>(true);
            if (_camera == null) _camera = FindCamera();
        }

        static Camera FindCamera()
        {
            Camera cam = Camera.main;
            if (cam == null) cam = Object.FindObjectOfType<Camera>();
            return cam;
        }

        void Update()
        {
            if (_camera == null)
            {
                _camera = FindCamera();
                if (_camera == null) return;
            }
            if (menu == null) menu = Object.FindObjectOfType<StartMenuController>();

            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            {
                if (visual != null) visual.SetHover(false);
                return;
            }

            bool hovered = IsPointerOnSpark();
            if (visual != null) visual.SetHover(hovered);

            if (!hovered) return;

            if (Input.GetMouseButtonDown(0))
            {
                if (visual != null) visual.Flare();
                if (menu != null) menu.StartGame();
            }
        }

        bool IsPointerOnSpark()
        {
            if (_camera == null) return false;
            Ray ray = _camera.ScreenPointToRay(Input.mousePosition);
            RaycastHit hit;
            if (!Physics.Raycast(ray, out hit, maxRayDistance, ~0, QueryTriggerInteraction.Ignore)) return false;
            if (hit.collider == null) return false;
            return hit.collider.GetComponentInParent<SparkClick>() == this;
        }
    }
}
