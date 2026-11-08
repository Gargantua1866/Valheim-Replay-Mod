using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ReplayMod
{
    [BepInPlugin(Guid, "Replay Mod", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.gargantua.replaymod";
        public const string Version = "0.0.32";

        internal static ManualLogSource Log;
        internal static string ReplayDir, BufferDir;
        internal static ConfigEntry<bool> ReplayBuffer;
        internal static ConfigEntry<float> ReplayBufferMinutes;
        internal static ConfigEntry<KeyboardShortcut> ReplayBufferKey, ReplayBufferToggleKey;
        internal static ConfigEntry<bool> ExportOpen;

        internal static ConfigEntry<KeyboardShortcut> RecordKey;
        internal static ConfigEntry<float> SampleRate;
        internal static ConfigEntry<float> DynamicRadius;
        internal static ConfigEntry<float> ReplayLodBias;
        internal static ConfigEntry<float> MinSoundPitch;
        internal static ConfigEntry<float> MenuWidth, MenuHeight;
        internal static ConfigEntry<string> ClosedSections;
        internal static readonly ConfigEntry<string>[] CameraPresets = new ConfigEntry<string>[4];
        internal static ConfigEntry<int> ExportFps, ExportScale, ScreenshotScale;
        internal static ConfigEntry<bool> ExportAudio;
        internal static ConfigEntry<string> FfmpegPath;
        internal static string ExportDir;
        internal static Plugin Instance;
        internal static ConfigEntry<int> StaticZoneRing;
        internal static ConfigEntry<float> KeyframeInterval;
        internal static ConfigEntry<bool> RecordHiFi;
        internal static ConfigEntry<float> HiFiRadius;
        internal static ConfigEntry<bool> RecordPose;
        internal static ConfigEntry<float> PoseRate;
        internal static ConfigEntry<string> ExtraRpcs;
        internal static ConfigEntry<bool> ReplayModRpcs;
        internal static ConfigEntry<int> ReplayLoadRadius;

        private void Awake()
        {
            Log = Logger;
            MoveOldData();
            // New key names: the old ones hold Ctrl+F12, which Steam uses to record a clip, and would stay in existing config files.
            RecordKey = Config.Bind("Recording", "RecordShortcut", new KeyboardShortcut(KeyCode.F10, KeyCode.LeftControl), "Start or stop recording.");
            ReplayBuffer = Config.Bind("Recording", "ReplayBuffer", false, "Keep recording quietly while you play, so the save key can keep what just happened. Uses as much as a normal recording does.");
            ReplayBufferMinutes = Config.Bind("Recording", "ReplayBufferMinutes", 2f, new ConfigDescription("How many minutes the save key keeps.", new AcceptableValueRange<float>(0.5f, 30f)));
            ReplayBufferMinutes.SettingChanged += (s, e) => MenuUI.ListChanged();
            ReplayBufferKey = Config.Bind("Recording", "ReplayBufferShortcut", new KeyboardShortcut(KeyCode.F10, KeyCode.LeftControl, KeyCode.LeftShift), "Save the last minutes as a replay.");
            ReplayBufferToggleKey = Config.Bind("Recording", "ReplayBufferToggleShortcut", new KeyboardShortcut(KeyCode.F10, KeyCode.LeftControl, KeyCode.LeftAlt), "Turn the Replay Buffer on or off while in the game.");
            ExportOpen = Config.Bind("Export", "ShowInExplorer", false, "Show the finished video in File Explorer.");
            SampleRate = Config.Bind("Recording", "SampleRate", 20f, "Object samples per second (vanilla network rate is 20).");
            // New key name: the old DynamicRadius (80) would otherwise stay in existing config files.
            DynamicRadius = Config.Bind("Recording", "MovingObjectRadius", 0f, "Players, creatures, ships and projectiles within this many meters are recorded. 0 = everything the game has loaded around you (follows your simulation distance).");
            StaticZoneRing = Config.Bind("Recording", "StaticZoneRing", 1, "Scenery (trees, rocks, buildings) is recorded for whole 64 m zones within this many zones of you (1 = 3x3 zones).");
            KeyframeInterval = Config.Bind("Recording", "KeyframeInterval", 5f, "Seconds between full snapshots of moving objects.");
            RecordHiFi = Config.Bind("Recording", "RecordHiFi", true, "Also record the positions of nearby characters, and of vehicles and other moving objects, at a high rate (at least 50 Hz) for smooth motion. Files grow when many things move.");
            HiFiRadius = Config.Bind("Recording", "HiFiRadius", 30f, "Characters within this many meters get the 50 Hz recording.");
            RecordPose = Config.Bind("Recording", "RecordPose", true, "Record your own character's final bone pose, so animation mods (and anything else that moves bones) replay exactly as you saw them.");
            PoseRate = Config.Bind("Recording", "PoseRate", 30f, "Pose samples per second.");
            ExtraRpcs = Config.Bind("Playback", "ExtraRpcs", "", "More recorded RPC names to replay, comma-separated, on top of the built-in visual-only list.");
            ReplayModRpcs = Config.Bind("Playback", "ReplayModRpcs", true, "Replay other mods' RPCs aimed at recorded objects (weapon fire, effects). Turn off if a mod misbehaves in replays.");
            ReplayLodBias = Config.Bind("Playback", "ReplayLodBias", 7f, "LOD bias in replays, so creatures and small objects are drawn from further away. 0 = leave your setting alone.");
            MinSoundPitch = Config.Bind("Playback", "MinSoundPitch", 0.35f, "In slow motion, sounds slow down with the picture but never below this pitch (lower turns them into a rumble). 0.01 = always match the picture.");
            for (int i = 0; i < CameraPresets.Length; i++) CameraPresets[i] = Config.Bind("UI", $"CameraPreset{i + 1}", "", "Saved from the replay menu.");
            ClosedSections = Config.Bind("UI", "ClosedSections", "Camera presets|Time and weather|HUD and characters", "Replay menu sections that start closed.");
            MenuWidth = Config.Bind("UI", "MenuWidth", 360f, "Replay menu width (drag its corner to change).");
            MenuHeight = Config.Bind("UI", "MenuHeight", 640f, "Replay menu height.");
            ExportFps = Config.Bind("Export", "FrameRate", 60, new ConfigDescription("Video frame rate.", new AcceptableValueList<int>(24, 30, 60, 120, 144)));
            ExportScale = Config.Bind("Export", "ResolutionScale", 1, new ConfigDescription("Video size as a multiple of the game window (2 = supersampled).", new AcceptableValueRange<int>(1, 4)));
            ExportAudio = Config.Bind("Export", "Audio", true, "After the video, play the range again in real time to record its sound and merge it into the MP4.");
            FfmpegPath = Config.Bind("Export", "FfmpegPath", "", "ffmpeg.exe, which export needs. Empty = look in WinGet's folder and on PATH.");
            ScreenshotScale = Config.Bind("Export", "ScreenshotScale", 1, new ConfigDescription("Screenshot size as a multiple of the game window.", new AcceptableValueRange<int>(1, 4)));
            ReplayLoadRadius = Config.Bind("Playback", "ReplayLoadRadius", 7, "Zones of full-detail terrain loaded around the replay camera (your graphics setting is used if it's higher). Valheim's far terrain never shows terraforming, so a larger radius shows more of it.");

            Instance = this;
            ReplayDir = Path.Combine(Application.persistentDataPath, "ReplayMod", "replays");
            ExportDir = Path.Combine(Application.persistentDataPath, "ReplayMod", "exports");
            BufferDir = Path.Combine(Application.persistentDataPath, "ReplayMod", "buffer");
            Directory.CreateDirectory(ReplayDir);
            // What a crash left behind in the replay buffer.
            if (Directory.Exists(BufferDir))
            {
                foreach (string old in Directory.GetFiles(BufferDir))
                {
                    try { File.Delete(old); }
                    catch (System.Exception e) when (e is IOException || e is System.UnauthorizedAccessException) { Log.LogWarning($"couldn't delete {old}: {e.Message}"); }
                }
            }
            Application.runInBackground = true;
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            Camera.onPreCull += Recorder.OnPreCull;
            CameraPath.SelfCheck();
            Clipper.SelfCheck();
            ReplayUI.SelfCheck();
            ReplaySession.SelfCheck();
            if (!FreeCam.PresetFromText(FreeCam.PresetToText())) Log.LogError("camera preset self-check FAILED");
            Log.LogInfo($"Replay Mod {Version} loaded; replays in {ReplayDir}");
        }

        // The mod was called Muninn: bring along recordings and settings from then.
        private void MoveOldData()
        {
            string root = Path.Combine(Application.persistentDataPath, "ReplayMod");
            string oldRoot = Path.Combine(Application.persistentDataPath, "Muninn");
            string oldConfig = Path.Combine(Paths.ConfigPath, "com.gargantua.muninn.cfg");
            try
            {
                if (Directory.Exists(oldRoot) && !Directory.Exists(root)) Directory.Move(oldRoot, root);
                string replays = Path.Combine(root, "replays");
                if (Directory.Exists(replays))
                {
                    foreach (string f in Directory.GetFiles(replays, "*.muninn")) File.Move(f, Path.ChangeExtension(f, ".replay"));
                }
                if (File.Exists(oldConfig) && !File.Exists(Config.ConfigFilePath))
                {
                    File.Move(oldConfig, Config.ConfigFilePath);
                    Config.Reload();
                }
            }
            catch (System.Exception e) when (e is IOException || e is System.UnauthorizedAccessException) { Log.LogWarning($"couldn't move data from the old Muninn folders: {e.Message}"); }
        }

        private Player _spawned;
        private float _spawnedAt;

        private void Update()
        {
            if (!ReplaySession.Active && Player.m_localPlayer != null)
            {
                if (RecordKey.Value.IsDown()) Recorder.Toggle();
                else if (ReplayBufferKey.Value.IsDown()) Recorder.SaveBuffer();
                else if (ReplayBufferToggleKey.Value.IsDown())
                {
                    ReplayBuffer.Value = !ReplayBuffer.Value;
                    Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, $"Replay Mod: Replay Buffer {(ReplayBuffer.Value ? "on" : "off")}");
                }
                // A character that has just spawned isn't posed yet, and the camera isn't on it.
                if (Player.m_localPlayer != _spawned)
                {
                    _spawned = Player.m_localPlayer;
                    _spawnedAt = Time.time;
                }
                if (ReplayBuffer.Value && !Recorder.Active && ZNet.World != null && Time.time - _spawnedAt > 2f) Recorder.StartBuffer();
                else if (!ReplayBuffer.Value && Recorder.Buffering) Recorder.Stop();
            }
            string clip = Clipper.TakeMessage();
            if (clip != null)
            {
                if (ReplaySession.Active) ReplayUI.Notify(clip);
                else Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, $"Replay Mod: {clip}");
                MenuUI.ListChanged();
            }
            Recorder.Tick();
            ReplaySession.Update();
            MenuUI.Tick();
        }

        private void FixedUpdate()
        {
            Recorder.FixedTick();
        }

        private void OnGUI()
        {
            Recorder.OnGUI();
            MenuUI.OnGUI();
            ReplayUI.OnGUI();
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= Recorder.OnPreCull;
            Recorder.Stop();
        }
    }
}
