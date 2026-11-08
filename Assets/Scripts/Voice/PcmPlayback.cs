using UnityEngine;

namespace EpicSpark.Voice
{
    // Gapless streaming playback of PCM16 mono audio.
    //
    // The TTS server pushes audio *faster than real time*, so a fixed-size ring
    // would wrap and overwrite the not-yet-played beginning of the reply (which
    // sounds like the first part is jumbled together). Instead we keep a
    // growable circular buffer: Feed() appends, OnRead() (audio thread) drains.
    public class PcmPlayback : MonoBehaviour
    {
        AudioSource _src;
        AudioClip _clip;
        float[] _buf = new float[1];
        int _head, _count;
        readonly object _lock = new object();
        int _rate;

        public void Init(int sampleRate)
        {
            _rate = Mathf.Max(8000, sampleRate);
            _buf = new float[_rate * 4];   // initial 4 s, grows on demand

            // stream=true: Unity keeps invoking OnRead to fill the (small) clip.
            _clip = AudioClip.Create("EpicSparkVoiceTts", 1024, 1, _rate, true, OnRead);

            _src = gameObject.AddComponent<AudioSource>();
            _src.clip = _clip;
            _src.loop = true;
            _src.playOnAwake = false;
            _src.spatialBlend = 0f;         // 2D playback
            _src.volume = 1f;
            _src.bypassEffects = true;
            _src.bypassListenerEffects = true;
        }

        void OnRead(float[] data)
        {
            lock (_lock)
            {
                int take = Mathf.Min(data.Length, _count);
                for (int i = 0; i < take; i++)
                {
                    data[i] = _buf[_head];
                    _head++;
                    if (_head >= _buf.Length) _head = 0;
                }
                for (int i = take; i < data.Length; i++) data[i] = 0f;
                _count -= take;
            }
        }

        public void Feed(byte[] pcm16)
        {
            if (pcm16 == null || pcm16.Length < 2) return;
            int samples = pcm16.Length / 2;
            lock (_lock)
            {
                EnsureCapacity(samples);
                int tail = _head + _count;
                if (tail >= _buf.Length) tail -= _buf.Length;
                for (int i = 0; i < samples; i++)
                {
                    short s = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
                    _buf[tail] = s / 32768f;
                    tail++;
                    if (tail >= _buf.Length) tail = 0;
                }
                _count += samples;
            }
        }

        // Grows the backing array (keeping sample order) when more room is needed.
        void EnsureCapacity(int extra)
        {
            if (_count + extra <= _buf.Length) return;
            int newCap = Mathf.NextPowerOfTwo(Mathf.Max(_buf.Length * 2, _count + extra));
            var nb = new float[newCap];
            for (int i = 0; i < _count; i++)
            {
                nb[i] = _buf[_head];
                _head++;
                if (_head >= _buf.Length) _head = 0;
            }
            _buf = nb;
            _head = 0;
        }

        /// <summary>播放音量（0..1）。</summary>
        public void SetVolume(float volume)
        {
            if (_src != null) _src.volume = Mathf.Clamp01(volume);
        }

        public float BufferedSeconds
        {
            get { lock (_lock) return _count / (float)_rate; }
        }

        public bool IsPlaying => _src != null && _src.isPlaying;

        // Start playback once a small jitter buffer has accumulated.
        public void PlayIfNeeded(float minSeconds = 0.15f)
        {
            if (_src == null) return;
            if (!_src.isPlaying && BufferedSeconds > minSeconds) _src.Play();
        }

        public void Clear()
        {
            lock (_lock) { _head = 0; _count = 0; }
            if (_src != null && _src.isPlaying) _src.Stop();
        }

        public void Shutdown()
        {
            Clear();
            if (_src != null) { _src.clip = null; Destroy(_src); _src = null; }
            if (_clip != null) { Destroy(_clip); _clip = null; }
        }
    }
}
