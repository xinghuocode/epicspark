#if ENABLE_INPUT_SYSTEM
using UnityEditor;
using UnityEngine;

namespace EpicSpark.MovementTest.EditorTools
{
    /// <summary>
    /// 把当前打开场景中的 Player 从 legacy 控制器切换到 Input System 控制器。
    /// 菜单:Tools → Movement Test → 切换到 Input System 控制器
    /// </summary>
    public static class SwitchToInputSystemMenu
    {
        [MenuItem("Tools/Movement Test/切换到 Input System 控制器")]
        public static void Switch()
        {
            var player = GameObject.Find("Player");
            if (player == null)
            {
                Debug.LogError("[MovementTest] 场景中没有名为 Player 的对象,请先构建移动测试场景");
                return;
            }

            var oldComp = player.GetComponent<SimplePlayerController>();
            if (oldComp != null)
            {
                // 把旧组件的关键设置迁移过去
                var newComp = player.AddComponent<InputSystemPlayerController>();
                newComp.moveSpeed = oldComp.moveSpeed;
                newComp.jumpHeight = oldComp.jumpHeight;
                newComp.gravity = oldComp.gravity;
                newComp.stickDeadZone = oldComp.stickDeadZone;
                newComp.sprintMultiplier = oldComp.sprintMultiplier;
                newComp.swapStickXY = oldComp.swapStickXY;
                newComp.invertX = oldComp.invertX;
                newComp.invertY = oldComp.invertY;
                newComp.showDebugOverlay = oldComp.showDebugOverlay;
                newComp.uiScaleMultiplier = oldComp.uiScaleMultiplier;

                Object.DestroyImmediate(oldComp);
                EditorUtility.SetDirty(player);
                Debug.Log("[MovementTest] 已切换到 Input System 控制器(旧组件已移除,设置已迁移)");
            }
            else if (player.GetComponent<InputSystemPlayerController>() != null)
            {
                Debug.Log("[MovementTest] Player 已在用 Input System 控制器");
            }
            else
            {
                player.AddComponent<InputSystemPlayerController>();
                Debug.Log("[MovementTest] 已添加 Input System 控制器");
            }

            Selection.activeGameObject = player;
        }
    }
}
#endif
