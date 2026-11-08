#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""局域网展示端 + 游戏端协议的端到端自动验收（不依赖 Unity）。

流程：
  1. 起一个模拟游戏端（Viewer/tools/mock_game.py，协议与 Unity 端一致）
  2. 起展示端（Viewer/epicspark_viewer.py）
  3. 验证 UDP 自动发现 → /api/devices 出现设备
  4. 验证代理 MJPEG：4 秒内收到足够帧、每帧 JPEG SOI/EOI 完整、fps 合理、seq 递增
  5. 验证 /api/status = live、字段齐全
  6. 验证 --game 手动指定模式（不起 UDP）
  7. 验证 UDP 不可达时子网扫描兜底
  8. 验证游戏端重启后展示端自动恢复
  9. Edge headless 截图页面并检查不是纯黑/空白
 10. 输出 Logs/display-verify/report.json 与截图

用法：
    python Tools/verify_display.py                # 全流程
    python Tools/verify_display.py --skip-shots   # 不截图（无 Edge 时）
    python Tools/verify_display.py --keep         # 保留进程（排查用，Ctrl+C 退出）

退出码：0 全部通过；1 有失败项。
"""
import argparse
import base64
import json
import os
import re
import shutil
import socket
import struct
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VIEWER = os.path.join(ROOT, "Viewer", "epicspark_viewer.py")
MOCK = os.path.join(ROOT, "Viewer", "tools", "mock_game.py")
OUT_DIR = os.path.join(ROOT, "Logs", "display-verify")
EDGE_PATHS = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
]

MOCK_HTTP = 45291
MOCK_UDP = 45290
VIEWER_PORT = 18766
VIEWER_PORT_MANUAL = 18767
VIEWER_PORT_SWEEP = 18768


# ────────────────────────────── 基础工具 ──────────────────────────────

def log(msg):
    print("[verify] " + msg, flush=True)


# 本机/局域网请求绕开系统代理（否则 Windows 注册表代理会让 127.0.0.1 直接 502）
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def run_http(url, timeout=5, data=None, headers=None):
    req = urllib.request.Request(url, data=data, headers=headers or {})
    with OPENER.open(req, timeout=timeout) as resp:
        body = resp.read()
        return resp.status, dict(resp.headers), body


def get_json(url, timeout=5):
    status, headers, body = run_http(url, timeout=timeout)
    return json.loads(body.decode("utf-8")), headers


def post_json(url, payload, timeout=5):
    body = json.dumps(payload).encode("utf-8")
    status, headers, raw = run_http(
        url, timeout=timeout, data=body,
        headers={"Content-Type": "application/json"},
    )
    return json.loads(raw.decode("utf-8"))


def wait_for(fn, timeout, interval=0.25, what="条件"):
    start = time.time()
    last_error = None
    while time.time() - start < timeout:
        try:
            value = fn()
            if value:
                return value, time.time() - start
        except Exception as exc:  # noqa: BLE001 - 轮询期间忽略
            last_error = exc
        time.sleep(interval)
    raise TimeoutError("等待超时（%.0fs）：%s（最后一次错误：%s）"
                       % (timeout, what, last_error))


def port_free(port, host="127.0.0.1"):
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        sock.settimeout(0.3)
        return sock.connect_ex((host, port)) != 0
    finally:
        sock.close()


def ensure_ports_free(ports):
    busy = [p for p in ports if not port_free(p)]
    if busy:
        raise RuntimeError("端口被占用，先清理：%s" % busy)


def start_process(name, args, log_path):
    os.makedirs(os.path.dirname(log_path), exist_ok=True)
    log_file = open(log_path, "ab")
    creationflags = 0
    if os.name == "nt":
        creationflags = getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
    proc = subprocess.Popen(
        [sys.executable] + args,
        cwd=ROOT,
        stdout=log_file,
        stderr=subprocess.STDOUT,
        creationflags=creationflags,
    )
    proc._log_file = log_file  # type: ignore[attr-defined]
    log("启动 %s：pid=%s 日志=%s" % (name, proc.pid, os.path.relpath(log_path, ROOT)))
    return proc


def kill_process(proc, name):
    if proc is None or proc.poll() is not None:
        return
    try:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=5)
    except Exception as exc:  # noqa: BLE001
        log("结束 %s 失败：%s" % (name, exc))
    finally:
        try:
            proc._log_file.close()  # type: ignore[attr-defined]
        except Exception:  # noqa: BLE001
            pass


# ────────────────────────────── MJPEG 客户端 ──────────────────────────────

def read_mjpeg(url, seconds):
    """读取代理 MJPEG，返回 (parts, total_bytes, elapsed)。parts = [(headers, jpeg)]"""
    parts = []
    deadline = time.time() + seconds
    total = 0
    req = urllib.request.Request(url, headers={"Connection": "close"})
    with OPENER.open(req, timeout=8) as resp:
        content_type = resp.headers.get("Content-Type", "")
        boundary = b"--"
        match = re.search(r"boundary=([^;]+)", content_type)
        if match:
            boundary = b"--" + match.group(1).strip().strip('"').encode()
        buf = b""
        while time.time() < deadline:
            chunk = resp.read(4096)
            if not chunk:
                break
            total += len(chunk)
            buf += chunk
            while True:
                idx = buf.find(boundary)
                if idx < 0:
                    break
                head_end = buf.find(b"\r\n\r\n", idx)
                if head_end < 0:
                    break
                head = buf[idx:head_end].decode("latin-1", "replace")
                headers = {}
                for line in head.split("\r\n")[1:]:
                    if ":" in line:
                        k, v = line.split(":", 1)
                        headers[k.strip().lower()] = v.strip()
                try:
                    length = int(headers.get("content-length", ""))
                except ValueError:
                    length = -1
                if length < 0:
                    buf = buf[head_end + 4:]
                    continue
                body_start = head_end + 4
                if len(buf) < body_start + length:
                    break
                jpeg = buf[body_start:body_start + length]
                parts.append((headers, jpeg))
                buf = buf[body_start + length:]
    return parts, total, seconds


def validate_jpeg(parts):
    ok = 0
    sizes = []
    seqs = []
    for headers, data in parts:
        if len(data) < 300:
            continue
        if data[:2] == b"\xff\xd8" and data[-2:] == b"\xff\xd9":
            ok += 1
            sizes.append(len(data))
            if "x-seq" in headers:
                try:
                    seqs.append(int(headers["x-seq"]))
                except ValueError:
                    pass
    return ok, sizes, seqs


# ────────────────────────────── 截图（CDP，真实等待） ──────────────────────────────

def find_edge():
    for path in EDGE_PATHS:
        if os.path.exists(path):
            return path
    return None


class _WebSocket(object):
    """极简 WebSocket 客户端（仅够和 Chrome DevTools Protocol 通信）。"""

    def __init__(self, sock, reader):
        self.sock = sock
        self.reader = reader
        self._id = 0

    @classmethod
    def connect(cls, url, timeout=10):
        # ws://host:port/path
        parsed = url.replace("ws://", "", 1)
        host_port, _, path = parsed.partition("/")
        host, _, port = host_port.partition(":")
        sock = socket.create_connection((host, int(port or 80)), timeout)
        sock.settimeout(timeout)
        key = base64.b64encode(os.urandom(16)).decode("ascii")
        request = (
            "GET /%s HTTP/1.1\r\nHost: %s\r\nUpgrade: websocket\r\n"
            "Connection: Upgrade\r\nSec-WebSocket-Key: %s\r\n"
            "Sec-WebSocket-Version: 13\r\n\r\n" % (path, host_port, key)
        )
        sock.sendall(request.encode("ascii"))
        reader = sock.makefile("rb")
        status = reader.readline()
        if b"101" not in status:
            raise RuntimeError("WebSocket 握手失败：%r" % status)
        while True:
            line = reader.readline()
            if line in (b"\r\n", b"\n", b""):
                break
        return cls(sock, reader)

    def close(self):
        try:
            self.sock.close()
        except OSError:
            pass

    def _read_frame(self):
        header = self.reader.read(2)
        if not header or len(header) < 2:
            raise EOFError("WebSocket 连接已关闭")
        opcode = header[0] & 0x0F
        length = header[1] & 0x7F
        if length == 126:
            length = struct.unpack(">H", self.reader.read(2))[0]
        elif length == 127:
            length = struct.unpack(">Q", self.reader.read(8))[0]
        payload = self.reader.read(length)
        return opcode, payload

    def send(self, text):
        data = text.encode("utf-8")
        header = bytearray([0x81])
        mask = os.urandom(4)
        length = len(data)
        if length < 126:
            header.append(0x80 | length)
        elif length < 65536:
            header.append(0x80 | 126)
            header.extend(struct.pack(">H", length))
        else:
            header.append(0x80 | 127)
            header.extend(struct.pack(">Q", length))
        header.extend(mask)
        masked = bytes(byte ^ mask[i % 4] for i, byte in enumerate(data))
        self.sock.sendall(bytes(header) + masked)

    def call(self, method, params=None, timeout=60):
        self._id += 1
        message_id = self._id
        self.send(json.dumps({"id": message_id, "method": method,
                              "params": params or {}}))
        deadline = time.time() + timeout
        while time.time() < deadline:
            opcode, payload = self._read_frame()
            if opcode == 0x8:
                raise EOFError("WebSocket 被关闭")
            if opcode == 0x9:  # ping
                self.sock.sendall(bytes([0x8A, len(payload)]) + payload)
                continue
            if opcode not in (0x1, 0x0):
                continue
            message = json.loads(payload.decode("utf-8", "replace"))
            if message.get("id") == message_id:
                if "error" in message:
                    raise RuntimeError("CDP 错误：%s" % message["error"])
                return message.get("result", {})
        raise TimeoutError("CDP 调用超时：%s" % method)


def _free_port():
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]
    finally:
        sock.close()


def screenshot(url, out_path, wait_seconds=12.0):
    """用 CDP 打开页面、真实等待画面稳定后截图，并返回页面自检状态。

    之所以不用 --virtual-time-budget：虚拟时间会把页面里基于 Date.now 的重连看门狗
    跑飞，截出来的状态不能代表真实运行情况。
    """
    edge = find_edge()
    if not edge:
        return None, "未找到 Edge/Chrome", {}
    port = _free_port()
    user_dir = tempfile.mkdtemp(prefix="epicspark-edge-")
    proc = subprocess.Popen(
        [
            edge,
            "--headless=new",
            "--disable-gpu",
            "--hide-scrollbars",
            "--no-first-run",
            "--no-default-browser-check",
            "--user-data-dir=" + user_dir,
            "--window-size=1600,900",
            "--remote-debugging-port=%d" % port,
            "about:blank",
        ],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    ws = None
    try:
        def _ready():
            try:
                run_http("http://127.0.0.1:%d/json/version" % port, timeout=1)
                return True
            except Exception:  # noqa: BLE001
                return False
        wait_for(_ready, 15, interval=0.25, what="Edge 调试端口就绪")
        listing = json.loads(run_http("http://127.0.0.1:%d/json/list" % port,
                                      timeout=5)[2].decode("utf-8"))
        page = next((t for t in listing if t.get("type") == "page"), None)
        if page is None:
            return None, "没有找到浏览器 page target", {}
        ws = _WebSocket.connect(page["webSocketDebuggerUrl"], timeout=20)
        ws.call("Page.enable")
        ws.call("Page.navigate", {"url": url})
        time.sleep(float(wait_seconds))
        probe = ws.call("Runtime.evaluate", {
            "expression": (
                "JSON.stringify({"
                "pill:(document.getElementById('statusPill')||{}).dataset?"
                "document.getElementById('statusPill').dataset.state:'',"
                "status:(document.getElementById('statusText')||{}).textContent||'',"
                "fps:(document.getElementById('statFps')||{}).textContent||'',"
                "res:(document.getElementById('statRes')||{}).textContent||'',"
                "device:(document.getElementById('deviceName')||{}).textContent||'',"
                "overlay:document.getElementById('placeholder').classList.contains('hidden'),"
                "canvas:[document.getElementById('stream').width,document.getElementById('stream').height]"
                "})"
            ),
            "returnByValue": True,
        })
        page_state = {}
        try:
            page_state = json.loads(probe.get("result", {}).get("value") or "{}")
        except ValueError:
            page_state = {}
        shot = ws.call("Page.captureScreenshot", {"format": "png"}, timeout=60)
        data = shot.get("data", "")
        if not data:
            return None, "CDP 截图返回空", page_state
        with open(out_path, "wb") as fh:
            fh.write(base64.b64decode(data))
        return out_path, None, page_state
    except Exception as exc:  # noqa: BLE001
        return None, "CDP 截图失败：%r" % (exc,), {}
    finally:
        if ws is not None:
            try:
                ws.call("Browser.close", timeout=5)
            except Exception:  # noqa: BLE001
                pass
            ws.close()
        try:
            proc.terminate()
            proc.wait(timeout=10)
        except Exception:  # noqa: BLE001
            try:
                proc.kill()
            except Exception:  # noqa: BLE001
                pass
        shutil.rmtree(user_dir, ignore_errors=True)


def image_not_blank(path):
    """纯标准库近似判断：文件够大 + 解析 PNG/JPEG 尺寸 + 用 Pillow 若可用。"""
    size = os.path.getsize(path)
    if size < 15000:
        return False, "文件过小（%d B），疑似空白" % size
    try:
        from PIL import Image  # 仅测试环境可用
        with Image.open(path) as img:
            img = img.convert("RGB")
            colors = img.resize((64, 36)).getcolors(64 * 36)
            distinct = len(colors) if colors else 0
            ex = img.getextrema()
            spread = max(c[1] - c[0] for c in ex)
            if distinct < 8 or spread < 40:
                return False, "画面过于单一（distinct=%d spread=%d）" % (distinct, spread)
            return True, "尺寸=%s distinct=%d spread=%d size=%dB" % (
                "x".join(map(str, img.size)), distinct, spread, size)
    except ImportError:
        return True, "未安装 Pillow，仅按文件大小判断（%d B）" % size


# ────────────────────────────── 测试用例 ──────────────────────────────

class Verifier:
    def __init__(self, keep=False, skip_shots=False):
        self.keep = keep
        self.skip_shots = skip_shots
        self.procs = {}
        self.checks = []

    def check(self, name, ok, detail="", metrics=None):
        self.checks.append({"name": name, "ok": bool(ok), "detail": detail, "metrics": metrics or {}})
        log(("  [PASS] " if ok else "  [FAIL] ") + name + (" · " + detail if detail else ""))

    def start_mock(self, extra=None):
        args = [MOCK, "--http", str(MOCK_HTTP), "--udp", str(MOCK_UDP), "--fps", "15"]
        args += extra or []
        self.procs["mock"] = start_process(
            "模拟游戏端", args, os.path.join(OUT_DIR, "mock.log"))

    def start_viewer(self, key, port, extra):
        args = [VIEWER, "--http-port", str(port), "--no-browser"] + extra
        self.procs[key] = start_process(
            "展示端(%s)" % key, args, os.path.join(OUT_DIR, "viewer-%s.log" % key))
        base = "http://127.0.0.1:%d" % port
        wait_for(lambda: run_http(base + "/api/status", timeout=2)[0] == 200, 20,
                 what="展示端本地服务启动")
        return base

    def cleanup(self):
        for key, proc in list(self.procs.items()):
            if self.keep:
                log("保留进程 %s (pid=%s)，Ctrl+C 结束" % (key, proc.pid))
            else:
                kill_process(proc, key)
        self.procs.clear()

    # ---------- 用例 ----------

    def case_discovery_and_stream(self):
        log("── 用例 1：UDP 自动发现 + MJPEG 实时流")
        self.start_mock()
        time.sleep(1.0)
        base = self.start_viewer("auto", VIEWER_PORT,
                                 ["--udp-port", str(MOCK_UDP)])

        devices, elapsed = wait_for(
            lambda: (get_json(base + "/api/devices")[0].get("devices") or None),
            25, what="/api/devices 出现设备")
        dev = devices[0]
        self.check("UDP 自动发现", True,
                   "耗时 %.1fs，设备 %s (%s:%s)，source=%s"
                   % (elapsed, dev.get("name"), dev.get("ip"),
                      dev.get("http"), dev.get("source")),
                   {"discoverSec": round(elapsed, 2)})

        info, _ = get_json(base + "/api/devices")
        active = info.get("active")
        self.check("发现设备协议字段", bool(active and active.get("svc") == "epicspark.display"
                                     and active.get("cameras")),
                   "active=%s" % json.dumps(active, ensure_ascii=False)[:300])

        parts, total, seconds = read_mjpeg(base + "/api/proxy/stream.mjpg", 4.0)
        ok_frames, sizes, seqs = validate_jpeg(parts)
        fps = ok_frames / seconds if seconds else 0
        seq_monotonic = all(b > a for a, b in zip(seqs, seqs[1:])) if len(seqs) > 1 else False
        self.check("MJPEG 代理收到帧", ok_frames >= 20,
                   "%d 帧 / %.1fs = %.1f fps，平均 %.1f KB，seq=%s"
                   % (ok_frames, seconds, fps, (sum(sizes) / len(sizes) / 1024) if sizes else 0,
                      ("递增" if seq_monotonic else "缺失/非递增")),
                   {"frames": ok_frames, "fps": round(fps, 2),
                    "avgFrameKB": round((sum(sizes) / len(sizes) / 1024) if sizes else 0, 1)})
        self.check("帧完整性与序号", ok_frames == len(parts) and seq_monotonic,
                   "parts=%d validJpeg=%d seqs=%s" % (len(parts), ok_frames, seqs[:6]))
        self.check("帧率接近 15fps", 8 <= fps <= 20, "实测 %.1f fps" % fps)

        status, headers = get_json(base + "/api/status")
        stream = status.get("stream", {})
        self.check("/api/status 状态为 live", stream.get("state") == "live",
                   "state=%s fps=%s bytes=%s err=%s"
                   % (stream.get("state"), stream.get("fps"), stream.get("bytes"),
                      stream.get("error")))

        game_info, _ = get_json("http://127.0.0.1:%d/api/info" % MOCK_HTTP)
        self.check("游戏端 /api/info 契约字段",
                   game_info.get("svc") == "epicspark.display"
                   and game_info.get("v") == 1
                   and game_info.get("capture", {}).get("frames", 0) > 0
                   and game_info.get("stream", {}).get("boundary") == "epicsparkframe",
                   "cameras=%s capture.frames=%s"
                   % ([c.get("id") for c in game_info.get("cameras", [])],
                      game_info.get("capture", {}).get("frames")))

        shot = os.path.join(OUT_DIR, "screenshot-auto.png")
        if not self.skip_shots:
            path, err, page = screenshot(base + "/", shot)
            if path:
                ok, detail = image_not_blank(path)
                self.check("页面截图非空白", ok, detail + " · " + os.path.relpath(path, ROOT))
                self.check("页面 UI 处于实时播放", page.get("pill") == "live"
                           and page.get("overlay") is True
                           and (page.get("canvas") or [0])[0] > 0,
                           "pill=%s status=%s fps=%s 分辨率=%s canvas=%s"
                           % (page.get("pill"), page.get("status"), page.get("fps"),
                              page.get("res"), page.get("canvas")))
            else:
                self.check("页面截图", False, err or "未知错误")

        return base, dev

    def case_manual(self, dev):
        log("── 用例 2：--game 手动指定（不依赖 UDP）")
        target = "%s:%d" % (dev.get("ip", "127.0.0.1"), MOCK_HTTP)
        base = self.start_viewer("manual", VIEWER_PORT_MANUAL, ["--game", target])
        found, elapsed = wait_for(
            lambda: (get_json(base + "/api/devices")[0].get("active")),
            15, what="手动设备生效")
        self.check("--game 手动连接", bool(found),
                   "%.1fs 后 active=%s" % (elapsed, found.get("ip") if found else None))
        parts, total, seconds = read_mjpeg(base + "/api/proxy/stream.mjpg", 2.5)
        ok_frames, _sizes, _seqs = validate_jpeg(parts)
        self.check("手动模式拉流", ok_frames >= 12, "%d 帧 / %.1fs" % (ok_frames, seconds))
        kill_process(self.procs.pop("manual"), "manual")

    def case_sweep(self):
        log("── 用例 3：UDP 被拦时子网扫描兜底")
        base = self.start_viewer(
            "sweep", VIEWER_PORT_SWEEP,
            ["--udp-port", str(MOCK_UDP + 100), "--scan-ports", str(MOCK_HTTP)])
        devices, elapsed = wait_for(
            lambda: (get_json(base + "/api/devices")[0].get("devices") or None),
            35, interval=0.5, what="子网扫描发现设备")
        dev = devices[0]
        self.check("子网扫描兜底", dev.get("source") in ("sweep", "manual"),
                   "%.1fs 发现 %s:%s source=%s"
                   % (elapsed, dev.get("ip"), dev.get("http"), dev.get("source")))
        parts, _total, seconds = read_mjpeg(base + "/api/proxy/stream.mjpg", 2.0)
        ok_frames = validate_jpeg(parts)[0]
        self.check("扫描模式拉流", ok_frames >= 8, "%d 帧 / %.1fs" % (ok_frames, seconds))
        kill_process(self.procs.pop("sweep"), "sweep")

    def case_reconnect(self, base):
        log("── 用例 4：游戏端重启后展示端自动恢复")
        kill_process(self.procs.pop("mock"), "mock")
        gone, elapsed = wait_for(
            lambda: get_json(base + "/api/status")[0].get("stream", {}).get("state") != "live",
            20, what="展示端感知断流")
        state = get_json(base + "/api/status")[0].get("stream", {}).get("state")
        self.check("断流后状态切换", bool(gone), "%.1fs 后 state=%s" % (elapsed, state))

        self.start_mock()
        wait_for(lambda: run_http("http://127.0.0.1:%d/api/health" % MOCK_HTTP,
                                  timeout=2)[0] == 200, 30, what="模拟游戏端重启")
        target = get_json(base + "/api/devices")[0].get("active")
        if not target:
            # 设备表可能依赖 beacon，给它一点时间
            time.sleep(3)
            target = get_json(base + "/api/devices")[0].get("active")
        parts = []
        try:
            backup, _total, seconds = read_mjpeg(base + "/api/proxy/stream.mjpg", 6.0)
            ok_frames = validate_jpeg(backup)[0]
            self.check("重启后自动恢复出画面", ok_frames >= 20,
                       "6s 内 %d 帧（目标设备 %s）" % (ok_frames, (target or {}).get("ip")))
        except Exception as exc:  # noqa: BLE001
            self.check("重启后自动恢复出画面", False, repr(exc))

    def run(self):
        ensure_ports_free([MOCK_HTTP, VIEWER_PORT, VIEWER_PORT_MANUAL, VIEWER_PORT_SWEEP])
        os.makedirs(OUT_DIR, exist_ok=True)
        if not os.path.exists(VIEWER):
            raise SystemExit("缺少展示端：%s" % VIEWER)
        if not os.path.exists(MOCK):
            raise SystemExit("缺少模拟游戏端：%s" % MOCK)
        log("输出目录：%s" % OUT_DIR)
        base = dev = None
        try:
            base, dev = self.case_discovery_and_stream()
            self.case_manual(dev)
            self.case_sweep()
            self.case_reconnect(base)
        finally:
            self.cleanup()

        ok = sum(1 for c in self.checks if c["ok"])
        total = len(self.checks)
        report = {
            "time": time.strftime("%Y-%m-%d %H:%M:%S"),
            "ok": ok == total,
            "summary": "%d/%d 项通过" % (ok, total),
            "checks": self.checks,
            "ports": {"mockHttp": MOCK_HTTP, "mockUdp": MOCK_UDP,
                      "viewer": [VIEWER_PORT, VIEWER_PORT_MANUAL, VIEWER_PORT_SWEEP]},
        }
        report_path = os.path.join(OUT_DIR, "report.json")
        with open(report_path, "w", encoding="utf-8") as fh:
            json.dump(report, fh, ensure_ascii=False, indent=2)
        log("结果：%s → %s" % (report["summary"], os.path.relpath(report_path, ROOT)))
        for item in self.checks:
            if not item["ok"]:
                log("失败项：%s · %s" % (item["name"], item["detail"]))
        return 0 if report["ok"] else 1


def main():
    try:
        sys.stdout.reconfigure(errors="replace")
        sys.stderr.reconfigure(errors="replace")
    except Exception:  # noqa: BLE001 - 旧 Python 无 reconfigure
        pass
    ap = argparse.ArgumentParser()
    ap.add_argument("--keep", action="store_true", help="保留后台进程便于排查")
    ap.add_argument("--skip-shots", action="store_true", help="跳过 Edge 截图")
    args = ap.parse_args()
    return Verifier(keep=args.keep, skip_shots=args.skip_shots).run()


if __name__ == "__main__":
    sys.exit(main())
