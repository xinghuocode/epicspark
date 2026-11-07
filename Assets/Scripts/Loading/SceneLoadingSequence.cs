// 新场景加载流程（设计文档第六节）四步曲：
// 1) 屏幕渐黑保持，视角切到新场景出生点；2) 灰盒阶段：灰色线条显示区域边界；
// 3) 搭建阶段：蓝绿色线条显示每个美术资产的棱，前后遮挡正确；4) 贴图以张为单位由近及远显现，约 1 秒后进入游玩。
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using EpicSpark.Core;

namespace EpicSpark.Loading
{
    public static class SceneLoadingSequence
    {
        public const float DefaultGrayboxSeconds = 1.1f;
        public const float DefaultWireSeconds = 1.5f;
        public const float DefaultRevealSeconds = 1.0f;

        public static async Task PlayAsync(
            SceneInstance instance,
            Vector3 viewerPosition,
            CancellationToken ct,
            float grayboxSeconds = DefaultGrayboxSeconds,
            float wireSeconds = DefaultWireSeconds,
            float revealSeconds = DefaultRevealSeconds)
        {
            if (instance == null) return;

            // 阶段 1：黑屏保持（由 HUD 的黑幕负责），此时场景尚未呈现
            GameEvents.RaisePhase(ScenePhase.Graybox);
            SetModelsVisible(instance, false);
            SetActive(instance.wireRoot, false);
            SetActive(instance.grayboxRoot, true);
            CaptureGraybox(instance);   // 调试：灰盒规划结果截图（顶视）
            await Delay(grayboxSeconds, ct);

            // 阶段 2：搭建阶段 —— 灰线退场，蓝绿棱线登场
            GameEvents.RaisePhase(ScenePhase.Build);
            SetActive(instance.grayboxRoot, false);
            SetActive(instance.wireRoot, true);
            await Delay(wireSeconds, ct);

            // 阶段 3：贴图由近及远逐张显现（棱线保留到第一张贴图出现再撤）
            GameEvents.RaisePhase(ScenePhase.Reveal);
            await SceneAssembler.RevealTextureAsync(instance, viewerPosition, revealSeconds, ct);
            SetActive(instance.wireRoot, false);
            SetModelsVisible(instance, true);

            // 调试：拼装结果截图（顶视 + 玩家视角）
            CaptureAssembled(instance);

            // 阶段 4：真正进入场景
            GameEvents.RaisePhase(ScenePhase.Playing);
        }

        private static void CaptureGraybox(SceneInstance instance)
        {
            string dir = EpicSpark.Debugging.SceneDebugRecorder.CurrentDir;
            if (string.IsNullOrEmpty(dir) || instance == null) return;
            float size = Mathf.Max(instance.width, instance.length) * 0.62f;
            EpicSpark.Debugging.SceneDebugRecorder.CaptureTopDown(dir, "07_灰盒规划_顶视.png",
                instance.grayboxRoot, Vector3.zero, size);
        }

        private static void CaptureAssembled(SceneInstance instance)
        {
            string dir = EpicSpark.Debugging.SceneDebugRecorder.CurrentDir;
            if (string.IsNullOrEmpty(dir) || instance == null) return;
            float size = Mathf.Max(instance.width, instance.length) * 0.62f;
            EpicSpark.Debugging.SceneDebugRecorder.CaptureTopDown(dir, "09_拼装_顶视.png",
                instance.root, Vector3.zero, size);
            EpicSpark.Debugging.SceneDebugRecorder.CaptureView(dir, "10_拼装_玩家视角.png", Camera.main);
        }

        /// <summary>场景复用时的快速呈现：不做四段动画，直接点亮。</summary>
        public static void RevealImmediate(SceneInstance instance)
        {
            if (instance == null) return;
            SetActive(instance.grayboxRoot, false);
            SetActive(instance.wireRoot, false);
            SetModelsVisible(instance, true);
            if (instance.models != null)
            {
                for (int i = 0; i < instance.models.Count; i++)
                {
                    ModelView view = instance.models[i];
                    if (view == null || view.revealed) continue;
                    view.revealed = true;
                    if (view.renderer != null && view.model != null && view.model.material != null)
                        view.renderer.sharedMaterial = view.model.material;
                }
            }
            GameEvents.RaisePhase(ScenePhase.Playing);
        }

        private static void SetModelsVisible(SceneInstance instance, bool visible)
        {
            if (instance == null || instance.models == null) return;
            for (int i = 0; i < instance.models.Count; i++)
            {
                ModelView view = instance.models[i];
                if (view == null || view.renderer == null) continue;
                view.renderer.enabled = visible;
            }
        }

        private static void SetActive(GameObject go, bool active)
        {
            if (go != null) go.SetActive(active);
        }

        private static async Task Delay(float seconds, CancellationToken ct)
        {
            if (seconds <= 0f) return;
            int ms = Mathf.RoundToInt(seconds * 1000f);
            try { await Task.Delay(ms, ct); }
            catch (TaskCanceledException) { }
        }
    }
}
