// 场景复用（设计文档第五节）：
// 当剧情&核心 Agent 决定下一个场景是之前出现过的场景时，不重走「描写场景→灰盒→搭建」，
// 而是在既有场景基础上生成新的游戏流程，因为拼好的场景是现成的。
using System.Collections.Generic;
using UnityEngine;

namespace EpicSpark.Loading
{
    public class SceneCache
    {
        private readonly Dictionary<string, SceneInstance> _scenes = new Dictionary<string, SceneInstance>();
        private Transform _parent;

        public SceneCache(Transform parent)
        {
            _parent = parent;
        }

        public int Count { get { return _scenes.Count; } }

        public bool Contains(string sceneId)
        {
            return !string.IsNullOrEmpty(sceneId) && _scenes.ContainsKey(sceneId);
        }

        public SceneInstance Get(string sceneId)
        {
            SceneInstance instance;
            if (!string.IsNullOrEmpty(sceneId) && _scenes.TryGetValue(sceneId, out instance)) return instance;
            return null;
        }

        public void Store(SceneInstance instance)
        {
            if (instance == null || string.IsNullOrEmpty(instance.sceneId)) return;
            instance.root.SetActive(false);
            _scenes[instance.sceneId] = instance;
        }

        /// <summary>显式丢弃某个场景（剧情推进到不可能再回去时可以让上层调用）。</summary>
        public void Forget(string sceneId)
        {
            SceneInstance instance = Get(sceneId);
            if (instance == null) return;
            SceneAssembler.DestroyInstance(instance);
            _scenes.Remove(sceneId);
        }

        public void Activate(SceneInstance instance)
        {
            if (instance == null || instance.root == null) return;
            instance.root.SetActive(true);
            if (_parent != null) instance.root.transform.SetParent(_parent, false);
        }

        public void DeactivateAll()
        {
            foreach (KeyValuePair<string, SceneInstance> pair in _scenes)
            {
                if (pair.Value != null && pair.Value.root != null) pair.Value.root.SetActive(false);
            }
        }

        public void Clear()
        {
            foreach (KeyValuePair<string, SceneInstance> pair in _scenes)
                SceneAssembler.DestroyInstance(pair.Value);
            _scenes.Clear();
        }

        public List<string> KnownSceneIds()
        {
            return new List<string>(_scenes.Keys);
        }
    }
}
