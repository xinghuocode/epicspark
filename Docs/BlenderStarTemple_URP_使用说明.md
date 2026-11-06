# Blender 星火神殿 · Unity URP 实时场景

## 打开场景

使用 **Unity 2022.3.62f3c1** 打开项目 `D:\unityproject\epic`，然后打开：

`Assets/Scenes/BlenderStarTemple_URP.unity`

该场景是独立的新场景，不替换原有 `StartScene`、`SampleScene` 或 `SparkCodeTemple`。新场景在 Build Settings 中以禁用状态添加，不改变项目启动场景。

如需第一人称操作，请打开玩法场景 `Assets/Scenes/BlenderStarTemple_Gameplay.unity`，操作与键位说明见 `Docs/BlenderStarTemple_键盘移动系统说明.md`。

## 模型与单位

- 模型：`Assets/Art/Models/BlenderStarTemple.fbx`
- 来源：`D:\dshproject\starttemple\output\星火神殿.blend`
- 单位：米。
- FBX 使用 `-Z Forward / Y Up`，保留 Unity FBX 导入器的轴向转换。
- 几何包含 16 根立柱、开孔穹顶、藻井、水池、中心岛、四芒星、流光和漂浮光点。
- 当前 Blender 版本的柱后净距为 7.227 m，外墙内半径为 17.087 m。
- 已为程序化网格补充逐面盒式 UV，并使用项目已有的大理石、水面和星火材质。
- 本次场景使用独立的双面材质副本，保证穹顶、壁龛和流光背面可见；石墙灰缝后另有深色连续砂浆背衬。

## URP 实时光照

场景使用项目现有的 **URP High Fidelity** 配置：

- HDR、4× MSAA、SRP Batcher。
- 主光与附加光实时计算。
- 主光和附加光软阴影，4 级阴影级联。
- 每对象最多 8 盏附加光。
- ACES Tonemapping、Bloom、Color Adjustments 和 Vignette。
- 实时 Reflection Probe 采用分面刷新，降低每帧开销。

主要实时灯光：

- `Sun_Oculus_Realtime`：穿过圆孔方向的实时 Directional Light。
- `Oculus_Spot_Realtime`：顶部聚光，启用软阴影。
- `Spark_Key_Realtime`：星火金色点光，启用软阴影。
- 穹顶、水池、柱廊和外侧回廊的补光。

**URP 的实时直接光不等于实时路径追踪全局光照。** 场景使用环境光、实时光源、发光材质和反射探针构成可交互效果；不依赖 Blender Cycles。透明光束网格是实时光束近似，不是体积路径追踪。发光材质本身在 URP 中不会自动照亮周围物体，因此星火附近另配有点光。

## 操作

运行场景后：

- `1`：主视角。
- `2`：穹顶仰视。
- `3`：星火近景。
- `4`：加宽回廊侧视。

`TempleShowcaseController` 控制相机切换和星火缓慢旋转、上下浮动。不需要动画时，可在该组件上把 `starSpinSpeed` 和 `starBobAmplitude` 设为 0。

## 性能调整

模型包含较多独立 Mesh Renderer。建筑已启用 Static Batching，星火的材质发光不使用逐光点光源。如果目标设备性能有限：

1. 将 Quality 切换到 Balanced。
2. 将反射探针 `refreshMode` 从 `EveryFrame` 改为 `OnAwake`。
3. 降低附加光阴影分辨率或关闭星火点光阴影。
4. 在 Unity 中进一步合并静态建筑网格。

## 重新生成

Blender 导出脚本：`Tools/blender/export_unity_startemple.py`。它读取当前 Blender 文件，但不覆盖源 `.blend`。

Unity 场景生成器：`Assets/Editor/Generation/StarTempleURPImport.cs`。

在 Unity 菜单选择 `SparkCode → 导入新版 Blender 神殿 (URP 实时光)` 可重建并截图；选择 `验证新版神殿 URP 场景` 可单独验证。

命令行：

```powershell
D:\unityanzhuang\2022.3.62f3c1\Editor\Unity.exe -batchmode -projectPath D:\unityproject\epic -executeMethod SparkCode.Generation.StarTempleURPImport.Build -quit

D:\unityanzhuang\2022.3.62f3c1\Editor\Unity.exe -batchmode -projectPath D:\unityproject\epic -executeMethod SparkCode.Generation.StarTempleURPImport.Capture -quit
```

验证报告：`Docs/BlenderStarTemple_UnityValidation.json`。
截图：`Screenshots/BlenderStarTemple_*.png`。
