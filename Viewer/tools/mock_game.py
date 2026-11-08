#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""EpicSpark 游戏端模拟器（无 Unity 环境下的测试替身）。

严格按 Docs/局域网展示端.md 的协议契约实现：
  * UDP 45230：每 2 s 向 255.255.255.255:45230 广播 beacon JSON；
    收到 "EPICSPARK_DISCOVER 1" 探测包时向来源地址单播回复同一份 beacon。
  * HTTP 45231：GET / 、/api/info 、/api/health 、/snapshot.jpg 、/stream.mjpg。
  * /stream.mjpg 为 multipart/x-mixed-replace，每帧带
    Content-Length / X-Seq / X-Frame-Utc-Ms。

画面用 Pillow 动态生成（渐变背景 + 移动方块 + 大字时间戳/帧号 +
"EpicSpark 模拟画面"），仅本工具允许使用 Pillow；展示端本体只用标准库。

用法：
    python Viewer/tools/mock_game.py
    python Viewer/tools/mock_game.py --http 45231 --udp 45230 --fps 15
    python Viewer/tools/mock_game.py --no-udp --name "EpicSpark · 测试机"
"""

import argparse
import datetime
import hashlib
import json
import math
import os
import signal
import socket
import sys
import threading
import time
from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

try:
    from PIL import Image, ImageDraw, ImageFont, ImageOps
except ImportError:  # pragma: no cover - 仅用于提示
    sys.stderr.write("mock_game.py 需要 Pillow：python -m pip install pillow\n")
    raise

SVC = "epicspark.display"
PROTOCOL_VERSION = 1
BOUNDARY = "epicsparkframe"
DEFAULT_HTTP_PORT = 45231
DEFAULT_UDP_PORT = 45230
DEFAULT_FPS = 15
DEFAULT_WIDTH = 960
DEFAULT_HEIGHT = 540
DEFAULT_QUALITY = 75
MAX_STREAM_CLIENTS = 8


# ---------------------------------------------------------------------------
# 小工具
# ---------------------------------------------------------------------------

def now_ms():
    return int(time.time() * 1000)


def utc_iso(ts=None):
    dt = datetime.datetime.fromtimestamp(time.time() if ts is None else ts,
                                        datetime.timezone.utc)
    return dt.isoformat(timespec="milliseconds").replace("+00:00", "Z")


def local_hostname():
    try:
        return socket.gethostname() or "unknown-host"
    except OSError:
        return "unknown-host"


def local_ipv4():
    """尽力获取本机对外 IPv4（不实际发包）。"""
    ip = "127.0.0.1"
    sock = None
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.connect(("8.8.8.8", 80))
        ip = sock.getsockname()[0]
    except OSError:
        pass
    finally:
        if sock is not None:
            try:
                sock.close()
            except OSError:
                pass
    return ip


_FONT_CACHE = {}


def load_font(size):
    """加载支持中文的字体，找不到时退化为 Pillow 默认字体。"""
    key = int(size)
    if key in _FONT_CACHE:
        return _FONT_CACHE[key]
    candidates = [
        "C:/Windows/Fonts/msyh.ttc",
        "C:/Windows/Fonts/msyhbd.ttc",
        "C:/Windows/Fonts/simhei.ttf",
        "C:/Windows/Fonts/simsun.ttc",
        "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/System/Library/Fonts/PingFang.ttc",
    ]
    font = None
    for path in candidates:
        if not os.path.exists(path):
            continue
        try:
            font = ImageFont.truetype(path, key)
            break
        except (OSError, ValueError):
            continue
    if font is None:
        try:
            font = ImageFont.load_default()
        except Exception:  # pragma: no cover
            font = None
    _FONT_CACHE[key] = font
    return font


# ---------------------------------------------------------------------------
# 模拟游戏
# ---------------------------------------------------------------------------

class MockGame(object):
    def __init__(self, args):
        self.args = args
        self.hostname = local_hostname()
        self.device_name = args.name or ("EpicSpark · %s（模拟）" % self.hostname)
        self.device_id = "epi-mock-" + hashlib.sha1(
            (self.hostname + ":" + str(args.http)).encode("utf-8")).hexdigest()[:8]
        self.http_port = int(args.http)
        self.udp_port = int(args.udp)
        self.width = int(args.width)
        self.height = int(args.height)
        self.fps_target = max(1, min(30, int(args.fps)))
        self.quality = max(20, min(95, int(args.quality)))
        self.stage = args.stage
        self.scene = args.scene
        self.camera_id = "main"
        self.camera_name = "主摄像头（玩家视角）"

        self.started_at = time.time()
        self.lock = threading.Lock()
        self.frame_cond = threading.Condition(self.lock)
        self.stop_event = threading.Event()

        self.frame_count = 0
        self.latest_seq = 0
        self.latest_jpeg = None
        self.latest_frame_ts_ms = 0
        self.latest_frame_local_ms = 0
        self.frame_times = deque(maxlen=300)
        self.client_streaming = 0
        self.client_total = 0

        self.http_server = None
        self.udp_sock = None
        self.threads = []

    # -- 画面生成 ---------------------------------------------------------
    def _render_frame(self, seq):
        w, h = self.width, self.height
        t = time.time() - self.started_at

        # 缓慢流动的深色渐变背景
        base = Image.linear_gradient("L").resize((w, h))
        phase = (math.sin(t * 0.35) + 1.0) / 2.0
        black = (8, int(12 + 10 * phase), int(28 + 24 * phase))
        white = (int(38 + 22 * phase), int(24 + 12 * phase), int(92 + 30 * phase))
        img = ImageOps.colorize(base, black=black, white=white).convert("RGBA")

        overlay = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        od = ImageDraw.Draw(overlay)

        # 背景网格
        grid = (255, 255, 255, 18)
        for x in range(0, w, 60):
            od.line([(x, 0), (x, h)], fill=grid, width=1)
        for y in range(0, h, 60):
            od.line([(0, y), (w, y)], fill=grid, width=1)

        # 缓慢移动的光斑
        cx = int(w * (0.5 + 0.28 * math.sin(t * 0.6)))
        cy = int(h * (0.45 + 0.22 * math.cos(t * 0.8)))
        for radius, alpha in ((300, 22), (190, 34), (110, 46)):
            od.ellipse((cx - radius, cy - radius, cx + radius, cy + radius),
                       fill=(70, 150, 255, alpha))

        # 移动方块（模拟场景中的物体）
        colors = [(0, 229, 255, 210), (255, 92, 168, 200), (255, 196, 0, 200)]
        for i in range(3):
            size = 72 + i * 26
            px = int((w + size) * ((t * (0.11 + 0.045 * i) + 0.23 * i) % 1.0)) - size
            py = int(h * 0.5 + math.sin(t * (0.9 + 0.2 * i) + i * 1.7) * h * 0.24)
            od.rounded_rectangle((px, py, px + size, py + size), radius=14,
                                 fill=colors[i], outline=(255, 255, 255, 150), width=2)

        # 扫描线
        scan_y = int((t * 90) % max(1, h))
        od.line([(0, scan_y), (w, scan_y)], fill=(120, 230, 255, 95), width=2)

        img = Image.alpha_composite(img, overlay).convert("RGB")
        draw = ImageDraw.Draw(img)

        title_font = load_font(max(24, int(h * 0.075)))
        mid_font = load_font(max(16, int(h * 0.042)))
        big_font = load_font(max(22, int(h * 0.062)))
        small_font = load_font(max(13, int(h * 0.03)))

        # 顶部信息条
        draw.rectangle((0, 0, w, 64), fill=(5, 9, 18))
        draw.text((24, 18), "EpicSpark 模拟画面", font=title_font,
                  fill=(235, 243, 255))
        draw.text((w - 260, 22), "MOCK GAME · %d×%d" % (w, h),
                  font=small_font, fill=(120, 210, 255))
        draw.line([(0, 64), (w, 64)], fill=(60, 130, 255), width=2)

        # 中央说明
        draw.text((w // 2, h // 2), "EpicSpark 模拟画面", font=big_font,
                  fill=(255, 255, 255), anchor="mm")
        draw.text((w // 2, h // 2 + int(h * 0.088)),
                  "局域网展示端测试 · 主摄像头（玩家视角）",
                  font=mid_font, fill=(150, 210, 255), anchor="mm")

        # 底部时间戳 / 帧号
        draw.rectangle((0, h - 104, w, h), fill=(4, 8, 16))
        ts_text = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S.") + \
            "%03d" % int((time.time() * 1000) % 1000)
        draw.text((24, h - 86), ts_text, font=big_font, fill=(0, 229, 255))
        draw.text((24, h - 34),
                  "FRAME #%06d   SEQ %06d   TARGET %d FPS   STAGE %s"
                  % (seq, seq, self.fps_target, self.stage),
                  font=small_font, fill=(160, 180, 210))

        # 右上角场景
        draw.text((w - 24, 84),
                  "场景：%s" % (self.scene or "标题界面"),
                  font=small_font, fill=(220, 230, 245), anchor="ra")
        draw.text((w - 24, 112),
                  "设备：%s" % self.hostname,
                  font=small_font, fill=(140, 165, 200), anchor="ra")

        # 四角取景框
        margin, seg = 14, 46
        corner = (255, 255, 255)
        for (x, y, dx, dy) in ((margin, margin, 1, 1),
                               (w - margin, margin, -1, 1),
                               (margin, h - margin, 1, -1),
                               (w - margin, h - margin, -1, -1)):
            draw.line([(x, y), (x + dx * seg, y)], fill=corner, width=3)
            draw.line([(x, y), (x, y + dy * seg)], fill=corner, width=3)

        buf = __import__("io").BytesIO()
        img.save(buf, format="JPEG", quality=self.quality)
        return buf.getvalue()

    def capture_loop(self):
        interval = 1.0 / self.fps_target
        next_t = time.time()
        while not self.stop_event.is_set():
            try:
                seq = self.frame_count + 1
                jpeg = self._render_frame(seq)
                ts_ms = now_ms()
                with self.frame_cond:
                    self.frame_count = seq
                    self.latest_seq = seq
                    self.latest_jpeg = jpeg
                    self.latest_frame_ts_ms = ts_ms
                    self.latest_frame_local_ms = now_ms()
                    self.frame_times.append(time.time())
                    self.frame_cond.notify_all()
                self._write_log("采集帧 #%d (%d bytes)" % (seq, len(jpeg)),
                                verbose_only=True)
            except Exception as exc:  # 采集异常不退出
                self._write_log("采集失败：%r" % (exc,))
                time.sleep(0.2)
            next_t += interval
            delay = next_t - time.time()
            if delay > 0:
                self.stop_event.wait(delay)
            else:
                next_t = time.time()

    # -- 状态快照 ---------------------------------------------------------
    def measured_fps(self):
        with self.lock:
            times = list(self.frame_times)
        if len(times) < 2:
            return 0.0
        span = times[-1] - times[0]
        if span <= 0:
            return 0.0
        return round((len(times) - 1) / span, 1)

    def last_frame_age_ms(self):
        with self.lock:
            if not self.latest_frame_local_ms:
                return None
            return max(0, now_ms() - self.latest_frame_local_ms)

    def cameras(self):
        return [{
            "id": self.camera_id,
            "name": self.camera_name,
            "width": self.width,
            "height": self.height,
        }]

    def info_dict(self):
        with self.lock:
            frames = self.frame_count
            streaming = self.client_streaming
            total = self.client_total
        return {
            "svc": SVC,
            "v": PROTOCOL_VERSION,
            "game": "EpicSpark",
            "id": self.device_id,
            "name": self.device_name,
            "host": local_ipv4(),
            "startedAt": utc_iso(self.started_at),
            "uptimeSec": round(time.time() - self.started_at, 1),
            "stage": self.stage,
            "scene": self.scene,
            "ports": {"http": self.http_port, "discovery": self.udp_port},
            "cameras": self.cameras(),
            "capture": {
                "cameraId": self.camera_id,
                "mode": "mock",
                "width": self.width,
                "height": self.height,
                "targetFps": self.fps_target,
                "actualFps": self.measured_fps(),
                "jpegQuality": self.quality,
                "frames": frames,
                "lastFrameAgeMs": self.last_frame_age_ms(),
                "lastError": None,
            },
            "stream": {
                "mjpeg": "/stream.mjpg",
                "snapshot": "/snapshot.jpg",
                "boundary": BOUNDARY,
            },
            "clients": {"streaming": streaming, "total": total},
        }

    def beacon_dict(self):
        return {
            "svc": SVC,
            "v": PROTOCOL_VERSION,
            "id": self.device_id,
            "name": self.device_name,
            "game": "EpicSpark",
            "http": self.http_port,
            "udp": self.udp_port,
            "stage": self.stage,
            "scene": self.scene,
            "camera": {"id": self.camera_id, "name": self.camera_name},
            "cameras": self.cameras(),
            "ts": now_ms(),
        }

    def beacon_json(self):
        return json.dumps(self.beacon_dict(), ensure_ascii=False,
                          separators=(",", ":")).encode("utf-8")

    # -- UDP 发现 ---------------------------------------------------------
    def start_udp(self):
        if self.args.no_udp:
            self._write_log("UDP 发现已禁用（--no-udp）")
            return
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        except OSError:
            pass
        sock.settimeout(0.5)
        try:
            sock.bind(("", self.udp_port))
        except OSError as exc:
            self._write_log("UDP %d 绑定失败：%s（继续仅 HTTP）" %
                            (self.udp_port, exc))
            try:
                sock.close()
            except OSError:
                pass
            return
        self.udp_sock = sock
        thread = threading.Thread(target=self._udp_loop, name="mock-udp",
                                  daemon=True)
        thread.start()
        self.threads.append(thread)
        self._write_log("UDP 发现监听 0.0.0.0:%d，每 2 s 广播 beacon" %
                        self.udp_port)

    def _udp_loop(self):
        last_broadcast = 0.0
        broadcast_target = ("255.255.255.255", self.udp_port)
        while not self.stop_event.is_set():
            now = time.time()
            if now - last_broadcast >= 2.0:
                last_broadcast = now
                try:
                    self.udp_sock.sendto(self.beacon_json(), broadcast_target)
                except OSError as exc:
                    self._write_log("beacon 广播失败：%s" % exc)
            try:
                data, addr = self.udp_sock.recvfrom(2048)
            except socket.timeout:
                continue
            except OSError:
                break
            try:
                text = data.decode("utf-8", "replace").strip()
            except Exception:
                continue
            if text == "EPICSPARK_DISCOVER 1":
                try:
                    self.udp_sock.sendto(self.beacon_json(), addr)
                    self._write_log("应答探测 %s:%d" % (addr[0], addr[1]),
                                    verbose_only=True)
                except OSError:
                    pass
        try:
            self.udp_sock.close()
        except OSError:
            pass

    # -- HTTP -------------------------------------------------------------
    def start_http(self):
        handler = MockRequestHandler
        server = ThreadingHTTPServer(("0.0.0.0", self.http_port), handler)
        server.daemon_threads = True
        server.game = self
        self.http_server = server
        thread = threading.Thread(target=server.serve_forever,
                                  kwargs={"poll_interval": 0.2},
                                  name="mock-http", daemon=True)
        thread.start()
        self.threads.append(thread)
        self._write_log("HTTP 接口 http://%s:%d/ （本机可用 127.0.0.1）" %
                        (local_ipv4(), self.http_port))

    # -- 生命周期 ---------------------------------------------------------
    def start(self):
        self.start_http()
        self.start_udp()
        cap = threading.Thread(target=self.capture_loop, name="mock-capture",
                               daemon=True)
        cap.start()
        self.threads.append(cap)
        self._write_log("设备 ID %s，名称「%s」，%d fps %d×%d" %
                        (self.device_id, self.device_name, self.fps_target,
                         self.width, self.height))
        self._write_log("按 Ctrl+C 退出")

    def stop(self):
        if self.stop_event.is_set():
            return
        self.stop_event.set()
        with self.frame_cond:
            self.frame_cond.notify_all()
        if self.http_server is not None:
            try:
                self.http_server.shutdown()
                self.http_server.server_close()
            except Exception:
                pass
        if self.udp_sock is not None:
            try:
                self.udp_sock.close()
            except OSError:
                pass
        self._write_log("已停止")

    def _write_log(self, message, verbose_only=False):
        if verbose_only and not self.args.verbose:
            return
        stamp = time.strftime("%H:%M:%S")
        sys.stdout.write("[%s] [mock] %s\n" % (stamp, message))
        sys.stdout.flush()


# ---------------------------------------------------------------------------
# HTTP 路由
# ---------------------------------------------------------------------------

class MockRequestHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "EpicSparkMock/1.0"
    sys_version = ""

    def log_message(self, fmt, *args):
        game = getattr(self.server, "game", None)
        if game is not None and game.args.verbose:
            sys.stdout.write("[mock-http] %s - %s\n" % (self.address_string(),
                                                        fmt % args))
            sys.stdout.flush()

    # -- 响应帮助 ---------------------------------------------------------
    def _common_headers(self):
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Cache-Control", "no-store, no-cache")

    def _send_json(self, obj, status=200):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self._common_headers()
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _send_bytes(self, body, content_type, status=200):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self._common_headers()
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _not_found(self):
        self._send_json({"error": "not_found", "path": self.path}, status=404)

    # -- 方法 -------------------------------------------------------------
    def do_OPTIONS(self):
        self.send_response(204)
        self._common_headers()
        self.send_header("Access-Control-Allow-Methods", "GET, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")
        self.send_header("Content-Length", "0")
        self.end_headers()

    def do_GET(self):
        path = self.path.split("?", 1)[0].split("#", 1)[0]
        game = self.server.game
        try:
            if path == "/":
                self._send_bytes(self._self_test_page(), "text/html; charset=utf-8")
            elif path == "/api/info":
                self._send_json(game.info_dict())
            elif path == "/api/health":
                self._send_json({"ok": True, "svc": SVC, "v": PROTOCOL_VERSION})
            elif path == "/snapshot.jpg":
                self._send_snapshot()
            elif path in ("/stream.mjpg", "/stream"):
                self._serve_stream()
            else:
                self._not_found()
        except (BrokenPipeError, ConnectionResetError):
            self.close_connection = True
        except Exception as exc:
            game._write_log("HTTP 处理异常 %s：%r" % (path, exc))
            try:
                self._send_json({"error": "internal", "message": repr(exc)}, 500)
            except Exception:
                pass

    # -- 各接口 -----------------------------------------------------------
    def _send_snapshot(self):
        game = self.server.game
        with game.lock:
            jpeg = game.latest_jpeg
        if not jpeg:
            self._send_json({"error": "no_frame"}, status=503)
            return
        self._send_bytes(jpeg, "image/jpeg")

    def _serve_stream(self):
        game = self.server.game
        with game.lock:
            if game.client_streaming >= MAX_STREAM_CLIENTS:
                too_many = True
            else:
                game.client_streaming += 1
                game.client_total += 1
                too_many = False
        if too_many:
            self._send_json({"error": "too_many_streams",
                             "limit": MAX_STREAM_CLIENTS}, status=503)
            return

        self.send_response(200)
        self.send_header("Content-Type",
                         "multipart/x-mixed-replace; boundary=%s" % BOUNDARY)
        self._common_headers()
        self.send_header("Pragma", "no-cache")
        self.send_header("Connection", "close")
        self.end_headers()
        self.close_connection = True

        last_seq = 0
        try:
            while not game.stop_event.is_set():
                with game.frame_cond:
                    if game.latest_seq <= last_seq:
                        game.frame_cond.wait(0.5)
                    if game.stop_event.is_set():
                        break
                    if game.latest_seq <= last_seq:
                        continue
                    seq = game.latest_seq
                    jpeg = game.latest_jpeg
                    ts_ms = game.latest_frame_ts_ms
                if jpeg is None:
                    continue
                header = (
                    "--" + BOUNDARY + "\r\n"
                    "Content-Type: image/jpeg\r\n"
                    "Content-Length: " + str(len(jpeg)) + "\r\n"
                    "X-Seq: " + str(seq) + "\r\n"
                    "X-Frame-Utc-Ms: " + str(ts_ms) + "\r\n"
                    "\r\n"
                ).encode("ascii")
                self.wfile.write(header)
                self.wfile.write(jpeg)
                self.wfile.write(b"\r\n")
                self.wfile.flush()
                last_seq = seq
        except (BrokenPipeError, ConnectionResetError, OSError):
            pass
        finally:
            with game.lock:
                game.client_streaming = max(0, game.client_streaming - 1)
            self.close_connection = True

    def _self_test_page(self):
        return (
            "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">"
            "<title>EpicSpark 游戏端自检</title>"
            "<style>body{background:#0a0e14;color:#e6edf7;font-family:system-ui;"
            "margin:0;padding:24px}img{max-width:100%;border-radius:8px;"
            "background:#000}code{color:#7fd7ff}.row{margin:8px 0}</style></head>"
            "<body><h2>EpicSpark 游戏端自检页（模拟）</h2>"
            "<div class=\"row\">设备：<span id=\"name\">—</span> · 摄像头："
            "<span id=\"cam\">—</span> · FPS：<span id=\"fps\">—</span> · "
            "帧数：<span id=\"frames\">—</span></div>"
            "<img src=\"/stream.mjpg\" alt=\"stream\">"
            "<p><code>GET /api/info</code> · <code>GET /snapshot.jpg</code> · "
            "<code>GET /stream.mjpg</code></p>"
            "<script>setInterval(async()=>{try{const r=await fetch('/api/info');"
            "const j=await r.json();document.getElementById('name').textContent=j.name;"
            "document.getElementById('cam').textContent=(j.cameras[0]||{}).name||'-';"
            "document.getElementById('fps').textContent=(j.capture.actualFps||0)+' / '+"
            "j.capture.targetFps;document.getElementById('frames').textContent="
            "j.capture.frames;}catch(e){}},1000);</script></body></html>"
        ).encode("utf-8")


# ---------------------------------------------------------------------------
# 入口
# ---------------------------------------------------------------------------

def parse_args(argv=None):
    parser = argparse.ArgumentParser(
        description="EpicSpark 游戏端模拟器（UDP 发现 + HTTP MJPEG，Pillow 动态画面）")
    parser.add_argument("--http", type=int, default=DEFAULT_HTTP_PORT,
                        help="HTTP 端口（默认 45231）")
    parser.add_argument("--udp", type=int, default=DEFAULT_UDP_PORT,
                        help="发现 UDP 端口（默认 45230）")
    parser.add_argument("--fps", type=int, default=DEFAULT_FPS,
                        help="采集帧率 1-30（默认 15）")
    parser.add_argument("--no-udp", action="store_true",
                        help="禁用 UDP beacon（用于验证展示端子网扫描兜底）")
    parser.add_argument("--name", default=None,
                        help="设备显示名（默认 EpicSpark · <机器名>（模拟））")
    parser.add_argument("--stage", default="playing",
                        help="模拟游戏阶段（title/generating/playing/paused）")
    parser.add_argument("--scene", default="法国大革命",
                        help="模拟当前场景名")
    parser.add_argument("--width", type=int, default=DEFAULT_WIDTH,
                        help="画面宽（默认 960）")
    parser.add_argument("--height", type=int, default=DEFAULT_HEIGHT,
                        help="画面高（默认 540）")
    parser.add_argument("--quality", type=int, default=DEFAULT_QUALITY,
                        help="JPEG 质量 20-95（默认 75）")
    parser.add_argument("--verbose", action="store_true", help="打印详细日志")
    return parser.parse_args(argv)


def main(argv=None):
    args = parse_args(argv)
    game = MockGame(args)

    def _on_sigterm(signum, frame):
        raise KeyboardInterrupt

    try:
        signal.signal(signal.SIGTERM, _on_sigterm)
    except (ValueError, OSError, AttributeError):
        pass

    try:
        game.start()
        while True:
            time.sleep(0.4)
    except KeyboardInterrupt:
        sys.stdout.write("\n")
        game._write_log("收到退出信号，正在清理…")
    finally:
        game.stop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
