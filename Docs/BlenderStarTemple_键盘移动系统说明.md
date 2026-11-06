# 星火神殿 · 第一人称键盘移动系统

## 打开游戏场景

在 Unity 2022.3.62f3c1 中打开项目 `D:\unityproject\epic`，然后打开：

`Assets/Scenes/BlenderStarTemple_Gameplay.unity`

该场景是从 URP 展示场景复制出的独立玩法场景，原 `BlenderStarTemple_URP.unity` 的展示相机与 1–4 切换仍保留，不会被覆盖。玩法场景在 Build Settings 中以禁用状态添加，需要发布时手动勾选。

## 默认键位

| 操作 | 默认键 |
|---|---|
| 前进 / 后退 / 左移 / 右移 | `W` / `S` / `A` / `D` |
| 跳跃 | `空格` |
| 蹲下 | `左 Shift` |
| 疾跑 | `Q` |
| 呼出 / 锁定鼠标 | `E` |
| 左键攻击 | `鼠标左键` |
| 右键交互 | `鼠标右键` |
| 打开键位设置 | `F1` |

移动方向相对于玩家当前视角；`Q` 疾跑为按住生效，`左 Shift` 蹲下优先于疾跑。

## 鼠标与交互

- 进入运行状态时鼠标默认锁定。
- 按 `E` 呼出鼠标，镜头转动暂停；再按 `E` 锁定。
- **呼出鼠标时不会禁用攻击和交互**：左键攻击、右键交互仍然有效，这是本系统的明确行为。
- 鼠标锁定状态与设置面板状态不会改变攻击/交互的输入逻辑。
- 左键攻击会从画面中心发出射线，显示短促金色射线，并对实现 `IStarTempleDamageable` 的目标造成伤害。
- 右键交互会寻找实现 `IStarTempleInteractable` 的目标。
- 神殿星火已挂载示例交互体：攻击会提示吸收伤害，右键交互会让星火在慢速/快速旋转间切换。

## 修改键位

1. 按 `F1` 打开键位设置（会自动释放鼠标）。
2. 点击要修改的按键按钮。
3. 按下新的键盘键或鼠标左/右键。
4. 相同按键会自动与已有动作交换；`Esc` 取消本次修改。
5. “恢复默认键位”可恢复全部默认设置。

所有修改保存在 `PlayerPrefs` 的 `StarTemple.Binding.*` 键中，重新进入场景或重启 Unity 后仍然有效。`F1` 固定保留为设置面板快捷键，不可绑定。

## 实现细节

- 玩家根对象：`StarTemple_Player`，使用 `CharacterController`。
- 玩家相机：`Player Camera`，URP HDR、实时阴影、FXAA、Bloom 后处理。
- 移动速度、疾跑速度、蹲下速度、跳跃高度、鼠标灵敏度、攻击/交互距离均可在 Inspector 中调整。
- 蹲下会降低胶囊高度与视角高度；头顶空间不足时不能站起。
- 攻击、交互都通过接口暴露，可直接给其他物体添加脚本接入：

```csharp
public class MyTarget : MonoBehaviour, IStarTempleDamageable, IStarTempleInteractable
{
    public void TakeDamage(float amount, StarTemplePlayerController source, RaycastHit hit) { }
    public void Interact(StarTemplePlayerController source) { }
}
```

接口定义及完整控制器位于：

`Assets/Scripts/SparkCode/Gameplay/StarTemplePlayerController.cs`

场景生成与验证代码位于：

`Assets/Editor/Generation/StarTempleGameplaySetup.cs`

## 重新生成与验证

Unity 菜单：

- `SparkCode → 制作第一人称键盘移动系统`：重新复制场景、创建玩家并截图。
- `SparkCode → 验证第一人称键盘移动系统`：验证玩家、碰撞体、相机、监听器、星火交互体与默认键位。

命令行：

```powershell
D:\unityanzhuang\2022.3.62f3c1\Editor\Unity.exe -batchmode -projectPath D:\unityproject\epic -executeMethod SparkCode.Generation.StarTempleGameplaySetup.BuildAndCapture -quit

D:\unityanzhuang\2022.3.62f3c1\Editor\Unity.exe -batchmode -projectPath D:\unityproject\epic -executeMethod SparkCode.Generation.StarTempleGameplaySetup.Validate -quit
```

验证报告：`Docs/BlenderStarTemple_GameplayValidation.json`。

第一人称画面：`Screenshots/BlenderStarTemple_Gameplay.png`。
