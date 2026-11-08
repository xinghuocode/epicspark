using System;

namespace EpicSpark.Voice
{
    // Qwen-TTS Realtime over the DashScope WebSocket event protocol
    // (wss://dashscope.aliyuncs.com/api-ws/v1/realtime?model=...).
    //
    // Lifecycle: Start() -> session.created -> UpdateSession() -> Append(text)*
    // -> Finish(). Audio arrives as base64 PCM inside `response.audio.delta`
    // events; `session.finished` marks the end of the session.
    public class TtsClient : IDisposable
    {
        readonly WsConnection _ws = new WsConnection();
        readonly VoiceConfig _cfg;

        public TtsClient(VoiceConfig cfg) { _cfg = cfg; }

        public bool TryDequeue(out string msg)
        {
            if (_ws == null) { msg = null; return false; }
            return _ws.TryDequeue(out msg);
        }
        public string Error => _ws.Error;
        public bool Closed => _ws.Closed;
        public bool Connected => _ws.Connected;

        public void Start()
        {
            string url = _cfg.ttsUrl + "?model=" + Uri.EscapeDataString(_cfg.ttsModel);
            _ws.Start(url, _cfg.apiKey);
        }

        public void UpdateSession()
        {
            string json =
                "{\"event_id\":" + MiniJson.Quote(EventId()) + ",\"type\":\"session.update\",\"session\":{" +
                "\"voice\":" + MiniJson.Quote(_cfg.ttsVoice) + "," +
                "\"mode\":\"server_commit\"," +
                "\"language_type\":\"Chinese\"," +
                "\"response_format\":\"pcm\"," +
                "\"sample_rate\":" + _cfg.ttsSampleRate + "}}";
            _ws.SendText(json);
        }

        public void Append(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string json =
                "{\"event_id\":" + MiniJson.Quote(EventId()) +
                ",\"type\":\"input_text_buffer.append\",\"text\":" + MiniJson.Quote(text) + "}";
            _ws.SendText(json);
        }

        public void Finish()
        {
            string json = "{\"event_id\":" + MiniJson.Quote(EventId()) + ",\"type\":\"session.finish\"}";
            _ws.SendText(json);
        }

        static string EventId() => "event_" + Guid.NewGuid().ToString("N");

        public void Close() => _ws.Close();
        public void Dispose() => _ws.Dispose();
    }
}
