// 传送锚点路由：AnchorPoint 只负责记录与时间跳转，剧情推进由上层订阅 AnchorEntered 决定。
using System;
using System.Collections.Generic;
using UnityEngine;
using EpicSpark.Core;

namespace EpicSpark.Anchors
{
    /// <summary>锚点进入事件的唯一出口，Lead 的 GameDirector 订阅即可推进剧情。</summary>
    public class AnchorRouter : MonoBehaviour
    {
        public static AnchorRouter Instance { get; private set; }

        /// <summary>玩家进入某个锚点。参数为锚点描述（剧情&核心 Agent 生成的原始数据）。</summary>
        public event Action<SceneAnchorSpec> AnchorEntered;

        [Tooltip("同一锚点的重复触发冷却（秒）")]
        public float duplicateGuardSeconds = 1f;

        [Tooltip("当前场景 id，用于日志；为空时回退 RuntimeContext")]
        public string sceneId = "";

        private readonly Dictionary<string, float> _lastEnter = new Dictionary<string, float>();

        public bool HasSubscribers { get { return AnchorEntered != null; } }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.Log("[EpicSpark] 已存在 AnchorRouter，销毁重复实例。");
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>场景内没有 AnchorRouter 时创建一个。</summary>
        public static AnchorRouter EnsureInstance()
        {
            if (Instance != null) return Instance;
            GameObject go = new GameObject("AnchorRouter");
            return go.AddComponent<AnchorRouter>();
        }

        /// <summary>请求进入锚点。同一锚点在冷却期内重复请求会被忽略。</summary>
        public bool RequestEnter(SceneAnchorSpec spec)
        {
            if (spec == null) return false;

            string key = string.IsNullOrEmpty(spec.anchorId) ? (spec.displayName ?? "") : spec.anchorId;
            float last;
            if (_lastEnter.TryGetValue(key, out last) && Time.unscaledTime - last < duplicateGuardSeconds)
                return false;
            _lastEnter[key] = Time.unscaledTime;

            string label = string.IsNullOrEmpty(spec.displayName) ? spec.anchorId : spec.displayName;
            GameEvents.RaiseNotice("传送锚点：进入「" + label + "」");

            Action<SceneAnchorSpec> handler = AnchorEntered;
            if (handler != null)
            {
                handler(spec);
                return true;
            }

            Debug.LogWarning("[EpicSpark] AnchorEntered 没有订阅者（Lead 的 GameDirector 未接线），锚点「" + label + "」的剧情推进被跳过。");
            return false;
        }
    }
}
