# EpicSpark · 史诗火花

一个 Unity 2022.3（URP）项目：输入一个历史事件，**四层 Agent 实时生成可自由探索的 3D 历史场景**并连成一段可玩剧情。

```
剧情&核心 Agent  →  灰盒 Agent  →  搭建 Agent  →  资产生成 Agent
   （五幕剧情）      （区域/路网/槽位）  （检索美术库/落位）   （缺失资产：文生图→抠图→生3D）
```

- 完整设计说明、架构、坐标与资产约定、滑块语义、测试方法：[Docs/架构与使用文档.md](Docs/架构与使用文档.md)
- 主场景：`Assets/Scenes/EpicSpark.unity`
- 无 API Key 也能完整游玩（全链路确定性离线兜底）；配置 Key 后走真实大模型。

## 三步跑起来

1. Unity **2022.3.62f3c1** 打开本工程（首次导入需要几分钟）。
2. 打开 `Assets/Scenes/EpicSpark.unity`。
3. Play → 在标题界面输入或选择事件。

## 配置真实大模型

**推荐用游戏内设置界面**：标题界面点「设置 API Key 与模型」，或游戏中按 **F10**。
可填 API Key、Base URL、对话模型、向量模型、文生图模型、单图生 3D 模型，保存即热生效。
也可以设置环境变量 `EPICSPARK_API_KEY`，或在场景 `EpicSpark` 对象的 `GameBootstrap` 里填 `Api Key`。
优先级：设置界面（PlayerPrefs）> 环境变量 > Inspector。留空 = 离线模式，不发任何网络请求。

## 自检

- 编译：`Unity.exe -batchmode -quit -projectPath <工程> -logFile Logs/compile.log`
- 端到端冒烟（Play Mode，离线跑通两场景 + 场景复用 + 存档读档）：见文档第 12 节，报告输出到 `Logs/smoke.json`。
