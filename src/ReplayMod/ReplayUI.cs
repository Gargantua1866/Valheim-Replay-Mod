using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ReplayMod
{
    // In-replay controls: keys plus an IMGUI panel (Tab).
    internal static class ReplayUI
    {
        public enum Grid { Off, Thirds, Golden, Center, Safe, Wide, Vertical, Horizon }

        public static bool CursorFree;
        public static bool MouseOverPanel;
        public static Grid GridMode = Grid.Off;
        public static bool ShowStatus = true;
        public static bool CombatHud;
        public static bool GuideHidden;
        // A text field in the menu has the keyboard: the camera and hotkeys leave the letters alone.
        public static bool Typing { get; private set; }

        private static readonly float[] Speeds = { 0.01f, 0.05f, 0.1f, 0.25f, 0.5f, 1f, 2f, 4f, 10f };
        private static Rect _panel = new Rect(20, 60, 360, 640);
        private static Vector2 _scroll, _weatherScroll;
        private static List<string> _weathers;
        private static bool _confirmClear, _confirmExit;
        private static float _panelW = 360f, _panelH = 640f;
        private static GUIStyle _label;

        public static void HandleKeys()
        {
            // Nothing happens behind the pause menu, and an export started now would film it.
            if (Menu.IsVisible()) return;
            // F10 starts or cancels an export; while exporting, nothing else may touch the replay.
            if (ZInput.GetKeyDown(KeyCode.F10, false)) StartExport();
            if (Exporter.Busy) return;
            if (_queued != null)
            {
                System.Action run = _queued;
                _queued = null;
                run();
            }
            if (Typing) return;
            if (ZInput.GetKeyDown(KeyCode.F9, false)) Screenshot();
            if (ZInput.GetKeyDown(KeyCode.Tab, false)) CursorFree = !CursorFree;
            if (ZInput.GetKeyDown(KeyCode.Space, false)) TogglePause();
            if (ZInput.GetKeyDown(KeyCode.Backspace, false)) ReplaySession.Seek(0f);
            float jump = ZInput.GetKey(KeyCode.LeftShift, false) ? 30f : 5f;
            if (RepeatKey(KeyCode.LeftArrow, 0.12f)) ReplaySession.Seek(ReplaySession.T - jump);
            if (RepeatKey(KeyCode.RightArrow, 0.12f)) ReplaySession.Seek(ReplaySession.T + jump);
            if (ZInput.GetKeyDown(KeyCode.UpArrow, false)) StepSpeed(1);
            if (ZInput.GetKeyDown(KeyCode.DownArrow, false)) StepSpeed(-1);
            // G tapped: next guide (or back from hidden). G held: hide or show the guide, and it stays that way.
            if (ZInput.GetKeyDown(KeyCode.G, false))
            {
                _gDownAt = Time.unscaledTime;
                _gHeld = false;
            }
            if (_gDownAt >= 0f && !_gHeld && Time.unscaledTime - _gDownAt >= GridHoldTime)
            {
                _gHeld = true;
                GuideHidden = !GuideHidden;
                Toast(GuideHidden ? "Guide hidden (hold G to show it)" : "Guide shown");
            }
            if (ZInput.GetKeyUp(KeyCode.G, false))
            {
                if (_gDownAt >= 0f && !_gHeld)
                {
                    if (GuideHidden) GuideHidden = false;
                    else GridMode = (Grid)(((int)GridMode + 1) % System.Enum.GetValues(typeof(Grid)).Length);
                }
                _gDownAt = -1f;
            }
            if (ZInput.GetKeyDown(KeyCode.O, false)) FreeCam.UseOriginal = !FreeCam.UseOriginal;
            if (ZInput.GetKeyDown(KeyCode.H, false)) ReplaySession.UseHiFi = !ReplaySession.UseHiFi;
            // Camera path
            if (ZInput.GetKeyDown(KeyCode.K, false)) AddKeyHere();
            if (ZInput.GetKeyDown(KeyCode.Delete, false)) DeleteKeys();
            bool ctrlHeld = ZInput.GetKey(KeyCode.LeftControl, false) || ZInput.GetKey(KeyCode.RightControl, false);
            bool shiftHeld = ZInput.GetKey(KeyCode.LeftShift, false) || ZInput.GetKey(KeyCode.RightShift, false);
            // The game's own Ctrl+F3 only hears the left Ctrl.
            if (Hud.instance && ZInput.GetKey(KeyCode.RightControl, false) && !ZInput.GetKey(KeyCode.LeftControl, false) && ZInput.GetKeyDown(KeyCode.F3, false)) Hud.instance.m_userHidden = !Hud.instance.m_userHidden;
            if (ctrlHeld && ZInput.GetKeyDown(KeyCode.Z, false))
            {
                if (shiftHeld) RedoEdit();
                else UndoEdit();
            }
            if (ctrlHeld && ZInput.GetKeyDown(KeyCode.Y, false)) RedoEdit();
            if (ZInput.GetKeyDown(KeyCode.J, false)) ToggleAttach(ZInput.GetKey(KeyCode.LeftShift, false) ? FreeCam.AttachMode.Mount : FreeCam.AttachMode.Follow);
            if (ZInput.GetKeyDown(KeyCode.L, false)) ToggleLook();
            if (ZInput.GetKeyDown(KeyCode.U, false)) HideAimed();
            // One frame at a time, at the export frame rate.
            if (RepeatKey(KeyCode.Comma, 0.05f)) StepFrame(-1);
            if (RepeatKey(KeyCode.Period, 0.05f)) StepFrame(1);
            if (ZInput.GetKeyDown(KeyCode.P, false)) CameraPath.Follow = !CameraPath.Follow;
            if (ctrlHeld && ZInput.GetKeyDown(KeyCode.C, false)) CopyKey();
            if (ctrlHeld && ZInput.GetKeyDown(KeyCode.V, false)) PasteKey();
            if (!ctrlHeld && ZInput.GetKeyDown(KeyCode.V, false)) CameraPath.Show = !CameraPath.Show;
            if (ZInput.GetKeyDown(KeyCode.B, false)) ToggleAttach(FreeCam.AttachMode.Orbit);
            if (ZInput.GetKeyDown(KeyCode.N, false)) ToggleTake();
            if (FreeCam.Take != null && ReplaySession.T >= ReplaySession.Duration) ToggleTake();
            // While playing, the key just passed is a moment behind the playhead: skip it, or [ seems to do nothing.
            if (RepeatKey(KeyCode.LeftBracket, 0.2f)) ReplaySession.Seek(CameraPath.Neighbor(ReplaySession.T - (ReplaySession.Paused ? 0f : 0.5f), -1));
            if (RepeatKey(KeyCode.RightBracket, 0.2f)) ReplaySession.Seek(CameraPath.Neighbor(ReplaySession.T, 1));
            if (ZInput.GetKeyDown(KeyCode.T, false)) ShowStatus = !ShowStatus;
            // Bookmarks: Ctrl+1..9 saves the camera pose, 1..9 jumps back to it.
            bool ctrl = ZInput.GetKey(KeyCode.LeftControl, false) || ZInput.GetKey(KeyCode.RightControl, false);
            for (int i = 0; i < 9; i++)
            {
                if (!ZInput.GetKeyDown(KeyCode.Alpha1 + i, false)) continue;
                if (ctrl)
                {
                    CameraPath.SetBookmark(i, FreeCam.Pos, FreeCam.Yaw, FreeCam.Pitch, FreeCam.Roll, FreeCam.Fov);
                    Toast($"Bookmark {i + 1} saved");
                }
                else if (CameraPath.GetBookmark(i) is CameraPath.Key b) MoveCameraTo(b);
            }
        }

        private const float GridHoldTime = 0.25f;
        private static float _gDownAt = -1f;
        private static bool _gHeld;

        // Keys picked on the timeline (Shift+click): they move and delete together.
        private static readonly HashSet<CameraPath.Key> _picked = new HashSet<CameraPath.Key>();

        private static void DeleteKeys()
        {
            if (_picked.Count == 0)
            {
                CameraPath.RemoveNearest(ReplaySession.T, 0.5f);
                return;
            }
            CameraPath.RemoveKeys(_picked);
            _picked.Clear();
        }

        // Which camera key the playhead is on or between.
        private static string KeyText()
        {
            int count = CameraPath.Keys.Count;
            if (count == 0) return "";
            int on = CameraPath.Nearest(ReplaySession.T, 0.05f);
            if (on >= 0) return $"KEY {on + 1} of {count}";
            int seg = CameraPath.SegmentAt(ReplaySession.T);
            return seg < 0 ? $"before key 1 of {count}" : seg >= count - 1 ? $"after key {count} of {count}" : $"between keys {seg + 1} and {seg + 2} of {count}";
        }

        // Held keys and buttons: once at once, then after a moment over and over.
        private static readonly Dictionary<int, float> _repeat = new Dictionary<int, float>();
        private static System.Action _queued;

        private static bool Repeats(int id, bool held, float every)
        {
            if (!held)
            {
                _repeat.Remove(id);
                return false;
            }
            float now = Time.unscaledTime;
            if (!_repeat.TryGetValue(id, out float next))
            {
                _repeat[id] = now + 0.35f;
                return true;
            }
            if (now < next) return false;
            _repeat[id] = now + every;
            return true;
        }

        private static bool RepeatKey(KeyCode key, float every) => Repeats((int)key, ZInput.GetKey(key, false), every);

        // RepeatButton reports being held during repaints, when changing the replay would change this
        // event's layout, so the action waits for the next Update.
        private static void HeldButton(string label, System.Action action, float every = 0.12f)
        {
            bool held = Ui.RepeatButton(label);
            if (Event.current.type == EventType.Repaint && Repeats(label.GetHashCode(), held, every)) _queued = action;
        }

        private static CameraPath.Key _clipboard;

        private static void CopyKey()
        {
            int i = CameraPath.Nearest(ReplaySession.T, 0.5f);
            if (i < 0)
            {
                Toast("No key near the playhead to copy");
                return;
            }
            _clipboard = CameraPath.Keys[i].Clone();
            Toast($"Key {i + 1} copied");
        }

        private static void PasteKey()
        {
            if (_clipboard == null)
            {
                Toast("Copy a key first (Ctrl+C)");
                return;
            }
            CameraPath.AddKey(ReplaySession.T, _clipboard.pos, _clipboard.yaw, _clipboard.pitch, _clipboard.roll, _clipboard.fov);
            Toast($"Pose pasted at {Clock(ReplaySession.T)}");
        }

        private static void ToggleTake()
        {
            if (FreeCam.Take == null)
            {
                CameraPath.Follow = false;
                FreeCam.UseOriginal = false;
                FreeCam.Take = new List<CameraPath.Key>();
                ReplaySession.Paused = false;
                Toast($"Recording a take at {ReplaySession.Speed:0.##}x: fly the camera, N stops");
                return;
            }
            List<CameraPath.Key> take = FreeCam.Take;
            FreeCam.Take = null;
            int kept = CameraPath.AddTake(take);
            _picked.Clear();
            Toast(kept > 0 ? $"Take saved as {kept} keys. P plays it, and up/down set its speed" : "Take too short");
        }

        private static Camera ViewCamera => GameCamera.instance ? GameCamera.instance.GetComponent<Camera>() : null;

        private static void ToggleLook() => Toast(FreeCam.ToggleLookAt(ViewCamera));

        private static void HideAimed()
        {
            ZNetView nv = FreeCam.AimedObject(ViewCamera);
            if (!nv)
            {
                Toast("Aim at a recorded object first (screen center)");
                return;
            }
            ZDOID id = nv.GetZDO().m_uid;
            CameraPath.Snapshot("hide object");
            ReplaySession.Hidden.Add(new RecId(id.UserID, id.ID));
            Toast($"Hid {Utils.GetPrefabName(nv.gameObject)}");
        }

        private static void StepFrame(int dir)
        {
            ReplaySession.Paused = true;
            ReplaySession.Seek(ReplaySession.T + dir * ReplaySession.PlaySpeed / Plugin.ExportFps.Value);
        }

        // A message that stays up until the user does something, plus a moment to read it.
        public static void Notify(string text)
        {
            Toast(text);
            _toastSticky = true;
        }

        private static void StartExport()
        {
            string msg = Exporter.Toggle();
            if (msg != null) Notify(msg);
        }

        private static void Screenshot() => Toast($"Screenshot saved: {System.IO.Path.GetFileName(Exporter.Screenshot())}");

        private static void UndoEdit()
        {
            _picked.Clear();
            string what = CameraPath.Undo();
            Toast(what != null ? $"Undid {what}" : "Nothing to undo");
        }

        private static void RedoEdit()
        {
            _picked.Clear();
            string what = CameraPath.Redo();
            Toast(what != null ? $"Redid {what}" : "Nothing to redo");
        }

        private static void ToggleAttach(FreeCam.AttachMode mode) =>
            Toast(FreeCam.ToggleAttach(mode, GameCamera.instance ? GameCamera.instance.GetComponent<Camera>() : null));

        // Behind and above a player, looking at them.
        private static void GoTo(Player pl)
        {
            CameraPath.Follow = false;
            FreeCam.UseOriginal = false;
            FreeCam.Attach = FreeCam.AttachMode.None;
            Transform t = pl.transform;
            Vector3 pos = t.position - t.forward * 4f + Vector3.up * 2.5f;
            Vector3 e = Quaternion.LookRotation(t.position + Vector3.up * 1.2f - pos).eulerAngles;
            FreeCam.SetPose(pos, e.y, Mathf.DeltaAngle(0f, e.x), 0f, FreeCam.Fov);
        }

        // Puts the freecam exactly on a key or bookmark pose.
        private static void MoveCameraTo(CameraPath.Key k)
        {
            CameraPath.Follow = false;
            FreeCam.UseOriginal = false;
            FreeCam.SetPose(k.pos, k.yaw, k.pitch, k.roll, k.fov);
        }

        private static void AddKeyHere()
        {
            bool replaced = CameraPath.Nearest(ReplaySession.T, 0.05f) >= 0;
            CameraPath.AddWorldKey(ReplaySession.T, FreeCam.Pos, FreeCam.Yaw, FreeCam.Pitch, FreeCam.Roll, FreeCam.Fov);
            Toast($"Key {CameraPath.Nearest(ReplaySession.T, 0.05f) + 1} {(replaced ? "updated" : "added")} at {Clock(ReplaySession.T)}");
        }

        // Feedback: a short click and a message in the middle of the screen. The click ignores the
        // listener pause, so it's heard while the replay is paused.
        private static string _toast;
        private static float _toastUntil;
        private static AudioSource _click;

        private static bool _toastSticky;

        private static bool UserActed()
        {
            Keyboard kb = Keyboard.current;
            Mouse mouse = Mouse.current;
            return (kb != null && kb.anyKey.isPressed) || (mouse != null && (mouse.leftButton.isPressed || mouse.rightButton.isPressed))
                || ZInput.GetMouseDelta().sqrMagnitude > 0f;
        }

        private static void Toast(string text)
        {
            _toastSticky = false;
            _toast = text;
            _toastUntil = Time.unscaledTime + 1.5f;
            if (!_click)
            {
                var go = new GameObject("ReplayModClick");
                Object.DontDestroyOnLoad(go);
                _click = go.AddComponent<AudioSource>();
                _click.ignoreListenerPause = true;
                _click.spatialBlend = 0f;
                _click.volume = 0.5f;
                // A dry UI sound: no cave or building reverb, no filters on the listener.
                _click.bypassEffects = _click.bypassListenerEffects = _click.bypassReverbZones = true;
                _click.reverbZoneMix = 0f;
                const int rate = 44100;
                var data = new float[rate / 12];
                for (int i = 0; i < data.Length; i++) data[i] = Mathf.Sin(2f * Mathf.PI * 1320f * i / rate) * Mathf.Exp(-i / (rate * 0.015f));
                _click.clip = AudioClip.Create("ReplayModClick", data.Length, 1, rate, false);
                // net48 can't bind Unity 6's ReadOnlySpan overload: pick the float[] one (see the modding guide).
                typeof(AudioClip).GetMethod("SetData", new[] { typeof(float[]), typeof(int) }).Invoke(_click.clip, new object[] { data, 0 });
            }
            _click.Play();
        }

        private static void DrawToast()
        {
            if (_toastSticky && UserActed())
            {
                _toastSticky = false;
                _toastUntil = Time.unscaledTime + 3f;
            }
            if (_toast == null || (!_toastSticky && Time.unscaledTime > _toastUntil)) return;
            Vector2 size = UiStyle.Big.CalcSize(new GUIContent(_toast));
            var r = new Rect((Screen.width - size.x) / 2f - 12f, Screen.height * 0.3f, size.x + 24f, size.y + 10f);
            UiStyle.Fill(r, UiStyle.Panel);
            GUI.Label(new Rect(r.x + 12f, r.y + 5f, size.x, size.y), _toast, UiStyle.Big);
        }

        private static void TogglePause()
        {
            if (ReplaySession.Paused && ReplaySession.T >= ReplaySession.Duration) ReplaySession.Seek(0f);
            ReplaySession.Paused = !ReplaySession.Paused;
        }

        // While on the path this sets the whole path's speed; its keys keep their speeds relative to each other.
        private static void StepSpeed(int dir)
        {
            bool onPath = ReplaySession.PathSpeed;
            float current = onPath ? CameraPath.Rate : ReplaySession.Speed;
            int i = 0;
            float best = float.MaxValue;
            for (int k = 0; k < Speeds.Length; k++)
            {
                float d = Mathf.Abs(Speeds[k] - current);
                if (d < best) { best = d; i = k; }
            }
            float next = Speeds[Mathf.Clamp(i + dir, 0, Speeds.Length - 1)];
            if (onPath) CameraPath.SetRate(next);
            else ReplaySession.Speed = next;
        }

        public static void OnGUI()
        {
            // The game's pause and settings menus are drawn below IMGUI: nothing of ours goes on top of them.
            if (!ReplaySession.Active || !ReplaySession.Ready || Exporter.HideUi || Menu.IsVisible()) return;
            Typing = CursorFree && GUIUtility.keyboardControl != 0;
            if (Event.current.type == EventType.MouseDown && !(CursorFree && _panel.Contains(Event.current.mousePosition))) GUIUtility.keyboardControl = 0;
            if (_label == null)
            {
                _label = UiStyle.Label;
                _panelW = Plugin.MenuWidth.Value;
                _panelH = Plugin.MenuHeight.Value;
            }
            DrawGrid();
            if (GameCamera.instance) CameraPath.DrawLabels(GameCamera.instance.GetComponent<Camera>());
            DrawToast();
            if (ReplaySession.Loading) DrawLoading();
            MouseOverPanel = false;
            if (ShowStatus) Timeline();
            if (!CursorFree) return;
            _panel = GUILayout.Window(0x4D554E, new Rect(_panel.x, _panel.y, _panelW, _panelH), Panel, "Replay Mod", UiStyle.Window,
                GUILayout.Width(_panelW), GUILayout.Height(_panelH));
            MouseOverPanel |= _panel.Contains(Event.current.mousePosition);
        }

        // Over the loading screen while the destination of a far jump loads (see ReplaySession.Loading).
        private static void DrawLoading()
        {
            string text = $"Loading the area after a long jump ({ReplaySession.LoadingSeconds:0} s). The replay goes on when it's ready.";
            Vector2 size = _label.CalcSize(new GUIContent(text));
            var box = new Rect((Screen.width - size.x) / 2f - 14f, Screen.height * 0.42f, size.x + 28f, size.y + 16f);
            GUI.Box(box, GUIContent.none);
            GUI.Label(new Rect(box.x + 14f, box.y + 8f, size.x, size.y), text, _label);
        }

        // Timeline across the bottom. Click or drag on the track to seek (Tab frees the cursor).
        private static bool _dragging, _dragMoved, _selecting, _scrubbing;
        private static float _pressX;

        // Red and blinking for a moment when flying inputs arrive that the path or original camera is ignoring.
        private static string ViewText()
        {
            string text = (CameraPath.Follow ? "   ON PATH" : "") + (FreeCam.UseOriginal ? "   ORIGINAL CAMERA" : "");
            bool flash = Time.unscaledTime - FreeCam.IgnoredInputAt < 0.6f && Mathf.Repeat(Time.unscaledTime * 6f, 1f) < 0.5f;
            return flash && text.Length > 0 ? $"<color=#ff3030>{text}</color>" : text;
        }
        private static string LookText() => FreeCam.LookName != null ? $"   LOOKING AT {FreeCam.LookName}" : "";
        private static CameraPath.Key _dragKey;
        private static float _lastDragSeek;

        private static void Timeline()
        {
            float dur = Mathf.Max(ReplaySession.Duration, 0.01f);
            float w = Screen.width - 32f;
            if (!CursorFree)
            {
                // Flying: a slim progress line, the state in large text and the keys below, on a dark backing.
                Rect thin = new Rect(16f, Screen.height - 10f, w, 4f);
                UiStyle.Fill(thin, UiStyle.Track);
                UiStyle.Fill(new Rect(thin.x, thin.y, thin.width * ReplaySession.T / dur, thin.height), UiStyle.Played);
                string state = $"{Clock(ReplaySession.T)} / {Clock(dur)}   {(ReplaySession.Paused ? "PAUSED   " : "")}{SpeedText()}   {KeyText()}" +
                    $"{ViewText()}{(FreeCam.Take != null ? "   RECORDING TAKE" : "")}" +
                    $"{(FreeCam.Attach != FreeCam.AttachMode.None ? $"   {FreeCam.ModeName(FreeCam.Attach).ToUpperInvariant()} {FreeCam.AttachName}" : "")}{LookText()}";
                const string keys = "Tab timeline/menu   Space play   ←/→ seek   ↑/↓ speed   K key   N take   J follow (Shift: mount)   B orbit   L look at   , . frame   F9 screenshot   F10 export   P follow path   [ ] prev/next key   1-9 bookmark (Ctrl+ saves)   G guide (hold: hide/show)   T hide";
                Vector2 s1 = UiStyle.Big.CalcSize(new GUIContent(state)), s2 = UiStyle.Small.CalcSize(new GUIContent(keys));
                Rect back = new Rect(16f, Screen.height - 22f - s1.y - s2.y - 8f, Mathf.Max(s1.x, s2.x) + 20f, s1.y + s2.y + 8f);
                UiStyle.Fill(back, UiStyle.Panel);
                GUI.Label(new Rect(back.x + 10f, back.y + 4f, s1.x, s1.y), state, UiStyle.Big);
                GUI.Label(new Rect(back.x + 10f, back.y + 4f + s1.y, s2.x, s2.y), keys, UiStyle.Small);
                return;
            }

            Rect bar = new Rect(16f, Screen.height - 100f, w, 88f);
            UiStyle.Fill(bar, UiStyle.Panel);
            MouseOverPanel |= bar.Contains(Event.current.mousePosition);

            Rect row = new Rect(bar.x + 10f, bar.y + 8f, bar.width - 20f, 26f);
            if (GUI.Button(new Rect(row.x, row.y, 70f, 26f), ReplaySession.Paused ? "Play" : "Pause")) TogglePause();
            if (GUI.Button(new Rect(row.x + 76f, row.y, 34f, 26f), "-")) StepSpeed(-1);
            GUI.Label(new Rect(row.x + 114f, row.y + 3f, 46f, 22f), $"<b>{SpeedText()}</b>", _label);
            if (GUI.Button(new Rect(row.x + 160f, row.y, 34f, 26f), "+")) StepSpeed(1);
            GUI.Label(new Rect(row.x + 206f, row.y + 3f, 800f, 22f), $"<b>{Clock(ReplaySession.T)}</b> / {Clock(dur)}     <b>{KeyText()}{ViewText()}{LookText()}</b>", _label);

            // Far enough below the buttons that the camera keys above the track stay clear of them.
            Rect track = new Rect(bar.x + 10f, bar.y + 56f, bar.width - 20f, 22f);
            UiStyle.Fill(track, UiStyle.Track);
            float step = dur > 600f ? 60f : dur > 120f ? 10f : 5f;
            for (float s = step; s < dur; s += step)
            {
                float x = track.x + track.width * s / dur;
                bool major = Mathf.Approximately(s % (step * 6f), 0f);
                UiStyle.Fill(new Rect(x, track.y + (major ? 0f : 12f), 1f, major ? track.height : 10f), UiStyle.Tick);
            }
            UiStyle.Fill(new Rect(track.x, track.y + track.height - 4f, track.width * ReplaySession.T / dur, 4f), UiStyle.Played);
            float head = track.x + track.width * ReplaySession.T / dur;
            UiStyle.Fill(new Rect(head - 1.5f, track.y - 4f, 3f, track.height + 8f), Color.white);

            // The clip range, when part of the replay is marked for trimming.
            if (_clipIn > 0f || _clipOut > 0f)
            {
                float clipEnd = _clipOut > 0f ? _clipOut : dur;
                UiStyle.Fill(new Rect(track.x + track.width * _clipIn / dur, track.y, track.width * (clipEnd - _clipIn) / dur, track.height), new Color(0.4f, 0.8f, 1f, 0.35f));
            }

            Event e = Event.current;
            Vector2 mouse = e.mousePosition;

            // Auto-markers sit in the track as colored ticks; hovering one shows what happened.
            string tip = null;
            foreach (ReplayEvent mk in ReplaySession.Data.Markers)
            {
                float x = track.x + track.width * mk.T / dur;
                UiStyle.Fill(new Rect(x - 1.5f, track.y, 3f, track.height), MarkerColor((MarkerKind)(int)mk.F));
                if (new Rect(x - 4f, track.y, 8f, track.height).Contains(mouse)) tip = $"{Clock(mk.T)}  {mk.S}";
            }

            // Camera keys sit just above the track: click jumps to one, drag retimes it, right-click deletes it.
            int keyNo = 0;
            foreach (CameraPath.Key k in CameraPath.Keys.ToArray())
            {
                keyNo++;
                Rect m = new Rect(track.x + track.width * k.t / dur - 6f, track.y - 16f, 12f, 12f);
                UiStyle.Fill(m, _picked.Contains(k) ? new Color(1f, 0.4f, 0.15f) : k == _dragKey || Mathf.Abs(k.t - ReplaySession.T) < 0.05f ? Color.white : UiStyle.Played);
                if (m.Contains(mouse)) tip = $"Key {keyNo}  {Clock(k.t)}  {EaseName(k.ease)}  {k.speed:0.##}x  FOV {k.fov:0}  roll {k.roll:0}°";
                if (e.type == EventType.MouseDown && m.Contains(mouse))
                {
                    if (e.button == 1)
                    {
                        _picked.Remove(k);
                        CameraPath.RemoveNearest(k.t, 0.01f);
                    }
                    else if (e.shift)
                    {
                        if (!_picked.Remove(k)) _picked.Add(k);
                    }
                    else if (e.clickCount == 2)
                    {
                        // Double-click: go to the key's time and put the camera exactly on its pose
                        // (following the path already does both; the freecam needs the pose set).
                        ReplaySession.Seek(k.t);
                        FreeCam.UseOriginal = false;
                        CameraPath.KeyWorld(k, out Vector3 worldPos, out float worldYaw, out float worldPitch, out float worldRoll);
                        FreeCam.SetPose(worldPos, worldYaw, worldPitch, worldRoll, k.fov);
                        _dragKey = null;
                    }
                    else
                    {
                        if (!_picked.Contains(k)) _picked.Clear();
                        _dragKey = k;
                        _dragMoved = false;
                    }
                    e.Use();
                }
            }
            if (_dragKey != null)
            {
                if (e.type == EventType.MouseDrag)
                {
                    if (!_dragMoved) CameraPath.Snapshot(_picked.Count > 1 ? $"move {_picked.Count} keys" : "move key");
                    float to = Mathf.Clamp01((mouse.x - track.x) / track.width) * dur;
                    // Snaps to markers within a few pixels; Alt turns that off.
                    if (!e.alt)
                    {
                        foreach (ReplayEvent mk in ReplaySession.Data.Markers)
                        {
                            if (Mathf.Abs(track.width * (mk.T - to) / dur) <= 6f) to = mk.T;
                        }
                    }
                    if (_picked.Contains(_dragKey))
                    {
                        // Moves as a group, stopping when one of them reaches an end of the replay.
                        float move = Mathf.Clamp(to - _dragKey.t, -_picked.Min(k => k.t), dur - _picked.Max(k => k.t));
                        foreach (CameraPath.Key k in _picked) k.t += move;
                    }
                    else _dragKey.t = to;
                    _dragMoved = true;
                    CameraPath.Resort();
                    GUI.Label(new Rect(mouse.x - 30f, track.y - 40f, 90f, 22f), Clock(_dragKey.t), _label);
                    e.Use();
                }
                else if (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp)
                {
                    if (_dragMoved) CameraPath.Changed();
                    else ReplaySession.Seek(_dragKey.t);
                    _dragKey = null;
                    e.Use();
                }
            }

            Rect hit = new Rect(track.x, track.y - 2f, track.width, track.height + 8f);
            if (tip != null)
            {
                Vector2 size = UiStyle.Small.CalcSize(new GUIContent(tip));
                var r = new Rect(Mathf.Clamp(mouse.x - size.x / 2f, 8f, Screen.width - size.x - 16f), bar.y - size.y - 12f, size.x + 12f, size.y + 6f);
                UiStyle.Fill(r, UiStyle.Panel);
                GUI.Label(new Rect(r.x + 6f, r.y + 3f, size.x, size.y), tip, UiStyle.Small);
            }
            else if (hit.Contains(mouse))
            {
                float ht = Mathf.Clamp01((mouse.x - track.x) / track.width) * dur;
                GUI.Label(new Rect(mouse.x - 30f, track.y - 26f, 90f, 22f), Clock(ht), _label);
            }
            // A click seeks. Dragging selects a section for Trim, except from the playhead, which scrubs.
            if (e.type == EventType.MouseDown && e.button == 1 && hit.Contains(mouse))
            {
                _clipIn = _clipOut = 0f;
                e.Use();
            }
            else if (e.type == EventType.MouseDown && e.button == 0 && hit.Contains(mouse))
            {
                _dragging = true;
                _selecting = false;
                _scrubbing = Mathf.Abs(mouse.x - head) <= 10f;
                _pressX = mouse.x;
                e.Use();
            }
            else if (_dragging && e.type == EventType.MouseDrag)
            {
                if (_scrubbing) SeekTo(mouse.x, track, dur, force: false);
                else if (_selecting || Mathf.Abs(mouse.x - _pressX) > 4f)
                {
                    _selecting = true;
                    float a = Mathf.Clamp01((_pressX - track.x) / track.width) * dur, b = Mathf.Clamp01((mouse.x - track.x) / track.width) * dur;
                    _clipIn = Mathf.Min(a, b);
                    _clipOut = Mathf.Max(a, b);
                }
                e.Use();
            }
            else if (_dragging && (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp))
            {
                _dragging = false;
                if (!_selecting) SeekTo(mouse.x, track, dur, force: true);
                else if (_clipOut - _clipIn < 0.1f) _clipIn = _clipOut = 0f;
                e.Use();
            }
        }

        private static Color MarkerColor(MarkerKind k)
        {
            switch (k)
            {
                case MarkerKind.Death: return new Color(0.9f, 0.25f, 0.25f);
                case MarkerKind.Kill: return new Color(1f, 0.6f, 0.15f);
                case MarkerKind.Boss: return new Color(0.75f, 0.4f, 1f);
                case MarkerKind.Portal: return new Color(0.3f, 0.85f, 1f);
                default: return new Color(0.4f, 0.9f, 0.4f);
            }
        }

        private static void SeekTo(float mouseX, Rect track, float dur, bool force)
        {
            // Seeking rebuilds every object, so while dragging do it at most ~15 times a second.
            if (!force && Time.unscaledTime - _lastDragSeek < 0.066f) return;
            _lastDragSeek = Time.unscaledTime;
            ReplaySession.Seek(Mathf.Clamp01((mouseX - track.x) / track.width) * dur);
        }

        // Drag the bottom-right corner of the menu to resize it. Runs first inside the window, so neither
        // the scroll view nor the window drag takes the click; hotControl keeps the drag ours outside the window.
        private const float Grip = 22f;

        private static void ResizeGrip()
        {
            int id = GUIUtility.GetControlID(FocusType.Passive);
            Event e = Event.current;
            var grip = new Rect(_panelW - Grip, _panelH - Grip, Grip, Grip);
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && grip.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        // Window-local mouse position = the new size.
                        _panelW = Mathf.Clamp(e.mousePosition.x + Grip / 2f, 280f, Screen.width - _panel.x);
                        _panelH = Mathf.Clamp(e.mousePosition.y + Grip / 2f, 200f, Screen.height - _panel.y);
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        Plugin.MenuWidth.Value = _panelW; // saved to the config file
                        Plugin.MenuHeight.Value = _panelH;
                        e.Use();
                    }
                    break;
            }
        }

        private static void Panel(int id)
        {
            // Highlighted search words are rich text. The skin is shared with other mods, so it goes back after.
            bool labelRich = GUI.skin.label.richText, buttonRich = GUI.skin.button.richText, toggleRich = GUI.skin.toggle.richText;
            GUI.skin.label.richText = GUI.skin.button.richText = GUI.skin.toggle.richText = true;
            PanelContents();
            GUI.skin.label.richText = labelRich;
            GUI.skin.button.richText = buttonRich;
            GUI.skin.toggle.richText = toggleRich;
        }

        // GUILayout controls whose text shows the search words highlighted.
        private static class Ui
        {
            public static void Label(string text, params GUILayoutOption[] o) => GUILayout.Label(Highlight(text, _search), o);
            public static void Label(string text, GUIStyle style, params GUILayoutOption[] o) => GUILayout.Label(Highlight(text, _search), style, o);
            public static bool Button(string text, params GUILayoutOption[] o) => GUILayout.Button(Highlight(text, _search), o);
            public static bool Button(string text, GUIStyle style, params GUILayoutOption[] o) => GUILayout.Button(Highlight(text, _search), style, o);
            public static bool Toggle(bool on, string text, params GUILayoutOption[] o) => GUILayout.Toggle(on, Highlight(text, _search), o);
            public static bool Toggle(bool on, string text, GUIStyle style, params GUILayoutOption[] o) => GUILayout.Toggle(on, Highlight(text, _search), style, o);
            public static bool RepeatButton(string text, params GUILayoutOption[] o) => GUILayout.RepeatButton(Highlight(text, _search), o);
        }

        // Colors each searched word in the text, leaving rich text tags as they are.
        internal static string Highlight(string text, string search)
        {
            if (string.IsNullOrEmpty(search)) return text;
            string[] words = search.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return text;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < text.Length;)
            {
                if (text[i] == '<')
                {
                    int close = text.IndexOf('>', i);
                    if (close > i)
                    {
                        sb.Append(text, i, close - i + 1);
                        i = close + 1;
                        continue;
                    }
                }
                int length = 0;
                foreach (string w in words)
                {
                    if (w.Length > length && string.Compare(text, i, w, 0, w.Length, System.StringComparison.OrdinalIgnoreCase) == 0 && text.IndexOf('<', i, w.Length) < 0) length = w.Length;
                }
                if (length == 0)
                {
                    sb.Append(text[i++]);
                    continue;
                }
                sb.Append("<color=#ffd040>").Append(text, i, length).Append("</color>");
                i += length;
            }
            return sb.ToString();
        }

        public static void SelfCheck()
        {
            if (Highlight("Hide the <b>roll</b> Roller", "ROLL") != "Hide the <b><color=#ffd040>roll</color></b> <color=#ffd040>Roll</color>er"
                || Highlight("<b>b</b>", "b") != "<b><color=#ffd040>b</color></b>" || Highlight("x", "") != "x")
            {
                Plugin.Log.LogError("search highlight self-check FAILED");
            }
        }

        private static void PanelContents()
        {
            Event ev = Event.current;
            // Opening a section changes the layout, so that waits for the start of the next layout pass.
            if (ev.type == EventType.Layout && _foldToggle != null)
            {
                if (!_closed.Remove(_foldToggle)) _closed.Add(_foldToggle);
                _foldToggle = null;
                Plugin.ClosedSections.Value = string.Join("|", _closed);
            }
            // So does a change of search text.
            if (ev.type == EventType.Layout && _searchNext != null)
            {
                _search = _searchNext;
                _searchNext = null;
            }
            // Clicking elsewhere, Tab or Escape leaves a text field.
            if (ev.type == EventType.MouseDown || (ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.Tab || ev.keyCode == KeyCode.Escape))) GUIUtility.keyboardControl = 0;
            ResizeGrip();

            GUILayout.BeginHorizontal();
            Ui.Label("Search:", GUILayout.ExpandWidth(false));
            string typed = GUILayout.TextField(_search);
            if (typed != _search) _searchNext = typed;
            if (Ui.Button("x", GUILayout.Width(24)))
            {
                _searchNext = "";
                GUIUtility.keyboardControl = 0;
            }
            GUILayout.EndHorizontal();

            _scroll = GUILayout.BeginScrollView(_scroll);

            Ui.Label($"{System.IO.Path.GetFileNameWithoutExtension(ReplaySession.Data.Path)}");
            Ui.Label($"World '{ReplaySession.Data.Header.WorldName}', recorded by {ReplaySession.Data.Header.PlayerName}");

            _searchHit = false;
            if (Fold("Playback", "play pause restart seek frame step speed slow motion")) PlaybackSection();
            if (Fold("Camera path", "key keys keyframe add delete copy paste undo redo clear previous next take record follow show line colour color ease speed time of day weather match original fov roll select move attach anchor rides")) PathSection();
            if (Fold("Camera target", "follow mount orbit detach look at aim attach softness lag projectile arrow bolt spear bomb spell meteor")) TargetSection();
            if (Fold("Camera controls", "original fov zoom roll pan move speed smoothing aim sensitivity yaw pitch invert lock height axis")) ControlsSection();
            if (Fold("Framing and focus", "guide grid thirds golden center safe wide vertical horizon depth of field dof focus blur aperture")) FramingSection();
            if (Fold("Picture", "exposure brightness temperature colour color saturation contrast bloom lens dirt sun shafts look grading revert")) PictureSection();
            if (Fold("Camera presets", "preset save load")) PresetsSection();
            if (Fold("Players", "player go to follow eyes first person pov")) PlayersSection();
            if (Fold("Hide from the shot", "hide hidden show object players held items weapons projectiles arrows")) HideSection();
            if (Fold("Time and weather", "time of day clock sun night weather rain wind environment")) EnvironmentSection();
            if (Fold("HUD and characters", "hud health bars damage numbers timeline status pose 50 hz motion")) HudSection();
            if (Fold("Trim", "cut clip shorter trim start end save")) TrimSection();
            if (Fold("Export", "video mp4 ffmpeg render frame rate fps size resolution sound audio screenshot folder explorer flash light brightness exposure")) ExportSection();
            if (Fold("Info", "objects rpc exit leave quit save without saving")) InfoSection();
            if (_search.Length > 0 && !_searchHit) Ui.Label("Nothing matches.");

            GUILayout.EndScrollView();
            GUI.Label(new Rect(_panelW - Grip, _panelH - Grip, Grip, Grip), "<b>//</b>", _label);
            if (ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter)) GUIUtility.keyboardControl = 0;
            GUI.DragWindow();
        }

        private static HashSet<string> _closed;
        private static string _foldToggle;
        private static GUIStyle _foldStyle;

        private static string _search = "", _searchNext;
        private static bool _searchHit;

        // Every word typed must appear in the section's title or keywords. Matching sections show open.
        private static bool SearchMatches(string text)
        {
            foreach (string word in _search.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                if (text.IndexOf(word, System.StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            return true;
        }

        private static bool Fold(string title, string keywords)
        {
            if (_search.Length > 0)
            {
                if (!SearchMatches(title + " " + keywords)) return false;
                _searchHit = true;
                GUILayout.Space(4);
                Ui.Label($"<b>{title}</b>", _label);
                return true;
            }
            if (_closed == null) _closed = new HashSet<string>(Plugin.ClosedSections.Value.Split(new[] { '|' }, System.StringSplitOptions.RemoveEmptyEntries));
            if (_foldStyle == null) _foldStyle = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, richText = true };
            bool open = !_closed.Contains(title);
            GUILayout.Space(4);
            if (Ui.Button($"{(open ? "-" : "+")}  {title}", _foldStyle)) _foldToggle = title;
            return open;
        }

        private static void PlaybackSection()
        {
            GUILayout.BeginHorizontal();
            if (Ui.Button(ReplaySession.Paused ? "Play" : "Pause")) TogglePause();
            if (Ui.Button("Restart")) _queued = () => ReplaySession.Seek(0f);
            HeldButton("-5 s", () => ReplaySession.Seek(ReplaySession.T - 5f));
            HeldButton("+5 s", () => ReplaySession.Seek(ReplaySession.T + 5f));
            HeldButton("< Frame (,)", () => StepFrame(-1), 0.05f);
            HeldButton("Frame (.) >", () => StepFrame(1), 0.05f);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            foreach (float s in Speeds)
            {
                bool on = Mathf.Approximately(s, ReplaySession.Speed);
                if (Ui.Toggle(on, $"{s:0.##}x", GUI.skin.button) && !on) ReplaySession.Speed = s;
            }
            GUILayout.EndHorizontal();
        }

        private static bool _keySliding;
        private static Vector2 _keyWeatherScroll;

        private static List<string> Weathers()
        {
            if (_weathers == null && EnvMan.instance) _weathers = EnvMan.instance.m_environments.Select(e => e.m_name).Distinct().OrderBy(n => n).ToList();
            return _weathers ?? new List<string>();
        }

        // A number typed into a field: Enter applies it, leaving the field any other way forgets it.
        // The change waits for the next Update, because it can change this event's layout.
        private static string _editName, _editText;

        private static void NumberField(string label, float value, System.Action<float> commit)
        {
            string name = "ReplayModNumber" + label;
            Ui.Label(label, GUILayout.ExpandWidth(false));
            GUI.SetNextControlName(name);
            bool focused = GUI.GetNameOfFocusedControl() == name;
            if (!focused && _editName == name) _editName = null;
            string shown = _editName == name ? _editText : value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            Event e = Event.current;
            bool enter = focused && e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);
            string typed = GUILayout.TextField(shown, GUILayout.Width(62));
            if (typed != shown)
            {
                _editName = name;
                _editText = typed;
            }
            if (enter && _editName == name)
            {
                if (float.TryParse(_editText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)) _queued = () => commit(v);
                _editName = null;
                GUIUtility.keyboardControl = 0;
                e.Use();
            }
        }

        private static void AttachPath(bool turn)
        {
            ZNetView nv = FreeCam.TargetOrAimed(ViewCamera);
            if (!nv)
            {
                Toast("Aim at a recorded object first (screen center)");
                return;
            }
            ZDOID id = nv.GetZDO().m_uid;
            string name = FreeCam.NameOf(nv);
            CameraPath.SetAnchor(new CameraPath.PathAnchor { Target = new RecId(id.UserID, id.ID), Turn = turn, Name = name });
            Toast($"Path now {(turn ? "moves and turns" : "moves")} with {name}");
        }

        private static void PathSection()
        {
            Ui.Label($"{CameraPath.Keys.Count} keys. Fly to a pose, move the timeline, press K. K on a key's time re-poses it; drag keys on the timeline to retime them.");
            GUILayout.BeginHorizontal();
            if (Ui.Button("Add key (K)")) AddKeyHere();
            if (Ui.Button("Delete (Del)")) DeleteKeys();
            if (Ui.Button("Copy (Ctrl+C)")) CopyKey();
            if (Ui.Button("Paste (Ctrl+V)")) PasteKey();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUI.enabled = CameraPath.CanUndo;
            if (Ui.Button("Undo (Ctrl+Z)")) UndoEdit();
            GUI.enabled = CameraPath.CanRedo;
            if (Ui.Button("Redo (Ctrl+Y)")) RedoEdit();
            GUI.enabled = true;
            if (Ui.Button("Clear all") && CameraPath.Keys.Count > 0) _confirmClear = true;
            GUILayout.EndHorizontal();
            if (_confirmClear)
            {
                GUILayout.BeginHorizontal();
                Ui.Label($"Delete all {CameraPath.Keys.Count} keys?");
                if (Ui.Button("Yes")) { CameraPath.Clear(); _confirmClear = false; }
                if (Ui.Button("No")) _confirmClear = false;
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            HeldButton("< Prev key ([)", () => ReplaySession.Seek(CameraPath.Neighbor(ReplaySession.T, -1)), 0.2f);
            HeldButton("Next key (]) >", () => ReplaySession.Seek(CameraPath.Neighbor(ReplaySession.T, 1)), 0.2f);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (Ui.Button("Select all keys"))
            {
                _picked.Clear();
                _picked.UnionWith(CameraPath.Keys);
            }
            if (Ui.Button($"Select none ({_picked.Count})")) _picked.Clear();
            GUILayout.EndHorizontal();
            Ui.Label("Shift+click keys on the timeline to select them: they drag and delete together. Dragging snaps to markers (Alt: no snap).");
            if (Ui.Button(FreeCam.Take != null ? "Stop take (N)" : $"Record a take at {ReplaySession.Speed:0.##}x: fly by hand, saved as keys (N)")) ToggleTake();
            Ui.Label("Whole path speed (up/down while following):");
            GUILayout.BeginHorizontal();
            foreach (float s in Speeds)
            {
                bool on = Mathf.Approximately(s, CameraPath.Rate);
                if (Ui.Toggle(on, $"{s:0.##}x", GUI.skin.button) && !on) CameraPath.SetRate(s);
            }
            GUILayout.EndHorizontal();
            if (CameraPath.Anchor != null)
            {
                if (Ui.Button($"Path {(CameraPath.Anchor.Turn ? "moves and turns" : "moves")} with {CameraPath.Anchor.Name}. Click to fix it in the world")) _queued = () => CameraPath.SetAnchor(null);
            }
            else
            {
                GUILayout.BeginHorizontal();
                if (Ui.Button("Path moves with target")) _queued = () => AttachPath(false);
                if (Ui.Button("Moves and turns with it")) _queued = () => AttachPath(true);
                GUILayout.EndHorizontal();
            }
            CameraPath.Follow = Ui.Toggle(CameraPath.Follow, "Follow path (P)");
            CameraPath.Show = Ui.Toggle(CameraPath.Show, CameraPath.Keys.Count >= 2
                ? $"Show path (V): green slow, red fast, fastest {CameraPath.FastestSpeed:0.#} m/s"
                : "Show path (V, hidden while following)");

            int seg = CameraPath.SegmentAt(ReplaySession.T);
            if (seg < 0) return;
            CameraPath.Key at = CameraPath.Keys[seg];
            Ui.Label($"<b>Key {seg + 1}</b> at {Clock(at.t)}", _label);
            GUILayout.BeginHorizontal();
            NumberField("Time", at.t, v =>
            {
                CameraPath.Snapshot("edit key");
                at.t = Mathf.Clamp(v, 0f, ReplaySession.Duration);
                CameraPath.Changed();
                ReplaySession.Seek(at.t); // stay on this key
            });
            NumberField("FOV", at.fov, v =>
            {
                CameraPath.Snapshot("edit key");
                at.fov = Mathf.Clamp(v, 5f, 140f);
                CameraPath.Changed();
            });
            NumberField("Roll", -at.roll, v =>
            {
                CameraPath.Snapshot("edit key");
                at.roll = -v;
                CameraPath.Changed();
            });
            GUILayout.EndHorizontal();
            float fov = Slider("FOV", at.fov, 5f, 140f, "0", 65f);
            float roll = -Slider("Roll", -at.roll, -180f, 180f, "0", 0f);
            // Time of day: blends to the next key that sets it. Weather: changes at the key and stays.
            bool setsDay = at.day >= 0f;
            if (Ui.Toggle(setsDay, "Set the time of day at this key") != setsDay)
            {
                _queued = () =>
                {
                    CameraPath.Snapshot("key time of day");
                    at.day = setsDay || !EnvMan.instance ? -1f : EnvMan.instance.GetDayFraction();
                    CameraPath.Changed();
                };
            }
            float day = setsDay ? Slider($"Time {DayClock(at.day)}", at.day, 0f, 0.999f, "0.000", 0.5f) : at.day;
            if (setsDay && Ui.Button("Match original time")) _queued = () => MatchOriginal(at, true);
            if (fov != at.fov || roll != at.roll || day != at.day)
            {
                if (!_keySliding) CameraPath.Snapshot("edit key");
                _keySliding = true;
                at.fov = fov;
                at.roll = roll;
                at.day = day;
                CameraPath.Resort();
            }
            bool setsWeather = at.weather.Length > 0;
            if (Ui.Toggle(setsWeather, "Change the weather at this key") != setsWeather)
            {
                _queued = () =>
                {
                    CameraPath.Snapshot("key weather");
                    at.weather = setsWeather || !EnvMan.instance ? "" : EnvMan.instance.GetCurrentEnvironment()?.m_name ?? "";
                    CameraPath.Changed();
                };
            }
            if (setsWeather)
            {
                if (Ui.Button("Match original weather")) _queued = () => MatchOriginal(at, false);
                _keyWeatherScroll = GUILayout.BeginScrollView(_keyWeatherScroll, GUILayout.Height(110));
                foreach (string w in Weathers())
                {
                    if (Ui.Toggle(at.weather == w, w, GUI.skin.button) && at.weather != w)
                    {
                        CameraPath.Snapshot("key weather");
                        at.weather = w;
                        CameraPath.Changed();
                    }
                }
                GUILayout.EndScrollView();
            }
            if (_keySliding && GUIUtility.hotControl == 0)
            {
                _keySliding = false;
                CameraPath.Changed();
            }
            // Speed track: the replay speed at this key while following the path. The camera keeps
            // its pace, so slow motion doesn't slow the camera move.
            Ui.Label($"Replay speed here while following: {at.speed:0.##}x");
            GUILayout.BeginHorizontal();
            foreach (float sp in Speeds)
            {
                if (Ui.Toggle(Mathf.Approximately(at.speed, sp), $"{sp:0.##}x", GUI.skin.button) && !Mathf.Approximately(at.speed, sp))
                {
                    CameraPath.Snapshot("change key speed");
                    at.speed = sp;
                    CameraPath.Changed();
                }
            }
            GUILayout.EndHorizontal();
            if (seg >= CameraPath.Keys.Count - 1) return;
            Ui.Label($"Move to key {seg + 2}:");
            GUILayout.BeginHorizontal();
            foreach (CameraPath.Ease ease in System.Enum.GetValues(typeof(CameraPath.Ease)))
            {
                if (Ui.Toggle(at.ease == ease, EaseName(ease), GUI.skin.button) && at.ease != ease)
                {
                    CameraPath.Snapshot("change ease");
                    at.ease = ease;
                    CameraPath.Changed();
                }
            }
            GUILayout.EndHorizontal();
        }

        // The key takes what the recording had at the key's time.
        private static void MatchOriginal(CameraPath.Key k, bool day)
        {
            ReplayEvent rec = ReplaySession.SampleEnv(k.t);
            if (rec == null)
            {
                Toast("This recording has no time or weather saved");
                return;
            }
            CameraPath.Snapshot(day ? "key time of day" : "key weather");
            if (day) k.day = rec.F;
            else k.weather = rec.S;
            CameraPath.Changed();
        }

        private static void TargetSection()
        {
            Ui.Label(FreeCam.Attach == FreeCam.AttachMode.None
                ? "Aim the screen center at a character, vehicle or projectile (pause first to catch a fast one)."
                : $"{FreeCam.ModeName(FreeCam.Attach)} {FreeCam.AttachName}. Fly to change the offset.");
            GUILayout.BeginHorizontal();
            if (FreeCam.Attach == FreeCam.AttachMode.None)
            {
                if (Ui.Button("Follow (J)")) ToggleAttach(FreeCam.AttachMode.Follow);
                if (Ui.Button("Mount (Shift+J)")) ToggleAttach(FreeCam.AttachMode.Mount);
                if (Ui.Button("Orbit (B)")) ToggleAttach(FreeCam.AttachMode.Orbit);
            }
            else if (Ui.Button("Detach (J)")) ToggleAttach(FreeCam.AttachMode.None);
            GUILayout.EndHorizontal();
            if (Ui.Button(FreeCam.LookName != null ? $"Stop looking at {FreeCam.LookName} (L)" : "Look at aimed object (L)")) ToggleLook();
            FreeCam.LookSmoothing = Slider("Look softness (s)", FreeCam.LookSmoothing, 0f, 2f, "0.00", 0.25f);
            FreeCam.FollowLag = Slider("Follow softness (s)", FreeCam.FollowLag, 0f, 2f, "0.00", 0f);
            FreeCam.OrbitSpeed = Slider("Orbit speed (°/s)", FreeCam.OrbitSpeed, -90f, 90f, "0", 20f);
        }

        private static void HideSection()
        {
            GUILayout.BeginHorizontal();
            if (Ui.Button("Hide aimed object (U)")) HideAimed();
            GUI.enabled = ReplaySession.Hidden.Count > 0;
            if (Ui.Button($"Show hidden ({ReplaySession.Hidden.Count})"))
            {
                CameraPath.Snapshot("show hidden");
                ReplaySession.ShowAllHidden();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            ReplaySession.HidePlayers = Ui.Toggle(ReplaySession.HidePlayers, "Hide all players");
            ReplaySession.HideHeld = Ui.Toggle(ReplaySession.HideHeld, "Hide what players hold");
            ReplaySession.HideProjectiles = Ui.Toggle(ReplaySession.HideProjectiles, "Hide projectiles (arrows, spells, thrown things)");
        }

        private static void PictureSection()
        {
            Picture.Brightness = Slider("Brightness", Picture.Brightness, -0.5f, 0.5f, "0.00", 0f);
            Picture.Exposure = Slider("Exposure", Picture.Exposure, -3f, 3f, "0.00", 0f);
            Picture.Temperature = Slider("Temperature", Picture.Temperature, -100f, 100f, "0", 0f);
            Picture.Saturation = Slider("Saturation", Picture.Saturation, 0f, 2f, "0.00", 1f);
            Picture.Contrast = Slider("Contrast", Picture.Contrast, 0.5f, 2f, "0.00", 1f);
            Picture.Bloom = Slider("Bloom", Picture.Bloom, 0f, 3f, "0.00", 1f);
            Picture.LensDirt = Ui.Toggle(Picture.LensDirt, "Lens dirt in the bloom");
            Picture.Shafts = Slider("Sun shafts", Picture.Shafts, 0f, 3f, "0.00", 1f);
            if (Ui.Button("Revert all")) Picture.ResetValues();
        }

        // Part of the replay to save as its own shorter replay.
        private static float _clipIn, _clipOut;

        public static void NewReplay()
        {
            _clipIn = _clipOut = 0f;
            _picked.Clear();
        }

        private static void TrimSection()
        {
            float end = _clipOut > 0f ? _clipOut : ReplaySession.Duration;
            Ui.Label($"From {Clock(_clipIn)} to {Clock(end)} ({end - _clipIn:0.0} s). Camera keys in that part go with it.");
            Ui.Label("Drag along the timeline to select a section; right-click it to clear.");
            GUILayout.BeginHorizontal();
            if (Ui.Button("Start at playhead")) _clipIn = Mathf.Min(ReplaySession.T, end - 0.1f);
            if (Ui.Button("End at playhead")) _clipOut = Mathf.Max(ReplaySession.T, _clipIn + 0.1f);
            if (Ui.Button("Whole replay")) _clipIn = _clipOut = 0f;
            GUILayout.EndHorizontal();
            GUI.enabled = !Clipper.Busy;
            if (Ui.Button(Clipper.Busy ? "Saving..." : "Save as a new replay"))
            {
                string source = ReplaySession.Data.Path;
                string dest = Clipper.FreeName(System.IO.Path.GetFileNameWithoutExtension(source));
                Clipper.Start(new[] { source }, _clipIn, end, dest, CameraPath.ClipText(_clipIn, end), false);
                Toast("Saving the clip...");
            }
            GUI.enabled = true;
        }

        private static void ControlsSection()
        {
            FreeCam.UseOriginal = Ui.Toggle(FreeCam.UseOriginal, "Original camera (what the recorder saw) (O)");
            FreeCam.Fov = Slider("FOV", FreeCam.Fov, 5f, 140f, "0", 65f);
            FreeCam.Roll = -Slider("Roll", -FreeCam.Roll, -180f, 180f, "0", 0f); // right on the slider = roll right
            FreeCam.MoveSpeed = Slider("Pan speed (m/s)", FreeCam.MoveSpeed, 0.2f, 200f, "0.0", 8f);
            FreeCam.Smoothing = Slider("Pan smoothing", FreeCam.Smoothing, 0f, 1f, "0.00", 0.3f);
            FreeCam.AimSmoothing = Slider("Aim smoothing", FreeCam.AimSmoothing, 0f, 1f, "0.00", 0f);
            FreeCam.SensYaw = Slider("Yaw sensitivity", FreeCam.SensYaw, 0.05f, 4f, "0.00", 1f);
            FreeCam.SensPitch = Slider("Pitch sensitivity", FreeCam.SensPitch, 0.05f, 4f, "0.00", 1f);
            FreeCam.RollSpeed = Slider("Roll speed (°/s, Z/C)", FreeCam.RollSpeed, 2f, 180f, "0", 45f);
            FreeCam.InvertY = Ui.Toggle(FreeCam.InvertY, "Invert Y");
            GUILayout.BeginHorizontal();
            FreeCam.LockYaw = Ui.Toggle(FreeCam.LockYaw, "Lock yaw");
            FreeCam.LockPitch = Ui.Toggle(FreeCam.LockPitch, "Lock pitch");
            FreeCam.LockRoll = Ui.Toggle(FreeCam.LockRoll, "Lock roll");
            GUILayout.EndHorizontal();
            FreeCam.LockHeight = Ui.Toggle(FreeCam.LockHeight, "Lock height (W, A, S and D stay level; Q and E still move)");
            GUILayout.BeginHorizontal();
            Ui.Label("Move only along:");
            foreach (FreeCam.AxisLock a in System.Enum.GetValues(typeof(FreeCam.AxisLock)))
            {
                if (Ui.Toggle(FreeCam.MoveAxis == a, a == FreeCam.AxisLock.None ? "any" : a.ToString(), GUI.skin.button)) FreeCam.MoveAxis = a;
            }
            GUILayout.EndHorizontal();
        }

        private static void FramingSection()
        {
            Ui.Label("Guide (G):");
            GUILayout.BeginHorizontal();
            int shown = 0;
            foreach (Grid g in System.Enum.GetValues(typeof(Grid)))
            {
                if (shown++ == 4)
                {
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                }
                if (Ui.Toggle(GridMode == g, g.ToString(), GUI.skin.button) && GridMode != g)
                {
                    GridMode = g;
                    GuideHidden = false;
                }
            }
            GUILayout.EndHorizontal();
            GuideHidden = Ui.Toggle(GuideHidden, "Hide the guide (hold G)");
            FreeCam.DofOn = Ui.Toggle(FreeCam.DofOn, "Depth of field");
            if (!FreeCam.DofOn) return;
            FreeCam.DofAuto = Ui.Toggle(FreeCam.DofAuto, "Auto focus (look-at target, or the screen center)");
            if (FreeCam.DofAuto) Ui.Label($"Focus: {FreeCam.DofDistance:0.0} m");
            else FreeCam.DofDistance = Slider("Focus (m)", FreeCam.DofDistance, 0.5f, 200f, "0.0", 10f);
            FreeCam.DofAperture = Slider("Blur", FreeCam.DofAperture, 0.05f, FreeCam.DofMaxAperture, "0.00", 0.5f);
        }

        private static void PresetsSection()
        {
            for (int i = 0; i < Plugin.CameraPresets.Length; i++)
            {
                GUILayout.BeginHorizontal();
                Ui.Label($"Preset {i + 1}", GUILayout.Width(70));
                GUI.enabled = Plugin.CameraPresets[i].Value.Length > 0;
                if (Ui.Button("Load"))
                {
                    Toast(FreeCam.PresetFromText(Plugin.CameraPresets[i].Value) ? $"Preset {i + 1} loaded" : $"Preset {i + 1} is damaged");
                }
                GUI.enabled = true;
                if (Ui.Button("Save"))
                {
                    Plugin.CameraPresets[i].Value = FreeCam.PresetToText();
                    Toast($"Preset {i + 1} saved");
                }
                GUILayout.EndHorizontal();
            }
        }

        private static void PlayersSection()
        {
            bool anyPlayer = false;
            foreach (Player pl in Player.GetAllPlayers())
            {
                if (pl == Player.m_localPlayer || !pl.GetComponent<ZNetView>() || pl.GetComponent<ZNetView>().GetZDO() == null) continue;
                anyPlayer = true;
                GUILayout.BeginHorizontal();
                Ui.Label(pl.GetPlayerName(), GUILayout.Width(110));
                if (Ui.Button("Go to")) GoTo(pl);
                if (Ui.Button("Follow")) { GoTo(pl); Toast(FreeCam.AttachTo(pl.GetComponent<ZNetView>(), FreeCam.AttachMode.Follow)); }
                if (Ui.Button("Eyes")) { CameraPath.Follow = false; FreeCam.UseOriginal = false; Toast(FreeCam.AttachTo(pl.GetComponent<ZNetView>(), FreeCam.AttachMode.Pov)); }
                GUILayout.EndHorizontal();
            }
            if (!anyPlayer) Ui.Label("No players at this time.");
        }

        private static void EnvironmentSection()
        {
            // Any change here is applied instantly, even while paused.
            var before = (ReplaySession.FollowRecordedTime, ReplaySession.TimeOverride, ReplaySession.FollowRecordedWeather, ReplaySession.WeatherOverride);
            if (CameraPath.Keys.Exists(k => k.day >= 0f || k.weather.Length > 0)) Ui.Label("Camera keys that set the time or weather override these.");
            ReplaySession.WindOverride = Ui.Toggle(ReplaySession.WindOverride, "Set the wind");
            if (ReplaySession.WindOverride)
            {
                ReplaySession.WindAngle = Slider("Direction (°)", ReplaySession.WindAngle, 0f, 360f, "0", 0f);
                ReplaySession.WindStrength = Slider("Strength", ReplaySession.WindStrength, 0f, 1f, "0.00", 0.5f);
            }
            ReplaySession.FollowRecordedTime = Ui.Toggle(ReplaySession.FollowRecordedTime, "Time of day follows the recording");
            if (!ReplaySession.FollowRecordedTime)
            {
                ReplaySession.TimeOverride = Slider($"Time {DayClock(ReplaySession.TimeOverride)}", ReplaySession.TimeOverride, 0f, 1f, "0.000", 0.5f);
                Ui.Label("Camera keys that set the time of day override this.");
            }
            ReplaySession.FollowRecordedWeather = Ui.Toggle(ReplaySession.FollowRecordedWeather, "Weather follows the recording");
            if (!ReplaySession.FollowRecordedWeather)
            {
                _weatherScroll = GUILayout.BeginScrollView(_weatherScroll, GUILayout.Height(140));
                foreach (string w in Weathers())
                {
                    if (Ui.Toggle(ReplaySession.WeatherOverride == w, w, GUI.skin.button)) ReplaySession.WeatherOverride = w;
                }
                GUILayout.EndScrollView();
            }
            if (before != (ReplaySession.FollowRecordedTime, ReplaySession.TimeOverride, ReplaySession.FollowRecordedWeather, ReplaySession.WeatherOverride))
            {
                ReplaySession.EnvironmentChanged();
            }
        }

        private static void HudSection()
        {
            CombatHud = Ui.Toggle(CombatHud, "Show health bars and damage numbers (rest of the HUD stays hidden)");
            if (Hud.instance) Hud.instance.m_userHidden = !Ui.Toggle(!Hud.instance.m_userHidden, "Show the normal game HUD (same as Ctrl+F3)");
            ShowStatus = Ui.Toggle(ShowStatus, "Timeline and status line (T)");
            ReplaySession.UsePose = Ui.Toggle(ReplaySession.UsePose, $"Exact pose for the recording player and their vehicle ({ReplaySession.Data.Poses.Count} + {ReplaySession.Data.Parts.Count} tracks)");
            ReplaySession.UseHiFi = Ui.Toggle(ReplaySession.UseHiFi, $"High-rate motion (H) ({ReplaySession.Data.HiFi.Count} tracks)");
        }

        private static void ExportSection()
        {
            GUILayout.BeginHorizontal();
            Ui.Label("Range:");
            foreach (Exporter.Span sp in System.Enum.GetValues(typeof(Exporter.Span)))
            {
                string label = sp == Exporter.Span.CameraPath ? "Camera path" : sp == Exporter.Span.PlayheadToEnd ? "Playhead to end" : "Whole replay";
                if (Ui.Toggle(Exporter.Range == sp, label, GUI.skin.button)) Exporter.Range = sp;
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Ui.Label("Frame rate:");
            foreach (int f in new[] { 24, 30, 60, 120, 144 })
            {
                if (Ui.Toggle(Plugin.ExportFps.Value == f, f.ToString(), GUI.skin.button)) Plugin.ExportFps.Value = f;
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Ui.Label("Size:");
            for (int k = 1; k <= 3; k++)
            {
                if (Ui.Toggle(Plugin.ExportScale.Value == k, $"{k}x ({Screen.width * k}x{Screen.height * k})", GUI.skin.button)) Plugin.ExportScale.Value = k;
            }
            GUILayout.EndHorizontal();
            Plugin.ExportAudio.Value = Ui.Toggle(Plugin.ExportAudio.Value, "Sound (plays the range again in real time afterwards)");
            Plugin.ExportOpen.Value = Ui.Toggle(Plugin.ExportOpen.Value, "Show the video in File Explorer when it's done");
            bool ff = Exporter.FindFfmpeg() != null;
            Ui.Label(ff ? "Output: MP4 (FFmpeg found)" : Exporter.NeedFfmpeg);
            if (!ff && Ui.Button("Install FFmpeg (WinGet)")) Notify(Exporter.InstallFfmpeg());
            if (Exporter.RangeFor(Exporter.Range, out float es, out float ee))
            {
                Ui.Label($"{Clock(es)} to {Clock(ee)}" + (Exporter.Range == Exporter.Span.CameraPath ? $", {CameraPath.OutputTime(ee) - CameraPath.OutputTime(es):0.0} s of video (speed track)" : ""));
            }
            GUILayout.BeginHorizontal();
            if (Ui.Button("Start export (F10)")) StartExport();
            if (Ui.Button("Screenshot (F9)")) Screenshot();
            if (Ui.Button("Open folder"))
            {
                System.IO.Directory.CreateDirectory(Plugin.ExportDir);
                Application.OpenURL("file:///" + Plugin.ExportDir.Replace('\\', '/'));
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Ui.Label("Screenshot size:");
            for (int k = 1; k <= 4; k++)
            {
                if (Ui.Toggle(Plugin.ScreenshotScale.Value == k, $"{k}x", GUI.skin.button)) Plugin.ScreenshotScale.Value = k;
            }
            GUILayout.EndHorizontal();
            Picture.Brightness = Slider("Brightness", Picture.Brightness, -0.5f, 0.5f, "0.00", 0f);
            Picture.Exposure = Slider("Exposure", Picture.Exposure, -3f, 3f, "0.00", 0f);
            FreeCam.FlashOn = Ui.Toggle(FreeCam.FlashOn, "Camera flash on screenshots");
            if (FreeCam.FlashOn)
            {
                FreeCam.FlashStrength = Slider("Flash strength", FreeCam.FlashStrength, 0.1f, 8f, "0.0", 2f);
                FreeCam.FlashRange = Slider("Flash reach (m)", FreeCam.FlashRange, 2f, 100f, "0", 25f);
                FreeCam.FlashPreview = Ui.Toggle(FreeCam.FlashPreview, "Keep it lit to line up the shot");
            }
            if (Exporter.Status.Length > 0) Ui.Label(Exporter.Status);
        }

        private static void InfoSection()
        {
            Ui.Label($"{ReplaySession.AliveCount} objects, {ReplaySession.RpcsApplied} RPCs replayed, {ReplaySession.RpcErrors} RPC errors");
            if (!_confirmExit)
            {
                if (Ui.Button("Exit replay"))
                {
                    _confirmExit = true;
                    _scroll.y = 1e7f; // this section is last, and the scroll view stops at its end
                }
            }
            else
            {
                GUILayout.BeginHorizontal();
                Ui.Label("Leave the replay?");
                if (Ui.Button("Cancel")) _confirmExit = false;
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (Ui.Button("Save and exit")) { _confirmExit = false; Game.instance?.Logout(save: false); }
                if (Ui.Button("Exit without saving"))
                {
                    _confirmExit = false;
                    CameraPath.RevertFile();
                    Game.instance?.Logout(save: false);
                }
                GUILayout.EndHorizontal();
                Ui.Label("Without saving: camera keys and bookmarks go back to how they were when this replay opened.");
            }
        }

        // Right-click anywhere on the row: back to the default. A slider grabs any mouse button, so the
        // click is read before it, and its grab is let go after.
        private static float Slider(string label, float v, float min, float max, string fmt, float def)
        {
            Event e = Event.current;
            bool rightDown = e.type == EventType.MouseDown && e.button == 1;
            Vector2 at = e.mousePosition;
            GUILayout.BeginHorizontal();
            Ui.Label($"{label}: {v.ToString(fmt)}", GUILayout.Width(170));
            v = GUILayout.HorizontalSlider(v, min, max);
            GUILayout.EndHorizontal();
            if (rightDown && GUILayoutUtility.GetLastRect().Contains(at))
            {
                v = def;
                GUIUtility.hotControl = 0;
                e.Use();
            }
            return v;
        }

        private static void DrawGrid()
        {
            if (GridMode == Grid.Off || GuideHidden) return;
            float w = Screen.width, h = Screen.height;
            GUI.color = new Color(1f, 1f, 1f, 0.45f);
            switch (GridMode)
            {
                case Grid.Thirds:
                    Lines(w, h, 1f / 3f, 2f / 3f);
                    break;
                case Grid.Golden:
                    Lines(w, h, 0.382f, 0.618f);
                    break;
                case Grid.Center:
                    GUI.DrawTexture(new Rect(w / 2f - 0.5f, 0, 1, h), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(0, h / 2f - 0.5f, w, 1), Texture2D.whiteTexture);
                    break;
                case Grid.Safe: // action safe and title safe
                    Outline(w * 0.9f, h * 0.9f, w, h);
                    Outline(w * 0.8f, h * 0.8f, w, h);
                    break;
                case Grid.Wide: // 2.39:1
                    Outline(w, Mathf.Min(h, w / 2.39f), w, h);
                    break;
                case Grid.Vertical: // 9:16
                    Outline(Mathf.Min(w, h * 9f / 16f), h, w, h);
                    break;
                case Grid.Horizon:
                    Camera cam = ViewCamera;
                    if (cam)
                    {
                        // How far the world's horizon is turned on screen by the camera's roll.
                        float tilt = Mathf.Atan2(Vector3.Dot(Vector3.up, cam.transform.right), Vector3.Dot(Vector3.up, cam.transform.up)) * Mathf.Rad2Deg;
                        Matrix4x4 saved = GUI.matrix;
                        GUIUtility.RotateAroundPivot(tilt, new Vector2(w / 2f, h / 2f));
                        GUI.DrawTexture(new Rect(-w, h / 2f - 0.5f, w * 3f, 1), Texture2D.whiteTexture);
                        GUI.matrix = saved;
                    }
                    break;
            }
            GUI.color = Color.white;
        }

        private static void Outline(float bw, float bh, float w, float h)
        {
            float x = (w - bw) / 2f, y = (h - bh) / 2f;
            GUI.DrawTexture(new Rect(x, y, bw, 1), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(x, y + bh - 1f, bw, 1), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(x, y, 1, bh), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(x + bw - 1f, y, 1, bh), Texture2D.whiteTexture);
        }

        private static void Lines(float w, float h, float a, float b)
        {
            GUI.DrawTexture(new Rect(w * a, 0, 1, h), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(w * b, 0, 1, h), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(0, h * a, w, 1), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(0, h * b, w, 1), Texture2D.whiteTexture);
        }

        private static string EaseName(CameraPath.Ease e) =>
            e == CameraPath.Ease.EaseInOut ? "Ease in/out" : e == CameraPath.Ease.EaseIn ? "Ease in" : e == CameraPath.Ease.EaseOut ? "Ease out" : e.ToString();

        private static string SpeedText() => ReplaySession.PathSpeed ? $"{ReplaySession.PlaySpeed:0.##}x (path)" : $"{ReplaySession.Speed:0.##}x";

        private static string Clock(float s) => $"{(int)s / 60:00}:{s % 60f:00.0}";

        private static string DayClock(float f)
        {
            float hours = f * 24f;
            return $"{(int)hours:00}:{(int)((hours % 1f) * 60f):00}";
        }
    }
}
