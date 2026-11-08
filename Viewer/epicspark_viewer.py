#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""EpicSpark 局域网展示端。

和游戏本体（Unity EpicSpark）在同一局域网内时：
  1. 监听 UDP 45230 的 beacon（并主动广播探测），自动发现游戏；
  2. 广播被防火墙拦住时，自动扫描本机所在 /24 网段的 HTTP 45231；
  3. 起一个本地网页（默认 http://127.0.0.1:8686），把游戏的 MJPEG 画面代理给浏览器，
     自动连接、断线重连、全屏显示。

协议见 Docs/局域网展示端.md。本文件只依赖 Python 标准库（3.8+）。

用法：
    python Viewer/epicspark_viewer.py                       # 自动发现 + 打开浏览器
    python Viewer/epicspark_viewer.py --game 192.168.1.20:45231
    python Viewer/epicspark_viewer.py --no-browser          # 投屏/Kiosk
    python Viewer/epicspark_viewer.py --list                # 只发现并打印设备
"""

import argparse
import json
import os
import re
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

APP_DIR = os.path.dirname(os.path.abspath(__file__))
WEB_DIR = os.path.join(APP_DIR, "web")

SVC = "epicspark.display"
PROTOCOL_VERSION = 1
PROBE_PAYLOAD = b"EPICSPARK_DISCOVER 1"
DEFAULT_UDP_PORT = 45230
DEFAULT_GAME_HTTP_PORT = 45231
DEFAULT_LOCAL_PORT = 8686
DEFAULT_SCAN_PORTS = [DEFAULT_GAME_HTTP_PORT]
STALE_AFTER_SEC = 12.0          # 多久没收到 beacon 视为“可能掉线”
STREAM_CLIENT_TIMEOUT = 15.0    # 本地浏览器连接最长空闲
SOURCE_IDLE_TIMEOUT = 8.0       # 游戏端多久没新帧就断开重连
MAX_STREAM_CLIENTS = 8


def now_ms():
    return int(time.time() * 1000)


def log(message):
    stamp = time.strftime("%H:%M:%S")
    sys.stdout.write("[%s] %s\n" % (stamp, message))
    sys.stdout.flush()


def log_err(message):
    stamp = time.strftime("%H:%M:%S")
    sys.stderr.write("[%s] %s\n" % (stamp, message))
    sys.stderr.flush()


# 局域网/本机请求绝不走系统代理（Windows 的 IE/registry 代理会让 127.0.0.1 请求直接 502）
_OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


# ---------------------------------------------------------------------------
# 本机地址 / 子网
# ---------------------------------------------------------------------------

def local_ipv4_addresses():
    """尽力枚举本机所有 IPv4（排除回环/链路本地）。"""
    ips = set()

    try:
        for ip in socket.gethostbyname_ex(socket.gethostname())[2]:
            ips.add(ip)
    except OSError:
        pass

    # 默认路由出口（不会真正发 UDP 包）
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            sock.connect(("8.8.8.8", 80))
            ips.add(sock.getsockname()[0])
        finally:
            sock.close()
    except OSError:
        pass

    # Windows 兜底：解析 ipconfig（gethostbyname 有时只返回一个网卡）
    if os.name == "nt":
        try:
            completed = subprocess.run(
                ["ipconfig"], capture_output=True, timeout=6,
                encoding="gbk", errors="replace")
            for match in re.finditer(r"IPv4[^:：]*[:：]\s*([0-9]{1,3}(?:\.[0-9]{1,3}){3})",
                                     completed.stdout or ""):
                ips.add(match.group(1))
        except (OSError, subprocess.SubprocessError):
            pass

    result = []
    for ip in ips:
        if ip.startswith("127.") or ip.startswith("169.254.") or ip == "0.0.0.0":
            continue
        result.append(ip)
    return sorted(result)


def subnet_hosts(ips, limit=254):
    """把本机地址映射为需扫描的候选主机（同 /24 网段 + 本机地址 + 回环）。

    本机地址也要扫：游戏可能就跑在展示端这台机器上（Unity 编辑器 Play 调试）。
    """
    candidates = set(ips)
    candidates.add("127.0.0.1")
    for ip in ips:
        parts = ip.split(".")
        if len(parts) != 4:
            continue
        prefix = ".".join(parts[:3])
        for host in range(1, min(limit, 255)):
            candidates.add("%s.%d" % (prefix, host))
    return sorted(candidates)


# ---------------------------------------------------------------------------
# 设备发现
# ---------------------------------------------------------------------------

class Device(object):
    __slots__ = ("key", "ip", "http_port", "udp_port", "device_id", "name",
                 "stage", "scene", "camera", "cameras", "source", "last_seen",
                 "beacon_ts", "manual")

    def __init__(self, ip, http_port, source):
        self.ip = ip
        self.http_port = int(http_port)
        self.key = "%s:%d" % (self.ip, self.http_port)
        self.udp_port = 0
        self.device_id = ""
        self.name = ip
        self.stage = ""
        self.scene = ""
        self.camera = None
        self.cameras = []
        self.source = source
        self.last_seen = time.time()
        self.beacon_ts = 0
        self.manual = (source == "manual")

    def update_from_beacon(self, data, source=None):
        if not isinstance(data, dict):
            return
        self.device_id = str(data.get("id") or self.device_id or self.key)
        self.name = str(data.get("name") or self.name)
        self.stage = str(data.get("stage") or self.stage)
        self.scene = str(data.get("scene") or "")
        camera = data.get("camera")
        if isinstance(camera, dict) and camera:
            self.camera = camera
        cameras = data.get("cameras")
        if isinstance(cameras, list):
            self.cameras = [c for c in cameras if isinstance(c, dict)]
        try:
            self.http_port = int(data.get("http") or self.http_port)
        except (TypeError, ValueError):
            pass
        try:
            self.udp_port = int(data.get("udp") or self.udp_port)
        except (TypeError, ValueError):
            pass
        try:
            self.beacon_ts = int(data.get("ts") or self.beacon_ts)
        except (TypeError, ValueError):
            pass
        self.last_seen = time.time()
        if source:
            self.source = source

    def snapshot(self):
        return {
            "svc": SVC,
            "v": PROTOCOL_VERSION,
            "id": self.device_id or self.key,
            "name": self.name,
            "ip": self.ip,
            "http": self.http_port,
            "httpPort": self.http_port,
            "udp": self.udp_port,
            "stage": self.stage,
            "scene": self.scene,
            "camera": self.camera,
            "cameras": self.cameras,
            "source": self.source,
            "manual": self.manual,
            "lastSeenMs": int(max(0.0, time.time() - self.last_seen) * 1000),
            "stale": (time.time() - self.last_seen) > STALE_AFTER_SEC,
            "key": self.key,
        }


def http_get_json(host, port, path, timeout=1.0):
    url = "http://%s:%d%s" % (host, int(port), path)
    req = urllib.request.Request(url, headers={"Connection": "close"})
    with _OPENER.open(req, timeout=timeout) as resp:
        body = resp.read(65536)
    return json.loads(body.decode("utf-8", "replace"))


def http_get_bytes(host, port, path, timeout=3.0):
    url = "http://%s:%d%s" % (host, int(port), path)
    req = urllib.request.Request(url, headers={"Connection": "close"})
    with _OPENER.open(req, timeout=timeout) as resp:
        return resp.status, resp.headers.get("Content-Type", "image/jpeg"), resp.read()


class Discovery(object):
    """UDP beacon 监听 + 主动探测 + 子网扫描 + 活跃设备管理。"""

    def __init__(self, udp_port, scan_ports, manual_target=None, no_sweep=False):
        self.udp_port = int(udp_port)
        self.scan_ports = list(scan_ports) or list(DEFAULT_SCAN_PORTS)
        self.manual_target = manual_target        # (ip, port) 或 None
        self.no_sweep = no_sweep
        self.lock = threading.RLock()
        self.devices = {}                          # key -> Device
        self.active_key = None
        self.stop_event = threading.Event()
        self.udp_ready = False
        self.sweeping = False
        self.last_sweep = 0.0
        self.last_sweep_found = 0
        self.threads = []
        self._udp_sock = None

    # -- 生命周期 ---------------------------------------------------------
    def start(self):
        if self.manual_target:
            ip, port = self.manual_target
            self._remember_manual(ip, port)
        self._start_probe_socket()
        self.threads.append(threading.Thread(target=self._probe_loop, name="probe",
                                             daemon=True))
        self.threads[-1].start()
        if not self.no_sweep and not self.manual_target:
            self.threads.append(threading.Thread(target=self._sweep_loop, name="sweep",
                                                 daemon=True))
            self.threads[-1].start()
        self.threads.append(threading.Thread(target=self._supervise_loop,
                                             name="supervise", daemon=True))
        self.threads[-1].start()

    def stop(self):
        self.stop_event.set()
        sock = self._udp_sock
        if sock is not None:
            try:
                sock.close()
            except OSError:
                pass

    # -- UDP --------------------------------------------------------------
    def _start_probe_socket(self):
        """展示端不绑定固定发现端口，只用临时端口发探测、收游戏端的单播回复。

        原因：Windows 上多个进程用 SO_REUSEADDR 绑定同一个 UDP 端口会互相抢包，
        展示端如果绑 45230 会把同机游戏端的发现监听挤掉；探测-回复模型不需要绑定固定端口。
        """
        try:
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            try:
                sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
            except OSError:
                pass
            sock.settimeout(0.4)
            self._udp_sock = sock
            self.udp_ready = True
            log("UDP 发现探测已启动（每 2s 广播 EPICSPARK_DISCOVER，端口 %d）" % self.udp_port)
        except OSError as exc:
            self.udp_ready = False
            log_err("UDP 探测 socket 创建失败：%s（将依赖子网扫描）" % exc)

    def _probe_loop(self):
        """用固定源端口（临时端口）发探测，游戏端会单播回复 beacon 到这个 socket。

        注意：不能每次新建 socket 发完就关，否则游戏端单播回复会发到已关闭的端口。
        """
        sock = self._udp_sock
        if sock is None:
            return
        targets = [("255.255.255.255", self.udp_port)]
        # 同一台机器上的游戏端（编辑器 Play）也要能发现
        targets.append(("127.0.0.1", self.udp_port))
        try:
            while not self.stop_event.is_set():
                for target in targets:
                    try:
                        sock.sendto(PROBE_PAYLOAD, target)
                    except OSError:
                        pass
                deadline = time.time() + 1.2
                while time.time() < deadline and not self.stop_event.is_set():
                    try:
                        data, addr = sock.recvfrom(4096)
                    except socket.timeout:
                        break
                    except OSError:
                        break
                    self._handle_datagram(data, addr)
                self.stop_event.wait(max(0.0, 2.0 - 1.2))
        finally:
            try:
                sock.close()
            except OSError:
                pass

    def _handle_datagram(self, data, addr):
        try:
            payload = json.loads(data.decode("utf-8", "replace"))
        except (UnicodeDecodeError, ValueError):
            return
        if not isinstance(payload, dict) or payload.get("svc") != SVC:
            return
        try:
            port = int(payload.get("http") or DEFAULT_GAME_HTTP_PORT)
        except (TypeError, ValueError):
            return
        device = self._upsert(addr[0], port, payload, source="udp")
        if device is not None:
            self._maybe_auto_activate(device)

    # -- 子网扫描 ---------------------------------------------------------
    def _sweep_loop(self):
        # 先给 UDP 广播 5 秒机会，避免刚启动就无谓扫全网段
        self.stop_event.wait(5.0)
        while not self.stop_event.is_set():
            with self.lock:
                device_count = len(self.devices)
            if device_count == 0:
                self._sweep_once()
            self.stop_event.wait(15.0)

    def _sweep_once(self):
        ips = local_ipv4_addresses()
        hosts = subnet_hosts(ips)
        if not hosts:
            return
        self.sweeping = True
        self.last_sweep = time.time()
        found = 0
        log("未发现设备，开始扫描 %d 个地址（端口 %s）…"
            % (len(hosts), ",".join(str(p) for p in self.scan_ports)))
        try:
            from concurrent.futures import ThreadPoolExecutor, as_completed
            jobs = []
            with ThreadPoolExecutor(max_workers=64) as pool:
                for ip in hosts:
                    for port in self.scan_ports:
                        jobs.append(pool.submit(self._probe_host, ip, port))
                for future in as_completed(jobs):
                    try:
                        device = future.result()
                    except Exception:  # noqa: BLE001 - 扫描失败忽略
                        continue
                    if device is not None:
                        found += 1
                        self._maybe_auto_activate(device)
        except Exception as exc:  # noqa: BLE001
            log_err("扫描异常：%s" % exc)
        finally:
            self.sweeping = False
            self.last_sweep_found = found
        if found:
            log("扫描发现 %d 台游戏设备" % found)

    def _probe_host(self, ip, port):
        try:
            health = http_get_json(ip, port, "/api/health", timeout=0.6)
        except (urllib.error.URLError, OSError, ValueError, json.JSONDecodeError):
            return None
        if not isinstance(health, dict) or health.get("svc") != SVC:
            return None
        try:
            info = http_get_json(ip, port, "/api/info", timeout=1.5)
        except (urllib.error.URLError, OSError, ValueError, json.JSONDecodeError):
            info = {"svc": SVC}
        return self._upsert(ip, port, info, source="sweep")

    # -- 设备表 -----------------------------------------------------------
    @staticmethod
    def _is_loopback(ip):
        return (ip or "").startswith("127.")

    def _upsert(self, ip, port, payload, source):
        try:
            port = int(port)
        except (TypeError, ValueError):
            return None
        key = "%s:%d" % (ip, port)
        with self.lock:
            device = self.devices.get(key)
            incoming_id = str(payload.get("id") or "")
            if device is None and incoming_id:
                # 同一台设备可能同时从局域网 IP 与 127.0.0.1 被探测到：按设备 ID 去重，
                # 优先保留非回环地址（真实局域网地址）。
                for other_key, other in list(self.devices.items()):
                    if other.device_id != incoming_id:
                        continue
                    if self._is_loopback(ip) and not self._is_loopback(other.ip):
                        other.update_from_beacon(payload)
                        return other
                    if not self._is_loopback(ip) and self._is_loopback(other.ip):
                        del self.devices[other_key]
                        if self.active_key == other_key:
                            self.active_key = key
                        device = Device(ip, port, source)
                        self.devices[key] = device
                        break
                    other.update_from_beacon(payload)
                    return other
            if device is None:
                device = Device(ip, port, source)
                self.devices[key] = device
                log("发现游戏：%s（%s，%s:%d）"
                    % (payload.get("name") or ip, source, ip, port))
            device.update_from_beacon(payload, source=source if device.source != "manual"
                                      else None)
            if device.name == ip and payload.get("name"):
                device.name = str(payload.get("name"))
        return device

    def _remember_manual(self, ip, port):
        device = Device(ip, port, "manual")
        try:
            info = http_get_json(ip, port, "/api/info", timeout=2.0)
            device.update_from_beacon(info, source=None)
            device.name = str(info.get("name") or ip)
        except Exception:  # noqa: BLE001 - 设备可能还没启动
            pass
        with self.lock:
            self.devices[device.key] = device
            self.active_key = device.key
        log("手动指定游戏：%s:%d" % (ip, port))

    def _maybe_auto_activate(self, device):
        with self.lock:
            if self.active_key is not None:
                return
            if self.manual_target is not None:
                return
            self.active_key = device.key
            log("自动连接：%s（%s）" % (device.name, device.key))

    def _supervise_loop(self):
        """活跃设备失效时尝试切换到其它在线设备。"""
        while not self.stop_event.is_set():
            self.stop_event.wait(3.0)
            if self.stop_event.is_set():
                break
            with self.lock:
                active = self.devices.get(self.active_key) if self.active_key else None
                if active is not None and (time.time() - active.last_seen) < STALE_AFTER_SEC:
                    continue
                if self.manual_target is not None:
                    continue
                fresh = [d for d in self.devices.values()
                         if (time.time() - d.last_seen) < STALE_AFTER_SEC
                         and d.key != (self.active_key or "")]
                if fresh:
                    fresh.sort(key=lambda d: d.last_seen, reverse=True)
                    self.active_key = fresh[0].key
                    log("切换到在线设备：%s（%s）" % (fresh[0].name, fresh[0].key))

    # -- 对外 -------------------------------------------------------------
    def select(self, ip, port=None):
        port = int(port or DEFAULT_GAME_HTTP_PORT)
        key = "%s:%d" % (ip, port)
        with self.lock:
            device = self.devices.get(key)
        if device is None:
            device = self._upsert(ip, port, {"svc": SVC, "name": ip}, source="manual")
        with self.lock:
            self.active_key = device.key
        log("切换展示目标：%s:%d" % (ip, port))
        return device.snapshot()

    def rescan(self):
        with self.lock:
            self.devices = {k: v for k, v in self.devices.items() if v.manual}
            self.active_key = None
        self.sweeping = False
        self.last_sweep = 0.0
        try:
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
            sock.sendto(PROBE_PAYLOAD, ("255.255.255.255", self.udp_port))
            sock.sendto(PROBE_PAYLOAD, ("127.0.0.1", self.udp_port))
            sock.close()
        except OSError:
            pass
        log("手动重新扫描：已清空设备表，重新广播探测")
        return {"ok": True}

    def active(self):
        with self.lock:
            if not self.active_key:
                return None
            device = self.devices.get(self.active_key)
            return device.snapshot() if device else None

    def snapshot(self):
        with self.lock:
            devices = sorted(self.devices.values(),
                             key=lambda d: (d.key != self.active_key, -d.last_seen))
            active = None
            if self.active_key and self.active_key in self.devices:
                active = self.devices[self.active_key].snapshot()
            return {
                "devices": [d.snapshot() for d in devices],
                "active": active,
                "discovery": {
                    "udp": self.udp_ready,
                    "sweep": (not self.no_sweep and self.manual_target is None),
                    "sweeping": self.sweeping,
                    "lastSweepMs": int(max(0.0, time.time() - self.last_sweep) * 1000) if self.last_sweep else None,
                    "lastSweepFound": self.last_sweep_found,
                    "scanPorts": self.scan_ports,
                    "udpPort": self.udp_port,
                    "manual": ("%s:%d" % self.manual_target) if self.manual_target else None,
                },
            }


# ---------------------------------------------------------------------------
# 流统计 / MJPEG 代理
# ---------------------------------------------------------------------------

class StreamStats(object):
    def __init__(self):
        self.lock = threading.Lock()
        self.reset()

    def reset(self, state="idle", error=None):
        with self.lock:
            self.state = state
            self.error = error
            self.frames = 0
            self.bytes = 0
            self.seq = 0
            self.latency_ms = None
            self.resolution = None
            self.device_key = None
            self.started_at = time.time()
            self.last_frame_at = 0.0
            self._times = []

    def begin(self, device_key):
        self.reset(state="connecting")
        with self.lock:
            self.device_key = device_key

    def fail(self, message):
        with self.lock:
            self.state = "error"
            self.error = message

    def record(self, size, seq=None, frame_utc_ms=None):
        now = time.time()
        with self.lock:
            self.state = "live"
            self.error = None
            self.frames += 1
            self.bytes += int(size)
            self.last_frame_at = now
            if seq is not None:
                try:
                    self.seq = max(self.seq, int(seq))
                except (TypeError, ValueError):
                    pass
            if frame_utc_ms:
                try:
                    delta = now_ms() - int(frame_utc_ms)
                except (TypeError, ValueError):
                    delta = None
                if delta is not None and -5000 < delta < 120000:
                    self.latency_ms = int(delta)
            self._times.append(now)
            if len(self._times) > 90:
                self._times = self._times[-90:]

    def set_resolution(self, width, height):
        with self.lock:
            self.resolution = [int(width), int(height)]

    def snapshot(self):
        with self.lock:
            fps = 0.0
            if len(self._times) >= 2:
                span = self._times[-1] - self._times[0]
                if span > 0.2:
                    fps = (len(self._times) - 1) / span
            age = max(0.0, time.time() - self.last_frame_at) if self.last_frame_at else None
            # 没有代理连接时不会有人来改状态：超过 4 秒没新帧就不算 live
            state = self.state
            error = self.error
            if state == "live" and age is not None and age > 4.0:
                state = "connecting"
                error = error or "画面超时（超过 4 秒未收到新帧）"
            return {
                "state": state,
                "error": error,
                "fps": round(fps, 1),
                "frames": self.frames,
                "bytes": self.bytes,
                "seq": self.seq,
                "latencyMs": self.latency_ms,
                "resolution": self.resolution,
                "device": self.device_key,
                "lastFrameAgeMs": int(age * 1000) if age is not None else None,
                "uptimeSec": round(time.time() - self.started_at, 1),
            }

    def is_live(self):
        with self.lock:
            return self.state == "live" and (time.time() - self.last_frame_at) < 3.0


def _parse_mjpeg_boundary(content_type):
    match = re.search(r"boundary=([^;]+)", content_type or "")
    if match:
        return match.group(1).strip().strip('"')
    return "epicsparkframe"


def _parse_headers(raw):
    headers = {}
    for line in raw.split(b"\r\n"):
        if not line or b":" not in line:
            continue
        name, value = line.split(b":", 1)
        headers[name.strip().lower().decode("latin-1")] = value.strip().decode("latin-1")
    return headers


def open_game_stream(device, timeout=8.0):
    url = "http://%s:%d/stream.mjpg" % (device["ip"], int(device["http"]))
    req = urllib.request.Request(url, headers={"Connection": "close"})
    return _OPENER.open(req, timeout=timeout)


# ---------------------------------------------------------------------------
# 本地 Web 服务
# ---------------------------------------------------------------------------

class ViewerServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, addr, handler, discovery, stats):
        super().__init__(addr, handler)
        self.discovery = discovery
        self.stats = stats
        self.started_at = time.time()


class ViewerHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "EpicSparkDisplay/1.0"
    sys_version = ""
    verbose = False

    # -- 基础 -------------------------------------------------------------
    def log_message(self, fmt, *args):
        if self.verbose:
            sys.stdout.write("[web] %s - %s\n" % (self.address_string(), fmt % args))
            sys.stdout.flush()

    def _send_json(self, obj, status=200):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store, no-cache")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _send_text(self, text, status=200, content_type="text/plain; charset=utf-8"):
        body = text.encode("utf-8") if isinstance(text, str) else text
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Cache-Control", "no-store, no-cache")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _send_file(self, relative_path, content_type):
        safe = os.path.normpath(os.path.join(WEB_DIR, relative_path))
        if not safe.startswith(os.path.normpath(WEB_DIR) + os.sep):
            self._send_json({"error": "forbidden"}, 403)
            return
        try:
            with open(safe, "rb") as fh:
                body = fh.read()
        except OSError:
            self._send_json({"error": "not_found", "path": self.path}, 404)
            return
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    # -- 方法 -------------------------------------------------------------
    def do_OPTIONS(self):
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")
        self.send_header("Content-Length", "0")
        self.end_headers()

    def do_POST(self):
        path = urlparse(self.path).path
        if path == "/api/select":
            self._handle_select()
        elif path == "/api/rescan":
            self.server.discovery.rescan()
            self._send_json({"ok": True})
        else:
            self._send_json({"error": "not_found", "path": path}, 404)

    def do_GET(self):
        path = urlparse(self.path).path
        try:
            if path == "/" or path == "/index.html":
                self._send_file("index.html", "text/html; charset=utf-8")
            elif path == "/app.js":
                self._send_file("app.js", "application/javascript; charset=utf-8")
            elif path == "/style.css":
                self._send_file("style.css", "text/css; charset=utf-8")
            elif path == "/favicon.ico":
                self._send_file("favicon.ico", "image/x-icon")
            elif path == "/api/devices":
                self._send_json(self.server.discovery.snapshot())
            elif path == "/api/status":
                self._send_json(self._status())
            elif path == "/api/proxy/stream.mjpg":
                self._proxy_stream()
            elif path == "/api/proxy/snapshot.jpg":
                self._proxy_snapshot()
            else:
                self._send_json({"error": "not_found", "path": path}, 404)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            self.close_connection = True
        except Exception as exc:  # noqa: BLE001 - 任何异常都不能带崩服务
            log_err("处理 %s 异常：%r" % (path, exc))
            try:
                self._send_json({"error": "internal", "message": repr(exc)}, 500)
            except Exception:  # noqa: BLE001
                self.close_connection = True

    # -- 各接口 -----------------------------------------------------------
    def _status(self):
        discovery = self.server.discovery
        stats = self.server.stats
        active = discovery.active()
        payload = discovery.snapshot()
        return {
            "ok": True,
            "svc": SVC,
            "v": PROTOCOL_VERSION,
            "server": {
                "port": self.server.server_address[1],
                "uptimeSec": round(time.time() - self.server.started_at, 1),
                "version": self.server_version,
            },
            "active": active,
            "stream": stats.snapshot(),
            "discovery": payload["discovery"],
            "deviceCount": len(payload["devices"]),
        }

    def _read_body(self, limit=65536):
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            length = 0
        if length <= 0:
            return {}
        data = self.rfile.read(min(length, limit))
        try:
            return json.loads(data.decode("utf-8", "replace"))
        except ValueError:
            return {}

    def _handle_select(self):
        body = self._read_body()
        ip = str(body.get("ip") or "").strip()
        port = body.get("httpPort") or body.get("http") or DEFAULT_GAME_HTTP_PORT
        if not ip:
            self._send_json({"ok": False, "error": "missing_ip"}, 400)
            return
        try:
            port = int(port)
        except (TypeError, ValueError):
            port = DEFAULT_GAME_HTTP_PORT
        device = self.server.discovery.select(ip, port)
        self.server.stats.begin(device["key"])
        self._send_json({"ok": True, "active": device})

    def _proxy_stream(self):
        discovery = self.server.discovery
        stats = self.server.stats
        device = discovery.active()
        if not device:
            self._send_json({"error": "no_device"}, 503)
            return

        if stats.snapshot()["state"] != "live" or stats.snapshot()["device"] != device["key"]:
            stats.begin(device["key"])

        try:
            source = open_game_stream(device)
        except (urllib.error.URLError, OSError) as exc:
            stats.fail("连接游戏失败：%s" % exc)
            self._send_json({"error": "connect_failed", "message": str(exc)}, 502)
            return

        boundary = _parse_mjpeg_boundary(source.headers.get("Content-Type"))
        marker = ("--" + boundary).encode("latin-1")

        self.send_response(200)
        self.send_header("Content-Type", "multipart/x-mixed-replace; boundary=%s" % boundary)
        self.send_header("Cache-Control", "no-store, no-cache, must-revalidate")
        self.send_header("Pragma", "no-cache")
        self.send_header("Connection", "close")
        self.end_headers()
        self.close_connection = True
        self.wfile.flush()

        buffer = b""
        last_frame_at = time.time()
        try:
            while True:
                try:
                    chunk = source.read(8192)
                except (socket.timeout, TimeoutError):
                    chunk = b""
                if not chunk:
                    break
                buffer += chunk
                if len(buffer) > 8 * 1024 * 1024:      # 防御异常数据流
                    buffer = buffer[-1024 * 1024:]
                while True:
                    idx = buffer.find(marker)
                    if idx < 0:
                        break
                    header_end = buffer.find(b"\r\n\r\n", idx)
                    if header_end < 0:
                        break
                    headers = _parse_headers(buffer[idx + len(marker):header_end])
                    try:
                        length = int(headers.get("content-length", "0"))
                    except ValueError:
                        length = 0
                    if length <= 0:
                        buffer = buffer[header_end + 4:]
                        continue
                    body_start = header_end + 4
                    if len(buffer) < body_start + length:
                        break
                    jpeg = buffer[body_start:body_start + length]
                    buffer = buffer[body_start + length:]

                    seq = headers.get("x-seq")
                    frame_utc = headers.get("x-frame-utc-ms")
                    part = (
                        b"--" + marker[2:] + b"\r\n"
                        b"Content-Type: image/jpeg\r\n"
                        b"Content-Length: " + str(len(jpeg)).encode("ascii") + b"\r\n"
                    )
                    if seq:
                        part += b"X-Seq: " + seq.encode("latin-1", "replace") + b"\r\n"
                    if frame_utc:
                        part += b"X-Frame-Utc-Ms: " + frame_utc.encode("latin-1", "replace") + b"\r\n"
                    part += b"\r\n" + jpeg + b"\r\n"
                    self.wfile.write(part)
                    self.wfile.flush()
                    stats.record(len(jpeg), seq=seq, frame_utc_ms=frame_utc)
                    last_frame_at = time.time()

                    if not stats.snapshot()["resolution"]:
                        resolution = self._jpeg_size(jpeg)
                        if resolution:
                            stats.set_resolution(*resolution)

                if time.time() - last_frame_at > SOURCE_IDLE_TIMEOUT:
                    stats.fail("游戏端 %.0f 秒没有新画面" % SOURCE_IDLE_TIMEOUT)
                    break
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass
        except OSError as exc:
            stats.fail("画面流中断：%s" % exc)
        finally:
            try:
                source.close()
            except OSError:
                pass
            self.close_connection = True
            with stats.lock:
                if stats.device_key == device["key"] and (time.time() - stats.last_frame_at) > 2.0:
                    stats.state = "connecting"

    @staticmethod
    def _jpeg_size(data):
        """从 JPEG 的 SOF 段读宽高（避免依赖 Pillow）。"""
        index = 2
        size = len(data)
        while index + 9 < size:
            if data[index] != 0xFF:
                index += 1
                continue
            marker = data[index + 1]
            if marker in (0xD8, 0xD9) or 0xD0 <= marker <= 0xD7:
                index += 2
                continue
            try:
                segment = (data[index + 2] << 8) | data[index + 3]
            except IndexError:
                return None
            if marker in (0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7,
                          0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF):
                try:
                    height = (data[index + 5] << 8) | data[index + 6]
                    width = (data[index + 7] << 8) | data[index + 8]
                except IndexError:
                    return None
                return (width, height)
            index += 2 + segment
        return None

    def _proxy_snapshot(self):
        discovery = self.server.discovery
        device = discovery.active()
        if not device:
            self._send_json({"error": "no_device"}, 503)
            return
        try:
            status, content_type, body = http_get_bytes(
                device["ip"], int(device["http"]), "/snapshot.jpg", timeout=5.0)
        except (urllib.error.URLError, OSError) as exc:
            self._send_json({"error": "snapshot_failed", "message": str(exc)}, 502)
            return
        if status != 200 or not body:
            self._send_json({"error": "no_frame"}, 503)
            return
        self.send_response(200)
        self.send_header("Content-Type", content_type or "image/jpeg")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


# ---------------------------------------------------------------------------
# 入口
# ---------------------------------------------------------------------------

def parse_target(text):
    text = (text or "").strip()
    if not text:
        return None
    if "://" in text:
        parsed = urlparse(text)
        host = parsed.hostname or ""
        port = parsed.port or DEFAULT_GAME_HTTP_PORT
        return (host, int(port))
    if ":" in text:
        host, port = text.rsplit(":", 1)
        return (host.strip(), int(port))
    return (text, DEFAULT_GAME_HTTP_PORT)


def parse_ports(text):
    ports = []
    for piece in (text or "").split(","):
        piece = piece.strip()
        if not piece:
            continue
        try:
            ports.append(int(piece))
        except ValueError:
            continue
    return ports or list(DEFAULT_SCAN_PORTS)


def list_devices(discovery, timeout):
    discovery.start()
    deadline = time.time() + timeout
    while time.time() < deadline:
        snapshot = discovery.snapshot()
        if snapshot["devices"]:
            break
        time.sleep(0.3)
    snapshot = discovery.snapshot()
    # 给手动设备一次兜底：直接取 /api/info
    print("")
    if not snapshot["devices"]:
        print("未发现设备（%.0f 秒）。检查：同网段？防火墙放行 UDP %d？"
              % (timeout, discovery.udp_port))
        discovery.stop()
        return 1
    print("发现 %d 台设备：" % len(snapshot["devices"]))
    for device in snapshot["devices"]:
        mark = " *" if device["key"] == (snapshot["active"] or {}).get("key") else "  "
        print("%s %-28s %-16s %-6s %-8s %s"
              % (mark, device["name"][:28], "%s:%d" % (device["ip"], device["http"]),
                 device["stage"] or "-", device["source"],
                 device["scene"] or ""))
    discovery.stop()
    return 0


def build_arg_parser():
    parser = argparse.ArgumentParser(
        description="EpicSpark 局域网展示端：自动发现游戏主机并实时显示游戏内主摄像头画面")
    parser.add_argument("--game", default=None,
                        help="手动指定游戏主机，格式 ip[:port]（跳过自动扫描）")
    parser.add_argument("--http-port", type=int, default=DEFAULT_LOCAL_PORT,
                        help="本地展示网页端口（默认 %d，被占用自动顺延）" % DEFAULT_LOCAL_PORT)
    parser.add_argument("--udp-port", type=int, default=DEFAULT_UDP_PORT,
                        help="游戏发现 UDP 端口（默认 %d）" % DEFAULT_UDP_PORT)
    parser.add_argument("--scan-ports", default="%d" % DEFAULT_GAME_HTTP_PORT,
                        help="子网扫描候选 HTTP 端口，逗号分隔（默认 %d）"
                             % DEFAULT_GAME_HTTP_PORT)
    parser.add_argument("--no-browser", action="store_true", help="不自动打开浏览器")
    parser.add_argument("--no-sweep", action="store_true", help="禁用子网扫描兜底")
    parser.add_argument("--list", action="store_true", help="只发现并打印设备，不启动网页")
    parser.add_argument("--list-timeout", type=float, default=10.0,
                        help="--list 的等待秒数（默认 10）")
    parser.add_argument("--verbose", action="store_true", help="打印 HTTP 访问日志")
    return parser


def serve(args):
    manual = parse_target(args.game)
    discovery = Discovery(
        udp_port=args.udp_port,
        scan_ports=parse_ports(args.scan_ports),
        manual_target=manual,
        no_sweep=args.no_sweep,
    )
    stats = StreamStats()
    ViewerHandler.verbose = bool(args.verbose)

    server = None
    port = int(args.http_port)
    last_error = None
    for offset in range(0, 20):
        try:
            server = ViewerServer(("127.0.0.1", port + offset), ViewerHandler,
                                  discovery, stats)
            port = port + offset
            break
        except OSError as exc:
            last_error = exc
    if server is None:
        log_err("本地网页端口 %d 起不来：%s" % (args.http_port, last_error))
        return 1

    discovery.start()
    thread = threading.Thread(target=server.serve_forever,
                              kwargs={"poll_interval": 0.2},
                              name="viewer-http", daemon=True)
    thread.start()

    url = "http://127.0.0.1:%d/" % port
    log("展示端已启动：%s" % url)
    log("自动发现：UDP %d%s；手动目标：%s"
        % (args.udp_port,
           "（已禁用子网扫描）" if (args.no_sweep or manual) else "；无结果时扫描 /24 网段",
           "%s:%d" % manual if manual else "无（等待自动发现）"))
    log("按 Ctrl+C 退出")
    if not args.no_browser:
        threading.Timer(0.8, lambda: webbrowser.open(url)).start()

    try:
        while True:
            time.sleep(0.5)
    except KeyboardInterrupt:
        sys.stdout.write("\n")
    finally:
        log("正在关闭…")
        discovery.stop()
        try:
            server.shutdown()
        except Exception:  # noqa: BLE001
            pass
        try:
            server.server_close()
        except Exception:  # noqa: BLE001
            pass
    return 0


def main(argv=None):
    args = build_arg_parser().parse_args(argv)
    if args.list:
        manual = parse_target(args.game)
        discovery = Discovery(args.udp_port, parse_ports(args.scan_ports),
                              manual_target=manual, no_sweep=args.no_sweep)
        return list_devices(discovery, args.list_timeout)
    return serve(args)


if __name__ == "__main__":
    sys.exit(main())
