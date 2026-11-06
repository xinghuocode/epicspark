using System.Collections;
using UnityEngine;

namespace SparkCode.Gameplay
{
    /// <summary>Example interactable wired to the floating star in the gameplay scene.</summary>
    public class StarTempleSparkInteractable : MonoBehaviour, IStarTempleInteractable, IStarTempleDamageable
    {
        TempleShowcaseController showcase;
        Vector3 baseScale;
        Coroutine pulse;

        void Awake()
        {
            showcase = GetComponentInParent<TempleShowcaseController>();
            baseScale = transform.localScale;
        }

        public void Interact(StarTemplePlayerController source)
        {
            if (showcase != null)
            {
                showcase.starSpinSpeed = Mathf.Approximately(showcase.starSpinSpeed, 2f) ? 10f : 2f;
            }
            if (source != null) source.Notice("星火回应了交互：旋转变速");
            Pulse();
        }

        public void TakeDamage(float amount, StarTemplePlayerController source, RaycastHit hit)
        {
            if (source != null) source.Notice("星火吸收了 " + Mathf.RoundToInt(amount) + " 点攻击");
            Pulse();
        }

        void Pulse()
        {
            if (!gameObject.activeInHierarchy) return;
            if (pulse != null) StopCoroutine(pulse);
            pulse = StartCoroutine(PulseRoutine());
        }

        IEnumerator PulseRoutine()
        {
            float duration = 0.45f;
            for (float t = 0f; t < duration; t += Time.deltaTime)
            {
                float s = 1f + Mathf.Sin(t / duration * Mathf.PI) * 0.08f;
                transform.localScale = baseScale * s;
                yield return null;
            }
            transform.localScale = baseScale;
            pulse = null;
        }
    }
}
