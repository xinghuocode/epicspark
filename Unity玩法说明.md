# 星火神殿 · Unity 第一人称玩法说明

本说明对应 Unity 场景：

`Assets/Scenes/BlenderStarTemple_Gameplay.unity`

打开项目后加载该场景，按 **Play** 即可试玩。项目仅保留此场景作为可进入场景，旧开始、展示和移动测试场景已移除。

## 操作

| 输入 | 功能 |
| --- | --- |
| `W A S D` | 前后左右移动 |
| `Space` | 跳跃 |
| `Left Shift` | 下蹲 |
| `Q` | 冲刺 |
| `E` | 显示 / 隐藏鼠标指针 |
| `F1` | 打开按键重绑定面板 |
| 鼠标移动 | 未显示指针时转动视角 |
| 鼠标左键 / 右键 | 攻击 / 互动；准星指向亮点时读取记忆 |
| 鼠标左键或右键 | 点击世界空间 UI 按钮 |

所有按键均可在 `F1` 面板中重新绑定。鼠标显示时仍可攻击、互动和点击 UI；指针落在世界空间面板或按钮上时，角色攻击/互动会暂时让位给 UI，避免一次点击触发两个动作。

## 手机端（Android）

- 固定为 **左侧横屏（Landscape Left）**，构建使用 **IL2CPP + ARM64**。
- **陀螺仪控制视角**：使用无偏旋转速率，横屏默认交换设备 X/Y 轴，支持轴交换、灵敏度和偏航/俯仰反向校准；不占用右摇杆。
- 常规手柄：左摇杆移动，`A` 跳跃/确认，`X` 或右扳机攻击，`Y` 或左扳机交互，左肩键疾跑，`B` 或右肩键蹲下。
- 菜单：使用陀螺仪将视角中心指向按钮，`A` 确认，`B / Start` 返回；不使用摇杆循环切换选项。面板打开期间仍可移动、疾跑和调整视角。退出游戏对话框直接使用 `A = 是`、`B = 否`。
- `SC-B03`：单摇杆移动（自动应用轴交换与反向修正），`A` 跳跃/确认，`X` 攻击，`B/Y` 交互，确定键疾跑；其固件本身的按键锁存限制仍需以实体设备表现为准。
- 手机端不再显示电脑键位重绑定提示；电脑端继续使用原有键盘鼠标操作。

### 手机 VR 模式

- 屏幕右上角提供 **VR 模式 / 退出 VR** 按钮。
- 使用 **Google Cardboard XR Plugin v1.30.1** 负责双眼立体渲染、Viewer Profile、单眼投影、镜片畸变和头部追踪，不再叠加手写的固定瞳距分屏与陀螺仪逻辑。
- 首次进入可扫描 VR 盒子 QR 码，或载入通用 Cardboard 参数。Viewer Profile 决定镜片和视口校准，不再用固定 63 mm 代替完整光学配置。
- 配置后需将手机平放在水平桌面、屏幕朝上，再点击“已平放，开始校准”。系统启动官方头部追踪，以平放姿态作为正下方参考视角，并提供约 1.2 秒静止校准过程。
- 普通模式使用原中间主相机；VR 模式由 Cardboard 负责左右眼输出，退出后恢复普通视角、FOV、后处理和渲染设置。
- VR 中暂时关闭相机后处理，渲染比例降为 0.8，并以 60 FPS 为目标，优先降低重影、延迟和眩晕风险。

退出确认框打开期间会强制显示鼠标，并冻结角色的移动、视角、跳跃、下蹲、冲刺、`F1`、指针切换以及攻击/互动；只有点击 `是` 或 `否` 才会解除。

## 一、悬浮亮点与记忆卡片

神殿中央金色星火周围有 **5 个大型悬浮亮点**。将准星（或可见鼠标指针）指向其中任意一个，亮点旁会出现浮空记忆卡片，显示标题、正文和图片。

当玩家水平距离星火中心小于 **8.0 m** 时，整组星火（金色星火、5 个悬浮亮点以及瞄准后出现的记忆卡片）会平滑下降 **1.25 m**；离开到 **10.0 m** 以外后平滑升回原高度。下降速度为 **1.2 m/s**，带有滞回区间，避免在边界反复抖动。

星火原有的缓慢上下浮动和自转仍然保留，下降高度会叠加在浮动高度之上，因此不会互相覆盖。

### 光点聚焦阅读

1. 将准星（或可见鼠标指针）指向任意一个悬浮光点，按 **鼠标右键** 与该光点交互。
2. 星火和全部悬浮光点向上平滑升起 **1.6 m**；被选中光点的记忆卡片移动到星火下方圆形岛的正上方，卡片中心与玩家当前视线高度平齐。
3. 聚焦期间仍可移动和转动视角，卡片高度会跟随当前视线高度。
4. 将鼠标指针移到卡片上，或用准星对准卡片，再按 **鼠标右键**：卡片沿路径回到对应光点旁，星火和光点平滑降回交互前的高度。
5. 上升和回落过渡时间约 **0.55 s**；聚焦阅读模式优先于普通瞄准卡片，其他光点的卡片不会同时弹出。

默认亮点：

| pointId | 默认标题 |
| --- | --- |
| `spark_memory_01` | 星火之核 |
| `spark_memory_02` | 第一束光 |
| `spark_memory_03` | 远航者的誓言 |
| `spark_memory_04` | 时间的回声 |
| `spark_memory_05` | 未竟的史诗 |

### 运行时 API

亮点对象上挂有 `SparkCode.Gameplay.StarTempleLorePoint`：

```csharp
using SparkCode.Gameplay;
using UnityEngine;

public class LoreExample : MonoBehaviour
{
    public StarTempleLorePoint target;
    public Texture2D texture;
    public string imagePath;

    void Start()
    {
        // 写入标题和正文；立即持久化到 lore.json。
        target.SetContent("新的标题", "新的正文内容。");

        // 从硬盘图片复制到持久化目录（支持 PNG / JPG）。
        target.SetImageFromFile(imagePath);

        // 也可以直接传入可读 Texture2D（需要 Read/Write Enabled）。
        // target.SetImage(texture);

        // 重新读取文件；运行中修改文件后可调用。
        target.ReloadFromFile();
    }
}
```

可用成员：

- `SetContent(string title, string body)`：写入文字并保存。
- `SetImage(Texture2D image)`：编码为 PNG 保存；传 `null` 清除自定义图片。
- `SetImageFromFile(string sourcePath)`：把外部图片复制进持久化目录。
- `SaveToFile()`：保存当前 `Title` / `Body`。
- `ReloadFromFile()`：重新读取文字和图片。
- `Title`、`Body`、`CurrentImage`、`PersistentImagePath`：当前内容。
- `pointId`、`defaultTitle`、`defaultBody`、`defaultImage`：可在 Inspector 配置的默认值。

### 持久化文件

`StarTempleLoreStore.DataDirectory` 指向：

```text
Application.persistentDataPath/StarTempleLore/
├─ lore.json
└─ images/
```

Windows 编辑器下通常为：

```text
C:\Users\<用户名>\AppData\LocalLow\DefaultCompany\epic\StarTempleLore\
```

菜单 **SparkCode → 打开星火亮点持久化档案目录** 可直接打开该文件夹。

`lore.json` 结构示例：

```json
{
  "version": 1,
  "note": "Edit this file and add images to the images folder to customize the floating lore cards.",
  "entries": [
    {
      "id": "spark_memory_01",
      "title": "星火之核",
      "body": "正文内容。",
      "imageFile": "spark_memory_01_20261106200000000.png"
    }
  ]
}
```

可以直接编辑 `lore.json`、把图片放入 `images/`，然后重新进入 Play 或调用 `ReloadFromFile()`。`imageFile` 为空时使用 `defaultImage`。

默认卡片图片：`Assets/Generated/BlenderStarTempleURP/Lore_Default.png`。

## 二、桥头史诗对话框

出生点前、短桥靠地面的一端有世界空间对话框。玩家进入 **6.5 m** 范围时自动打开，离开 **8.0 m** 后关闭。

层级：

1. **第一级**
   - 左上角：`返回上一级`
   - 标题：`星火史诗`
   - 两个选项：`进入史诗...`、`设置`
2. **进入史诗...**
   - 上半部左右两栏：
     - 左：`创建新史诗`
     - 右：`加载已有史诗`
   - 两栏下方内容为空。
3. **设置**
   - 标题：`设置`
   - 内容为空。

行为：

- 左上角 `返回上一级` 在二级页返回第一级；在第一级点击会关闭对话框。
- 第一级关闭后，需要先离开 8.0 m 再重新进入，才会再次自动打开。
- 所有按钮都同时接受 **鼠标左键和右键**。
- 面板采用**固定朝向**：可读正面始终朝向神殿后方的白色发光长方体，不会随玩家相机转动；玩家可以自由移动和转动视角，从其他角度看面板仍保持同一方向。

## 三、白色光柱与退出确认

在对话框后方、靠近神殿外墙的位置有一根 **竖直、薄、纯白色发光长方体**（`Temple_Exit_Obelisk`）。

- 只有玩家几乎贴住长方体（水平距离 **0.9 m** 以内）时，屏幕正中央才会弹出 `退出程序？` 确认框，同时画面轻微压暗、鼠标强制可见。
- 确认框未关闭时，角色的移动、视角、跳跃、下蹲、冲刺、`F1`、指针切换和攻击/互动全部冻结；世界空间 UI 也不响应，只有确认框内的按钮可以点击。
- `是`：退出程序。在 Unity 编辑器中会停止 Play Mode。
- `否`：关闭确认框、解除冻结并恢复打开前的鼠标状态；在玩家退到 **1.6 m** 以外并再次贴近之前不会重复弹出。
- 两个按钮同样支持鼠标左键和右键。

## 四、场景位置速查

| 对象 | 世界坐标 / 范围 |
| --- | --- |
| 玩家出生点 | `(0, 0.25, 10.2)`，朝向星火 |
| 星火中心 | `(0, 5.75, 0)` |
| 桥头对话框锚点 | `(-1.6, 1.55, 6.0)` |
| 白色光柱 / 退出锚点 | `(-1.6, 0.15, 13.9)` |
| 亮点卡片判定距离 | 25 m 内射线命中 |
| 星火下降 | 8.0 m 内下降 1.25 m，10.0 m 外升回 |
| 桥头对话框 | 6.5 m 打开，8.0 m 关闭 |
| 退出确认框 | 0.9 m 内（几乎贴住）在屏幕正中打开，1.6 m 重置 |

## 五、主要脚本

| 文件 | 职责 |
| --- | --- |
| `Assets/Scripts/SparkCode/Gameplay/StarTempleLorePoint.cs` | 单个亮点的内容、图片和持久化 API |
| `Assets/Scripts/SparkCode/Gameplay/StarTempleLoreData.cs` | `lore.json`、图片目录、增删改查与加载 |
| `Assets/Scripts/SparkCode/Gameplay/StarTempleWorldUiController.cs` | 亮点注视、光点聚焦阅读、星火随距离升降、桥头页面、屏幕中央退出确认框、左右键按钮命中 |
| `Assets/Scripts/SparkCode/Gameplay/StarTempleWorldUiButton.cs` | 世界空间按钮的动作、悬停与标签 |
| `Assets/Scripts/SparkCode/Gameplay/StarTemplePlayerController.cs` | 第一人称移动、跳跃、下蹲、冲刺、指针、重绑定、攻击/互动抑制与确认框冻结 |
| `Assets/Scripts/SparkCode/Gameplay/StarTempleSparkInteractable.cs` | 星火本体的互动与伤害脉冲 |
| `Assets/Editor/Generation/StarTempleStoryUiSetup.cs` | 生成世界空间 UI、亮点、白色光柱 |
| `Assets/Editor/Generation/StarTempleGameplaySetup.cs` | 构建玩法场景、校验并输出截图 |
| `Assets/Scripts/SparkCode/TempleShowcaseController.cs` | 星火自转/上下浮动，并提供高度偏移接口供靠近下降叠加 |

UI 使用项目内字体资源：

`Assets/Generated/BlenderStarTempleURP/Fonts/NotoSansSC-VF.ttf`

因此不依赖玩家系统是否安装微软雅黑等中文字体。

## 六、重建与截图

编辑器菜单：

- **SparkCode → 制作第一人称键盘移动系统**：重建玩法场景并输出截图。
- **SparkCode → 验证第一人称键盘移动系统**：只做场景与数据校验。

批处理命令：

```powershell
& "D:\unityanzhuang\2022.3.62f3c1\Editor\Unity.exe" `
  -batchmode -projectPath "D:\unityproject\epic" `
  -executeMethod SparkCode.Generation.StarTempleGameplaySetup.BuildAndCapture `
  -quit -logFile "D:\unityproject\epic\Logs\BlenderStarTemple_StoryUi.log"
```

截图输出：

- `Screenshots/BlenderStarTemple_Gameplay.png`
- `Screenshots/BlenderStarTemple_StoryDialogue.png`
- `Screenshots/BlenderStarTemple_StoryLore.png`
- `Screenshots/BlenderStarTemple_LoreFocus.png`
- `Screenshots/BlenderStarTemple_StarLowered.png`
- `Screenshots/BlenderStarTemple_ExitPrompt.png`

最近一次构建校验结果：`passed:true`，5 个亮点、5 个按钮、5 条持久化词条、白色光柱与重绑定默认值全部通过。新增光点聚焦自检 8 项全部通过，覆盖：星火与全部光点上升、卡片位于岛中心上方且与视线同高、结束后星火回落、卡片返回并隐藏。此前星火升降与退出距离回归自检 9 项同样通过。
