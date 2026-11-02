using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UnityAgentBridge
{
    /// <summary>Screenshot capture for both edit mode and play mode.</summary>
    public static class ScreenshotCommands
    {
        public static void Register()
        {
            CommandRegistry.Register("screenshot.game", "抓取游戏画面（播放模式用 ScreenCapture；编辑模式渲染主相机，不含 Screen Space Overlay UI）", "width?=1280, height?=720, path?, inline?",
                delegate(Dictionary<string, object> args)
                {
                    EnsureGraphics();
                    int width = Mathf.Clamp(J.Int(args, "width", 1280), 16, 8192);
                    int height = Mathf.Clamp(J.Int(args, "height", 720), 16, 8192);
                    string path = J.Str(args, "path");
                    bool inline = J.Bool(args, "inline", string.IsNullOrEmpty(path));

                    if (EditorApplication.isPlaying)
                    {
                        try
                        {
                            var texture = ScreenCapture.CaptureScreenshotAsTexture();
                            if (texture != null)
                            {
                                try { return Result(texture.EncodeToPNG(), texture.width, texture.height, path, inline, "screencapture"); }
                                finally { Object.DestroyImmediate(texture); }
                            }
                        }
                        catch (Exception)
                        {
                            // fall back to camera rendering below
                        }
                    }

                    var camera = Camera.main;
                    if (camera == null)
                    {
                        var cameras = Object.FindObjectsOfType<Camera>();
                        for (int i = 0; i < cameras.Length; i++)
                        {
                            if (cameras[i].isActiveAndEnabled) { camera = cameras[i]; break; }
                        }
                    }
                    if (camera == null) throw new CommandException("场景中没有可用相机", "not_found");

                    return CaptureCamera(camera, width, height, path, inline, "camera-render");
                });

            CommandRegistry.Register("screenshot.camera", "用指定相机渲染一张图（可用于任意相机/预览）", "camera?(名称或#id，默认主相机), width?=1280, height?=720, path?, inline?",
                delegate(Dictionary<string, object> args)
                {
                    EnsureGraphics();
                    int width = Mathf.Clamp(J.Int(args, "width", 1280), 16, 8192);
                    int height = Mathf.Clamp(J.Int(args, "height", 720), 16, 8192);
                    string path = J.Str(args, "path");
                    bool inline = J.Bool(args, "inline", string.IsNullOrEmpty(path));

                    Camera camera = Camera.main;
                    string target = J.Str(args, "camera");
                    if (!string.IsNullOrEmpty(target))
                    {
                        var go = Obj.GameObject(target);
                        camera = go.GetComponent<Camera>();
                        if (camera == null) throw new CommandException(go.name + " 上没有 Camera 组件", "not_found");
                    }
                    if (camera == null) throw new CommandException("场景中没有可用相机", "not_found");

                    return CaptureCamera(camera, width, height, path, inline, "camera-render");
                });

            CommandRegistry.Register("screenshot.scene", "抓取当前 Scene 视图", "width?=1280, height?=720, path?, inline?",
                delegate(Dictionary<string, object> args)
                {
                    EnsureGraphics();
                    var sceneView = SceneView.lastActiveSceneView;
                    if (sceneView == null || sceneView.camera == null)
                        throw new CommandException("没有可用的 Scene 视图", "not_found");

                    int width = Mathf.Clamp(J.Int(args, "width", 1280), 16, 8192);
                    int height = Mathf.Clamp(J.Int(args, "height", 720), 16, 8192);
                    string path = J.Str(args, "path");
                    bool inline = J.Bool(args, "inline", string.IsNullOrEmpty(path));

                    return CaptureCamera(sceneView.camera, width, height, path, inline, "scene-view");
                });
        }

        static void EnsureGraphics()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new CommandException("当前编辑器没有图形设备（-nographics 批处理模式），无法截图", "no_graphics");
        }

        static Dictionary<string, object> CaptureCamera(Camera camera, int width, int height, string path, bool inline, string source)
        {
            var renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            Texture2D texture = null;

            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();

                RenderTexture.active = renderTexture;
                texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();

                return Result(texture.EncodeToPNG(), width, height, path, inline, source);
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (texture != null) Object.DestroyImmediate(texture);
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        static Dictionary<string, object> Result(byte[] png, int width, int height, string path, bool inline, string source)
        {
            var result = new Dictionary<string, object>
            {
                { "width", width },
                { "height", height },
                { "bytes", png.Length },
                { "source", source }
            };

            if (!string.IsNullOrEmpty(path))
            {
                string full = Path.IsPathRooted(path) ? path : Path.Combine(BridgePaths.ProjectRoot, path);
                string directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
                File.WriteAllBytes(full, png);
                result["path"] = full;
            }

            if (inline) result["base64"] = Convert.ToBase64String(png);
            return result;
        }
    }
}
