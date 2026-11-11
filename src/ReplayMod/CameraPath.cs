using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace ReplayMod
{
    // Camera keyframes on the replay timeline. Each key is a full camera pose (position, yaw, pitch,
    // roll, FOV) at a replay time; between keys the camera follows a time-parameterized Catmull-Rom
    // spline, so it passes through every key exactly and its speed follows the key timing.
    // Saved next to the replay as <name>.camera.txt.
    internal static class CameraPath
    {
        // How the camera travels from a key to the next one.
        public enum Ease { Smooth, EaseInOut, Linear, Hold, EaseIn, EaseOut } // saved as numbers: append only

        [Serializable]
        public class Key
        {
            public float t;
            public Vector3 pos;
            public float yaw, pitch, roll, fov;
            public Ease ease; // for the segment that starts at this key; older files read as Smooth
            public float speed = 1f; // replay speed at this key while following the path
            public float day = -1f; // time of day set here, 0..1, blending to the next that sets it; negative = none
            public string weather = ""; // weather set here, until the next that sets it; empty = none

            public Key Clone() => (Key)MemberwiseClone();
        }

        private static Key[] _bookmarks = new Key[9];

        public static readonly List<Key> Keys = new List<Key>();
        // Plays the whole path faster or slower, on top of each key's own speed.
        public static float Rate { get; private set; } = 1f;

        // The object the path rides on: its keys are then relative to that object, wherever it is at
        // the playhead. Turn = they turn with it too. Null = keys are fixed points in the world.
        public class PathAnchor
        {
            public RecId Target;
            public bool Turn;
            public string Name;
        }

        public static PathAnchor Anchor { get; private set; }
        public static bool Follow;    // drive the camera from the path
        public static bool Show = true; // draw the path in the world (hidden while following)

        private static string _file, _fileAtLoad;
        private static bool _canRevert, _reverted;

        // Where the camera was, with its settings, when the replay was last left with saving. Null = none.
        public class View
        {
            public float T;
            public Key Pose;
            public string Settings; // FreeCam.PresetToText
        }

        public static View LastView { get; private set; }
        private const int Dims = 7;

        public static void Load(string replayPath)
        {
            Keys.Clear();
            _undo.Clear();
            _redo.Clear();
            _bookmarks = new Key[9];
            Follow = false;
            Rate = 1f;
            Anchor = null;
            LastView = null;
            _reverted = false;
            _canRevert = false; // set once the file has been read: an unreadable one is never overwritten or deleted
            _file = Path.ChangeExtension(replayPath, ".camera.txt");
            try
            {
                _fileAtLoad = File.Exists(_file) ? File.ReadAllText(_file) : null;
                _canRevert = true;
                if (_fileAtLoad != null) FromText(_fileAtLoad, Keys, _bookmarks);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"camera path {_file} unreadable, starting empty: {e.Message}");
            }
            Keys.Sort((a, b) => a.t.CompareTo(b.t));
        }

        // Edits are saved as they happen, so "exit without saving" puts back the file as the replay opened it.
        public static void RevertFile()
        {
            if (!_canRevert) return;
            _reverted = true;
            try
            {
                if (_fileAtLoad != null) File.WriteAllText(_file, _fileAtLoad);
                else if (_file != null) File.Delete(_file);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Plugin.Log.LogWarning($"couldn't put back {_file}: {e.Message}");
            }
        }

        // Plain text, one key or bookmark per line. (JsonUtility wrote "{}" for these types in Valheim.)
        //   k <t> <x> <y> <z> <yaw> <pitch> <roll> <fov> <ease> <speed> <day> <weather>
        //   b <slot> <x> <y> <z> <yaw> <pitch> <roll> <fov>
        //   r <path speed>
        //   a <user> <id> <turn 0/1> <name>
        //   v <replay time> <x> <y> <z> <yaw> <pitch> <roll> <fov> <camera settings...>
        private static string ToText(List<Key> keys, Key[] bookmarks)
        {
            var sb = new StringBuilder("# ReplayMod camera path 1\n");
            string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
            if (Rate != 1f) sb.Append($"r {F(Rate)}\n");
            if (Anchor != null) sb.Append($"a {Anchor.Target.User.ToString(CultureInfo.InvariantCulture)} {Anchor.Target.Id.ToString(CultureInfo.InvariantCulture)} {(Anchor.Turn ? 1 : 0)} {Anchor.Name}\n");
            string Pose(Key k) => $"{F(k.pos.x)} {F(k.pos.y)} {F(k.pos.z)} {F(k.yaw)} {F(k.pitch)} {F(k.roll)} {F(k.fov)}";
            foreach (Key k in keys) sb.Append($"k {F(k.t)} {Pose(k)} {(int)k.ease} {F(k.speed)} {F(k.day)} {k.weather}\n");
            for (int i = 0; i < bookmarks.Length; i++)
            {
                if (bookmarks[i] != null && bookmarks[i].fov > 0f) sb.Append($"b {i} {Pose(bookmarks[i])}\n");
            }
            if (LastView != null) sb.Append($"v {F(LastView.T)} {Pose(LastView.Pose)} {LastView.Settings}\n");
            return sb.ToString();
        }

        private static void FromText(string text, List<Key> keys, Key[] bookmarks)
        {
            foreach (string raw in text.Split('\n'))
            {
                string[] f = raw.Trim().Split(' ');
                float P(int i) => float.Parse(f[i], CultureInfo.InvariantCulture);
                if (f[0] == "r" && f.Length == 2)
                {
                    Rate = P(1);
                    continue;
                }
                if (f[0] == "a" && f.Length >= 5)
                {
                    Anchor = new PathAnchor
                    {
                        Target = new RecId(long.Parse(f[1], CultureInfo.InvariantCulture), uint.Parse(f[2], CultureInfo.InvariantCulture)),
                        Turn = f[3] == "1",
                        Name = string.Join(" ", f, 4, f.Length - 4),
                    };
                    continue;
                }
                if (f[0] == "v" && f.Length > 9)
                {
                    LastView = new View
                    {
                        T = P(1),
                        Pose = new Key { pos = new Vector3(P(2), P(3), P(4)), yaw = P(5), pitch = P(6), roll = P(7), fov = P(8) },
                        Settings = string.Join(" ", f, 9, f.Length - 9),
                    };
                    continue;
                }
                if (f.Length < 9 || (f[0] != "k" && f[0] != "b")) continue;
                var k = new Key { pos = new Vector3(P(2), P(3), P(4)), yaw = P(5), pitch = P(6), roll = P(7), fov = P(8) };
                if (f[0] == "k")
                {
                    k.t = P(1);
                    if (f.Length > 9) k.ease = (Ease)int.Parse(f[9], CultureInfo.InvariantCulture);
                    if (f.Length > 10) k.speed = P(10);
                    if (f.Length > 11) k.day = P(11);
                    if (f.Length > 12) k.weather = string.Join(" ", f, 12, f.Length - 12);
                    keys.Add(k);
                }
                else
                {
                    int slot = int.Parse(f[1], CultureInfo.InvariantCulture);
                    if (slot >= 0 && slot < bookmarks.Length) bookmarks[slot] = k;
                }
            }
        }

        // While a key is dragged: keep time order (the spline needs it) without writing the file each event.
        public static void Resort()
        {
            Keys.Sort((a, b) => a.t.CompareTo(b.t));
            Rebuild();
        }

        public static void Changed() => Save();

        public static void SetBookmark(int slot, Vector3 pos, float yaw, float pitch, float roll, float fov)
        {
            Snapshot($"bookmark {slot + 1}");
            _bookmarks[slot] = new Key { pos = pos, yaw = yaw, pitch = pitch, roll = roll, fov = fov };
            Save();
        }

        // Leaving with saving keeps the view; "exit without saving" has already put the file back.
        public static void SaveView(float t)
        {
            if (_reverted || _file == null) return;
            LastView = new View
            {
                T = t,
                Pose = new Key { pos = FreeCam.Pos, yaw = FreeCam.Yaw, pitch = FreeCam.Pitch, roll = FreeCam.Roll, fov = FreeCam.Fov },
                Settings = FreeCam.PresetToText(),
            };
            Save();
        }

        public static Key GetBookmark(int slot) => _bookmarks[slot] != null && _bookmarks[slot].fov > 0f ? _bookmarks[slot] : null;

        private static void Save()
        {
            Resort();
            if (_file == null) return;
            try
            {
                File.WriteAllText(_file, ToText(Keys, _bookmarks));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"camera path save failed: {e.Message}");
            }
        }

        // Adds a key at time t, replacing one already within a frame or two of it.
        public static void AddKey(float t, Vector3 pos, float yaw, float pitch, float roll, float fov)
        {
            int old = Nearest(t, 0.05f);
            Ease ease = old >= 0 ? Keys[old].ease : Ease.Smooth; // re-posing a key keeps its ease
            float speed = old >= 0 ? Keys[old].speed : SpeedAt(t); // a new key doesn't change the speed curve
            float day = old >= 0 ? Keys[old].day : -1f;
            string weather = old >= 0 ? Keys[old].weather : "";
            Snapshot(old >= 0 ? "re-pose key" : "add key");
            Keys.RemoveAll(k => Mathf.Abs(k.t - t) < 0.05f);
            Keys.Add(new Key { t = t, pos = pos, yaw = yaw, pitch = pitch, roll = roll, fov = fov, ease = ease, speed = speed, day = day, weather = weather });
            Save();
        }

        public static void RemoveNearest(float t, float within)
        {
            int i = Nearest(t, within);
            if (i < 0) return;
            Snapshot("delete key");
            Keys.RemoveAt(i);
            Save();
        }

        public static void Clear()
        {
            Snapshot("clear keys");
            Keys.Clear();
            Save();
        }

        public static void RemoveKeys(ICollection<Key> keys)
        {
            Snapshot(keys.Count == 1 ? "delete key" : $"delete {keys.Count} keys");
            Keys.RemoveAll(keys.Contains);
            Save();
        }

        public static void SetRate(float rate)
        {
            Snapshot("path speed");
            Rate = rate;
            Save();
        }

        // Where the anchor is at replay time t: the live object at the playhead, otherwise the
        // recording. Without an anchor, or when it doesn't turn, the matching part is neutral.
        private static void AnchorFrame(float t, bool live, out Vector3 origin, out Quaternion rot)
        {
            origin = Vector3.zero;
            rot = Quaternion.identity;
            if (Anchor == null) return;
            ZNetView nv = live ? FreeCam.ViewOf(Anchor.Target) : null;
            if (nv)
            {
                origin = nv.transform.position;
                rot = nv.transform.rotation;
            }
            else ReplaySession.RecordedPose(Anchor.Target, t, out origin, out rot);
            if (!Anchor.Turn) rot = Quaternion.identity;
        }

        private static void Compose(Vector3 origin, Quaternion frame, Key k, out Vector3 pos, out float yaw, out float pitch, out float roll)
        {
            pos = origin + frame * k.pos;
            Vector3 e = (frame * Quaternion.Euler(k.pitch, k.yaw, k.roll)).eulerAngles;
            yaw = e.y;
            pitch = Mathf.DeltaAngle(0f, e.x);
            roll = Mathf.DeltaAngle(0f, e.z);
        }

        // A world pose as a key relative to the frame.
        private static Key Relative(Vector3 origin, Quaternion frame, float t, Vector3 pos, float yaw, float pitch, float roll, float fov)
        {
            Quaternion inv = Quaternion.Inverse(frame);
            Vector3 e = (inv * Quaternion.Euler(pitch, yaw, roll)).eulerAngles;
            return new Key { t = t, pos = inv * (pos - origin), yaw = e.y, pitch = Mathf.DeltaAngle(0f, e.x), roll = Mathf.DeltaAngle(0f, e.z), fov = fov };
        }

        // The key's pose in the world at its own time.
        public static void KeyWorld(Key k, out Vector3 pos, out float yaw, out float pitch, out float roll)
        {
            pos = k.pos;
            yaw = k.yaw;
            pitch = k.pitch;
            roll = k.roll;
            if (Anchor == null) return;
            AnchorFrame(k.t, false, out Vector3 origin, out Quaternion frame);
            Compose(origin, frame, k, out pos, out yaw, out pitch, out roll);
        }

        // Rides the path on another object, or none. The keys stay where they are now, seen from the new frame.
        public static void SetAnchor(PathAnchor next)
        {
            Snapshot(next != null ? "attach path" : "detach path");
            foreach (Key k in Keys)
            {
                KeyWorld(k, out Vector3 pos, out float yaw, out float pitch, out float roll);
                k.pos = pos;
                k.yaw = yaw;
                k.pitch = pitch;
                k.roll = roll;
            }
            Anchor = next;
            if (next != null)
            {
                foreach (Key k in Keys)
                {
                    AnchorFrame(k.t, false, out Vector3 origin, out Quaternion frame);
                    Key local = Relative(origin, frame, k.t, k.pos, k.yaw, k.pitch, k.roll, k.fov);
                    k.pos = local.pos;
                    k.yaw = local.yaw;
                    k.pitch = local.pitch;
                    k.roll = local.roll;
                }
            }
            Save();
        }

        // The camera's current world pose as a key at the playhead.
        public static Key KeyFromWorld(float t, Vector3 pos, float yaw, float pitch, float roll, float fov)
        {
            AnchorFrame(t, true, out Vector3 origin, out Quaternion frame);
            if (Anchor != null) return Relative(origin, frame, t, pos, yaw, pitch, roll, fov);
            return new Key { t = t, pos = pos, yaw = yaw, pitch = pitch, roll = roll, fov = fov };
        }

        public static void AddWorldKey(float t, Vector3 pos, float yaw, float pitch, float roll, float fov)
        {
            Key k = KeyFromWorld(t, pos, yaw, pitch, roll, fov);
            AddKey(t, k.pos, k.yaw, k.pitch, k.roll, k.fov);
        }

        // The keys of a time range, for a clip that starts at from.
        public static string ClipText(float from, float to)
        {
            var kept = new List<Key>();
            foreach (Key k in Keys)
            {
                if (k.t < from || k.t > to) continue;
                Key c = k.Clone();
                c.t -= from;
                kept.Add(c);
            }
            return ToText(kept, _bookmarks);
        }

        // Undo and redo for every edit of keys and bookmarks: the whole state before each change.
        private class State
        {
            public List<Key> Keys;
            public Key[] Marks;
            public HashSet<RecId> Hidden;
            public float Rate;
            public PathAnchor Anchor;
            public string What;
        }

        private static readonly Stack<State> _undo = new Stack<State>(), _redo = new Stack<State>();
        public static bool CanUndo => _undo.Count > 0;
        public static bool CanRedo => _redo.Count > 0;

        private static State Capture(string what)
        {
            var s = new State { Keys = new List<Key>(), Marks = new Key[_bookmarks.Length], Hidden = new HashSet<RecId>(ReplaySession.Hidden), Rate = Rate, Anchor = Anchor, What = what };
            foreach (Key k in Keys) s.Keys.Add(k.Clone());
            for (int i = 0; i < s.Marks.Length; i++) s.Marks[i] = _bookmarks[i]?.Clone();
            return s;
        }

        // Call before changing anything.
        public static void Snapshot(string what)
        {
            _undo.Push(Capture(what));
            _redo.Clear();
        }

        private static string Step(Stack<State> from, Stack<State> to)
        {
            if (from.Count == 0) return null;
            State s = from.Pop();
            to.Push(Capture(s.What));
            Keys.Clear();
            Keys.AddRange(s.Keys);
            _bookmarks = s.Marks;
            Rate = s.Rate;
            Anchor = s.Anchor;
            ReplaySession.RestoreHidden(s.Hidden);
            return s.What;
        }

        // Each returns what was undone or redone, or null if there's nothing to do.
        public static string Undo()
        {
            string what = Step(_undo, _redo);
            if (what != null) Save();
            return what;
        }

        public static string Redo()
        {
            string what = Step(_redo, _undo);
            if (what != null) Save();
            return what;
        }

        // The time of day the keys set at t, the short way around the clock between two of them.
        public static bool DayAt(float t, out float day)
        {
            Key a = null, b = null;
            foreach (Key k in Keys)
            {
                if (k.day < 0f) continue;
                if (k.t <= t) a = k;
                else
                {
                    b = k;
                    break;
                }
            }
            day = a != null ? a.day : b != null ? b.day : 0f;
            if (a == null || b == null) return a != null || b != null;
            day = Mathf.Repeat(a.day + Mathf.DeltaAngle(a.day * 360f, b.day * 360f) / 360f * Mathf.InverseLerp(a.t, b.t, t), 1f);
            return true;
        }

        // The weather the keys set at t, or null. Before the first key that sets one, that one.
        public static string WeatherAt(float t)
        {
            string found = null;
            foreach (Key k in Keys)
            {
                if (k.weather.Length == 0) continue;
                if (k.t > t && found != null) break;
                found = k.weather;
                if (k.t > t) break;
            }
            return found;
        }

        // The key whose segment contains t (the last key at or before it), or -1.
        public static int SegmentAt(float t)
        {
            int s = -1;
            for (int i = 0; i < Keys.Count && Keys[i].t <= t + 0.01f; i++) s = i;
            return s;
        }

        public static int Nearest(float t, float within)
        {
            int best = -1;
            for (int i = 0; i < Keys.Count; i++)
            {
                if (Mathf.Abs(Keys[i].t - t) <= within && (best < 0 || Mathf.Abs(Keys[i].t - t) < Mathf.Abs(Keys[best].t - t))) best = i;
            }
            return best;
        }

        // Time of the next key after t (dir > 0) or before it (dir < 0), wrapping around at the ends.
        public static float Neighbor(float t, int dir)
        {
            float best = t;
            foreach (Key k in Keys)
            {
                if (dir > 0 ? k.t > t + 0.01f && (best == t || k.t < best) : k.t < t - 0.01f && (best == t || k.t > best)) best = k.t;
            }
            if (best == t && Keys.Count > 0) best = Keys[dir > 0 ? 0 : Keys.Count - 1].t;
            return best;
        }

        // The camera in the world at the playhead.
        public static bool Evaluate(float t, out Vector3 pos, out float yaw, out float pitch, out float roll, out float fov)
        {
            if (!EvaluateLocal(t, out pos, out yaw, out pitch, out roll, out fov)) return false;
            if (Anchor == null) return true;
            AnchorFrame(t, true, out Vector3 origin, out Quaternion frame);
            Compose(origin, frame, new Key { pos = pos, yaw = yaw, pitch = pitch, roll = roll }, out pos, out yaw, out pitch, out roll);
            return true;
        }

        // The same relative to the anchor, which is what the spline runs through.
        private static bool EvaluateLocal(float t, out Vector3 pos, out float yaw, out float pitch, out float roll, out float fov)
        {
            pos = default;
            yaw = pitch = roll = fov = 0f;
            if (Keys.Count < 2) return false;
            float[] v = Sample(t);
            pos = new Vector3(v[0], v[1], v[2]);
            yaw = v[3];
            pitch = v[4];
            roll = v[5];
            fov = v[6];
            return true;
        }

        private static float[] Vec(Key k, float yaw) => new[] { k.pos.x, k.pos.y, k.pos.z, yaw, k.pitch, k.roll, k.fov };

        private static float[] Sample(float t)
        {
            int n = Keys.Count;
            // Unwrap yaw so the camera turns the short way between neighboring keys.
            var p = new float[n][];
            float yaw = Keys[0].yaw, pitch = Keys[0].pitch;
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                {
                    yaw += Mathf.DeltaAngle(Keys[i - 1].yaw, Keys[i].yaw);
                    pitch += Mathf.DeltaAngle(Keys[i - 1].pitch, Keys[i].pitch);
                }
                p[i] = Vec(Keys[i], yaw);
                p[i][4] = pitch;
            }
            // The spline runs on output (video) time, so slow-motion keys don't slow the camera.
            float[] k = Tau();
            t = Map(t, k, out _);
            if (t <= k[0]) return p[0];
            if (t >= k[n - 1]) return p[n - 1];
            int s = 0;
            while (k[s + 1] < t) s++;
            float t1 = k[s], t2 = k[s + 1];
            float u = t2 - t1 > 1e-5f ? (t - t1) / (t2 - t1) : 1f;
            switch (Keys[s].ease)
            {
                case Ease.Hold:
                    return p[s];
                case Ease.Linear:
                    var r = new float[Dims];
                    for (int d = 0; d < Dims; d++) r[d] = Mathf.Lerp(p[s][d], p[s + 1][d], u);
                    return r;
                case Ease.EaseIn:
                    t = t1 + u * u * (t2 - t1); // starts slowly at this key
                    break;
                case Ease.EaseOut:
                    t = t1 + (1f - (1f - u) * (1f - u)) * (t2 - t1); // stops at the next key
                    break;
                case Ease.EaseInOut:
                    t = t1 + Mathf.SmoothStep(0f, 1f, u) * (t2 - t1); // same curve, but stops at both keys
                    break;
            }
            // Ends repeat their key, so the camera starts and finishes slower instead of overshooting.
            float t0 = s > 0 ? k[s - 1] : t1 - (t2 - t1);
            float t3 = s + 2 < n ? k[s + 2] : t2 + (t2 - t1);
            float[] p0 = s > 0 ? p[s - 1] : p[s], p3 = s + 2 < n ? p[s + 2] : p[s + 1];
            return BarryGoldman(p0, p[s], p[s + 1], p3, t0, t1, t2, t3, t);
        }

        // Speed track. Each key has a replay speed; between keys the speed changes linearly in output
        // time, so a segment lasts (replay seconds) / (average speed) of output time. Output time equals
        // replay time while every speed is 1.
        private static float Spd(Key k) => Mathf.Max(k.speed * Rate, 0.01f);

        private static float[] Tau()
        {
            var tau = new float[Keys.Count];
            for (int i = 0; i < tau.Length; i++)
            {
                tau[i] = i == 0 ? Keys[0].t : tau[i - 1] + (Keys[i].t - Keys[i - 1].t) / ((Spd(Keys[i - 1]) + Spd(Keys[i])) * 0.5f);
            }
            return tau;
        }

        // Replay time -> output time, and the replay speed there.
        private static float Map(float t, float[] tau, out float speed)
        {
            int n = Keys.Count;
            speed = 1f;
            if (n == 0) return t;
            if (t <= Keys[0].t) { speed = Spd(Keys[0]); return tau[0] - (Keys[0].t - t) / speed; }
            if (t >= Keys[n - 1].t) { speed = Spd(Keys[n - 1]); return tau[n - 1] + (t - Keys[n - 1].t) / speed; }
            int s = 0;
            while (Keys[s + 1].t < t) s++;
            float d = tau[s + 1] - tau[s], a = Spd(Keys[s]), b = Spd(Keys[s + 1]);
            if (d < 1e-6f) return tau[s];
            // Replay time covered after u of the segment: d * (a u + (b - a) u^2 / 2). Solve for u.
            float x = (t - Keys[s].t) / d;
            float u = Mathf.Abs(b - a) < 1e-5f ? x / a : (-a + Mathf.Sqrt(Mathf.Max(a * a + 2f * (b - a) * x, 0f))) / (b - a);
            u = Mathf.Clamp01(u);
            speed = a + (b - a) * u;
            return tau[s] + u * d;
        }

        public static float SpeedAt(float t)
        {
            if (Keys.Count < 2) return 1f;
            Map(t, Tau(), out float speed);
            return speed;
        }

        public static float OutputTime(float t) => Map(t, Tau(), out _);

        // Catmull-Rom with arbitrary knot times (Barry & Goldman's pyramidal form).
        private static float[] BarryGoldman(float[] p0, float[] p1, float[] p2, float[] p3, float t0, float t1, float t2, float t3, float t)
        {
            var r = new float[Dims];
            for (int d = 0; d < Dims; d++)
            {
                float a1 = Lerp(p0[d], p1[d], t0, t1, t), a2 = Lerp(p1[d], p2[d], t1, t2, t), a3 = Lerp(p2[d], p3[d], t2, t3, t);
                float b1 = Lerp(a1, a2, t0, t2, t), b2 = Lerp(a2, a3, t1, t3, t);
                r[d] = Lerp(b1, b2, t1, t2, t);
            }
            return r;
        }

        private static float Lerp(float a, float b, float ta, float tb, float t) => tb - ta < 1e-5f ? a : a + (b - a) * (t - ta) / (tb - ta);

        // The spline must pass through every key and stay between them on a straight line of keys.
        public static void SelfCheck()
        {
            var saved = new List<Key>(Keys);
            Keys.Clear();
            for (int i = 0; i < 4; i++) Keys.Add(new Key { t = i * i, pos = new Vector3(i * 10f, 0f, 0f), yaw = 170f + i * 10f, fov = 60f });
            bool ok = true;
            foreach (Key k in Keys)
            {
                Evaluate(k.t, out Vector3 pos, out float yaw, out _, out _, out float fov);
                ok &= (pos - k.pos).sqrMagnitude < 1e-4f && Mathf.Abs(Mathf.DeltaAngle(yaw, k.yaw)) < 1e-3f && Mathf.Abs(fov - 60f) < 1e-3f;
            }
            Evaluate(2.5f, out Vector3 mid, out float myaw, out _, out _, out _);
            ok &= mid.x > 10f && mid.x < 20f && Mathf.Abs(Mathf.DeltaAngle(myaw, 185f)) < 6f; // crosses 180 the short way
            Keys[1].ease = Ease.Hold;
            Evaluate(2.5f, out Vector3 held, out _, out _, out _, out _);
            Keys[1].ease = Ease.Linear;
            Evaluate(2.5f, out Vector3 lin, out _, out _, out _, out _);
            Keys[1].ease = Ease.EaseInOut;
            Evaluate(4f, out Vector3 eased, out _, out _, out _, out _);
            ok &= (held - Keys[1].pos).sqrMagnitude < 1e-4f && Mathf.Abs(lin.x - 15f) < 1e-3f && (eased - Keys[2].pos).sqrMagnitude < 1e-4f;
            // Speed track: 10 s of replay slowing from 1x to 0.25x lasts 16 s of output.
            Keys.Clear();
            Keys.Add(new Key { t = 0f, fov = 60f, speed = 1f });
            Keys.Add(new Key { t = 10f, pos = new Vector3(10f, 0f, 0f), fov = 60f, speed = 0.25f });
            ok &= Mathf.Abs(OutputTime(10f) - 16f) < 1e-3f && Mathf.Abs(OutputTime(5f) - 5.784f) < 0.01f
                && Mathf.Abs(SpeedAt(10f) - 0.25f) < 1e-3f && Mathf.Abs(SpeedAt(0f) - 1f) < 1e-3f;
            Evaluate(5f, out Vector3 half, out _, out _, out _, out _);
            ok &= half.x > 3f && half.x < 4.5f; // the camera is ~36% along when the replay is halfway

            // Time of day crosses midnight the short way, weather changes at the key.
            Keys[0].day = 0.9f;
            Keys[1].day = 0.1f;
            Keys[1].weather = "Ash rain";
            ok &= DayAt(5f, out float midnight) && Mathf.Min(midnight, 1f - midnight) < 1e-3f && WeatherAt(0f) == "Ash rain";
            var weatherBack = new List<Key>();
            FromText(ToText(Keys, new Key[9]), weatherBack, new Key[9]);
            ok &= weatherBack[1].weather == "Ash rain" && weatherBack[0].weather == "" && Mathf.Abs(weatherBack[1].day - 0.1f) < 1e-5f;
            Keys[0].day = Keys[1].day = -1f;
            Keys[1].weather = "";
            ok &= !DayAt(5f, out _) && WeatherAt(5f) == null;

            // The whole path at twice the speed lasts half as long.
            Rate = 2f;
            ok &= Mathf.Abs(OutputTime(10f) - 8f) < 1e-3f && Mathf.Abs(SpeedAt(10f) - 0.5f) < 1e-3f;
            Rate = 1f;

            // Previous and next key wrap around at the ends.
            ok &= Neighbor(10f, 1) == 0f && Neighbor(0f, -1) == 10f && Neighbor(5f, 1) == 10f;

            // A pose relative to an anchor comes back as the same pose in the world.
            Quaternion turned = Quaternion.Euler(20f, 130f, -35f);
            Key rel = Relative(new Vector3(5f, 6f, 7f), turned, 0f, new Vector3(1f, 2f, 3f), 40f, -10f, 15f, 60f);
            Compose(new Vector3(5f, 6f, 7f), turned, rel, out Vector3 worldPos, out float wy, out float wp, out float wr);
            ok &= (worldPos - new Vector3(1f, 2f, 3f)).sqrMagnitude < 1e-3f
                && Quaternion.Angle(Quaternion.Euler(wp, wy, wr), Quaternion.Euler(-10f, 40f, 15f)) < 0.1f;

            // Undo and redo restore the state on each side of a change.
            Snapshot("test");
            Keys.RemoveAt(0);
            Step(_undo, _redo);
            ok &= Keys.Count == 2 && Keys[1].speed == 0.25f;
            Step(_redo, _undo);
            ok &= Keys.Count == 1 && Keys[0].t == 10f;
            Step(_undo, _redo);
            _undo.Clear();
            _redo.Clear();

            // Takes: a straight flight becomes its two ends, a corner is kept.
            var flight = new List<Key>();
            for (int i = 0; i <= 20; i++) flight.Add(new Key { t = i, pos = new Vector3(i, 0f, 0f), fov = 60f });
            ok &= Simplify(flight, TakeTolerance).Count == 2;
            for (int i = 10; i <= 20; i++) flight[i].pos = new Vector3(10f, 0f, i - 10f);
            List<Key> corner = Simplify(flight, TakeTolerance);
            ok &= corner.Count == 3 && corner[1].t == 10f;

            // The save format must give back exactly what it wrote.
            var marks = new Key[9];
            marks[4] = new Key { pos = new Vector3(1.5f, -2f, 3f), yaw = 10f, pitch = -5f, roll = 2f, fov = 50f };
            var back = new List<Key>();
            var backMarks = new Key[9];
            Keys[0].ease = Ease.Linear;
            Rate = 0.5f;
            Anchor = new PathAnchor { Target = new RecId(-5L, 7u), Turn = true, Name = "Bee hive" };
            LastView = new View { T = 12.5f, Pose = new Key { pos = new Vector3(4f, 5f, 6f), yaw = 90f, pitch = 3f, roll = 1f, fov = 40f }, Settings = "65 10 0.5 1 1 30 0 0.3 20" };
            string text = ToText(Keys, marks);
            Rate = 1f;
            Anchor = null;
            LastView = null;
            FromText(text, back, backMarks);
            ok &= Rate == 0.5f && Anchor != null && Anchor.Target.Equals(new RecId(-5L, 7u)) && Anchor.Turn && Anchor.Name == "Bee hive";
            ok &= LastView != null && LastView.T == 12.5f && LastView.Pose.pos == new Vector3(4f, 5f, 6f) && LastView.Pose.fov == 40f
                && LastView.Settings == "65 10 0.5 1 1 30 0 0.3 20";
            Rate = 1f;
            Anchor = null;
            LastView = null;
            ok &= back.Count == 2 && back[1].pos == Keys[1].pos && back[0].ease == Ease.Linear && back[1].t == 10f && back[1].speed == 0.25f
                && backMarks[4] != null && backMarks[4].pos == marks[4].pos && backMarks[4].fov == 50f && backMarks[0] == null;
            Keys.Clear();
            Keys.AddRange(saved);
            if (!ok) Plugin.Log.LogError("CameraPath self-check FAILED: spline or save format is wrong");
        }

        // World-space preview of the path, rebuilt when keys change. One line per segment, because a line
        // renderer's gradient holds only 8 colors. Green is the slowest camera, red the fastest.
        private static readonly List<LineRenderer> _lines = new List<LineRenderer>();
        private static Material _lineMaterial;
        private static GameObject _lineRoot;
        public static float FastestSpeed { get; private set; }

        private static LineRenderer NewLine(string name, float width)
        {
            var go = new GameObject(name) { layer = OverlayLayer };
            go.transform.SetParent(_lineRoot.transform, false);
            LineRenderer l = go.AddComponent<LineRenderer>();
            l.useWorldSpace = false; // the root rides on the path's anchor
            l.material = _lineMaterial;
            l.widthMultiplier = width;
            l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            l.receiveShadows = false;
            return l;
        }

        // Green, through yellow, to red.
        internal static Color SpeedColor(float fraction)
        {
            fraction = Mathf.Clamp01(fraction);
            return fraction < 0.5f ? Color.Lerp(Color.green, Color.yellow, fraction * 2f) : Color.Lerp(Color.yellow, Color.red, (fraction - 0.5f) * 2f);
        }

        private static void Rebuild()
        {
            if (!_lineMaterial) _lineMaterial = new Material(Shader.Find("Sprites/Default"));
            if (!_lineRoot) // first time, or the scene was reloaded
            {
                _lineRoot = new GameObject("ReplayModCameraPath");
                _lines.Clear();
                _gizmos.Clear();
            }
            RebuildGizmos();
            const int perSegment = 16;
            int segments = Mathf.Max(Keys.Count - 1, 0);
            var pts = new Vector3[segments][];
            var speeds = new float[segments][];
            FastestSpeed = 0f;
            for (int i = 0; i < segments; i++)
            {
                pts[i] = new Vector3[perSegment + 1];
                speeds[i] = new float[perSegment + 1];
                float prevOut = 0f;
                for (int j = 0; j <= perSegment; j++)
                {
                    float t = Mathf.Lerp(Keys[i].t, Keys[i + 1].t, j / (float)perSegment);
                    EvaluateLocal(t, out pts[i][j], out _, out _, out _, out _);
                    float output = OutputTime(t);
                    if (j > 0)
                    {
                        speeds[i][j] = (pts[i][j] - pts[i][j - 1]).magnitude / Mathf.Max(output - prevOut, 1e-4f);
                        FastestSpeed = Mathf.Max(FastestSpeed, speeds[i][j]);
                    }
                    prevOut = output;
                }
                speeds[i][0] = speeds[i][1];
            }
            while (_lines.Count < segments) _lines.Add(NewLine("ReplayModPathSegment", 0.06f));
            float scale = Mathf.Max(FastestSpeed, 5f); // a slow path stays green instead of showing tiny differences
            for (int i = 0; i < _lines.Count; i++)
            {
                if (i >= segments)
                {
                    _lines[i].positionCount = 0;
                    continue;
                }
                _lines[i].positionCount = perSegment + 1;
                _lines[i].SetPositions(pts[i]);
                var colors = new GradientColorKey[8];
                for (int c = 0; c < colors.Length; c++)
                {
                    float at = c / (float)(colors.Length - 1);
                    colors[c] = new GradientColorKey(SpeedColor(speeds[i][Mathf.RoundToInt(at * perSegment)] / scale), at);
                }
                var gradient = new Gradient();
                gradient.SetKeys(colors, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
                _lines[i].colorGradient = gradient;
            }
        }

        // A hand-flown take: pose samples become as few keys as can reproduce them within tolerance
        // (Ramer-Douglas-Peucker over position, angles and FOV). Replaces the keys it covers.
        public const float TakeTolerance = 0.15f; // meters; one degree counts as 5 cm

        public static int AddTake(List<Key> samples)
        {
            if (samples.Count < 2) return 0;
            float t0 = samples[0].t, t1 = samples[samples.Count - 1].t;
            List<Key> kept = Simplify(samples, TakeTolerance);
            Snapshot("record take");
            Keys.RemoveAll(k => k.t >= t0 && k.t <= t1);
            Keys.AddRange(kept);
            Save();
            return kept.Count;
        }

        internal static List<Key> Simplify(List<Key> samples, float tolerance)
        {
            // Unwrap the angles so 359 to 1 degrees counts as a small step.
            for (int i = 1; i < samples.Count; i++)
            {
                samples[i].yaw = samples[i - 1].yaw + Mathf.DeltaAngle(samples[i - 1].yaw, samples[i].yaw);
                samples[i].pitch = samples[i - 1].pitch + Mathf.DeltaAngle(samples[i - 1].pitch, samples[i].pitch);
                samples[i].roll = samples[i - 1].roll + Mathf.DeltaAngle(samples[i - 1].roll, samples[i].roll);
            }
            var keep = new bool[samples.Count];
            keep[0] = keep[samples.Count - 1] = true;
            var ranges = new Stack<(int a, int b)>();
            ranges.Push((0, samples.Count - 1));
            while (ranges.Count > 0)
            {
                (int a, int b) = ranges.Pop();
                int worst = -1;
                float worstError = tolerance;
                for (int i = a + 1; i < b; i++)
                {
                    float u = Mathf.InverseLerp(samples[a].t, samples[b].t, samples[i].t);
                    Key s = samples[i], ka = samples[a], kb = samples[b];
                    float error = (s.pos - Vector3.Lerp(ka.pos, kb.pos, u)).magnitude
                        + 0.05f * (Mathf.Abs(s.yaw - Mathf.Lerp(ka.yaw, kb.yaw, u)) + Mathf.Abs(s.pitch - Mathf.Lerp(ka.pitch, kb.pitch, u))
                        + Mathf.Abs(s.roll - Mathf.Lerp(ka.roll, kb.roll, u)) + Mathf.Abs(s.fov - Mathf.Lerp(ka.fov, kb.fov, u)));
                    if (error > worstError)
                    {
                        worstError = error;
                        worst = i;
                    }
                }
                if (worst < 0) continue;
                keep[worst] = true;
                ranges.Push((a, worst));
                ranges.Push((worst, b));
            }
            var kept = new List<Key>();
            for (int i = 0; i < samples.Count; i++)
            {
                if (keep[i]) kept.Add(samples[i]);
            }
            return kept;
        }

        // A small wireframe camera per key: a frustum pointing where the key looks, with a notch on its
        // top edge so roll is visible. One continuous polyline: notch, top, sides, then the four edges to the lens.
        private static readonly List<LineRenderer> _gizmos = new List<LineRenderer>();

        private static void RebuildGizmos()
        {
            while (_gizmos.Count < Keys.Count)
            {
                LineRenderer g = NewLine("ReplayModCameraKey", 0.025f);
                g.startColor = g.endColor = Color.white;
                _gizmos.Add(g);
            }
            for (int i = 0; i < _gizmos.Count; i++)
            {
                LineRenderer g = _gizmos[i];
                if (i >= Keys.Count)
                {
                    g.positionCount = 0;
                    continue;
                }
                Key k = Keys[i];
                Quaternion r = Quaternion.Euler(k.pitch, k.yaw, 0f) * Quaternion.Euler(0f, 0f, k.roll);
                const float depth = 0.6f;
                float h = depth * Mathf.Tan(k.fov * 0.5f * Mathf.Deg2Rad), w = h * 16f / 9f;
                Vector3 C(float x, float y) => k.pos + r * new Vector3(x, y, depth);
                Vector3 tl = C(-w, h), tr = C(w, h), br = C(w, -h), bl = C(-w, -h), notch = C(0f, h * 1.5f);
                var pts = new[] { tr, notch, tl, tr, br, bl, tl, k.pos, tr, br, k.pos, bl };
                g.positionCount = pts.Length;
                g.SetPositions(pts);
            }
        }

        // Called every frame in a replay: path and keys are only drawn while editing, never in the shot.
        public static void UpdateVisuals()
        {
            if (!_lineRoot && Keys.Count >= 1) Rebuild(); // first frame, or the scene was reloaded
            bool on = Show && !Follow && !Exporter.HideUi;
            foreach (LineRenderer l in _lines) if (l) l.enabled = on;
            foreach (LineRenderer g in _gizmos) if (g) g.enabled = on;
        }

        // The lines are drawn by a second camera after the main one, whose post-processing (depth of
        // field, bloom) would otherwise blur them. It starts from an empty depth buffer, so they show
        // through terrain, which also helps finding keys. Unused layer: the main camera must skip them.
        private static Camera _overlay;
        private static int _overlayLayer = -1;

        private static int OverlayLayer
        {
            get
            {
                if (_overlayLayer >= 0) return _overlayLayer;
                _overlayLayer = 31;
                for (int i = 31; i >= 8; i--)
                {
                    if (LayerMask.LayerToName(i).Length > 0) continue;
                    _overlayLayer = i;
                    break;
                }
                return _overlayLayer;
            }
        }

        // After the camera has moved this frame.
        public static void SyncOverlay(Camera main)
        {
            main.cullingMask &= ~(1 << OverlayLayer);
            if (_lineRoot)
            {
                AnchorFrame(ReplaySession.T, true, out Vector3 origin, out Quaternion frame);
                _lineRoot.transform.SetPositionAndRotation(origin, frame);
            }
            bool on = _lineRoot && Show && !Follow && !Exporter.HideUi;
            if (!_overlay)
            {
                if (!on) return;
                var go = new GameObject("ReplayModOverlayCamera");
                go.transform.SetParent(main.transform, false);
                _overlay = go.AddComponent<Camera>();
                _overlay.clearFlags = CameraClearFlags.Depth;
                _overlay.cullingMask = 1 << OverlayLayer;
                _overlay.renderingPath = RenderingPath.Forward;
                _overlay.useOcclusionCulling = false;
                _overlay.allowHDR = false;
                _overlay.allowMSAA = false;
            }
            _overlay.enabled = on;
            // Valheim's render scale gives the main camera its own texture, upscaled later: draw into that too.
            _overlay.targetTexture = main.targetTexture;
            _overlay.rect = main.rect;
            _overlay.depth = main.depth + 0.5f;
            _overlay.fieldOfView = main.fieldOfView;
            _overlay.nearClipPlane = main.nearClipPlane;
            _overlay.farClipPlane = main.farClipPlane;
        }

        // Key numbers next to each gizmo, in viewport space (Valheim renders below screen resolution).
        private static readonly List<Rect> _placedLabels = new List<Rect>();

        public static void DrawLabels(Camera cam)
        {
            if (!Show || Follow || !cam || Exporter.HideUi) return;
            _placedLabels.Clear();
            for (int i = 0; i < Keys.Count; i++)
            {
                Vector3 vp = cam.WorldToViewportPoint(_lineRoot ? _lineRoot.transform.TransformPoint(Keys[i].pos) : Keys[i].pos);
                if (vp.z <= 0f) continue;
                var at = new Vector2(vp.x * Screen.width, (1f - vp.y) * Screen.height);
                string label = (i + 1).ToString();
                Vector2 size = UiStyle.Big.CalcSize(new GUIContent(label));
                var back = new Rect(at.x + 8f, at.y - size.y - 4f, size.x + 8f, size.y);
                while (_placedLabels.Exists(r => r.Overlaps(back))) back.y -= size.y + 2f;
                _placedLabels.Add(back);
                UiStyle.Fill(back, UiStyle.Panel);
                GUI.Label(new Rect(back.x + 4f, back.y, size.x, size.y), label, UiStyle.Big);
            }
        }
    }
}
