using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EpicSpark.Voice
{
    // A small async WebSocket pump shared by the ASR / TTS clients.
    //
    // * Connects with an `Authorization: Bearer <key>` header.
    // * Sends queued text/binary frames strictly in order (a single send loop).
    // * Receives server text frames into a queue that the caller drains on the
    //   Unity main thread (no Unity API is touched here).
    public class WsConnection : IDisposable
    {
        struct OutMsg
        {
            public bool binary;
            public byte[] data;
        }

        readonly ConcurrentQueue<OutMsg> _out = new ConcurrentQueue<OutMsg>();
        readonly ConcurrentQueue<string> _in = new ConcurrentQueue<string>();
        readonly SemaphoreSlim _outSignal = new SemaphoreSlim(0);

        ClientWebSocket _ws;
        CancellationTokenSource _cts;
        Task _sendTask, _recvTask;

        public volatile string Error;
        public volatile bool Connected;
        public volatile bool Closed;

        public bool TryDequeue(out string msg)
        {
            if (_in == null) { msg = null; return false; }
            return _in.TryDequeue(out msg);
        }

        public void Start(string url, string apiKey)
        {
            _cts = new CancellationTokenSource();
            _ws = new ClientWebSocket();
            try { _ws.Options.SetRequestHeader("Authorization", "Bearer " + apiKey); } catch { }
            try { _ws.Options.SetRequestHeader("User-Agent", "EpicSpark-Voice/1.0"); } catch { }

            var connect = Task.Run(() => ConnectAsync(url));
            _sendTask = Task.Run(() => SendLoop(connect));
            _recvTask = Task.Run(() => RecvLoop(connect));
        }

        async Task ConnectAsync(string url)
        {
            try
            {
                await _ws.ConnectAsync(new Uri(url), _cts.Token);
                Connected = true;
            }
            catch (Exception e)
            {
                Error = "连接失败: " + e.Message;
            }
        }

        public void SendText(string json) => Enqueue(false, Encoding.UTF8.GetBytes(json ?? ""));
        public void SendBinary(byte[] data) => Enqueue(true, data);

        void Enqueue(bool binary, byte[] data)
        {
            if (Closed || data == null) return;
            _out.Enqueue(new OutMsg { binary = binary, data = data });
            try { _outSignal.Release(); } catch { }
        }

        async Task SendLoop(Task connect)
        {
            try
            {
                await connect;
                if (!Connected) return;
                while (!_cts.IsCancellationRequested)
                {
                    await _outSignal.WaitAsync(_cts.Token);
                    OutMsg m;
                    while (_out.TryDequeue(out m))
                    {
                        var type = m.binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text;
                        await _ws.SendAsync(new ArraySegment<byte>(m.data), type, true, _cts.Token);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Error = "发送错误: " + e.Message; }
        }

        async Task RecvLoop(Task connect)
        {
            var buf = new byte[8192];
            var ms = new MemoryStream();
            try
            {
                await connect;
                if (!Connected) { Closed = true; return; }
                while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    var r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buf, 0, r.Count);
                    if (r.EndOfMessage)
                    {
                        _in.Enqueue(Encoding.UTF8.GetString(ms.ToArray()));
                        ms.SetLength(0);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (Error == null) Error = "接收错误: " + e.Message; }
            finally { Closed = true; }
        }

        public void Close()
        {
            Closed = true;
            try { _cts?.Cancel(); } catch { }
            try
            {
                if (_ws != null && _ws.State == WebSocketState.Open)
                    _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).Wait(400);
            }
            catch { }
            try { _ws?.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }
        }

        public void Dispose() => Close();
    }
}
