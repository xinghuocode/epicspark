# EpicSpark · 史诗火花

一个 Unity 2022.3（URP）项目：输入一个历史事件，**四层 Agent 实时生成可自由探索的 3D 历史场景**并连成一段可玩剧情。

```
剧情&核心 Agent  →  灰盒 Agent  →  搭建 Agent  →  资产生成 Agent
   （五幕剧情）      （区域/路网/槽位）  （检索美术库/落位）   （缺失资产：文生图→抠图→生3D）
```

- 完整设计说明、架构、坐标与资产约定、滑块语义、测试方法：[Docs/架构与使用文档.md](Docs/架构与使用文档.md)
- NPC 语音对话（按 F 交谈后按住 **V** 说话 / 按 **T** 打字、语音合成与降级行为）：[Docs/语音对话模块.md](Docs/语音对话模块.md)
- 主场景：`Assets/Scenes/EpicSpark.unity`
- 生成场景带空间约束：每个模型都有空气墙，NPC / 传送锚点不会落在模型内，模型之间不穿模（见文档 §7.1）。
- 无 API Key 也能完整游玩（全链路确定性离线兜底）；配置 Key 后走真实大模型。

## 四步跑起来

1. Unity **2022.3.62f3c1** 打开本工程（首次导入需要几分钟）。
2. 打开 `Assets/Scenes/EpicSpark.unity`。
3. Play → 在标题界面输入或选择事件 → WASD 走进传送锚点推进剧情。
4. 与 NPC 对话：走到 3 米内按 **F**，对话中**按住 V 说话**（松开即识别）或按 **T** 打字（Enter 发送），再按 **F** 结束。
   没有语音 Key / 麦克风也能打字聊完；语音配置见 `Assets/StreamingAssets/VoiceConfig.json`，细节看 [语音对话模块](Docs/语音对话模块.md)。

## 配置真实大模型

**推荐用游戏内设置界面**：标题界面点「设置 API Key 与模型」，或游戏中按 **F10**。
可填 API Key、Base URL、对话模型、向量模型、文生图模型、单图生 3D 模型，保存即热生效。
也可以设置环境变量 `EPICSPARK_API_KEY`，或在场景 `EpicSpark` 对象的 `GameBootstrap` 里填 `Api Key`。
优先级：设置界面（PlayerPrefs）> 环境变量 > Inspector。留空 = 离线模式，不发任何网络请求。

## 局域网展示端（另一台设备实时看「中摄像头」画面）

游戏本体启动后会自动在局域网暴露只读接口（UDP 45230 发现 + HTTP 45231 MJPEG 流），
`Viewer/` 里的展示端在**同一局域网**内自动找到游戏、实时显示游戏内主摄像头（玩家视角）的画面，无需填 IP。

1. 游戏端：Unity 里点 Play（接口自动开启，日志前缀 `[展示端接口]`）。本机浏览器打开 `http://127.0.0.1:45231/` 可先自检。
2. 展示端：双击 `Viewer/run.bat`（或 `python Viewer/epicspark_viewer.py`）→ 自动打开全屏网页并连上游戏。
3. 找不到时：`python Viewer/epicspark_viewer.py --game <游戏主机IP>:45231` 手动指定。

协议、端口、环境变量、防火墙排查、自动验收脚本：见 [Docs/局域网展示端.md](Docs/局域网展示端.md)。

## 自检

- 编译：`Unity.exe -batchmode -quit -projectPath <工程> -logFile Logs/compile.log`
- 端到端冒烟（Play Mode，离线跑通两场景 + 场景复用 + 存档读档）：见文档第 12 节，报告输出到 `Logs/smoke.json`。
- 语音模块自检（不联网、不用麦克风）：`Unity.exe -batchmode -projectPath <工程> -executeMethod EpicSpark.EditorTools.EpicSparkVoiceSelfTest.Run -logFile Logs\voice-selftest.log`
  （不要加 `-quit`），报告输出到 `Logs/voice-selftest.json`，`passed: true` 即通过。见 [语音对话模块](Docs/语音对话模块.md)。
