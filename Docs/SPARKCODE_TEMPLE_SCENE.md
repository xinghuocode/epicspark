# 星火神殿 Unity 场景（SparkCode Temple）

## 文件位置

| 内容 | 路径 |
| --- | --- |
| Unity 场景 | `Assets/Scenes/SparkCodeTemple.unity` |
| 导入模型 | `Assets/Art/Models/SparkCodeTemple.fbx` |
| 场景生成器 | `Assets/Editor/Generation/SparkCodeTempleImport.cs` |
| 运行控制 | `Assets/Scripts/SparkCode/TempleShowcaseController.cs` |
| 生成材质/网格/后处理 | `Assets/Generated/SparkCodeTemple/`、`Assets/Generated/Profiles/SparkCodeTemplePost.asset` |
| Blender 源文件 | `Docs/Blender/sparkcode_temple.blend` |
| Blender 导出脚本 | `Docs/Blender/export_unity_fbx.py` |
| 参考截图 | `Screenshots/unity_temple_hero.png`、`unity_temple_oculus.png`、`unity_temple_star.png`、`unity_temple_wide.png` |

原有的 `Assets/Scenes/StartScene.unity`、`Temple.fbx` 和 StartScene 生成流程均未改动。

## 打开方式

1. 用 Unity 2022.3.62f3c1 打开 `D:\unityproject\epic`。
2. 在 Project 窗口双击 `Assets/Scenes/SparkCodeTemple.unity`。
3. 点击 Play。
4. 键盘 `1` `2` `3` `4` 切换四台预设相机：
   - `1` 全景（CAM_Hero）
   - `2` 穹顶/圆洞（CAM_Oculus）
   - `3` 星火近景（CAM_Star）
   - `4` 侧景（CAM_Wide）

星火会缓慢旋转、上下浮动，并带有金色光晕、星芒和漂浮光尘。

## 场景内容

- 由 Blender 场景导出的完整神殿网格：圆形大厅、20 根科林斯柱、藻井半球穹顶、圆洞、环形水池、中心小岛、桥与星火。
- 模型实际尺寸约 26.7 m × 19.7 m × 27.5 m；Y 轴向上，FBX 单位已校准（导入后约 19.7 m 高，无需手动缩放）。
- 材质复用项目现有的 URP 材质：白色大理石、地面大理石、池壁石材、抛光金、水面、星火自发光。
- 新增场景专用材质（不影响 StartScene）：
  - `Spark_StarScene.mat`：降低亮度、偏金色的星火表面
  - `Water_TempleScene.mat`：深青色水面
  - `Spark_HaloScene.mat` / `Spark_HaloScene_Inner.mat`：星火外层/内层光晕
  - `LightShaft_TempleScene.mat`：圆洞光柱
- 灯光：主太阳光、冷色正面补光、圆洞聚光、穹顶/柱廊/水池补光、星火主光与轮廓光。
- 后处理：ACES、Bloom、暗角、胶片颗粒、轻微色差/镜头畸变；实时反射探针（OnAwake 渲染一次）。
- 碰撞：地面、池底与中心岛自动添加 MeshCollider，供后续角色控制使用。

## 重新生成

在 Unity 菜单中执行：

- `SparkCode > 导入 Blender 星火神殿场景`：重新导入 FBX、生成场景并截图。
- `SparkCode > 重新导入 Blender 神殿模型`：只生成场景，不截图。
- `SparkCode > 截取 Blender 场景截图`：只截取 `Screenshots/unity_temple_*.png`。
- `SparkCode > 诊断导入场景`：输出相机、Volume、星火材质/UV、缺失脚本等诊断信息。

命令行（在项目根目录）：

```powershell
& "D:\unityanzhuang\2022.3.62f3c1\Editor\Unity.exe" -batchmode `
  -projectPath "D:\unityproject\epic" `
  -executeMethod SparkCode.Generation.SparkCodeTempleImport.BuildAndCapture `
  -quit -logFile "D:\dshproject\starttemple\scripts\unity_import.log"
```

## 注意

- 场景中的模型是**普通层级**（Prefab 已解包），这样星火网格可以挂到 `Spark Pivot` 下做旋转/浮动动画。
- 若重新运行生成器，会覆盖 `Assets/Scenes/SparkCodeTemple.unity`，但不会覆盖 StartScene。
- 新的 FBX 已在 Unity 导入设置里关闭相机/灯光/动画导入，并开启 Read/Write 以支持 MeshCollider。
- 由于 Blender 程序化网格没有原始 UV，导出脚本按世界坐标做了盒式投影 UV；大理石/水面纹理会按约 2–4 m 的尺度平铺。
