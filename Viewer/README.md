# EpicSpark · 局域网展示端

把 EpicSpark 游戏内**主摄像头（玩家视角，即「中摄像头」）**的实时画面，投到同一局域网内的另一台设备/另一块屏幕上。
展示端会自动发现游戏主机，**不需要填 IP**；断线会自动重连。

> 协议契约（端口、beacon 字段、HTTP 接口、帧格式）：[`Docs/局域网展示端.md`](../Docs/局域网展示端.md)

---

## 1. 快速开始

### 游戏主机（Unity 端）

1. 用 Unity 2022.3.62f3c1 打开本仓库，Play。
2. 游戏启动后自动暴露接口，控制台出现 `[展示端接口]` 日志，例如：

   ```
   [展示端接口] HTTP 45231 · 发现 UDP 45230 · 摄像头「主摄像头（玩家视角）」960×540@15fps
   ```

3. （可选）在本机浏览器打开 `http://127.0.0.1:45231/`，能直接看到自检画面 = 接口正常。
4. 第一次启动时如果 Windows 防火墙弹窗，勾选**专用网络**并允许。

### 展示端（任意一台同网段设备）

```bat
:: Windows：双击 run.bat，或
python epicspark_viewer.py
```

```sh
# Linux / macOS
./run.sh
```

展示端会：

1. 起本地网页 `http://127.0.0.1:8686/` 并自动打开浏览器；
2. 监听 UDP 45230 的广播（同时主动探测），5 秒没结果就扫描本机 /24 网段；
3. 自动连接第一台发现的游戏，全屏播放画面；游戏重启/切场景/断网后自动恢复。

页面快捷键：`F` 全屏、`R` 重新扫描、`D` 设备列表、`H` 帮助。

---

## 2. 命令行参数

```text
python epicspark_viewer.py [选项]

  --game ip[:port]     手动指定游戏主机（跳过自动扫描；端口默认 45231）
  --http-port 8686     本地展示网页端口（被占用自动顺延）
  --udp-port 45230     游戏发现 UDP 端口（要与游戏端一致）
  --scan-ports 45231   子网扫描候选 HTTP 端口，逗号分隔
  --no-browser         不自动打开浏览器（投屏 / Kiosk 模式）
  --no-sweep           禁用子网扫描兜底（只依赖 UDP 广播）
  --list               只发现并打印设备列表，不启动网页
  --list-timeout 10    --list 的等待秒数
  --verbose            打印每个 HTTP 请求
```

常用例子：

```bash
# 自动发现（默认）
python epicspark_viewer.py

# 广播被防火墙拦住：手动指定游戏主机
python epicspark_viewer.py --game 192.168.31.20:45231

# 大屏/Kiosk：只起本地服务，不弹浏览器
python epicspark_viewer.py --no-browser

# 只看局域网里有哪些游戏
python epicspark_viewer.py --list
```

---

## 3. 展示端本地接口（页面的后端，只监听 127.0.0.1）

| 方法/路径 | 说明 |
|---|---|
| `GET /` | 展示页面 |
| `GET /api/devices` | 已发现设备 + 当前活跃设备 + 发现方式状态 |
| `GET /api/status` | 目标设备、流状态（`connecting/live/error`）、fps、延迟、累计字节 |
| `POST /api/select` | `{"ip":"192.168.31.20","httpPort":45231}` 切换目标 |
| `POST /api/rescan` | 清空设备表并重新广播/扫描 |
| `GET /api/proxy/stream.mjpg` | 代理后的实时 MJPEG |
| `GET /api/proxy/snapshot.jpg` | 代理后的单帧快照 |

本地服务只绑 `127.0.0.1`，不向局域网暴露；对游戏端只做只读请求（`/api/info`、`/api/health`、`/stream.mjpg`、`/snapshot.jpg`）。

---

## 4. 自测（不需要 Unity）

仓库自带模拟游戏端 `tools/mock_game.py`（用 Pillow 生成动态测试画面，协议与真实游戏端完全一致）：

```bash
# 终端 1：模拟游戏主机
python Viewer/tools/mock_game.py

# 终端 2：展示端
python Viewer/epicspark_viewer.py
```

自动验收（发现 → 拉流 → 帧完整性 → 截图）：

```bash
python Tools/verify_display.py
```

报告输出到 `Logs/display-verify/report.json`，页面截图在同一目录。

---

## 5. 找不到游戏 / 连不上？

| 现象 | 原因 | 处理 |
|---|---|---|
| 一直「正在寻找游戏…」 | 两台设备不在同一网段 / 路由器开启了 AP 隔离 | 接到同一路由；或用 `--game <ip>:45231` 直连 |
| 扫描也找不到 | 游戏主机防火墙拦了 TCP 45231 | 放行端口，或改用游戏端环境变量 `EPICSPARK_DISPLAY_HTTP_PORT` 换端口 |
| 找到设备但一直「连接中」 | TCP 45231 不通（UDP 通不代表 TCP 通） | 防火墙放行 TCP；确认游戏端日志里的实际端口 |
| 画面全黑 | 游戏端采集相机异常 / 编辑器暂停 | 看游戏端日志与 `http://<游戏IP>:45231/api/info` 的 `capture.lastError` |
| 画面卡顿 | 采集分辨率/质量太高 | 游戏端降低 `EPICSPARK_DISPLAY_WIDTH/HEIGHT/FPS/QUALITY` |
| 浏览器打开是空白 | 页面文件缺失 / 服务端口变了 | 看启动日志里的实际 URL；确认 `Viewer/web/` 三个文件存在 |

---

## 6. 已知限制

- 只传画面，不传声音；不支持鼠标键盘反控（v1）。
- 展示端本体仅依赖 Python 3.8+ 标准库；`tools/mock_game.py` 额外需要 Pillow（仅测试用）。
- 同一网段有多台游戏时，默认连第一台，可在设备列表里切换。
- MJPEG 未加密、无鉴权，**仅限可信局域网**使用，不要暴露到公网。
