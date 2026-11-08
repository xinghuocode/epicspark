// 展示端接口 · 协议常量与「纯数据快照」。
// LanDisplayStatus 里没有任何 UnityEngine 对象，可以被 HTTP / UDP 后台线程安全地读取与序列化。
using System;

namespace EpicSpark.Streaming
{
    /// <summary>采集方式。</summary>
    public enum LanDisplayCaptureMode
    {
        /// <summary>独立相机渲染到 RenderTexture（推荐，URP 下不打断玩家视野）。</summary>
        DedicatedCamera = 0,

        /// <summary>整屏截图兜底（会把 UI 一起截进去；DedicatedCamera 异常时可用）。</summary>
        ScreenCapture = 1,
    }

    /// <summary>协议常量。展示端（Viewer/epicspark_viewer.py）与本文档必须保持一致。</summary>
    internal static class LanDisplayProtocol
    {
        public const string ServiceName = "epicspark.display";
        public const int Version = 1;
        public const string GameName = "EpicSpark";
        public const string ProbeText = "EPICSPARK_DISCOVER 1";
        public const string MultipartBoundary = "epicsparkframe";
        public const int DefaultHttpPort = 45231;
        public const int DefaultUdpPort = 45230;
        public const int MaxHttpPortTries = 20;
        public const int MaxStreamClients = 8;
        public const int DefaultWidth = 960;
        public const int DefaultHeight = 540;
        public const int DefaultFps = 15;
        public const int DefaultQuality = 75;
    }

    /// <summary>展示端对外暴露的运行时状态（序列化后用 JSON 返回）。</summary>
    public sealed class LanDisplayStatus
    {
        public string DeviceId = "";
        public string Name = "";
        public string Host = "";
        public string Stage = "playing";
        public string Scene = "";
        public string CameraId = "main";
        public string CameraName = "主摄像头（玩家视角）";
        public int HttpPort;
        public int DiscoveryPort;
        public int CaptureWidth = LanDisplayProtocol.DefaultWidth;
        public int CaptureHeight = LanDisplayProtocol.DefaultHeight;
        public int TargetFps = LanDisplayProtocol.DefaultFps;
        public int JpegQuality = LanDisplayProtocol.DefaultQuality;
        public string CaptureMode = "dedicated";
        public int Frames;
        public double ActualFps;
        public long LastFrameAgeMs = -1;
        public long UptimeSec;
        public long StartedUtcMs;
        public string LastError;
        public int StreamingClients;
        public int TotalClients;

        /// <summary>UDP 广播包（也用于回应探测包）。</summary>
        public string BuildBeaconJson()
        {
            var json = new JsonBuilder(640);
            json.BeginObject()
                .Property("svc", LanDisplayProtocol.ServiceName)
                .Property("v", LanDisplayProtocol.Version)
                .Property("game", LanDisplayProtocol.GameName)
                .Property("id", DeviceId)
                .Property("name", Name)
                .Property("host", Host)
                .Property("http", HttpPort)
                .Property("udp", DiscoveryPort)
                .Property("stage", Stage)
                .Property("scene", Scene)
                .Name("camera").BeginObject()
                    .Property("id", CameraId)
                    .Property("name", CameraName)
                    .Property("width", CaptureWidth)
                    .Property("height", CaptureHeight)
                .EndObject()
                .Property("fps", TargetFps)
                .Name("stream").BeginObject()
                    .Property("path", "/stream.mjpg")
                    .Property("width", CaptureWidth)
                    .Property("height", CaptureHeight)
                    .Property("fps", TargetFps)
                    .Property("format", "mjpeg")
                .EndObject()
                .EndObject();
            return json.ToString();
        }

        /// <summary>GET /api/info：展示端连接后先读这个接口自检。</summary>
        public string BuildInfoJson()
        {
            var json = new JsonBuilder(1600);
            json.BeginObject()
                .Property("ok", true)
                .Property("svc", LanDisplayProtocol.ServiceName)
                .Property("v", LanDisplayProtocol.Version)
                .Property("game", LanDisplayProtocol.GameName)
                .Property("id", DeviceId)
                .Property("name", Name)
                .Property("host", Host)
                .Property("stage", Stage)
                .Property("scene", Scene)
                .Property("http", HttpPort)
                .Property("udp", DiscoveryPort)
                .Property("fps", TargetFps)
                .Property("jpegQuality", JpegQuality)
                .Property("startedAtUtcMs", StartedUtcMs)
                .Property("uptimeSec", UptimeSec)
                .Name("camera").BeginObject()
                    .Property("id", CameraId)
                    .Property("name", CameraName)
                    .Property("width", CaptureWidth)
                    .Property("height", CaptureHeight)
                .EndObject()
                .Name("cameras").BeginArray()
                    .BeginObject()
                        .Property("id", CameraId)
                        .Property("name", CameraName)
                        .Property("width", CaptureWidth)
                        .Property("height", CaptureHeight)
                    .EndObject()
                .EndArray()
                .Name("endpoints").BeginObject()
                    .Property("stream", "/stream.mjpg")
                    .Property("snapshot", "/snapshot.jpg")
                    .Property("info", "/api/info")
                    .Property("health", "/api/health")
                .EndObject()
                .Name("capture").BeginObject()
                    .Property("mode", CaptureMode)
                    .Property("fps", ActualFps)
                    .Property("targetFps", TargetFps)
                    .Property("frames", Frames)
                    .Property("lastFrameAgeMs", LastFrameAgeMs)
                    .Property("width", CaptureWidth)
                    .Property("height", CaptureHeight)
                    .Name("lastError").Value(LastError)
                .EndObject()
                .Name("clients").BeginObject()
                    .Property("streaming", StreamingClients)
                    .Property("total", TotalClients)
                    .Property("maxStreaming", LanDisplayProtocol.MaxStreamClients)
                .EndObject()
                .EndObject();
            return json.ToString();
        }

        /// <summary>GET /api/health：探测扫描用，字段尽量小。</summary>
        public string BuildHealthJson()
        {
            var json = new JsonBuilder(256);
            json.BeginObject()
                .Property("ok", true)
                .Property("svc", LanDisplayProtocol.ServiceName)
                .Property("v", LanDisplayProtocol.Version)
                .Property("game", LanDisplayProtocol.GameName)
                .Property("id", DeviceId)
                .Property("name", Name)
                .Property("stage", Stage)
                .Property("cameraCount", 1)
                .Property("fps", TargetFps)
                .EndObject();
            return json.ToString();
        }

        /// <summary>生成展示端地址串，方便日志里直接给出。</summary>
        public string DescribeEndpoints()
        {
            return string.Format(
                "http://{0}:{1}/  （MJPEG: /stream.mjpg，快照: /snapshot.jpg，UDP 探测端口 {2}）",
                string.IsNullOrEmpty(Host) ? "127.0.0.1" : Host, HttpPort, DiscoveryPort);
        }

        public long NowUtcMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }
}
