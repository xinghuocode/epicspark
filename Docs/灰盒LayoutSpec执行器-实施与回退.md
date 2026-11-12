# 灰盒 LayoutSpec 执行器：实施说明与回退手册

> 目标：让灰盒从「自由规划器」变成「按剧情施工图施工的执行器」。
> 默认开启；远端灰盒只能做微调，不能删规格项、缩排屋、改坐标，不能把 50×20 连续街屋压成两栋大房子。

---

## 一、问题定位（05:55 那轮）

| 现象 | 根因 |
|---|---|
| 剧情写「南侧民居群 50×20，连续街屋」，灰盒只剩 `slot_houses_west/east` 两栋 | ① 灰盒拿到的只是自然语言，没有结构化规格；② 远端 prompt 只说「主要物件 8~20 个」，schema 没有数量/排屋字段；③ 远端 `plan.slots` 从空开始，本地草稿只在 3 米距离外补槽；④ 搭建阶段 `SizeForCategory("building")` 把槽位随机拉宽到 6~20 米，排屋单元互相压住 |
| 远端不调 `finish_graybox` 也照样被接受 | 旧路径只校验「≥3 个槽、在边界内、尺寸≤60」，没有数量/排屋/街道/重叠校验 |
| 锚点、NPC 坐标对不上 | `SceneAssembler` 用 `localPosition`，而本地规划器把零坐标扇形铺开 |

结论：**缺的不是一句更严的提示词，而是「规格 → 校验 → 回退」三层机制。**

---

## 二、现在的链路

```
剧情 SceneSpec
  └─ StoryLayoutParser           解析文字中的占地/道路/地标
      └─ StoryLayoutSpecBuilder  把「50×20」切成带 rowId/rowCount 的单元
          └─ GrayboxSpecExecutor 生成确定性 GrayboxPlan（specDriven=true）
              ├─ 离线：直接使用
              └─ 远端：作为 seed 交给 LLM，只允许在规格内微调
                  └─ GrayboxSpecCompliance.MergeRequired  缺失的规格槽补回/被移动的恢复
                      └─ GrayboxSpecCompliance.Validate   缺项/数量/位移/缩放/排屋缝隙/未 finish
                          ├─ 通过：采用远端结果
                          └─ 不通过：回退 GrayboxSpecExecutor 的确定性计划
```

### 关键文件

| 文件 | 作用 |
|---|---|
| `Assets/Scripts/Core/EpicSparkTypes.cs` | 新增 `LayoutSpec / LayoutItemSpec / LayoutPathSpec`；`GrayboxSlot` 增加 `specItemId/rowId/rowIndex/rowCount/specRequired`；`GrayboxPlan` 增加 `layoutSpec/specDriven/layoutShift/specCompliance/finished/deviations` |
| `Assets/Scripts/Story/StoryLayoutParser.cs` | 深进深排屋（50×20）保留聚合体 + `rowId/rowCount`；修正「桥头/台阶」把要塞误判成锚点、「台」把市政厅误拆成摊台、排屋 rotation 90° 导致宽深互换 |
| `Assets/Scripts/Story/StoryLayoutSpecBuilder.cs` | 按原始长轴切道路翼，再按 8.5 米开间拆单元；给锚点/NPC/道具补规格 |
| `Assets/Scripts/Graybox/GrayboxSpecExecutor.cs` | 把 `LayoutSpec` 落成确定性 `GrayboxPlan` |
| `Assets/Scripts/Graybox/GrayboxSpecCompliance.cs` | 合并/校验；远端重复道路去重；几何冲突默认只提醒 |
| `Assets/Scripts/Graybox/GrayboxAgent.cs` | 新链路接入：seed → 远端 → 合并 → 校验 → 回退 |
| `Assets/Scripts/Graybox/GrayboxToolSchema.cs` | prompt/schema 增加规格项坐标、排屋字段、seed 保留规则、seed 道路坐标 |
| `Assets/Scripts/Build/AssetBinding.cs` | `specRequired` 建筑按槽位原尺寸；排屋不抖动角度 |
| `Assets/Scripts/Build/BuildAgent.cs` | 规格建筑允许 60 m；规格槽低相似度/无模型告警；规格模式禁用 localPosition 扇形兜底 |
| `Assets/Scripts/Loading/SceneAssembler.cs` | 锚点/NPC 优先使用 plan 槽位坐标 |
| `Assets/Scripts/Core/EpicSparkFeatureFlags.cs` | 运行期开关 |

---

## 三、怎么验证的

### 1. 05:55 原场景（`04_场景JSON.txt`）

```powershell
# 把 05:55 的 04_场景JSON.txt 复制到 Logs/scene-spec.json 后：
Unity.exe -batchmode -projectPath D:\unityproject\epicsparktest4 `
  -executeMethod EpicSpark.EditorTools.EpicSparkSceneAudit.Run -offline -render `
  -logFile Logs\audit-final-new.log
```

结果：`规格项 26 · 槽位 26 · 排屋单元 4`，南侧 50×20 被 12 米主路切成
`西翼 2 个 9.325×20 + 东翼 2 个 9.325×20`，连续无缝隙；巴士底狱要塞恢复为
`50×35` 建筑槽（不再因为句子里有「桥头」变成锚点）。

### 2. 远端灰盒（离线场景 + 远端灰盒）

```powershell
Unity.exe -batchmode -projectPath D:\unityproject\epicsparktest4 `
  -executeMethod EpicSpark.EditorTools.EpicSparkSceneAudit.Run -offline -remote-graybox -render `
  -logFile Logs\audit-0555-remote-accept.log
```

结果：`远端规划按 LayoutSpec 校验通过：pass · 规格项 26 · 槽位 26`。
远端即使没有 `finish` 或重复定义道路，也只能微调；槽位由执行器补齐/去重。
如果远端真的删规格项/缩排屋/改坐标，会回退到确定性规格计划。

### 3. 回退开关

```powershell
$env:EPICSPARK_LAYOUT_SPEC='0'   # 完全走旧 GrayboxAgent 路径
```

验证结果：旧路径正常跑通（`地标 7 个...槽位 19 个`），不抛异常。

---

## 四、开关与回退

### 1. 运行期开关（推荐）

| 环境变量 | 默认 | 作用 |
|---|---|---|
| `EPICSPARK_LAYOUT_SPEC` | `1` | `0` = 完全关闭 LayoutSpec 执行器，回到旧灰盒 |
| `EPICSPARK_SPEC_STRICT` | `1` | `0` = 远端不合规时也接受（仅调试） |
| `EPICSPARK_SPEC_GEOMETRY` | `0` | `1` = 把压路/建筑重叠当硬错误（剧情自身冲突时会误回退） |
| `EPICSPARK_PLAN_UNIT_POS` | `1` | `0` = 锚点/NPC 回到旧 `localPosition` 扇形位置 |

代码里也可以：
```csharp
EpicSpark.Core.EpicSparkFeatureFlags.UseLayoutSpecExecutor = false;
```

### 2. 完全回退（保留旧代码）

- 预改动 WIP 备份：`Logs/pre-layout-spec-20261108_070242.patch`（200 KB，包含改动前未提交的工作区）
- 新增文件可单独删除：
  - `Assets/Scripts/Story/StoryLayoutSpecBuilder.cs`
  - `Assets/Scripts/Graybox/GrayboxSpecExecutor.cs`
  - `Assets/Scripts/Graybox/GrayboxSpecCompliance.cs`
  - `Assets/Scripts/Core/EpicSparkFeatureFlags.cs`
- 旧路径本身没有删除：`GrayboxPlanner.Plan`、`StoryLayoutPlanBuilder`、远端 `PlanRemoteAsync` 旧分支都保留；设置 `EPICSPARK_LAYOUT_SPEC=0` 即可绕开新链路。
- 若要恢复改动前的文件内容：
```powershell
cd D:\unityproject\epicsparktest4
git apply --reverse Logs/pre-layout-spec-20261108_070242.patch
```
（先确认工作区状态；patch 包含改动前 WIP，建议先提交当前改动或用 `git stash`。）

---

## 五、已知边界

1. **剧情自身冲突**：05:55 的「印刷作坊」占地和「西侧小巷」起点重叠，「巴士底狱要塞」占地和护城河/道路边缘重叠。这类冲突默认只记 warning，不阻断；需要硬校验时设 `EPICSPARK_SPEC_GEOMETRY=1`。
2. **资产库不足**：规格建筑能按真实尺寸落地，但现有模型库只有少量建筑，排屋会重复 `mdl_house_001`；这是资产层问题，不是灰盒层。
3. **远端未 finish**：如果槽位完整，按已收集结果收尾并记 warning；这是为了兼容推理型模型偶尔漏掉最后一步。
4. **旧场景兼容**：`SceneSpec.layoutSpec` 为空时自动从 `layoutDescription` 构建；已有 `layoutSpec` 的场景优先使用已有规格。
