using UnityEngine;
using UnityEngine.InputSystem;

namespace EpicSpark.AI.SceneAgentTest
{
    /// <summary>
    /// 场景体验控制器: 第一人称在生成的3D场景中行走。
    /// WASD/方向键移动, 鼠标视角, Shift 加速, 空格跳跃, Esc 退出体验(恢复相机与光标)。
    /// 由 SceneAgentRunner.EnterExperience 创建并 Begin; 退出时自动销毁。
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class SceneExperienceController : MonoBehaviour
    {
        public float moveSpeed = 5f;
        public float sprintMultiplier = 2f;
        public float lookSensitivity = 0.12f;
        public float gravity = -18f;
        public float jumpHeight = 1.1f;
        public float eyeHeight = 1.6f;

        CharacterController cc;
        Camera cam;
        Transform camTransform;
        Vector3 camOriginalLocalPos;
        Quaternion camOriginalLocalRot;
        Transform camOriginalParent;
        float yaw, pitch, verticalVelocity;
        bool active;

        /// <summary>进入体验: 接管主相机为子物体并锁定光标。</summary>
        public void Begin(Camera camera)
        {
            cc = GetComponent<CharacterController>();
            cam = camera;
            if (cam == null) { Destroy(gameObject); return; }

            camOriginalParent = cam.transform.parent;
            camOriginalLocalPos = cam.transform.localPosition;
            camOriginalLocalRot = cam.transform.localRotation;
            cam.transform.SetParent(transform, false);
            camTransform = cam.transform;
            camTransform.localPosition = new Vector3(0, eyeHeight, 0);
            camTransform.localRotation = Quaternion.identity;

            yaw = transform.eulerAngles.y;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            active = true;
        }

        void Update()
        {
            if (!active) return;
            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb.escapeKey.wasPressedThisFrame) { End(); return; }

            // ---- 鼠标视角 ----
            var mouse = Mouse.current;
            if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
            {
                Vector2 md = mouse.delta.ReadValue();
                yaw += md.x * lookSensitivity;
                pitch = Mathf.Clamp(pitch - md.y * lookSensitivity, -85f, 85f);
                transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                camTransform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
            }

            // ---- WASD 移动 ----
            Vector2 dir = Vector2.zero;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) dir.y += 1;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) dir.y -= 1;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) dir.x += 1;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) dir.x -= 1;
            if (dir.sqrMagnitude > 1f) dir.Normalize();

            float speed = moveSpeed * (kb.leftShiftKey.isPressed ? sprintMultiplier : 1f);
            Vector3 move = (transform.forward * dir.y + transform.right * dir.x) * speed;

            // ---- 重力 / 跳跃 ----
            if (cc.isGrounded)
            {
                verticalVelocity = -2f;
                if (kb.spaceKey.wasPressedThisFrame)
                    verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }
            else
            {
                verticalVelocity += gravity * Time.deltaTime;
            }
            move.y = verticalVelocity;
            cc.Move(move * Time.deltaTime);

            // 掉出地形兜底: 抬回当前位置上方
            if (transform.position.y < -50f)
            {
                cc.enabled = false;
                transform.position += Vector3.up * 30f;
                cc.enabled = true;
                verticalVelocity = 0f;
            }
        }

        /// <summary>退出体验: 归还相机、解锁光标、销毁自身。</summary>
        public void End()
        {
            if (!active) return;
            active = false;
            if (cam != null)
            {
                cam.transform.SetParent(camOriginalParent, false);
                cam.transform.localPosition = camOriginalLocalPos;
                cam.transform.localRotation = camOriginalLocalRot;
            }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (active) End();
        }
    }
}
