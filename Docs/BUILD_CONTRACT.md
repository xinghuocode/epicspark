# SparkCode 神殿开始场景 — 构建契约（v1）

本文件是本次多 Agent 并行开发的唯一接口契约。任何人只改自己名下的文件；跨模块通过下面的类型/方法签名衔接。
Unity 版本：**2022.3.62f3c1**，URP **14.0.12**，项目根目录 `D:\unityproject\epic`。
当前 shell 以高权限运行；Unity CLI：`D:\unityanzhuang\2022.3.62f3c1\Editor\Unity.exe`。**只有 Lead 运行 Unity**，其他人不要启动 Unity / dotnet 构建。

## 1. 目标
用程序化几何 + 自定义材质/Shader + UGUI 制作一个可直接运行的 Unity 开始场景：
西欧古典白色大理石神殿 · 圆形可活动区域 · 半球阶梯拱顶+穹顶天窗(oculus) · 内圈柱廊与环形水池 · 水池中央小岛 · 小岛上方漂浮金色“星火”（星形发光体） · 光柱/尘埃/水面/后期泛光 · 可交互开始菜单（开始游戏/设置/关于/退出）。
场景资产：`Assets/Scenes/StartScene.unity`（由 Lead 的 `TempleSceneBuilder` 生成，勿手改）。
菜单以 **Screen Space - Camera** Canvas 渲染（便于编辑器离屏截图），叠加在场景上。

## 2. 所有权 / 文件范围
| 角色 | 写入范围 |
|---|---|
| Lead | `Assets/Editor/Generation/TempleBuildKit.cs`, `TempleLayout.cs`, `TempleSceneBuilder.cs`, `LightingBuilder.cs`, `PostFxBuilder.cs`, `SceneCapture.cs`, `Assets/Art/Shaders/*`, `Assets/Art/Textures/*`(环境/星火), `Tools/generate_environment_textures.py`, `Assets/Scripts/SparkCode/OrbitCamera.cs`, `Docs/*`, `Screenshots/*` |
| architect | `Assets/Editor/Generation/Environment/**` 仅此目录 |
| spark | `Assets/Editor/Generation/Spark/**`, `Assets/Scripts/SparkCode/Spark/**` |
| menu | `Assets/Editor/Generation/UI/**`, `Assets/Scripts/SparkCode/UI/**`, `Tools/generate_ui_art.py`, `Assets/Art/UI/**`（PNG 由脚本生成） |
不要写别人目录、不要改 ProjectSettings/Packages/其他场景/kit/shaders。需要契约改动先给 Lead 发消息。

## 3. 代码约定
- C# 命名空间：生成器 `SparkCode.Generation`；运行时 `SparkCode` / `SparkCode.Spark` / `SparkCode.UI`。
- `Assets/Editor/**` 内的脚本自动进 Editor 程序集，可用 `UnityEditor`；运行时脚本**禁止** `using UnityEditor`。
- 只用 UnityEngine / UnityEngine.UI / UnityEditor，不引入第三方包。
- 生成器只在编辑器执行一次；场景里保存的是普通 GameObject/组件。所有材质、Mesh 通过 kit 保存为资产，避免场景引用丢失。
- 为截图/静帧考虑：构建完成时 UI/星火应处于“最终可展示状态”（alpha=1、正常颜色），不要依赖 Awake/Update 才能显示；同时提供 `PrepareCapture` 等接口。
- 不要创建 .meta；Unity 导入时自动生成。
- 所有面向玩家的文字用中文（英文仅品牌副标/按钮小字），标点可用中文。

## 4. 世界坐标与布局（米，Y 向上，原点=水池/小岛中心，+Z 为初始相机方向）
常量在 `TempleLayout`（Lead 提供，直接引用）：
- `PlazaRadius = 22f`，地面 y=0。
- 环形水池：外沿 `PoolOuterRadius = 7.2f`，内沿（小岛岸）`PoolInnerRadius = 4.5f`，水面 `WaterY = -0.35f`，池深到 `BasinFloorY = -1.7f`，池沿石顶 `RimTopY = 0.28f`。
- 小岛：半径 `IslandRadius = 2.9f`，顶面 `IslandTopY = 0.55f`，3 级台阶；岛中央低祭坛。
- 内圈柱廊：16 根柱，环半径 10.4，柱总高 10.6（含柱础/柱头），上接额枋 10.6→11.9。
- 穹顶：底部 y=11.9、半径 10.2；阶梯环向上向内收，顶部 oculus 半径 2.35、y=24.5；阶梯环数 12 左右（可见内表面 + 外侧挡光壳体）。
- 平顶格栅天花：y=16.2，内半径取穹顶在该高度的半径（约 7.6），外半径 21.4，方格/放射格栅，深色石材。
- 外墙：半径 21.4、高 17.4、厚 0.9；4 个主门洞（+X/+Z/-X/-Z 方向，宽约 4.5m、高 9m）透出天空；内壁有壁柱/壁龛。
- 外圈柱廊：24 根柱，环半径 18.0，柱总高 16.2，上接额枋 16.2→17.4（在天花下方）。
- 地面：方形大理石块拼花 + 同心铜环镶嵌（半径 8.6/13/17.5），外圈可留环形排水槽。
- 星火：锚点 `SparkAnchor = (0, 7.4, 0)`，四芒星整体高约 3.6、宽约 3.6、厚约 0.55；发光 + 光晕 + 环绕粒子 + 点光源。
- 相机关注点 `CameraTarget = (0, 0.6, 0)`；初始机位由 OrbitCamera 在 y≈3.6、半径≈14.5 处环绕（运行时）。截图脚本可自行摆放相机。
- 光照：日光从 oculus 斜射到池面/岛；暖白主光；星火暖金点光；天空为日间蓝天+薄云；后期 Bloom/ACES。

## 5. TempleBuildKit API（Lead 实现，architect/spark/menu 使用）
命名空间 `SparkCode.Generation`，文件 `Assets/Editor/Generation/TempleBuildKit.cs`。

```csharp
public class TempleBuildKit
{
    public Transform Root;                       // 生成内容父节点 "SparkCodeTemple"
    // 已导入纹理（可空则说明导入失败）
    public Texture2D MarbleAlbedo, MarbleNormal, FloorAlbedo, FloorNormal, WaterNormal;
    public Texture2D SparkHalo, SparkMote, SoftGlow, SparkSurfaceAlbedo, SparkSurfaceEmissive;
    // 已创建并保存的材质
    public Material Marble;        // 白色大理石（URP Lit）
    public Material MarbleFloor;   // 地面方砖大理石（URP Lit，UV 平铺）
    public Material MarbleTrim;    // 暖白/微黄装饰石
    public Material MarbleDark;    // 深暖灰石材（天花/屋顶）
    public Material MarbleBasin;   // 池壁湿石
    public Material Gold;          // 抛光金
    public Material Water;         // 自定义水面（SparkCode/Water）
    public Material SparkCore;     // 自定义星火表面（SparkCode/SparkCore）
    public Material SparkHaloMat;  // 加色光晕（SparkCode/AdditiveGlow）
    public Material SparkMoteMat;  // 加色粒子
    public Material LightShaftMat; // 加色光柱
    public Material DustMat;       // 加色尘埃/柔光

    public GameObject Create(string name, Transform parent = null);
    public Mesh SaveMesh(string name, Mesh mesh);                 // Assets/Generated/Meshes/*.asset
    public GameObject CreateMeshObject(string name, Mesh mesh, Material mat, Transform parent = null, bool saveMesh = true);
    public void AddMeshCollider(GameObject go, bool convex = false);
    public void AddBoxCollider(GameObject go, Vector3 center, Vector3 size);
    public Material CreateAdditiveMaterial(string name, Texture2D tex, Color tint, float intensity, float softFade = 0f);
    public Sprite LoadSprite(string assetPath);                   // 自动设置 Sprite 导入，缓存
    public static void MarkStatic(GameObject go);
}
```
自定义 Shader 名（Lead 提供）：`SparkCode/SparkCore`、`SparkCode/AdditiveGlow`、`SparkCode/Water`、`SparkCode/SkyGradient`。
`AdditiveGlow` 属性：`_MainTex`, `_Color`, `_Intensity`, `_SoftFade`, `_Scroll`(Vector), `_Pulse`。支持顶点色相乘（粒子）。
`Water` 属性：`_ShallowColor`,`_DeepColor`,`_FoamColor`,`_NormalMap`,`_NormalStrength`,`_FlowSpeed`,`_Smoothness`,`_FresnelColor`,`_Alpha`。
`SparkCore` 属性：`_MainTex`,`_EmissiveTex`,`_Color`,`_EmissionColor`,`_EmissionIntensity`,`_FlowSpeed`,`_FresnelColor`,`_FresnelPower`,`_SparkleScale`,`_SparkleIntensity`,`_Pulse`。
所有 kit 材质已保存为资产；需要变体时用 `CreateAdditiveMaterial` 或 `new Material(kit.X)` 后 `AssetDatabase.CreateAsset`（不要用未保存材质直接挂场景）。

## 6. 三个生成器入口（必须精确匹配签名）
```csharp
namespace SparkCode.Generation
{
    // EnvironmentInfo 已由 Lead 定义在 Assets/Editor/Generation/BuilderContracts.cs，请不要重复定义。
    public struct EnvironmentInfo {
        public Transform Root;
        public Transform SparkAnchor;     // 世界位置 = SparkAnchor 常量
        public Vector3 CameraTarget;      // (0, 0.6, 0)
    }
    public static class TempleEnvironmentBuilder {
        public static EnvironmentInfo Build(TempleBuildKit kit);
    }

    public struct SparkInfo {
        public GameObject Root;
        public Light CoreLight;
        public ParticleSystem MoteParticles;
        public Transform Anchor;
    }
    public static class SparkBuilder {
        // position = (0,7.4,0)；返回完整星火（网格/光晕/粒子/光源/点击）
        public static SparkInfo Build(TempleBuildKit kit, Vector3 position, StartMenuController menu);
    }
    public static class SparkAtmosphereBuilder {
        // oculus → 岛面的暖色光柱 + 漂浮尘埃；自行用 kit.LightShaftMat / DustMat
        public static void Build(TempleBuildKit kit, EnvironmentInfo env, SparkInfo spark);
    }

    public struct MenuInfo {
        public GameObject Root;
        public Canvas Canvas;
        public StartMenuController Controller;
    }
    public static class StartMenuBuilder {
        public static MenuInfo Build(TempleBuildKit kit, Camera camera, OrbitCamera orbit);
    }
}
```
`SparkBuilder` 内部创建点击逻辑：给星火根加 `SphereCollider`（半径约 2.2）与运行时组件 `SparkClick`，点击时调用 `StartMenuController.StartGame()`（通过 `Object.FindObjectOfType<StartMenuController>()` 获取，勿要求 builder 传引用之外的依赖），若鼠标位于 UI 上则忽略（`EventSystem.current.IsPointerOverGameObject()`）。

## 7. 菜单（menu 负责）要求
- Canvas：`ScreenSpaceCamera`，`worldCamera = camera`，`planeDistance = 2`；CanvasScaler `ScaleWithScreenSize` 1920×1080，match 0.5；需要 `GraphicRaycaster`、EventSystem+StandaloneInputModule。
- 布局以左上品牌 + 左侧竖排主菜单 + 底部提示/版本为骨架，**不要遮挡画面中央的星火与穹顶**。参考品牌图：`CODE / CREATE / BUILD / TOGETHER`。
- 主菜单项：`开始游戏` / `设置` / `关于` / `退出`。悬停：金色渐亮、轻微放大、光晕/粒子；点击有反馈音视觉脉冲。支持键盘 ↑↓ + Enter、Esc 返回/关闭。
- 文本资源：**不要用 TMP/字体资产**。用 Python(PIL) 把文字烘焙成透明 PNG 精灵（金色渐变/描边/外发光），Unity 端用 `Image` 显示。字体可用 `C:\Windows\Fonts\STXINGKA.TTF`（标题毛笔感）、`NotoSerifSC-VF.ttf`/`msyhbd.ttc`（正文）。输出到 `Assets/Art/UI/`，由 `StartMenuBuilder` 通过 `kit.LoadSprite` 载入（路径写死一致）。
- 设置面板：画质（低/中/高 → QualitySettings 名称 Performant/Balanced/High Fidelity）、音量（0/25/50/75/100，AudioListener.volume）、全屏（开/关，Screen.fullScreen）；值用烘焙数字/短标签切换，返回按钮。设置持久化 PlayerPrefs。
- 关于面板：标题、项目简介、制作人员（SparkCode Team）、引擎信息、返回。
- 开始行为：`StartGame()` → 菜单淡出、隐藏交互、`orbit.FocusOnSpark()`、星火 `Flare()`（若可获取 SparkVisual）、短暂金色闪白、显示字幕“序章 · 星火已点燃 / 按 ESC 返回主菜单”，进入 `started` 状态；ESC 返回主菜单。不加载其他场景。
- 退出：`Application.Quit()`；编辑器内 `#if UNITY_EDITOR` 停止播放。
- 关键公开 API（Lead 截图/集成用）：
```csharp
public class StartMenuController : MonoBehaviour {
    public OrbitCamera orbit;
    public bool CanInteract { get; }
    public bool IsStarted { get; }
    public void StartGame();
    public void ShowSettings();
    public void ShowAbout();
    public void BackToMain();
    public void PrepareCapture(string state, int hoverIndex = -1); // "main"/"settings"/"about"/"started"
}
```
`PrepareCapture` 在编辑器非播放状态也能把界面切成目标状态（直接设 alpha/scale/颜色/面板 active），用于离屏截图。

## 8. 星火（spark 负责）要求
- 造型参考最后一张 logo：**四芒星**（上下左右长尖，中心菱形凸起，薄片厚度 0.5m 左右），金色发光；可加背光十字/星芒层次与旋转的能量环，使其在 Minecraft 参考图的“加号”位置上有相似的体量。整体宽高约 3.6m。
- 材质用 `kit.SparkCore`（HDR 自发光，泛光）。加 `kit.SparkHaloMat` 的 billboard 光晕 quad（贴图 `kit.SparkHalo`，4~8m，随相机朝向/缓慢自转/呼吸）与 1~2 层星芒；点光源暖金色（intensity ≈ 8，range ≈ 28，阴影可开可关）。
- 粒子：`SparkMoteParticles` —— 金色微小光点绕星火缓慢旋转/上升，加色、柔和（用 `kit.SparkMote` + `kit.SparkMoteMat`），数量 ~120；可用 ParticleSystem 或自写 GPU 无关的简单粒子（推荐 ParticleSystem，构建时配置好）。
- 运行时 `SparkVisual`：自转 + 上下浮动 + 呼吸发光；`public void Flare()` 供开始按钮触发（短时变亮/放大）。
- `SparkAtmosphereBuilder` 需造：
  - 从 oculus 到小岛的加色锥形光柱（radial soft，顶点在 oculus 下方、底在岛面上方，alpha 低、朝光方向倾斜可选），务必薄而透明，不要遮挡画面中心。
  - 光柱内/穹顶下的尘埃粒子（缓慢飘动，加色，size ~0.03-0.12，数量 200 左右，颜色暖白）。
- 所有运行时脚本放 `Assets/Scripts/SparkCode/Spark/`；生成器放 `Assets/Editor/Generation/Spark/`。
- 若用到自定义额外 shader，只放 `Assets/Art/Shaders/Spark_*.shader` 并告知 Lead；优先用 kit 已有材质/贴图。

## 9. 环境（architect 负责）要求
- 生成层次清晰：`Temple/Floor`、`Temple/Pool`、`Temple/Columns`、`Temple/Dome`、`Temple/Ceiling`、`Temple/Walls`、`Temple/Details`。
- 全部 Mesh 由代码生成；柱、柱础、柱头要体现古典比例（收分、凹槽、柱头/柱基）；可复用 Mesh 资产，按环阵列实例化（共享 mesh）。
- 穹顶阶梯内表面必须从下方可见且正确受光；外侧加挡光壳体（避免从内部透出天空）；oculus 边沿加装饰环。
- 水池：水面 mesh 用 `kit.Water`，环形；池壁、池底（深色湿石）、池沿石（可带金色细边）；小岛 3 级台阶 + 低祭坛。
- 天花：格栅/方格藻井（深色石材 + 金色细线条），内圈紧贴穹顶。
- 外墙 4 门洞透天空；内壁壁柱/壁龛；整体不要漏光（除 oculus/门洞）。
- 不要制造挡住相机的物体；地面可交互范围半径 ≤ 19.5。
- 所有 UV 要适配大理石平铺（约 1 UV = 2m；地面 1 UV = 8m，使 2m 方砖对齐）。
- 性能：总三角面 < 60 万；Draw call 尽量通过合并/共享材质控制。

## 10. 集成 / 验收
- Lead 会按顺序调用：Environment → Lighting/PostFX/Camera → Menu → Spark → Atmosphere，然后保存场景并用离屏相机渲染多张截图检查。
- 验收：编译零错误；场景可见神殿/水池/星火/菜单；截图构图与参考图风格一致（白色大理石、巨大阶梯穹顶、天光光柱、金色星火、精致开始 UI）；菜单可交互（开始/设置/关于/退出/ESC）。
- 完成时向 Lead 报告：新增文件、关键参数、已知风险/待接线项。不要谎报已运行 Unity。

## 11. 参考图的视觉要点（务必对齐）
1. 内部以白/浅灰大理石为主，柱、墙、穹顶都带细微纹理；整体偏冷白，星火与光柱是唯一强烈暖金色。
2. 穹顶是**宽大的同心阶梯环**（不是光滑球面），从柱廊上方向内向上收，顶部一个明亮圆形天窗，天光形成体积光。
3. 空间尺度宏伟：柱子粗壮、天花深邃；前景有暗部柱影，形成框景。
4. 星火饱和、耀眼，带 bloom 光晕和细小星点，不能只是普通黄色球。
5. UI 高级、克制：金/黑/米白配色，毛笔感中文标题 + 英文字标，少量金色描线，不要花哨弹窗。
