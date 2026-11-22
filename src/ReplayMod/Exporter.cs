using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using UnityEngine;
using UnityEngine.PostProcessing;

namespace ReplayMod
{
    // Video export and screenshots.
    //
    // Video is rendered at a fixed frame rate (Time.captureFramerate): each output frame advances the
    // replay by exactly 1/fps of output time, however long the frame takes to render, so the result is
    // smooth even when the PC can't play it live. Frames are piped to FFmpeg, which writes the MP4.
    //
    // Unity can't mix audio faster or slower than real time, so sound is a second pass: the same range
    // is played again in real time while the listener's output is written to a WAV, then FFmpeg merges
    // the two. The camera must be deterministic (path, original camera or attached) for the passes to match.
    internal static class Exporter
    {
        public enum Span { CameraPath, PlayheadToEnd, Selection, WholeReplay }

        public static Span Range = Span.CameraPath;
        public static bool Busy { get; private set; }
        public static bool RecordingSound { get; private set; }
        // Time holds while another mod finishes drawing the view (FarLands' distant land); those frames aren't the video.
        public static bool Holding { get; private set; }
        private static float _waitSince = -1f;
        private const float FarLandsWait = 20f; // real seconds; past it the export goes on with what is there
        public static string Status = "";
        public static bool HideUi => Busy || _shotFrame == Time.frameCount;

        private static bool _cancel;
        private static int _shotFrame = -1, _flashUntil = -1;

        // A supersized screenshot can render a frame later, so the flash stays for two.
        public static bool FlashNow => Time.frameCount <= _flashUntil;

        public static string ReplayName => Path.GetFileNameWithoutExtension(ReplaySession.Data?.Path ?? "replay");

        // Saved at the end of this frame. The UI and path gizmos skip this frame (HideUi), so they're not in it.
        public static string Screenshot()
        {
            string dir = Path.Combine(Plugin.ExportDir, "screenshots");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{ReplayName}_{ReplaySession.T:0000.00}s_{DateTime.Now:HHmmss}.png");
            _shotFrame = Time.frameCount;
            _flashUntil = Time.frameCount + 1;
            ScreenCapture.CaptureScreenshot(file, Mathf.Clamp(Plugin.ScreenshotScale.Value, 1, 4));
            return file;
        }

        // A small picture of this frame for the replay list, without the overlays.
        public static IEnumerator Thumbnail(string replayPath)
        {
            _shotFrame = Time.frameCount;
            yield return new WaitForEndOfFrame();
            RenderTexture small = null;
            Texture2D shot = null, thumb = null;
            try
            {
                shot = ScreenCapture.CaptureScreenshotAsTexture();
                const int width = 320;
                int height = Mathf.Max(1, width * shot.height / Mathf.Max(shot.width, 1));
                small = RenderTexture.GetTemporary(width, height, 0);
                Graphics.Blit(shot, small);
                RenderTexture.active = small;
                thumb = new Texture2D(width, height, TextureFormat.RGB24, false);
                thumb.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                File.WriteAllBytes(Path.ChangeExtension(replayPath, ".jpg"), thumb.EncodeToJPG(80));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Plugin.Log.LogWarning($"thumbnail for {replayPath}: {e.Message}");
            }
            finally
            {
                RenderTexture.active = null;
                if (small) RenderTexture.ReleaseTemporary(small);
                if (shot) UnityEngine.Object.Destroy(shot);
                if (thumb) UnityEngine.Object.Destroy(thumb);
            }
        }

        public const string NeedFfmpeg = "Export needs FFmpeg. Use Install FFmpeg in the Tab menu, or set FfmpegPath in the config.";

        // The menu asks every GUI event, so look on disk at most every 2 s.
        private static string _ffmpeg;
        private static float _ffmpegChecked = -10f;

        public static string FindFfmpeg()
        {
            if (Time.unscaledTime - _ffmpegChecked < 2f) return _ffmpeg;
            _ffmpegChecked = Time.unscaledTime;
            return _ffmpeg = LookForFfmpeg();
        }

        private static string LookForFfmpeg()
        {
            string configured = Plugin.FfmpegPath.Value.Trim().Trim('"');
            if (configured.Length > 0) return File.Exists(configured) ? configured : null;
            foreach (string dir in FfmpegFolders())
            {
                try
                {
                    string exe = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim()), "ffmpeg.exe");
                    if (File.Exists(exe)) return exe;
                }
                catch (ArgumentException) { /* bad PATH entry */ }
            }
            // WinGet's portable packages, whatever their version folder is called.
            try
            {
                string packages = Path.Combine(LocalData, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(packages))
                {
                    foreach (string pkg in Directory.GetDirectories(packages, "*FFmpeg*"))
                    {
                        string[] found = Directory.GetFiles(pkg, "ffmpeg.exe", SearchOption.AllDirectories);
                        if (found.Length > 0) return found[0];
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Plugin.Log.LogWarning($"looking for FFmpeg: {e.Message}");
            }
            return null;
        }

        private static string LocalData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        private static IEnumerable<string> FfmpegFolders()
        {
            char sep = Path.PathSeparator;
            foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(sep)) yield return d;
            // The saved PATHs: the game's copy misses installs made after Steam or the game started.
            foreach (string d in RegistryPath(Registry.CurrentUser, "Environment").Split(sep)) yield return d;
            foreach (string d in RegistryPath(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment").Split(sep)) yield return d;
            // Usual installs that don't touch PATH.
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Path.Combine(LocalData, "Microsoft", "WinGet", "Links");
            yield return Path.Combine(Environment.GetEnvironmentVariable("ChocolateyInstall") ?? @"C:\ProgramData\chocolatey", "bin");
            yield return Path.Combine(home, "scoop", "shims");
            yield return Path.Combine(programs, "ffmpeg", "bin");
            yield return @"C:\ffmpeg\bin";
            yield return Path.Combine(programs, "ShareX");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShareX", "Tools");
        }

        private static string RegistryPath(RegistryKey hive, string key)
        {
            try
            {
                using (RegistryKey k = hive.OpenSubKey(key))
                {
                    return k?.GetValue("Path") as string ?? "";
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"reading PATH from {key}: {e.Message}");
                return "";
            }
        }

        // Opens WinGet in its own window, so its prompts and progress are visible.
        public static string InstallFfmpeg()
        {
            try
            {
                Process.Start(new ProcessStartInfo("winget", "install --id Gyan.FFmpeg -e --source winget") { UseShellExecute = true });
                return "Installing FFmpeg in a separate window. It's found automatically when that finishes.";
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException)
            {
                return "Couldn't start WinGet. Install FFmpeg from ffmpeg.org and set FfmpegPath in the config.";
            }
        }

        public static bool RangeFor(Span span, out float start, out float end)
        {
            start = 0f;
            end = ReplaySession.Duration;
            switch (span)
            {
                case Span.CameraPath:
                    if (CameraPath.Keys.Count < 2) return false;
                    start = CameraPath.Keys[0].t;
                    end = CameraPath.Keys[CameraPath.Keys.Count - 1].t;
                    break;
                case Span.PlayheadToEnd:
                    start = ReplaySession.T;
                    break;
                case Span.Selection:
                    if (!ReplayUI.Selection(out start, out end)) return false;
                    break;
            }
            return end - start > 0.05f;
        }

        // Starts, or cancels a running export.
        public static string Toggle()
        {
            if (Busy)
            {
                _cancel = true;
                return "Canceling export...";
            }
            if (FindFfmpeg() == null) return NeedFfmpeg;
            if (!RangeFor(Range, out float start, out float end))
            {
                return Range == Span.CameraPath ? "Export: the camera path needs at least 2 keys"
                    : Range == Span.Selection ? "Export: drag along the timeline to select a section first"
                    : "Export: nothing to export from here";
            }
            if (Range == Span.CameraPath) CameraPath.Follow = true;
            ReplayUI.CursorFree = false;
            Plugin.Instance.StartCoroutine(Run(start, end));
            return null;
        }

        private static IEnumerator Run(float start, float end)
        {
            Busy = true;
            _cancel = false;
            int fps = Plugin.ExportFps.Value;
            int scale = Mathf.Clamp(Plugin.ExportScale.Value, 1, 4);
            string ffmpeg = FindFfmpeg();
            string name = $"{ReplayName}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
            Directory.CreateDirectory(Plugin.ExportDir);
            string videoOnly = Path.Combine(Plugin.ExportDir, name + (Plugin.ExportAudio.Value ? "_video.mp4" : ".mp4"));
            string wav = Path.Combine(Plugin.ExportDir, name + ".wav");
            string final = Path.Combine(Plugin.ExportDir, name + ".mp4");
            Plugin.Log.LogInfo($"export {start:0.00}-{end:0.00} s at {fps} fps x{scale} to {videoOnly}");

            float volume = AudioListener.volume;
            Process proc = null;
            Stream pipe = null;
            int frames = 0;
            string error = null;
            // Above 1x the game camera renders each frame once, at the export size, into a texture the frame is read
            // from (the screen shows it scaled down). Unity's own larger capture is a second render, which left out
            // what mods queue for the camera's frame (FarLands' trees, rocks and buildings) and smeared motion blur
            // and antialiasing. The game HUD is drawn over the screen, not by the camera: with it on, the old capture
            // is kept, without the two effects that blend earlier frames.
            Camera cam = GameCamera.instance ? GameCamera.instance.GetComponent<Camera>() : null;
            bool direct = scale > 1 && cam && !GameHudShown();
            RenderTexture big = null, camWas = null;
            Texture2D frame = null;
            if (direct)
            {
                big = new RenderTexture(Screen.width * scale, Screen.height * scale, 24, RenderTextureFormat.ARGB32) { name = "Replay Mod export" };
                big.Create();
                frame = new Texture2D(big.width, big.height, TextureFormat.RGB24, false);
                camWas = cam.targetTexture;
                cam.targetTexture = big;
            }
            PostProcessingProfile post = scale > 1 && !direct ? Camera.main?.GetComponent<PostProcessingBehaviour>()?.profile : null;
            bool blurWas = post && post.motionBlur.enabled, aaWas = post && post.antialiasing.enabled;
            if (post)
            {
                post.motionBlur.enabled = false;
                post.antialiasing.enabled = false;
            }
            // Video pass.
            yield return SeekAndSettle(start);
            AudioListener.volume = 0f; // sound comes from the real-time pass
            Time.captureFramerate = fps;
            ReplaySession.Paused = false;
            float nextLog = 0f;
            // For the time left: the video pass by its pace so far, then the sound pass, which runs in real time.
            float videoSince = Time.realtimeSinceStartup;
            float soundSeconds = Plugin.ExportAudio.Value ? SoundSeconds(start, end) : 0f;
            while (!_cancel && ReplaySession.Active && ReplaySession.T < end - 1e-4f && error == null)
            {
                if (ReplaySession.Loading)
                {
                    // Time holds while the destination of a far jump loads; those frames aren't the video.
                    Status = "Exporting video: loading";
                    yield return new WaitForEndOfFrame();
                    if (big) Graphics.Blit(big, (RenderTexture)null);
                    ExportOverlay.Draw(Status + Left(videoSince, Mathf.InverseLerp(start, end, ReplaySession.T), soundSeconds), Mathf.InverseLerp(start, end, ReplaySession.T));
                    continue;
                }
                if (FarLandsCompat.Building)
                {
                    if (_waitSince < 0f) _waitSince = Time.realtimeSinceStartup;
                    if (Time.realtimeSinceStartup - _waitSince < FarLandsWait)
                    {
                        Holding = true;
                        Status = "Exporting video: waiting for FarLands' distant land";
                        yield return new WaitForEndOfFrame();
                        if (big) Graphics.Blit(big, (RenderTexture)null);
                        ExportOverlay.Draw(Status + Left(videoSince, Mathf.InverseLerp(start, end, ReplaySession.T), soundSeconds), Mathf.InverseLerp(start, end, ReplaySession.T));
                        continue;
                    }
                }
                else _waitSince = -1f;
                Holding = false;
                yield return new WaitForEndOfFrame();
                Texture2D tex;
                if (big)
                {
                    RenderTexture.active = big;
                    frame.ReadPixels(new Rect(0, 0, big.width, big.height), 0, 0, false);
                    RenderTexture.active = null;
                    tex = frame;
                    Graphics.Blit(big, (RenderTexture)null);
                }
                else tex = ScreenCapture.CaptureScreenshotAsTexture(scale);
                try
                {
                    if (proc == null)
                    {
                        proc = StartFfmpeg(ffmpeg, $"-y -f rawvideo -pix_fmt {PixFmt(tex.format)} -s {tex.width}x{tex.height} -r {fps} -i - " +
                            $"-vf vflip,crop=trunc(iw/2)*2:trunc(ih/2)*2 -c:v libx264 -preset medium -crf 16 -pix_fmt yuv420p \"{videoOnly}\"", stdin: true);
                        pipe = proc.StandardInput.BaseStream;
                    }
                    byte[] raw = tex.GetRawTextureData();
                    pipe.Write(raw, 0, raw.Length);
                    frames++;
                }
                catch (Exception e)
                {
                    error = e.Message;
                }
                if (tex != frame) UnityEngine.Object.Destroy(tex);
                Status = $"Exporting video: {Mathf.InverseLerp(start, end, ReplaySession.T) * 100f:0}% ({frames} frames)";
                ExportOverlay.Draw(Status + Left(videoSince, Mathf.InverseLerp(start, end, ReplaySession.T), soundSeconds), Mathf.InverseLerp(start, end, ReplaySession.T));
                if (Time.unscaledTime >= nextLog)
                {
                    nextLog = Time.unscaledTime + 5f;
                    Plugin.Log.LogInfo(Status);
                }
            }
            Time.captureFramerate = 0;
            if (big)
            {
                if (cam) cam.targetTexture = camWas;
                big.Release();
                UnityEngine.Object.Destroy(big);
                UnityEngine.Object.Destroy(frame);
            }
            if (post)
            {
                post.motionBlur.enabled = blurWas;
                post.antialiasing.enabled = aaWas;
            }
            Holding = false;
            _waitSince = -1f;
            AudioListener.volume = volume;
            ReplaySession.Paused = true;
            if (proc != null)
            {
                try { pipe.Close(); } catch (Exception) { /* ffmpeg already gone */ }
                while (!proc.HasExited) yield return null;
                if (proc.ExitCode != 0 && error == null) error = $"FFmpeg exited with code {proc.ExitCode} (see the BepInEx log)";
            }

            // Audio pass, in real time.
            if (!_cancel && error == null && Plugin.ExportAudio.Value && ReplaySession.Active)
            {
                yield return SeekAndSettle(start);
                WavCapture cap = WavCapture.Attach();
                if (cap)
                {
                    cap.Begin();
                    RecordingSound = true; // time doesn't wait for loading here, or the sound would drift
                    ReplaySession.Paused = false;
                    while (!_cancel && ReplaySession.Active && ReplaySession.T < end - 1e-4f)
                    {
                        Status = $"Recording sound (real time): {Mathf.InverseLerp(start, end, ReplaySession.T) * 100f:0}%";
                        yield return new WaitForEndOfFrame();
                        ExportOverlay.Draw(Status + $"   about {Duration(soundSeconds * (1f - Mathf.InverseLerp(start, end, ReplaySession.T)))} left", Mathf.InverseLerp(start, end, ReplaySession.T));
                    }
                    ReplaySession.Paused = true;
                    RecordingSound = false;
                    try
                    {
                        cap.End(wav);
                    }
                    catch (Exception e)
                    {
                        error = $"WAV: {e.Message}";
                    }
                    if (error == null && !_cancel)
                    {
                        Status = "Merging sound and video...";
                        Process mux = null;
                        try
                        {
                            mux = StartFfmpeg(ffmpeg, $"-y -i \"{videoOnly}\" -i \"{wav}\" -c:v copy -c:a aac -b:a 192k -shortest \"{final}\"", stdin: false);
                        }
                        catch (Exception e)
                        {
                            error = e.Message;
                        }
                        if (mux != null)
                        {
                            while (!mux.HasExited) yield return null;
                            if (mux.ExitCode == 0)
                            {
                                TryDelete(videoOnly);
                                TryDelete(wav);
                            }
                            else error = $"FFmpeg merge exited with code {mux.ExitCode}";
                        }
                    }
                }
            }

            Busy = RecordingSound = false;
            string where = File.Exists(final) ? final : videoOnly;
            Status = _cancel ? "Export canceled" : error != null ? $"Export failed: {error}" : $"Exported {frames} frames to {where}";
            if (error != null) Plugin.Log.LogError(Status);
            else Plugin.Log.LogInfo(Status);
            ReplayUI.Notify(Status);
            if (!_cancel && error == null && Plugin.ExportOpen.Value)
            {
                try { Process.Start("explorer.exe", $"/select,\"{where.Replace('/', '\\')}\""); }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException) { Plugin.Log.LogWarning($"couldn't open File Explorer: {e.Message}"); }
            }
        }

        // The game HUD, or its health bars and damage numbers, are on in the replay.
        private static bool GameHudShown() => ReplayUI.CombatHud || (Hud.instance && !Hud.instance.m_userHidden);

        // "   about 3 min 20 s left", once enough of the pass is done to judge its pace.
        private static string Left(float since, float done, float after)
        {
            if (done < 0.02f) return "";
            float left = (Time.realtimeSinceStartup - since) * (1f - done) / done + after;
            return $"   about {Duration(left)} left";
        }

        private static string Duration(float seconds)
        {
            int s = Mathf.Max(0, Mathf.CeilToInt(seconds));
            return s >= 60 ? $"{s / 60} min {s % 60:00} s" : $"{s} s";
        }

        // How long the real-time sound pass takes: the length of the video.
        private static float SoundSeconds(float start, float end) =>
            ReplaySession.PathSpeed ? CameraPath.OutputTime(end) - CameraPath.OutputTime(start) : (end - start) / Mathf.Max(ReplaySession.Speed, 0.01f);

        // Seek while paused and wait for objects to be built and posed before the first frame.
        private static IEnumerator SeekAndSettle(float t)
        {
            ReplaySession.Paused = true;
            ReplaySession.Seek(t);
            for (int i = 0; i < 45; i++) yield return null;
        }

        private static string PixFmt(TextureFormat f)
        {
            switch (f)
            {
                case TextureFormat.ARGB32: return "argb";
                case TextureFormat.BGRA32: return "bgra";
                case TextureFormat.RGB24: return "rgb24";
                default: return "rgba";
            }
        }

        private static Process StartFfmpeg(string exe, string args, bool stdin)
        {
            var psi = new ProcessStartInfo(exe, "-hide_banner -loglevel error " + args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = stdin,
                RedirectStandardError = true,
            };
            var p = new Process { StartInfo = psi };
            p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Plugin.Log.LogWarning($"ffmpeg: {e.Data}"); };
            p.Start();
            p.BeginErrorReadLine();
            return p;
        }

        private static void TryDelete(string file)
        {
            try { File.Delete(file); } catch (Exception) { /* leave it */ }
        }
    }

    // Records what the AudioListener hears. OnAudioFilterRead runs on the audio thread.
    internal class WavCapture : MonoBehaviour
    {
        private readonly List<float> _samples = new List<float>();
        private readonly object _lock = new object();
        private volatile bool _on;
        private int _channels = 2;

        public static WavCapture Attach()
        {
            AudioListener l = FindFirstObjectByType<AudioListener>();
            if (!l) return null;
            return l.GetComponent<WavCapture>() ?? l.gameObject.AddComponent<WavCapture>();
        }

        public void Begin()
        {
            lock (_lock) _samples.Clear();
            _on = true;
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (!_on) return;
            lock (_lock)
            {
                _channels = channels;
                _samples.AddRange(data);
            }
        }

        public void End(string path)
        {
            _on = false;
            float[] s;
            int ch;
            lock (_lock)
            {
                s = _samples.ToArray();
                ch = _channels;
                _samples.Clear();
            }
            WriteWav(path, s, ch, AudioSettings.outputSampleRate);
        }

        // 16-bit PCM WAV.
        internal static void WriteWav(string path, float[] samples, int channels, int rate)
        {
            using (var w = new BinaryWriter(File.Create(path)))
            {
                int bytes = samples.Length * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + bytes);
                w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)channels);
                w.Write(rate);
                w.Write(rate * channels * 2);
                w.Write((short)(channels * 2));
                w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(bytes);
                foreach (float f in samples) w.Write((short)Mathf.RoundToInt(Mathf.Clamp(f, -1f, 1f) * 32767f));
            }
        }
    }
}
