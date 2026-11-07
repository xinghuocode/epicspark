using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace EpicSpark.MovementTest.Tests
{
    public sealed class MovementTestSceneTests
    {
        [UnityTest]
        public IEnumerator MovementScene_LoadsExpectedPlayerAndCamera()
        {
            AsyncOperation load = SceneManager.LoadSceneAsync("MovementTest", LoadSceneMode.Single);
            Assert.That(load, Is.Not.Null, "MovementTest 必须已加入 Build Settings");
            yield return load;
            yield return null;

            var player = GameObject.Find("Player");
            Assert.That(player, Is.Not.Null, "场景应包含 Player");
            var character = player.GetComponent<CharacterController>();
            var controller = player.GetComponent<InputSystemPlayerController>();
            Assert.That(character, Is.Not.Null);
            Assert.That(controller, Is.Not.Null, "Player 应使用 Input System 控制器");
            Assert.That(controller.enabled, Is.True);
            Assert.That(player.GetComponent<SimplePlayerController>(), Is.Null,
                "场景不能同时启用 legacy 与 Input System 两套控制器");
            Assert.That(controller.swapStickXY, Is.True, "SC-B03 默认交换 X/Y");
            Assert.That(controller.invertY, Is.True, "SC-B03 默认反转 Y");
            Assert.That(Camera.main, Is.Not.Null, "场景应有主相机");

            Debug.Log($"[MovementTestSmoke] Scene loaded; player={player.transform.position}, " +
                      $"grounded={character.isGrounded}, camera={Camera.main.name}");

            // 仅显式设置环境变量时截图，普通无图形回归测试不依赖 GPU。
            if (Environment.GetEnvironmentVariable("EPICSPARK_CAPTURE") == "1")
            {
                string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs"));
                Directory.CreateDirectory(directory);
                string screenshot = Path.Combine(directory, "MovementTest-playmode.png");

                // 批处理没有可捕获的 Game View，使用主相机离屏渲染验证真实场景画面。
                yield return null;
                var camera = Camera.main;
                var renderTexture = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
                var image = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                RenderTexture previousTarget = camera.targetTexture;
                RenderTexture previousActive = RenderTexture.active;
                try
                {
                    camera.targetTexture = renderTexture;
                    camera.Render();
                    RenderTexture.active = renderTexture;
                    image.ReadPixels(new Rect(0f, 0f, 1280f, 720f), 0, 0);
                    image.Apply();
                    File.WriteAllBytes(screenshot, image.EncodeToPNG());
                }
                finally
                {
                    camera.targetTexture = previousTarget;
                    RenderTexture.active = previousActive;
                    renderTexture.Release();
                    UnityEngine.Object.DestroyImmediate(renderTexture);
                    UnityEngine.Object.DestroyImmediate(image);
                }

                Assert.That(File.Exists(screenshot), Is.True, "Play Mode 截图应已写入 Logs");
                Assert.That(new FileInfo(screenshot).Length, Is.GreaterThan(1024));
                Debug.Log($"[MovementTestSmoke] Off-screen render saved: {screenshot}");
            }
        }
    }
}
