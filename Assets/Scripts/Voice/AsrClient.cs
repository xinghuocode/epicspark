using System;

namespace EpicSpark.Voice
{
    // Paraformer real-time speech recognition over the DashScope WebSocket
    // "run-task" protocol (wss://dashscope.aliyuncs.com/api-ws/v1/inference).
    //
    // Lifecycle: Start() -> server sends task-started -> SendAudio() with 16 kHz
    // mono PCM16 -> Finish() (finish-task) -> server sends the final
    // result-generated events then task-finished.
    //
    // Server events are text JSON frames; drain them with TryDequeue() on the main
    // thread and parse with MiniJson (see VoiceDialogueController).
    public class AsrClient : IDisposable
    {
        readonly WsConnection _ws = new WsConnection();
        readonly VoiceConfig _cfg;
        string _taskId;

        public AsrClient(VoiceConfig cfg) { _cfg = cfg; }

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
            _taskId = Guid.NewGuid().ToString();
            SendRunTask();           // queued before the socket is up; the pump
            _ws.Start(_cfg.asrUrl, _cfg.apiKey);   // flushes it in order on connect
        }

        void SendRunTask()
        {
            string parameters =
                "{\"format\":\"pcm\"," +
                "\"sample_rate\":" + _cfg.asrSampleRate + "," +
                "\"heartbeat\":true," +
                "\"semantic_punctuation_enabled\":false}";

            string json =
                "{\"header\":{\"action\":\"run-task\",\"task_id\":" + MiniJson.Quote(_taskId) +
                ",\"streaming\":\"duplex\"}," +
                "\"payload\":{\"task_group\":\"audio\",\"task\":\"asr\",\"function\":\"recognition\"," +
                "\"model\":" + MiniJson.Quote(_cfg.asrModel) + ",\"parameters\":" + parameters + ",\"input\":{}}}";
            _ws.SendText(json);
        }

        public void SendAudio(byte[] pcm16)
        {
            if (pcm16 != null && pcm16.Length > 0) _ws.SendBinary(pcm16);
        }

        public void Finish()
        {
            string json =
                "{\"header\":{\"action\":\"finish-task\",\"task_id\":" + MiniJson.Quote(_taskId) +
                ",\"streaming\":\"duplex\"},\"payload\":{\"input\":{}}}";
            _ws.SendText(json);
        }

        public void Close() => _ws.Close();
        public void Dispose() => _ws.Dispose();
    }
}
