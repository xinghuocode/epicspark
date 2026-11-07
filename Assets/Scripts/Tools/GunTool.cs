// 枪械工具：弹匣容量、射速、从相机中心发射射线、命中 IDamageable 造成伤害。
// 设计文档：玩家的每一次开枪都必须记录行为日志，供剧情&核心 Agent 读取。
using System;
using UnityEngine;
using EpicSpark.Core;
using EpicSpark.Npc.Runtime;

namespace EpicSpark.Tools
{
    /// <summary>枪：左键开火，命中 NPC 扣血、命中环境记「环境破坏」日志。</summary>
    public class GunTool : HeldTool
    {
        [Header("弹药与射速")]
        public float magazineSize = 12f;
        public float ammoInMagazine = 12f;
        public float reserveAmmo = 60f;
        public float fireRate = 5f;
        public float damage = 25f;
        public float range = 120f;
        public float reloadSeconds = 1.4f;

        [Header("音效占位（AudioSource 可空）")]
        public AudioSource audioSource;
        public AudioClip fireClip;
        public bool generateProceduralShotSound = true;

        private float _nextFireTime;
        private float _reloadDoneTime;

        public override ToolKind Kind { get { return ToolKind.Gun; } }
        public override string DisplayName { get { return "枪"; } }

        /// <summary>当前弹匣余弹（对应 ToolInventory.Ammo）。</summary>
        public float Ammo { get { return ammoInMagazine; } }
        public float Capacity { get { return magazineSize; } }
        public float Reserve { get { return reserveAmmo; } }
        public bool IsReloading { get { return Time.time < _reloadDoneTime; } }

        private void Awake()
        {
            if (magazineSize < 1f) magazineSize = 1f;
            if (ammoInMagazine > magazineSize) ammoInMagazine = magazineSize;
            if (ammoInMagazine < 0f) ammoInMagazine = 0f;
            if (reserveAmmo < 0f) reserveAmmo = 0f;

            if (audioSource == null) audioSource = GetComponent<AudioSource>();
            if (audioSource == null && generateProceduralShotSound) audioSource = gameObject.AddComponent<AudioSource>();
            if (audioSource != null)
            {
                audioSource.playOnAwake = false;
                audioSource.spatialBlend = 0f;
                audioSource.volume = 0.25f;
            }
            if (fireClip == null && generateProceduralShotSound) fireClip = BuildShotClip();
        }

        public override void Use(Transform viewTransform)
        {
            TryFire(viewTransform);
        }

        /// <summary>开火。返回是否真的打出了一发（受射速与弹药限制）。</summary>
        public bool TryFire(Transform viewTransform)
        {
            if (viewTransform == null) return false;
            if (Time.time < _nextFireTime) return false;
            if (IsReloading) return false;

            if (ammoInMagazine < 1f)
            {
                LogAction("fire", "", "扣动扳机但弹匣已空（0/" + magazineSize.ToString("0") + "）", viewTransform.position);
                GameEvents.RaiseNotice("弹匣已空，正在换弹");
                Reload();
                return false;
            }

            _nextFireTime = Time.time + 1f / Mathf.Max(0.1f, fireRate);
            ammoInMagazine -= 1f;
            PlayShotSound();
            return ResolveShot(viewTransform);
        }

        /// <summary>换弹（从备弹补满弹匣）。</summary>
        public void Reload()
        {
            if (IsReloading) return;
            if (ammoInMagazine >= magazineSize) return;
            if (reserveAmmo <= 0f) return;
            _reloadDoneTime = Time.time + Mathf.Max(0f, reloadSeconds);
            float missing = magazineSize - ammoInMagazine;
            float take = Mathf.Min(missing, reserveAmmo);
            ammoInMagazine += take;
            reserveAmmo -= take;
        }

        private bool ResolveShot(Transform viewTransform)
        {
            string ammoText = ammoInMagazine.ToString("0") + "/" + magazineSize.ToString("0");
            Ray ray = new Ray(viewTransform.position, viewTransform.forward);
            RaycastHit hit;
            if (!Physics.Raycast(ray, out hit, range, ~0, QueryTriggerInteraction.Ignore))
            {
                LogAction("fire", "", "开火，未命中任何目标（剩余弹药 " + ammoText + "）", viewTransform.position);
                return true;
            }

            Transform targetRoot = hit.collider != null ? hit.collider.transform : null;
            if (targetRoot != null && Owner != null && (targetRoot == Owner.transform || targetRoot.IsChildOf(Owner.transform)))
            {
                LogAction("fire", "", "开火，误触自身（剩余弹药 " + ammoText + "）", viewTransform.position);
                return true;
            }

            IDamageable damageable = targetRoot != null ? targetRoot.GetComponentInParent<IDamageable>() : null;
            if (damageable == null && hit.collider != null) damageable = hit.collider.GetComponentInParent<IDamageable>();

            if (damageable != null)
            {
                string targetId = ResolveTargetId(damageable, targetRoot);
                string sourceId = Owner != null && !string.IsNullOrEmpty(Owner.ActorId) ? Owner.ActorId : "player";
                damageable.ApplyDamage(damage, sourceId);
                LogAction("fire", targetId,
                    "开枪命中「" + targetId + "」，造成 " + damage.ToString("0.#") + " 点伤害（剩余弹药 " + ammoText + "）",
                    viewTransform.position);
                return true;
            }

            string envName = hit.collider != null ? hit.collider.name : "环境物体";
            LogAction("fire", envName,
                "开枪命中环境物体「" + envName + "」，造成环境破坏（剩余弹药 " + ammoText + "）",
                viewTransform.position);
            ApplyEnvironmentImpact(targetRoot, hit.point, ray.direction);
            return true;
        }

        private static string ResolveTargetId(IDamageable damageable, Transform root)
        {
            NpcHealth npc = damageable as NpcHealth;
            if (npc != null && !string.IsNullOrEmpty(npc.NpcId)) return npc.NpcId;
            if (root != null) return root.name;
            return "target";
        }

        private void ApplyEnvironmentImpact(Transform targetRoot, Vector3 point, Vector3 direction)
        {
            if (targetRoot == null) return;
            Rigidbody body = targetRoot.GetComponentInParent<Rigidbody>();
            if (body == null || body.isKinematic) return;
            body.AddForceAtPosition(direction.normalized * damage, point, ForceMode.Impulse);
        }

        private void PlayShotSound()
        {
            if (audioSource == null || fireClip == null) return;
            audioSource.PlayOneShot(fireClip, 1f);
        }

        /// <summary>无美术/音频资源时的程序化枪声占位。</summary>
        private static AudioClip BuildShotClip()
        {
            const int sampleRate = 44100;
            int length = Mathf.Max(64, (int)(sampleRate * 0.16f));
            float[] data = new float[length];
            System.Random random = new System.Random(20260101);
            for (int i = 0; i < length; i++)
            {
                float t = i / (float)length;
                float envelope = Mathf.Pow(1f - t, 2.5f);
                float noise = (float)(random.NextDouble() * 2.0 - 1.0);
                data[i] = noise * envelope * 0.8f;
            }
            AudioClip clip = AudioClip.Create("GunShot", length, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
