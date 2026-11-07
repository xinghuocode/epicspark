// 运行时纯色材质工厂：本项目不使用任何美术资源，锚点光环、血条等全部代码生成。
using UnityEngine;

namespace EpicSpark.Tools
{
    /// <summary>用代码生成无光照纯色材质，兼容 URP 与内置管线。</summary>
    public static class RuntimeMaterialFactory
    {
        private static Shader _unlit;
        private static bool _searched;

        /// <summary>依次尝试 URP 无光照、内置无光照等着色器。</summary>
        public static Shader FindUnlitShader()
        {
            if (_searched) return _unlit;
            _searched = true;
            string[] names =
            {
                "Universal Render Pipeline/Unlit",
                "Unlit/Color",
                "Sprites/Default",
                "Unlit/Transparent",
                "Standard"
            };
            for (int i = 0; i < names.Length; i++)
            {
                Shader shader = Shader.Find(names[i]);
                if (shader != null)
                {
                    _unlit = shader;
                    break;
                }
            }
            return _unlit;
        }

        /// <summary>创建无光照纯色材质（颜色同时写入 _BaseColor / _Color / 自发光）。</summary>
        public static Material CreateUnlit(Color color, string name)
        {
            Shader shader = FindUnlitShader();
            if (shader == null) shader = Shader.Find("Standard");
            Material material = shader != null ? new Material(shader) : new Material(Shader.Find("Standard"));
            material.name = string.IsNullOrEmpty(name) ? "RuntimeUnlit" : name;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", color);
            }
            return material;
        }

        /// <summary>把物体上的 MeshRenderer 换成纯色材质，并关闭阴影。</summary>
        public static void Apply(GameObject target, Color color)
        {
            if (target == null) return;
            MeshRenderer renderer = target.GetComponent<MeshRenderer>();
            if (renderer == null) return;
            renderer.sharedMaterial = CreateUnlit(color, target.name + "_Mat");
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
    }
}
