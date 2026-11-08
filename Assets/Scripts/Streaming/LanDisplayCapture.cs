// 展示端接口 · 画面采集。
// 只允许在主线程调用：把玩家相机（Camera.main）的画面复制到一台隐藏的临时相机上，
// 渲染到独立 RenderTexture 后 ReadPixels + EncodeToJPG，避免干扰玩家视野与后处理。
using System;
using UnityEngine;

namespace EpicSpark.Streaming
{
    /// <summary>负责「渲染到 RenderTexture → 读像素 → 编码 JPEG」，并提供整屏截图兜底。</summary>
    internal sealed class LanDisplayCapture : IDisposable
    {
        private GameObject _go;
        private Camera _camera;
        private RenderTexture _rt;
        private Texture2D _tex;
        private int _width;
        private int _height;
        private string _lastSetupError;

        public LanDisplayCapture()
        {
            _go = new GameObject("[EpicSpark] LanDisplayCaptureCamera");
            _go.hideFlags = HideFlags.HideAndDontSave;
            _camera = _go.AddComponent<Camera>();
            _camera.enabled = false;
            _camera.depth = -100f;
            _camera.clearFlags = CameraClearFlags.Skybox;
        }

        /// <summary>临时相机（用于解析时排除自身，避免以后误把采集相机当玩家相机）。</summary>
        public Camera CaptureCamera
        {
            get { return _camera; }
        }

        public int Width
        {
            get { return _width; }
        }

        public int Height
        {
            get { return _height; }
        }

        public string LastSetupError
        {
            get { return _lastSetupError; }
        }

        /// <summary>准备/重建采集目标。分辨率变化时会重建 RT 与 Texture2D。</summary>
        public void Configure(int width, int height)
        {
            width = Mathf.Clamp(width, 64, 3840);
            height = Mathf.Clamp(height, 36, 2160);
            if (_rt != null && _tex != null && _width == width && _height == height) return;

            ReleaseTexture();
            _width = width;
            _height = height;
            try
            {
                _rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
                {
                    name = "[EpicSpark] LanDisplayRT",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    antiAliasing = 1,
                    useMipMap = false,
                };
                _rt.Create();
                _tex = new Texture2D(width, height, TextureFormat.RGB24, false)
                {
                    name = "[EpicSpark] LanDisplayReadback",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                _lastSetupError = null;
            }
            catch (Exception exc)
            {
                _lastSetupError = "创建采集纹理失败：" + exc.GetType().Name + ": " + exc.Message;
                ReleaseTexture();
            }
        }

        /// <summary>把玩家相机当前状态同步到采集相机，并打开采集帧。必须在 WaitForEndOfFrame 之前调用。</summary>
        public bool Prepare(Camera source)
        {
            if (source == null || _camera == null || _rt == null)
            {
                return false;
            }

            try
            {
                Transform from = source.transform;
                _camera.transform.SetPositionAndRotation(from.position, from.rotation);
                // CopyFrom 会把投影方式、裁剪面、cullingMask、后处理相关的相机设置一并复制过来。
                _camera.CopyFrom(source);
                _camera.targetTexture = _rt;
                _camera.rect = new Rect(0f, 0f, 1f, 1f);
                _camera.enabled = true;
                return true;
            }
            catch (Exception exc)
            {
                _lastSetupError = "同步相机失败：" + exc.GetType().Name + ": " + exc.Message;
                return false;
            }
        }

        /// <summary>从 RenderTexture 读回像素并编码为 JPEG。必须在 WaitForEndOfFrame 之后调用。</summary>
        public bool Complete(int jpegQuality, out byte[] jpeg, out int width, out int height, out string error)
        {
            jpeg = null;
            width = _width;
            height = _height;
            error = null;

            if (_rt == null || _tex == null)
            {
                error = _lastSetupError ?? "采集纹理尚未初始化";
                return false;
            }

            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = _rt;
                _tex.ReadPixels(new Rect(0f, 0f, _width, _height), 0, 0, false);
                _tex.Apply(false);
                jpeg = ImageConversion.EncodeToJPG(_tex, Mathf.Clamp(jpegQuality, 30, 95));
                if (jpeg == null || jpeg.Length == 0)
                {
                    error = "EncodeToJPG 返回空数据";
                    return false;
                }
                return true;
            }
            catch (Exception exc)
            {
                error = "读取/编码画面失败：" + exc.GetType().Name + ": " + exc.Message;
                return false;
            }
            finally
            {
                RenderTexture.active = previous;
                if (_camera != null)
                {
                    _camera.enabled = false;
                }
            }
        }

        /// <summary>整屏截图兜底（包含 UI）。必须在 WaitForEndOfFrame 之后调用。</summary>
        public bool CompleteScreenCapture(int jpegQuality, out byte[] jpeg, out int width, out int height, out string error)
        {
            jpeg = null;
            width = Screen.width;
            height = Screen.height;
            error = null;

            Texture2D shot = null;
            try
            {
                shot = ScreenCapture.CaptureScreenshotAsTexture();
                if (shot == null)
                {
                    error = "ScreenCapture.CaptureScreenshotAsTexture 返回 null";
                    return false;
                }
                width = shot.width;
                height = shot.height;
                jpeg = ImageConversion.EncodeToJPG(shot, Mathf.Clamp(jpegQuality, 30, 95));
                if (jpeg == null || jpeg.Length == 0)
                {
                    error = "EncodeToJPG 返回空数据";
                    return false;
                }
                return true;
            }
            catch (Exception exc)
            {
                error = "整屏截图失败：" + exc.GetType().Name + ": " + exc.Message;
                return false;
            }
            finally
            {
                if (shot != null)
                {
                    UnityEngine.Object.Destroy(shot);
                }
            }
        }

        public void ReleaseTexture()
        {
            if (_tex != null)
            {
                UnityEngine.Object.Destroy(_tex);
                _tex = null;
            }
            if (_rt != null)
            {
                _rt.Release();
                UnityEngine.Object.Destroy(_rt);
                _rt = null;
            }
        }

        public void Dispose()
        {
            ReleaseTexture();
            if (_go != null)
            {
                UnityEngine.Object.Destroy(_go);
                _go = null;
            }
            _camera = null;
        }
    }
}
