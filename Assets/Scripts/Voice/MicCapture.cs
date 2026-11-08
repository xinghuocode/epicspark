using System;
using System.Collections.Generic;
using UnityEngine;

namespace EpicSpark.Voice
{
    // Captures the microphone and emits 16 kHz mono PCM16 chunks, plus a simple
    // RMS level used for voice-activity detection. All work happens on the Unity
    // main thread (Update), so the event callback is safe to use directly.
    public class MicCapture : MonoBehaviour
    {
        public event Action<byte[]> OnPcm;

        public float Level { get; private set; }
        public bool Voiced { get; private set; }
        public float RmsThreshold = 0.012f;
        public bool IsRunning { get; private set; }
        public string Device { get; private set; }

        const int TargetRate = 16000;

        AudioClip _clip;
        float[] _buf;
        int _lastPos;
        readonly List<byte> _scratch = new List<byte>(8192);

        public bool StartCapture(string device, int clipSeconds = 2)
        {
            if (Microphone.devices.Length == 0) return false;
            Device = string.IsNullOrEmpty(device) ? Microphone.devices[0] : device;

            _clip = Microphone.Start(Device, true, Mathf.Max(1, clipSeconds), TargetRate);
            if (_clip == null || _clip.frequency <= 0)
            {
                _clip = null;
                return false;
            }
            _buf = new float[_clip.samples];
            _lastPos = 0;
            IsRunning = true;
            return true;
        }

        public void StopCapture()
        {
            IsRunning = false;
            if (!string.IsNullOrEmpty(Device))
            {
                try { Microphone.End(Device); } catch { }
            }
            _clip = null;
            _buf = null;
            Device = null;
            Level = 0f;
            Voiced = false;
        }

        void Update()
        {
            if (!IsRunning || _clip == null || _buf == null) return;
            if (Microphone.devices.Length == 0) return;

            int pos = Microphone.GetPosition(Device);
            if (pos < 0 || pos == _lastPos)
            {
                Level = 0f;
                Voiced = false;
                return;
            }

            _clip.GetData(_buf, 0);

            double sum = 0;
            int n = 0;
            _scratch.Clear();
            if (pos > _lastPos) EmitSegment(_lastPos, pos, ref sum, ref n);
            else
            {
                EmitSegment(_lastPos, _clip.samples, ref sum, ref n);
                EmitSegment(0, pos, ref sum, ref n);
            }
            _lastPos = pos;

            float rms = n > 0 ? Mathf.Sqrt((float)(sum / n)) : 0f;
            Level = rms;
            Voiced = rms >= RmsThreshold;

            if (_scratch.Count > 0 && OnPcm != null)
            {
                OnPcm(_scratch.ToArray());
            }
        }

        // Converts [from, to) of the current clip buffer to 16 kHz PCM16, appending
        // to _scratch, and accumulates the RMS level over the raw samples.
        void EmitSegment(int from, int to, ref double sum, ref int n)
        {
            int count = to - from;
            if (count <= 0) return;

            for (int i = from; i < to; i++)
            {
                double s = _buf[i];
                sum += s * s;
                n++;
            }

            if (_clip.frequency == TargetRate)
            {
                for (int i = from; i < to; i++) AppendInt16(_buf[i]);
                return;
            }

            // Linear resample to 16 kHz when the hardware runs at another rate.
            double ratio = (double)_clip.frequency / TargetRate;
            int outCount = (int)(count / ratio);
            for (int j = 0; j < outCount; j++)
            {
                double src = from + j * ratio;
                int i0 = (int)src;
                int i1 = Mathf.Min(i0 + 1, to - 1);
                double frac = src - i0;
                float v = (float)(_buf[i0] * (1.0 - frac) + _buf[i1] * frac);
                AppendInt16(v);
            }
        }

        void AppendInt16(float v)
        {
            int s = Mathf.Clamp((int)(v * 32767f), -32768, 32767);
            _scratch.Add((byte)(s & 0xFF));
            _scratch.Add((byte)((s >> 8) & 0xFF));
        }
    }
}
