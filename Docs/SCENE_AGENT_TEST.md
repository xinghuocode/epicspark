# 场景Agent测试场景 说明文档

> 用于测试 ARCHITECT.md §4.2 **场景Agent** 设计的独立测试床。
> 用户输入系统提示词与剧情Agent的 `generate_scene` 工具参数，场景Agent通过
> **原生 Function Calling** 逐步调用工具，在 Unity 中**实时构建真实 3D 场景**，并可第一人称进入体验。

## 1. 快速开始

1. Unity 打开工程，等待编译（首次会解析 `com.unity.cloud.gltfast` 包，需联网）
2. 菜单 **`EpicSpark/AI/创建场景Agent测试场景`**（生成 `Assets/Scenes/SceneAgentTest.unity`）
3. 打开该场景，点 **Play**
4. 左侧面板填写：
   - **Base URL**：`https://ai.gitee.com/v1`
   - **API Key / 模型名**：需支持 Function Calling 的模型
   - **系统提示词 / generate_scene 参数**：已预填范例，可直接修改
5. 点 **▶ 运行测试** —— 观察日志与 Scene 视图逐步生成
6. 生成完毕后点 **🚶 进入场景体验**

> Play 进入的是"当前打开的场景"。若工程曾被 `StarTempleEntryScene` 强制入口场景
> （该脚本已删除），重启 Unity 即可恢复正常行为。

## 2. 面板说明（SceneAgentTestPanel）

| 区域 | 内容 |
|---|---|
| 模型端点 | Base URL / API Key（可掩码显示）/ 模型名 / 超时秒数 |
| 系统提示词 | 预填内置默认，可改；带内嵌滚动条 |
| generate_scene 参数 | 预填"攻占巴士底狱前夜"范例 JSON，可改；带内嵌滚动条 |
| 运行控制 | 运行测试 / 停止 / 清空记录 / **进入场景体验 / 退出体验** |
| 逐步输出 | 每次工具调用与场景快照 |

- 温度 / max_tokens / 轮数均由**服务端默认值**控制（客户端不下发）
- 所有输入通过 `PlayerPrefs` 持久化

## 3. 架构

```
SceneAgentTestPanel (IMGUI)
    │ 运行测试
    ▼
SceneAgentRunner ──OpenAIClient──→ OpenAI 兼容端点 (Function Calling)
    │ 每轮解析 message.tool_calls
    ▼
SceneWorld (纯逻辑: 校验/状态/地形高度函数)
    │ ToolExecuted (日志)  Changed (OK时)
    ├──→ Console / 面板 Transcript (逐步文字输出)
    └──→ SceneBuilder3D (真实3D构建, 每次变更整体重建)
             │ init_terrain → UnityEngine.Terrain
             │ place_model  → glTFast 加载 GLB 实例化
             │ bar_shaped   → Cube拉伸 / 战壕真实下切
             │ anchors/start→ 标记球 + 文字
             ▼
SceneExperienceController (第一人称体验, 可选)
```

文件位于 `Assets/Scripts/AI/SceneAgentTest/`：

| 文件 | 职责 |
|---|---|
| `SceneAgentRunner.cs` | 工具调用主循环、对话上下文管理、3D/体验入口 |
| `SceneWorld.cs` | 7 个工具的逻辑实现 + 校验 + 地形高度函数 + 模型库读取 |
| `SceneToolDefinitions.cs` | 工具的 JSON Schema 定义（下发给模型） |
| `SceneBuilder3D.cs` | 场景状态 → 真实 3D（Terrain / GLB / 条状物 / 标记） |
| `SceneExperienceController.cs` | 第一人称控制器（Input System） |
| `SceneAgentTestPanel.cs` | 运行时 IMGUI 面板 |
| `MiniJson.cs` | 轻量 JSON 解析（工具参数/模型库/入参） |
| `Editor/SceneAgentTestBootstrap.cs` | 一键创建测试场景的菜单 |

## 4. 工具契约（7 个）

参数名遵循 ARCHITECT.md 拼写（如 `auchor`）。所有工具返回 `OK:` / `ERROR:` 文本，LLM 据 ERROR 自纠正。

| 工具 | 说明 | 关键校验 |
|---|---|---|
| `init_terrain` | 初始化地形（必须先调用） | base 仅 `plain`；smoothing_factor ∈ [0,1]；节点 `[x,y,height,width]` 越界拒绝 |
| `place_model` | 放置模型 | 模型必须存在于模型库；坐标/尺寸合法；`placed_name` 唯一 |
| `place_bar_shaped_object` | 墙/栅栏/道路/战壕 | type 枚举；≥2 节点 `[x,y,width]` |
| `set_auchor_point` | 传送锚点绑定模型实例 | 锚点名必须在剧情Agent列表中；实例必须已放置 |
| `set_start_point` | 玩家起始点 | 越界拒绝 |
| `search_model` | 搜索缓存模型库 | 空串返回全部（≤20 条） |
| `finish_scene` | 完成并终检 | 地形/起始点/全部锚点绑定/至少一个模型 |

> 已按需求移除 `generate_model` 与 `place_npc`。模型只能选自缓存库
> （`Assets/Generated/Models/model_library.json`，43 个 GLB）。

## 5. 3D 构建规则

- **坐标**：平面 (x, y) → 世界 (x, 地形高度, y)；场景尺寸 = `width × length`（米）
- **地形**：高度函数（山脉/河流沿节点连线形成**连续轮廓**，战壕沿线段下切 1.8m）
  采样为 257×257 高度图，URP Terrain/Lit 材质，带 TerrainCollider
- **水面**：沿河流折线铺半透明蓝色水面（水位 = 河道最大深度的 35%）
- **模型**：glTFast 加载 GLB（按 id 缓存），按 `size_x/size_y` 缩放，底面贴合地形
- **墙/栅栏/道路**：沿节点拉伸 Cube（3m / 1.5m / 贴地 0.12m）
- **战壕**：**真实下切 1.8m**（地形函数 `TrenchDepth=1.8`，smoothstep 断面），
  沟底铺深色底板；可走进沟里
- **锚点**：黄色标记球 + 悬浮名称；**起点**：绿色圆柱 + "起点"文字
- 每次工具调用成功后**整体重建**（带版本号防异步竞争）；GLB 只加载一次

## 6. 场景体验

- 出生点 = `set_start_point`（无则场景中心），自动落在地形上
- **WASD/方向键**移动、**鼠标**视角、**Shift** 加速、**空格** 跳跃、**Esc** 退出
- 相机进入时接管、退出时完整归还；重新运行测试自动退出体验

## 7. 已知限制 / 与架构文档的偏差

| 项 | 状态 |
|---|---|
| 墙/栅栏/道路 | 程序化 Cube，非真实模型；无碰撞（战壕除外，为真实地形下切） |
| 水面 | 半透明条带，随河道折线分段直线拼接（非流动/无反射） |
| generate_model / place_npc | 已移除（见第 4 节） |
| 模型重叠检测 | 未实现（系统提示词中"不要重叠"靠 LLM 自觉） |
| 词嵌入缓存阈值（§4.4） | 未接入（模型库为关键词搜索） |
| NPC 交互 / 剧情Agent | 不属于本测试床范围 |
| 文档拼写 | 锚点相关沿用文档原文 `auchor` |

## 8. 调试

- Console Tag：`[SceneAgentTest]` / `[SceneBuilder3D]`
- 面板"输出完整场景记录"（Runner 右键菜单）可导出全部 Transcript
- Unity 编辑器日志：`~/.config/unity3d/Editor.log`（Linux）
