using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.Networking;

namespace EpicSpark.AI.OpenAI
{
    /// <summary>
    /// SSE 流式下载处理器: 对 downloadHandler 收到的字节流按行(0x0A)切分,
    /// 每收到一条完整 "data: …" 事件就回调 onEvent(payload)。
    /// 按行解码 UTF-8, 避免多字节字符跨 chunk 时被截断乱码。
    /// 忽略 "data:" 之外的 SSE 字段(event:/id:/注释/空行), "data: [DONE]" 原样回调。
    /// </summary>
    internal class StreamingDownloadHandler : DownloadHandlerScript
    {
        const int MaxLineBytes = 1 << 20; // 单行上限 1MB, 超出丢弃, 防止异常流导致内存膨胀

        readonly Action<string> onEvent;
        readonly List<byte> pending = new List<byte>(4096); // 尚未遇到换行的残余字节

        public StreamingDownloadHandler(Action<string> onEvent)
        {
            this.onEvent = onEvent;
        }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength == 0) return true;
            for (int i = 0; i < dataLength; i++)
            {
                byte b = data[i];
                if (b == 0x0A) FlushLine();
                else if (pending.Count < MaxLineBytes) pending.Add(b);
            }
            return true;
        }

        void FlushLine()
        {
            if (pending.Count == 0) return; // 空行(SSE 事件分隔符), 忽略
            var segment = new byte[pending.Count];
            pending.CopyTo(segment);
            pending.Clear();
            string line = Encoding.UTF8.GetString(segment).TrimEnd('\r');
            if (line.StartsWith("data:"))
            {
                string payload = line.Substring(5).TrimStart();
                onEvent?.Invoke(payload);
            }
            // event:/id:/: 等其他 SSE 字段忽略
        }

        protected override void CompleteContent()
        {
            if (pending.Count > 0) FlushLine();
        }
    }
}
