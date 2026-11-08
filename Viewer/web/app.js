/* EpicSpark 局域网展示端 —— 页面逻辑（无第三方依赖）
 *
 * 画面不用 <img src="...stream.mjpg">：MJPEG 的 img.onload 在部分浏览器/无头模式下
 * 不触发，会导致「画面在播但一直盖着连接提示」。这里用 fetch + ReadableStream 解析
 * multipart，自己把每一帧画到 <canvas> 上，状态判定完全可控，断线时还能保留最后一帧。
 */
(function () {
  "use strict";

  const $ = (id) => document.getElementById(id);
  const state = {
    started: false,
    status: null,
    devices: [],
    active: null,
    activeKey: null,
    targetKey: null,
    hasFrame: false,
    live: false,
    streamAbort: null,
    streamGeneration: 0,
    retryTimer: null,
    reloadPending: false,
    localFrames: 0,
    localFrameTimes: [],
    lastDrawnAt: 0,
    panelOpen: false,
    toastTimer: null,
    idleTimer: null,
    drawSize: [0, 0],
  };

  const STAGE_TEXT = {
    title: "标题界面",
    generating: "场景生成中",
    playing: "游玩中",
    paused: "已暂停",
  };

  const SOURCE_TEXT = {
    udp: "UDP 广播发现",
    sweep: "子网扫描发现",
    manual: "手动指定",
  };

  const canvas = $("stream");
  const ctx = canvas.getContext("2d", { alpha: false });

  // ───────────────────────── 工具 ─────────────────────────
  async function api(path, options) {
    const opts = Object.assign({ cache: "no-store" }, options || {});
    const resp = await fetch(path, opts);
    if (!resp.ok) {
      let detail = "";
      try { detail = (await resp.json()).error || ""; } catch (e) { /* ignore */ }
      throw new Error("HTTP " + resp.status + (detail ? " · " + detail : ""));
    }
    return resp.json();
  }

  function fmtBytes(bytes) {
    if (!bytes) return "0 B";
    const units = ["B", "KB", "MB", "GB"];
    let value = bytes, i = 0;
    while (value >= 1024 && i < units.length - 1) { value /= 1024; i += 1; }
    return value.toFixed(value >= 10 || i === 0 ? 0 : 1) + " " + units[i];
  }

  function toast(message, timeout) {
    const el = $("toast");
    el.textContent = message;
    el.classList.add("show");
    clearTimeout(state.toastTimer);
    state.toastTimer = setTimeout(() => el.classList.remove("show"), timeout || 2400);
  }

  function setText(id, text) {
    const el = $(id);
    if (el && el.textContent !== text) el.textContent = text;
  }

  function activeDevice() {
    if (state.active) return state.active;
    if (!state.devices || !state.devices.length) return null;
    return state.devices.find((d) => d.key === state.activeKey) || null;
  }

  function setPill(kind, text) {
    const pill = $("statusPill");
    pill.dataset.state = kind;
    setText("statusText", text);
  }

  // ───────────────────────── 画面流（canvas + fetch multipart） ─────────────────────────
  function showOverlay(title, hint) {
    $("placeholder").classList.remove("hidden");
    setText("placeholderTitle", title);
    setText("placeholderHint", hint);
  }

  function hideOverlay() {
    $("placeholder").classList.add("hidden");
  }

  function showLost(text) {
    $("lostBanner").classList.remove("hidden");
    setText("lostText", text || "信号中断，正在自动重连…");
  }

  function hideLost() {
    $("lostBanner").classList.add("hidden");
  }

  function markFrame(width, height) {
    state.hasFrame = true;
    state.live = true;
    state.lastDrawnAt = Date.now();
    state.localFrames += 1;
    const now = performance.now();
    state.localFrameTimes.push(now);
    if (state.localFrameTimes.length > 120) state.localFrameTimes.shift();
    if (width > 0 && height > 0 && (state.drawSize[0] !== width || state.drawSize[1] !== height)) {
      state.drawSize = [width, height];
      canvas.width = width;
      canvas.height = height;
    }
    $("screen").classList.add("live");
    hideOverlay();
    hideLost();
  }

  function drawFrame(bitmap) {
    if (canvas.width !== bitmap.width || canvas.height !== bitmap.height) {
      canvas.width = bitmap.width;
      canvas.height = bitmap.height;
      state.drawSize = [bitmap.width, bitmap.height];
    }
    ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    markFrame(bitmap.width, bitmap.height);
  }

  function localFps() {
    const times = state.localFrameTimes;
    if (times.length < 2) return 0;
    const span = (times[times.length - 1] - times[0]) / 1000;
    if (span <= 0.2) return 0;
    return Math.round(((times.length - 1) / span) * 10) / 10;
  }

  function abortStream() {
    state.streamGeneration += 1;
    if (state.streamAbort) {
      try { state.streamAbort.abort(); } catch (e) { /* ignore */ }
      state.streamAbort = null;
    }
  }

  function stopStream() {
    abortStream();
    state.targetKey = null;
    state.hasFrame = false;
    state.live = false;
    state.localFrames = 0;
    state.localFrameTimes = [];
    $("screen").classList.remove("live");
    try {
      ctx.fillStyle = "#000";
      ctx.fillRect(0, 0, canvas.width, canvas.height);
    } catch (e) { /* ignore */ }
  }

  function scheduleStreamRetry(delay) {
    if (state.reloadPending) return;
    state.reloadPending = true;
    clearTimeout(state.retryTimer);
    state.retryTimer = setTimeout(function () {
      state.reloadPending = false;
      if (!activeDevice()) return;
      startStream("自动重连");
    }, delay || 1500);
  }

  function reloadStream(reason) {
    const device = activeDevice();
    if (!device) {
      stopStream();
      return;
    }
    abortStream();
    state.targetKey = device.key;
    state.hasFrame = false;
    state.live = false;
    state.localFrames = 0;
    state.localFrameTimes = [];
    $("screen").classList.remove("live");
    showOverlay("正在连接游戏…",
      "目标：" + device.name + "（" + device.ip + ":" + device.http + "）" + (reason ? " · " + reason : ""));
    startStream(reason);
  }

  async function startStream(reason) {
    const device = activeDevice();
    if (!device) return;
    const generation = state.streamGeneration;
    const controller = new AbortController();
    state.streamAbort = controller;
    const url = "/api/proxy/stream.mjpg?ts=" + Date.now() + "&gen=" + generation + "&reason=" +
      encodeURIComponent(reason || "");
    try {
      const resp = await fetch(url, { cache: "no-store", signal: controller.signal });
      if (!resp.ok || !resp.body) {
        throw new Error("HTTP " + resp.status);
      }
      const boundary = parseBoundary(resp.headers.get("Content-Type"));
      const reader = resp.body.getReader();
      let buffer = new Uint8Array(0);
      const marker = encodeAscii("--" + boundary);
      while (true) {
        const { value, done } = await reader.read();
        if (done) break;
        if (generation !== state.streamGeneration) break;
        buffer = concatBytes(buffer, value);
        let guard = 0;
        while (guard++ < 32) {
          const part = extractPart(buffer, marker);
          if (!part) break;
          buffer = part.rest;
          if (part.jpeg && part.jpeg.length > 100) {
            await drawJpeg(part.jpeg);
          }
        }
        if (buffer.length > 8 * 1024 * 1024) buffer = buffer.slice(-1024 * 1024);
      }
      throw new Error("流结束");
    } catch (err) {
      if (generation !== state.streamGeneration || (err && err.name === "AbortError")) {
        return;
      }
      state.live = false;
      $("screen").classList.remove("live");
      if (state.hasFrame) {
        showLost("画面中断，正在自动重连…");
      } else {
        showOverlay("画面连接失败",
          (err && err.message) || "正在重试连接游戏端…（确认防火墙放行 TCP 端口）");
      }
      scheduleStreamRetry(1500);
    }
  }

  async function drawJpeg(bytes) {
    try {
      const blob = new Blob([bytes], { type: "image/jpeg" });
      if (typeof createImageBitmap === "function") {
        const bitmap = await createImageBitmap(blob);
        drawFrame(bitmap);
        if (typeof bitmap.close === "function") bitmap.close();
      } else {
        const url = URL.createObjectURL(blob);
        await new Promise(function (resolve, reject) {
          const img = new Image();
          img.onload = function () {
            canvas.width = img.naturalWidth || 960;
            canvas.height = img.naturalHeight || 540;
            ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
            markFrame(canvas.width, canvas.height);
            URL.revokeObjectURL(url);
            resolve();
          };
          img.onerror = function () {
            URL.revokeObjectURL(url);
            reject(new Error("JPEG 解码失败"));
          };
          img.src = url;
        });
      }
    } catch (e) {
      /* 单帧解码失败不中断整个流 */
    }
  }

  function parseBoundary(contentType) {
    if (!contentType) return "epicsparkframe";
    const match = /boundary=([^;]+)/i.exec(contentType);
    return match ? match[1].trim().replace(/^"|"$/g, "") : "epicsparkframe";
  }

  function encodeAscii(text) {
    const out = new Uint8Array(text.length);
    for (let i = 0; i < text.length; i += 1) out[i] = text.charCodeAt(i) & 0xff;
    return out;
  }

  function indexOfBytes(haystack, needle, from) {
    const start = from || 0;
    const limit = haystack.length - needle.length;
    outer:
    for (let i = start; i <= limit; i += 1) {
      for (let j = 0; j < needle.length; j += 1) {
        if (haystack[i + j] !== needle[j]) continue outer;
      }
      return i;
    }
    return -1;
  }

  function concatBytes(a, b) {
    if (!a || !a.length) return b;
    const out = new Uint8Array(a.length + b.length);
    out.set(a, 0);
    out.set(b, a.length);
    return out;
  }

  function extractPart(buffer, marker) {
    const start = indexOfBytes(buffer, marker, 0);
    if (start < 0) {
      // 保留末尾可能是半个 boundary 的数据
      const keep = Math.min(buffer.length, marker.length - 1);
      return buffer.length > keep * 2 ? { rest: buffer.slice(buffer.length - keep), jpeg: null } : null;
    }
    const headerEnd = indexOfBytes(buffer, encodeAscii("\r\n\r\n"), start);
    if (headerEnd < 0) return null;
    const headerText = decodeAscii(buffer.subarray(start + marker.length, headerEnd));
    const match = /content-length:\s*(\d+)/i.exec(headerText);
    if (!match) {
      return { rest: buffer.slice(headerEnd + 4), jpeg: null };
    }
    const length = parseInt(match[1], 10);
    const bodyStart = headerEnd + 4;
    if (buffer.length < bodyStart + length) return null;
    const jpeg = buffer.slice(bodyStart, bodyStart + length);
    return { rest: buffer.slice(bodyStart + length), jpeg: jpeg };
  }

  function decodeAscii(bytes) {
    let text = "";
    for (let i = 0; i < bytes.length; i += 1) text += String.fromCharCode(bytes[i]);
    return text;
  }

  // ───────────────────────── 顶栏 / 底栏 / 设备列表 ─────────────────────────
  function renderHeader(device) {
    if (!device) {
      setText("deviceName", "正在寻找游戏…");
      setText("stageBadge", "—");
      setPill("searching", "搜索中");
      return;
    }
    setText("deviceName", device.name + " · " + device.ip + ":" + device.http + " · " +
      (SOURCE_TEXT[device.source] || device.source));
    setText("stageBadge", (STAGE_TEXT[device.stage] || device.stage || "—") +
      (device.scene ? " · " + device.scene : ""));
  }

  function renderStatusbar(stream) {
    if (!stream) return;
    const local = localFps();
    const live = state.live || stream.state === "live";
    setPill(live ? "live" : (stream.state === "connecting" ? "connecting"
      : (stream.state === "error" ? "error" : "searching")),
      live ? "实时" : (stream.state === "connecting" ? "连接中"
        : (stream.state === "error" ? "已断开" : "未连接")));
    setText("statFps", live ? (local || stream.fps || 0) + " fps" : "— fps");
    setText("statLatency", live && stream.latencyMs != null ? "延迟 " + stream.latencyMs + " ms" : "延迟 —");
    const resolution = state.drawSize[0] ? state.drawSize : stream.resolution;
    setText("statRes", resolution && resolution[0] ? resolution[0] + " × " + resolution[1] : "— × —");
    setText("statBytes", "已收 " + fmtBytes(stream.bytes));
    const device = activeDevice();
    setText("statSource", device ? (SOURCE_TEXT[device.source] || device.source) : "—");
  }

  function renderDevices() {
    const list = $("deviceList");
    const devices = state.devices || [];
    if (!devices.length) {
      list.innerHTML = '<div class="empty-tip">还没有发现游戏。<br>正在监听局域网广播并扫描同网段；也可以点击底部「重新扫描」。</div>';
      return;
    }
    const fragment = document.createDocumentFragment();
    devices.forEach((device) => {
      const row = document.createElement("button");
      row.type = "button";
      row.className = "device-row" + (device.key === state.activeKey ? " active" : "");
      row.innerHTML =
        '<div class="row-top"><span class="row-name"></span>' +
        '<span class="tag ' + (device.stale ? "stale" : "live") + '">' +
        (device.stale ? "离线" : "在线") + "</span></div>" +
        '<div class="row-meta"><span class="ip"></span><span class="stage"></span><span class="src"></span></div>' +
        '<div class="row-extra"></div>';
      row.querySelector(".row-name").textContent = device.name || device.ip;
      row.querySelector(".ip").textContent = device.ip + ":" + device.http;
      row.querySelector(".stage").textContent = STAGE_TEXT[device.stage] || device.stage || "—";
      row.querySelector(".src").textContent = SOURCE_TEXT[device.source] || device.source;
      row.querySelector(".row-extra").textContent = device.scene ? "场景：" + device.scene : "（场景名未提供）";
      row.addEventListener("click", function () { selectDevice(device); });
      fragment.appendChild(row);
    });
    list.innerHTML = "";
    list.appendChild(fragment);
  }

  async function selectDevice(device) {
    try {
      await api("/api/select", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ip: device.ip, httpPort: device.http }),
      });
      state.active = device;
      state.activeKey = device.key;
      toast("已切换到 " + (device.name || device.ip));
      reloadStream("切换设备");
      refreshDevices();
      refreshStatus();
    } catch (err) {
      toast("切换失败：" + err.message);
    }
  }

  function togglePanel(force) {
    const open = typeof force === "boolean" ? force : !state.panelOpen;
    state.panelOpen = open;
    $("devicePanel").classList.toggle("hidden", !open);
    if (open) refreshDevices();
  }

  // ───────────────────────── 轮询 ─────────────────────────
  async function refreshStatus() {
    let data;
    try {
      data = await api("/api/status");
    } catch (err) {
      setPill("error", "本地服务异常");
      showOverlay("展示端本地服务异常", String(err.message || err) + "（请检查运行 python 的终端）");
      return;
    }
    state.status = data;
    const active = data.active || null;
    state.active = active;
    state.activeKey = active ? active.key : null;
    const stream = data.stream || {};
    renderHeader(active);
    renderStatusbar(stream);

    if (!active) {
      if (state.targetKey) stopStream();
      setPill("searching", "搜索中");
      $("screen").classList.remove("live");
      showOverlay("正在寻找游戏…",
        "展示端会自动发现同一局域网内的 EpicSpark 游戏主机。" +
        (data.discovery && data.discovery.sweeping
          ? " 正在扫描同网段…"
          : " 已开始监听 UDP 广播，5 秒无结果会自动扫描网段。"));
      updateScanLog(data);
      return;
    }

    if (state.targetKey !== active.key) {
      reloadStream("发现设备");
      updateScanLog(data);
      return;
    }

    const live = state.live || stream.state === "live";
    if (live) {
      setPill("live", "实时");
      if (!state.hasFrame) {
        // 服务端已有帧在推但页面还没画出第一帧：给一点时间，超时则重连
        if (state.lastFrameWaitAt == null) state.lastFrameWaitAt = Date.now();
        else if (Date.now() - state.lastFrameWaitAt > 6000) {
          state.lastFrameWaitAt = null;
          scheduleStreamRetry(500);
        }
      } else {
        state.lastFrameWaitAt = null;
      }
      hideLost();
    } else if (stream.state === "error") {
      setPill("error", "已断开");
      if (state.hasFrame) {
        showLost("画面中断，正在自动重连…");
      } else {
        showOverlay("画面连接失败", stream.error || "正在重试连接游戏端…");
      }
      scheduleStreamRetry(1500);
    } else {
      setPill("connecting", "连接中");
      if (!state.hasFrame) {
        showOverlay("正在连接游戏…", active.name + " · " + active.ip + ":" + active.http);
      }
    }
    updateScanLog(data);
  }

  function updateScanLog(data) {
    const discovery = data.discovery || {};
    const parts = [];
    parts.push(discovery.udp ? "UDP :" + discovery.udpPort + " 探测中" : "UDP 不可用");
    if (discovery.manual) parts.push("手动目标 " + discovery.manual);
    else if (discovery.sweeping) parts.push("正在扫描 " + (discovery.scanPorts || []).join(","));
    else if (discovery.lastSweepMs != null) parts.push("上次扫描 " + Math.round(discovery.lastSweepMs / 1000) + "s 前，发现 " + (discovery.lastSweepFound || 0) + " 台");
    parts.push("已发现 " + (data.deviceCount || 0) + " 台");
    setText("scanLog", parts.join(" · "));
  }

  async function refreshDevices() {
    try {
      const data = await api("/api/devices");
      state.devices = data.devices || [];
      state.active = data.active || null;
      if (data.active) state.activeKey = data.active.key;
      renderDevices();
    } catch (err) { /* 状态轮询会显示错误，这里静默 */ }
  }

  // ───────────────────────── 交互 ─────────────────────────
  async function doRescan() {
    try {
      await api("/api/rescan", { method: "POST" });
      state.activeKey = null;
      state.active = null;
      stopStream();
      showOverlay("正在寻找游戏…", "已重新广播探测，并会扫描同网段。");
      toast("已重新扫描局域网…");
      setTimeout(refreshDevices, 600);
      refreshStatus();
    } catch (err) {
      toast("重新扫描失败：" + err.message);
    }
  }

  function toggleFullscreen() {
    const doc = document;
    if (!doc.fullscreenElement) {
      const target = doc.documentElement;
      const request = target.requestFullscreen || target.webkitRequestFullscreen;
      if (request) {
        request.call(target).catch(() => toast("浏览器拒绝全屏（需要用户手势）"));
      } else {
        toast("当前浏览器不支持全屏");
      }
    } else {
      (doc.exitFullscreen || doc.webkitExitFullscreen).call(doc);
    }
  }

  function markActivity() {
    document.body.classList.remove("idle");
    clearTimeout(state.idleTimer);
    state.idleTimer = setTimeout(function () {
      if (document.fullscreenElement) document.body.classList.add("idle");
    }, 3000);
  }

  // ───────────────────────── 启动 ─────────────────────────
  function bindEvents() {
    document.addEventListener("keydown", function (event) {
      if (event.target && /input|textarea|select/i.test(event.target.tagName)) return;
      const key = event.key.toLowerCase();
      if (key === "f") { event.preventDefault(); toggleFullscreen(); }
      else if (key === "r") { event.preventDefault(); doRescan(); }
      else if (key === "d") { event.preventDefault(); togglePanel(); }
      else if (key === "escape") { togglePanel(false); }
      else if (key === "h") { $("helpOverlay").classList.toggle("hidden"); }
      markActivity();
    });

    $("helpClose").addEventListener("click", () => $("helpOverlay").classList.add("hidden"));
    $("closePanel").addEventListener("click", () => togglePanel(false));
    $("rescanBtn").addEventListener("click", doRescan);
    document.addEventListener("mousemove", markActivity);
    document.addEventListener("touchstart", markActivity, { passive: true });

    document.addEventListener("fullscreenchange", function () {
      if (state.targetKey) setTimeout(() => reloadStream("全屏切换"), 250);
    });

    document.addEventListener("click", function (event) {
      if (!state.panelOpen) return;
      if ($("devicePanel").contains(event.target)) return;
      togglePanel(false);
    });

    document.addEventListener("visibilitychange", function () {
      if (!document.hidden && state.targetKey) {
        setTimeout(function () {
          if (Date.now() - state.lastDrawnAt > 4000) startStream("页面恢复");
        }, 800);
      }
    });

    window.addEventListener("beforeunload", function () { stopStream(); });
  }

  function tickClock() {
    const now = new Date();
    const pad = (n) => String(n).padStart(2, "0");
    setText("statClock", pad(now.getHours()) + ":" + pad(now.getMinutes()) + ":" + pad(now.getSeconds()));
  }

  function boot() {
    if (state.started) return;
    state.started = true;
    bindEvents();
    tickClock();
    setInterval(tickClock, 1000);
    refreshStatus();
    refreshDevices();
    setInterval(refreshStatus, 1000);
    setInterval(refreshDevices, 2500);

    // 看门狗：状态说 live，但页面 6 秒没画新帧 → 重连
    setInterval(function () {
      const stream = (state.status || {}).stream || {};
      if (!state.targetKey) return;
      if (stream.state === "live" && Date.now() - (state.lastDrawnAt || 0) > 6000) {
        startStream("画面看门狗");
      }
    }, 6000);

    if (!localStorage.getItem("epicspark.helpSeen")) {
      localStorage.setItem("epicspark.helpSeen", "1");
      $("helpOverlay").classList.remove("hidden");
      setTimeout(function () { $("helpOverlay").classList.add("hidden"); }, 8000);
    }
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
  } else {
    boot();
  }
})();
