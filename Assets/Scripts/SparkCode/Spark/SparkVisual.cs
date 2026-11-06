using UnityEngine;

namespace SparkCode.Spark
{
    /// <summary>
    /// Runtime motion / glow behaviour for the four-point star "Spark".
    /// Every reference below is assigned by the editor-side SparkBuilder, so the component
    /// needs no setup at runtime and the built scene already looks correct in a still frame.
    /// </summary>
    public class SparkVisual : MonoBehaviour
    {
        [Header("References (assigned by SparkBuilder)")]
        public Light CoreLight;
        public Transform VisualRoot;
        public Transform[] HaloTransforms = new Transform[0];
        public Renderer[] HaloRenderers = new Renderer[0];
        public Renderer[] CoreRenderers = new Renderer[0];
        public ParticleSystem MoteParticles;

        [Header("Idle motion")]
        public float baseLightIntensity = 8f;
        public float spinSpeed = 5.0f;          // degrees per second, in-plane roll
        public float wobbleDegrees = 9.0f;      // gentle out-of-plane wobble
        public float wobbleSpeed = 0.23f;       // Hz
        public float floatAmplitude = 0.16f;
        public float floatSpeed = 0.34f;        // Hz
        public float breathSpeed = 1.30f;       // Hz
        public float breathAmount = 0.12f;

        [Header("Flare")]
        public float flareDuration = 1.2f;
        public float flareEmissionBoost = 3.0f;
        public float flareScaleBoost = 0.38f;

        [Header("Hover feedback")]
        public float hoverStrength = 0.35f;
        public float hoverLerpSpeed = 5f;

        static readonly int EmissionIntensityId = Shader.PropertyToID("_EmissionIntensity");
        static readonly int IntensityId = Shader.PropertyToID("_Intensity");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        float _time;
        float _flare;
        float _hover;
        bool _hoverTarget;
        Vector3 _visualBasePos;
        Quaternion _visualBaseRot;
        Vector3 _visualBaseScale = Vector3.one;
        Vector3[] _haloBaseScale = new Vector3[0];
        float[] _haloBaseIntensity = new float[0];
        Color[] _haloBaseColor = new Color[0];
        float[] _coreBaseEmission = new float[0];
        MaterialPropertyBlock _mpb;

        /// <summary>1 = fully flared, 0 = idle. Useful for external audio / UI pulses.</summary>
        public float FlareAmount { get { return _flare; } }

        void Awake()
        {
            CacheBaseState();
        }

        void CacheBaseState()
        {
            if (VisualRoot != null)
            {
                _visualBasePos = VisualRoot.localPosition;
                _visualBaseRot = VisualRoot.localRotation;
                _visualBaseScale = VisualRoot.localScale;
            }

            int haloCount = HaloTransforms != null ? HaloTransforms.Length : 0;
            _haloBaseScale = new Vector3[haloCount];
            for (int i = 0; i < haloCount; i++)
                _haloBaseScale[i] = HaloTransforms[i] != null ? HaloTransforms[i].localScale : Vector3.one;

            _haloBaseIntensity = new float[HaloRenderers != null ? HaloRenderers.Length : 0];
            _haloBaseColor = new Color[_haloBaseIntensity.Length];
            for (int i = 0; i < _haloBaseIntensity.Length; i++)
            {
                Material m = HaloRenderers[i] != null ? HaloRenderers[i].sharedMaterial : null;
                _haloBaseIntensity[i] = m != null && m.HasProperty(IntensityId) ? m.GetFloat(IntensityId) : 1f;
                _haloBaseColor[i] = m != null && m.HasProperty(ColorId) ? m.GetColor(ColorId) : Color.white;
            }

            _coreBaseEmission = new float[CoreRenderers != null ? CoreRenderers.Length : 0];
            for (int i = 0; i < _coreBaseEmission.Length; i++)
            {
                Material m = CoreRenderers[i] != null ? CoreRenderers[i].sharedMaterial : null;
                _coreBaseEmission[i] = m != null && m.HasProperty(EmissionIntensityId) ? m.GetFloat(EmissionIntensityId) : 1f;
            }

            _mpb = new MaterialPropertyBlock();
        }

        void Update()
        {
            _time += Time.deltaTime;

            if (_flare > 0f)
                _flare = Mathf.Max(0f, _flare - Time.deltaTime / Mathf.Max(0.05f, flareDuration));

            _hover = Mathf.MoveTowards(_hover, _hoverTarget ? 1f : 0f, Time.deltaTime * hoverLerpSpeed);

            float flare = _flare * _flare;                                   // fast attack, smooth decay
            float breath = Mathf.Sin(_time * breathSpeed * Mathf.PI * 2f);    // -1..1
            float hover = _hover * hoverStrength;

            ApplyMotion(flare, breath);
            ApplyLight(flare, breath, hover);
            ApplyHalos(flare, breath, hover);
            ApplyCoreEmission(flare, breath, hover);
        }

        void ApplyMotion(float flare, float breath)
        {
            if (VisualRoot == null) return;

            float bob = Mathf.Sin(_time * floatSpeed * Mathf.PI * 2f) * floatAmplitude;
            VisualRoot.localPosition = _visualBasePos + Vector3.up * bob;

            float roll = Mathf.Repeat(_time * spinSpeed, 360f);
            float yaw = Mathf.Sin(_time * wobbleSpeed * Mathf.PI * 2f) * wobbleDegrees;
            float tilt = Mathf.Sin(_time * wobbleSpeed * Mathf.PI * 2f * 0.7f + 1.7f) * wobbleDegrees * 0.35f;
            VisualRoot.localRotation = _visualBaseRot * Quaternion.Euler(tilt, yaw, roll);

            float scale = 1f + flare * flareScaleBoost * 0.45f + _hover * 0.05f + breath * 0.012f;
            VisualRoot.localScale = _visualBaseScale * scale;
        }

        void ApplyLight(float flare, float breath, float hover)
        {
            if (CoreLight == null) return;
            float baseIntensity = baseLightIntensity > 0.01f ? baseLightIntensity : CoreLight.intensity;
            float k = 1f + breath * breathAmount + hover + flare * 2.1f;
            CoreLight.intensity = baseIntensity * k;
            CoreLight.range = Mathf.Lerp(CoreLight.range, 28f * (1f + flare * 0.35f), Time.deltaTime * 2f);
        }

        void ApplyHalos(float flare, float breath, float hover)
        {
            if (_mpb == null) _mpb = new MaterialPropertyBlock();

            float scaleK = 1f + breath * 0.035f + hover * 0.08f + flare * flareScaleBoost;
            int haloCount = HaloTransforms != null ? HaloTransforms.Length : 0;
            for (int i = 0; i < haloCount; i++)
            {
                Transform t = HaloTransforms[i];
                if (t == null) continue;
                Vector3 baseScale = i < _haloBaseScale.Length ? _haloBaseScale[i] : Vector3.one;
                t.localScale = baseScale * scaleK;
            }

            int rendererCount = HaloRenderers != null ? HaloRenderers.Length : 0;
            for (int i = 0; i < rendererCount; i++)
            {
                Renderer r = HaloRenderers[i];
                if (r == null) continue;
                float baseIntensity = i < _haloBaseIntensity.Length ? _haloBaseIntensity[i] : 1f;
                if (baseIntensity <= 0.0001f) continue;
                Color baseColor = i < _haloBaseColor.Length ? _haloBaseColor[i] : Color.white;
                Color flareColor = Color.Lerp(baseColor, new Color(1f, 0.94f, 0.78f, baseColor.a), flare * 0.7f);
                _mpb.Clear();
                _mpb.SetFloat(IntensityId, baseIntensity * (1f + breath * 0.16f + hover * 0.35f + flare * 2.4f));
                _mpb.SetColor(ColorId, flareColor);
                r.SetPropertyBlock(_mpb);
            }
        }

        void ApplyCoreEmission(float flare, float breath, float hover)
        {
            if (_mpb == null) _mpb = new MaterialPropertyBlock();

            int count = CoreRenderers != null ? CoreRenderers.Length : 0;
            for (int i = 0; i < count; i++)
            {
                Renderer r = CoreRenderers[i];
                if (r == null) continue;
                float baseEmission = i < _coreBaseEmission.Length ? _coreBaseEmission[i] : 1f;
                if (baseEmission <= 0.0001f) continue;
                _mpb.Clear();
                _mpb.SetFloat(EmissionIntensityId, baseEmission * (1f + breath * 0.10f + hover * 0.22f + flare * flareEmissionBoost));
                r.SetPropertyBlock(_mpb);
            }
        }

        /// <summary>Short, bright flare used when "开始游戏" is pressed (or the Spark is clicked).</summary>
        public void Flare()
        {
            _flare = 1f;
            if (MoteParticles != null && Application.isPlaying)
                MoteParticles.Emit(26);
        }

        /// <summary>Hover highlight (optional pointer feedback driven by SparkClick).</summary>
        public void SetHover(bool hovered)
        {
            _hoverTarget = hovered;
        }

        /// <summary>Restores the "beauty" idle state immediately (editor capture helper).</summary>
        public void ResetToIdle()
        {
            _flare = 0f;
            _hover = 0f;
            _hoverTarget = false;
            _time = 0f;
            if (VisualRoot != null)
            {
                VisualRoot.localPosition = _visualBasePos;
                VisualRoot.localRotation = _visualBaseRot;
                VisualRoot.localScale = _visualBaseScale;
            }
        }
    }
}
