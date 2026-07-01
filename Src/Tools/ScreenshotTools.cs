using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Lib.GAB.Tools;
using UnityEngine;

namespace ValBridgeServer.Tools
{
    /// <summary>
    /// Screenshot capture for the LIVE GPU client. Two modes:
    ///
    ///   includeUi = true  (default) — the fully composited on-screen frame, INCLUDING
    ///     Valheim's Screen Space Overlay UI: HUD, health/stamina, hotbar, and crucially
    ///     the MINIMAP and FULL MAP (both are overlay canvases). This is "exactly what
    ///     the player sees." Captured at WaitForEndOfFrame by reading back the screen
    ///     framebuffer — the standard, Linux/Vulkan-safe way to grab the composite.
    ///
    ///   includeUi = false — the game camera rendered off-screen with NO overlay UI.
    ///     A clean, un-occluded frame of the 3D world — best for eyeballing custom
    ///     geometry/art (portal mesh, cairn material, trailside camp) without a health
    ///     bar sitting over the thing under inspection. Overlay UI (incl. the map) is
    ///     absent by construction, since overlay canvases aren't part of any camera's
    ///     render.
    ///
    /// Both downscale to ~1100px longest edge by default (multi-MB PNGs trip HTTP 413
    /// on the way into chat). MUST run on the GPU client; a -nographics server renders
    /// every shader as InternalErrorShader (pink) and is effectively pixel-blind.
    /// </summary>
    public class ScreenshotTools
    {
        private const int DefaultMaxEdge = 1100;
        private const int MaxAllowedEdge = 2160;

        [Tool("capture_screenshot", Description =
            "Capture what the player currently sees on the GPU client and save it as a PNG on the game host; " +
            "returns the absolute file path and dimensions. includeUi=true (default) captures the full composited " +
            "screen WITH overlay UI — HUD, minimap, and the FULL MAP (needed to inspect map mods). includeUi=false " +
            "captures the 3D camera only with NO UI — a clean frame for inspecting custom geometry/art without HUD " +
            "occlusion (the map is NOT visible in this mode). Downscaled to ~1100px longest edge by default so it fits chat.")]
        public object CaptureScreenshot(
            [ToolParameter(Description = "Longest-edge pixel size of the output (default 1100, max 2160). Aspect ratio is preserved.")] int maxEdge = DefaultMaxEdge,
            [ToolParameter(Description = "true (default) = full composited screen WITH overlay UI (HUD, minimap, full map). false = 3D camera only, NO UI (clean geometry; map not shown).")] bool includeUi = true,
            [ToolParameter(Description = "Optional absolute output path. Defaults to a timestamped file under the system temp dir.")] string? outputPath = null)
        {
            var tcs = new TaskCompletionSource<object>();
            int edge = Mathf.Clamp(maxEdge <= 0 ? DefaultMaxEdge : maxEdge, 16, MaxAllowedEdge);
            string path = ResolvePath(outputPath);

            if (includeUi)
            {
                // Composited-frame path needs end-of-frame timing, which requires a
                // coroutine. Enqueue onto the main thread, then start the coroutine there.
                MainThreadDispatcher.Instance.Enqueue(() =>
                {
                    try
                    {
                        MainThreadDispatcher.Instance.StartCoroutine(CaptureCompositedAtEndOfFrame(edge, path, tcs));
                    }
                    catch (Exception ex)
                    {
                        tcs.SetResult(new { success = false, error = ex.Message });
                    }
                });
            }
            else
            {
                // Camera-only path is synchronous and valid from Update().
                MainThreadDispatcher.Instance.Enqueue(() => CaptureCameraOnly(edge, path, tcs));
            }

            return tcs.Task.Result;
        }

        // ── includeUi = true : composited screen incl. overlay UI ────────────────────
        private IEnumerator CaptureCompositedAtEndOfFrame(int edge, string path, TaskCompletionSource<object> tcs)
        {
            // Must wait until the frame (world + all overlay canvases) is fully drawn.
            // Keep the yield OUT of any try/catch — C# forbids `yield` inside a try that
            // has a catch clause.
            yield return new WaitForEndOfFrame();
            DoCompositedCapture(edge, path, tcs);
        }

        private void DoCompositedCapture(int edge, string path, TaskCompletionSource<object> tcs)
        {
            Texture2D? full = null;
            Texture2D? scaled = null;
            RenderTexture? rt = null;
            RenderTexture? prevActive = null;
            try
            {
                int srcW = Mathf.Max(1, Screen.width);
                int srcH = Mathf.Max(1, Screen.height);

                // Read the live screen framebuffer (RenderTexture.active == null == backbuffer).
                full = new Texture2D(srcW, srcH, TextureFormat.RGB24, false);
                full.ReadPixels(new Rect(0, 0, srcW, srcH), 0, 0);
                full.Apply(false);

                Texture2D outTex = full;
                ComputeOut(srcW, srcH, edge, out int outW, out int outH);
                if (outW != srcW || outH != srcH)
                {
                    // GPU bilinear downscale via Blit, then read back the smaller target.
                    rt = RenderTexture.GetTemporary(outW, outH, 0, RenderTextureFormat.ARGB32);
                    prevActive = RenderTexture.active;
                    Graphics.Blit(full, rt);
                    RenderTexture.active = rt;
                    scaled = new Texture2D(outW, outH, TextureFormat.RGB24, false);
                    scaled.ReadPixels(new Rect(0, 0, outW, outH), 0, 0);
                    scaled.Apply(false);
                    RenderTexture.active = prevActive;
                    prevActive = null;
                    outTex = scaled;
                }

                byte[] png = ImageConversion.EncodeToPNG(outTex);
                File.WriteAllBytes(path, png);
                tcs.SetResult(new
                {
                    success = true,
                    path,
                    width = outTex.width,
                    height = outTex.height,
                    bytes = png.Length,
                    includeUi = true
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { success = false, error = ex.Message });
            }
            finally
            {
                if (prevActive != null) RenderTexture.active = prevActive;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (scaled != null) UnityEngine.Object.Destroy(scaled);
                if (full != null) UnityEngine.Object.Destroy(full);
            }
        }

        // ── includeUi = false : 3D camera only, no overlay UI ────────────────────────
        private void CaptureCameraOnly(int edge, string path, TaskCompletionSource<object> tcs)
        {
            RenderTexture? rt = null;
            RenderTexture? prevActive = null;
            RenderTexture? prevCamTarget = null;
            Texture2D? tex = null;
            try
            {
                var cam = Camera.main;
                if (cam == null)
                {
                    var cams = Camera.allCameras;
                    if (cams != null && cams.Length > 0) cam = cams[0];
                }
                if (cam == null)
                {
                    tcs.SetResult(new { success = false, error = "No active camera found (is a world loaded?)" });
                    return;
                }

                int srcW = Mathf.Max(1, Screen.width);
                int srcH = Mathf.Max(1, Screen.height);
                ComputeOut(srcW, srcH, edge, out int outW, out int outH);

                rt = RenderTexture.GetTemporary(outW, outH, 24, RenderTextureFormat.ARGB32);
                prevActive = RenderTexture.active;
                prevCamTarget = cam.targetTexture;

                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(outW, outH, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, outW, outH), 0, 0);
                tex.Apply(false);

                cam.targetTexture = prevCamTarget;
                RenderTexture.active = prevActive;
                prevActive = null;

                byte[] png = ImageConversion.EncodeToPNG(tex);
                File.WriteAllBytes(path, png);
                tcs.SetResult(new
                {
                    success = true,
                    path,
                    width = outW,
                    height = outH,
                    bytes = png.Length,
                    includeUi = false,
                    camera = cam.name
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { success = false, error = ex.Message });
            }
            finally
            {
                if (prevActive != null) RenderTexture.active = prevActive;
                if (tex != null) UnityEngine.Object.Destroy(tex);
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }

        // ── helpers ──────────────────────────────────────────────────────────────────
        private static void ComputeOut(int srcW, int srcH, int edge, out int outW, out int outH)
        {
            float aspect = (float)srcW / Mathf.Max(1, srcH);
            if (aspect >= 1f) { outW = edge; outH = Mathf.Max(1, Mathf.RoundToInt(edge / aspect)); }
            else { outH = edge; outW = Mathf.Max(1, Mathf.RoundToInt(edge * aspect)); }
            // never upscale past source
            if (outW > srcW || outH > srcH) { outW = srcW; outH = srcH; }
        }

        private static string ResolvePath(string? outputPath)
        {
            string path = string.IsNullOrWhiteSpace(outputPath)
                ? Path.Combine(Path.GetTempPath(), $"valheim_capture_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png")
                : outputPath!;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            return path;
        }
    }
}
