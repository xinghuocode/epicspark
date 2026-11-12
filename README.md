# 星火史诗 EpicSpark

一个 Unity 2022.3（URP）项目：输入一个历史事件，生成可自由探索的 3D 历史场景并连成一段可玩剧情。

## 当前场景生成架构

```text
剧情 Agent：详细场景需求（区域 / 地标 / 人物 / 剧情关系）
    ↓ 只读 SceneBrief（SceneSpec + 可选语义提示）
灰盒 Agent：理解需求并调用受限工具操作独立灰盒工作区
    ↓ 分区 / 道路 / 地标 / 连续街屋 / 绿化 / 路灯 / 人群 / 锚点
严格灰盒验收：边界、道路净宽、建筑重叠、排屋连续性、剧情覆盖、锚点位置
    ↓ 通过后才允许搭建
搭建 Agent：只将灰盒槽位转换为 PrimitiveKit 简单几何体
```

- **当前强制使用简单几何体**：现有 GLB 美术资产库、模型检索和 AI 资产生成均不参与运行；`EPICSPARK_PRIMITIVES=0` 不再开启旧资产链路。
- 剧情的 `SceneSpec` 和语义 `LayoutSpec` 不会被灰盒工具就地改写；灰盒工具状态是唯一空间事实。
- **剧情 Agent 可自由规定每个场景的 `width` 与 `length`**，两者独立，不套固定尺寸模板；无效非有限值/非正值才会回退。
- 素模一次完整落地，不再让玩家先进入“缺核心地标”的分段首屏。
- 完整设计、工具契约、验证与边界见 [Docs/工具灰盒与素模重构.md](Docs/工具灰盒与素模重构.md)。
- 历史内容协议、任意自然语言输入、远端/修复/兜底来源与签名年份见 [Docs/历史内容与来源追踪.md](Docs/历史内容与来源追踪.md)。
- 历史架构及操作说明仍见 [Docs/架构与使用文档.md](Docs/架构与使用文档.md)，其中美术资产链路描述代表旧实现。

## 三步跑起来

1. Unity **2022.3.62f3c1** 打开本工程（首次导入需要几分钟）。
2. 打开 `Assets/Scenes/StarTempleStart.unity`（星火神殿开始场景）。
3. Play → 走近星火之桥，点漂浮窗「进入史诗…」→「创建新史诗」→ 输入或选择历史事件 →「开始生成」，自动载入主场景；
   或点「加载已有史诗」读取上次存档。

操作：`WASD` 移动 · `Q` 奔跑 · `E` 呼出/锁定鼠标 · `Space` 跳跃 · 左键攻击 · 右键交互 · `Shift` 下蹲 · `F10` 设置。
也可以直接打开 `Assets/Scenes/EpicSpark.unity` 点 Play，进入测试/兜底标题界面。

## 配置剧情模型

神殿漂浮窗「设置」或游戏内 **F10** 可配置 API Key、Base URL 和对话模型。也可设置环境变量 `EPICSPARK_API_KEY`。
优先级：设置界面（PlayerPrefs）> 环境变量 > Inspector。留空 Key = 离线确定性剧情，不发网络请求。
当前正式灰盒工具链为本地确定性执行；旧远端灰盒编辑器只保留给开发对照，不会在默认链路重新摆坐标。

## 自检

- Unity 编译：`Unity.exe -batchmode -quit -projectPath <工程> -logFile Logs/compile.log`
- **真实灰盒入口验收**：`-batchmode -quit -executeMethod EpicSpark.EditorTools.SceneGenProbe.Run`
  覆盖 `GrayboxAgent.PlanAsync`、自然语言输入、输入只读、排屋/锚点几何约束和纯素模搭建，输出 `Logs/scenegen/`。
- **历史内容与协议回归（不联网）**：`-batchmode -quit -executeMethod EpicSpark.EditorTools.StoryContentProbe.Run`
  验证任意事件、JSON 修复、远端/本地来源、未知事件诚实降级、BCE 与未知年代，输出 `Logs/story-content-report.txt`。
- 纯 .NET 离线回归：`scenegen_harness/Program.cs`，编译与运行命令见重构文档。
- 主流程 Play Mode 冒烟：`-batchmode -executeMethod EpicSpark.EditorTools.EpicSparkSmokeTest.Run`
- 神殿 → 主场景：`-batchmode -executeMethod EpicSpark.EditorTools.StarTempleStartPlayTest.Run`
- 美术资产生成菜单仅用于历史调试；当前正式游戏不会调用该服务。
