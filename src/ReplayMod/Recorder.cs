using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace ReplayMod
{
    // Records what this client has loaded: every ZNetScene instance whose ZDO changed (DataRevision),
    // every routed RPC that reaches us, the game camera, the environment, and a high-rate lane for
    // nearby characters and everything else that moves. Works the same as host, client or single player because it reads ZDOMan,
    // not the network.
    internal static class Recorder
    {
        private class Tracked
        {
            public uint Rev;
            public bool Dynamic;
            public Vector2s Zone;
        }

        public static bool Active { get; private set; }
        // Replay buffer: recording quietly into rolling files, kept only if the save key is pressed.
        public static bool Buffering { get; private set; }
        private static string _previousBuffer;
        private static float _previousLength;
        private static int _bufferFile;
        private static bool _thumbDue;

        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> Instances =
            AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");

        private static ReplayWriter _w;
        private static float _t0, _nextTick, _nextKey, _nextEnv, _nextStats;
        private static readonly Dictionary<ZDOID, Tracked> _tracked = new Dictionary<ZDOID, Tracked>();
        private static readonly Dictionary<ZDOID, bool> _dynamic = new Dictionary<ZDOID, bool>();
        private static readonly HashSet<ZDOID> _seen = new HashSet<ZDOID>();
        private static readonly List<ZDOID> _gone = new List<ZDOID>();
        private static readonly Dictionary<int, int> _rpcCounts = new Dictionary<int, int>();
        private static readonly ZPackage _pkg = new ZPackage();
        private static readonly HashSet<ZDOID> _bossesSeen = new HashSet<ZDOID>();
        private static float _maxStamina, _maxEitr;
        private static float _sampleMs, _sampleMsMax;
        private static int _samples;

        private static float Now => Time.time - _t0;
        private static readonly System.Reflection.MethodInfo SetCrouch = AccessTools.Method(typeof(Player), "SetCrouch");

        // Timeline markers (deaths, kills, bosses, teleports, skill-ups). Chat is left out on purpose.
        public static void Mark(MarkerKind kind, Vector3 pos, string text)
        {
            if (Active) _w.Marker(Now, kind, pos, text);
        }

        public static void Toggle()
        {
            if (Active && !Buffering) Stop();
            else
            {
                // A manual recording takes over; the buffer starts again after it.
                if (Buffering) Stop();
                Start();
            }
            // Ctrl is also the crouch key, and it toggled crouch just before the record key arrived.
            if (Player.m_localPlayer) SetCrouch.Invoke(Player.m_localPlayer, new object[] { false });
        }

        public static void Start() => Begin(Path.Combine(Plugin.ReplayDir, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Sanitize(ZNet.World?.m_name ?? "")}.replay"), false);

        public static void StartBuffer()
        {
            _previousBuffer = null;
            _previousLength = 0f;
            Begin(NextBufferFile(), true);
        }

        private static string NextBufferFile()
        {
            Directory.CreateDirectory(Plugin.BufferDir);
            string path;
            do path = Path.Combine(Plugin.BufferDir, $"buffer{++_bufferFile}.replay");
            while (File.Exists(path));
            return path;
        }

        private static void DeleteBufferFile(string path)
        {
            try { if (path != null) File.Delete(path); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Plugin.Log.LogWarning($"couldn't delete {path}: {e.Message}"); }
        }

        // The file only ever holds as long as the buffer asks for, so the last two always cover it.
        private static void RotateBuffer()
        {
            string finished = _w.Path;
            float length = Now;
            CloseWriter();
            Active = false;
            DeleteBufferFile(_previousBuffer);
            _previousBuffer = finished;
            _previousLength = length;
            Begin(NextBufferFile(), true);
        }

        // Replay buffer: cuts the last minutes out of the buffer files on a worker thread, and goes on buffering.
        public static void SaveBuffer()
        {
            Player p = Player.m_localPlayer;
            if (!Buffering)
            {
                p?.Message(MessageHud.MessageType.TopLeft, Active ? "Replay Mod: already recording" : $"Replay Mod: Replay Buffer is off ({Plugin.KeyText(Plugin.ReplayBufferToggleKey)} turns it on)");
                return;
            }
            string current = _w.Path;
            float length = Now;
            string previous = _previousBuffer;
            float total = _previousLength + length;
            string world = Sanitize(ZNet.World?.m_name ?? "");
            CloseWriter();
            Active = Buffering = false;
            _previousBuffer = null;
            string[] sources = previous != null ? new[] { previous, current } : new[] { current };
            string dest = Path.Combine(Plugin.ReplayDir, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{world}_buffer.replay");
            Clipper.Start(sources, Mathf.Max(0f, total - Plugin.ReplayBufferMinutes.Value * 60f), float.MaxValue, dest, null, true);
            p?.Message(MessageHud.MessageType.TopLeft, "Replay Mod: saving the Replay Buffer");
            StartBuffer();
        }

        private static void CloseWriter()
        {
            try { _w.Dispose(); }
            catch (Exception e) { Plugin.Log.LogError($"recording close failed: {e}"); }
            _w = null;
            _tracked.Clear();
        }

        private static void Begin(string path, bool buffer)
        {
            Player p = Player.m_localPlayer;
            if (Active || p == null || ZNet.World == null) return;
            World world = ZNet.World;
            var header = new ReplayHeader
            {
                GameVersion = global::Version.GetVersionString(),
                WorldName = world.m_name,
                SeedName = world.m_seedName,
                Seed = world.m_seed,
                WorldGenVersion = world.m_worldGenVersion,
                PlayerName = p.GetPlayerName(),
                StartPos = p.transform.position,
                Date = DateTime.Now.ToString("u"),
            };
            ZDO pz = p.GetComponent<ZNetView>()?.GetZDO();
            if (pz != null)
            {
                header.PlayerUser = pz.m_uid.UserID;
                header.PlayerId = pz.m_uid.ID;
            }
            try
            {
                _w = new ReplayWriter(path, header);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"can't start recording: {e.Message}");
                return;
            }
            _tracked.Clear();
            _movers.Clear();
            _hotbarKey = _hudKey = null;
            _selfId = ZDOID.None;
            _center = p.transform.position;
            _dynamic.Clear();
            _bossesSeen.Clear();
            _maxStamina = _maxEitr = -1f;
            _rpcCounts.Clear();
            _t0 = Time.time;
            _nextTick = _nextKey = _nextEnv = 0f;
            _nextStats = 10f;
            _sampleMs = _sampleMsMax = 0f;
            _samples = 0;
            _boneSource = null;
            _partsRoot = null;
            _nextPose = 0f;
            Active = true;
            Buffering = buffer;
            RecordMap();
            if (buffer) return;
            _thumbDue = true;
            Plugin.Log.LogInfo($"recording to {path}");
            p.Message(MessageHud.MessageType.TopLeft, "Replay Mod: recording");
        }

        public static void Stop()
        {
            if (!Active) return;
            Active = false;
            float duration = Now;
            if (Buffering)
            {
                Buffering = false;
                string current = _w.Path;
                CloseWriter();
                DeleteBufferFile(current);
                DeleteBufferFile(_previousBuffer);
                _previousBuffer = null;
                return;
            }
            try
            {
                _w.Dispose();
                long size = new FileInfo(_w.Path).Length;
                string report = Report(duration, size);
                File.WriteAllText(Path.ChangeExtension(_w.Path, ".json"), report);
                Plugin.Log.LogInfo($"recording stopped: {_w.Path}\n{report}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"recording close failed: {e}");
            }
            _w = null;
            _tracked.Clear();
            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, "Replay Mod: recording saved");
        }

        public static void Tick()
        {
            if (!Active) return;
            if (ZNetScene.instance == null || ZDOMan.instance == null)
            {
                Stop(); // logged out
                return;
            }
            // Dead and waiting to respawn, the world goes on (the body is removed, a ragdoll falls), so sampling
            // goes on around where the player was. Before, the body stood in the replay until the respawn.
            Player me = Player.m_localPlayer;
            if (me) _center = me.transform.position;
            float t = Now;
            if (Buffering && me && t >= Plugin.ReplayBufferMinutes.Value * 60f) // a new file needs a live player
            {
                RotateBuffer();
                t = Now;
            }
            if (_thumbDue && t >= 1f)
            {
                _thumbDue = false;
                Plugin.Instance.StartCoroutine(Exporter.Thumbnail(_w.Path));
            }
            if (t >= _nextTick)
            {
                _nextTick += 1f / Mathf.Max(1f, Plugin.SampleRate.Value);
                if (_nextTick < t) _nextTick = t + 1f / Mathf.Max(1f, Plugin.SampleRate.Value);
                bool key = t >= _nextKey;
                if (key) _nextKey = t + Plugin.KeyframeInterval.Value;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Sample(t, key);
                if (me)
                {
                    if (me.GetMaxStamina() != _maxStamina || me.GetMaxEitr() != _maxEitr)
                    {
                        _maxStamina = me.GetMaxStamina();
                        _maxEitr = me.GetMaxEitr();
                        _w.Vitals(t, _maxStamina, _maxEitr);
                    }
                    RecordHotbar(t, me);
                    RecordHud(t, me);
                    RecordSelf(t, me);
                }
                float ms = (float)sw.Elapsed.TotalMilliseconds;
                _sampleMs += ms;
                _sampleMsMax = Mathf.Max(_sampleMsMax, ms);
                _samples++;
                Camera cam = GameCamera.instance ? GameCamera.instance.GetComponent<Camera>() : null;
                // At t = 0 the replay buffer has just started with the character's spawn, and the camera hasn't been placed.
                if (cam && t > 0f) _w.Cam(t, cam.transform.position, cam.transform.rotation, cam.fieldOfView);
            }
            if (t >= _nextEnv && EnvMan.instance)
            {
                _nextEnv = t + 1f;
                _w.Env(t, EnvMan.instance.GetDayFraction(), EnvMan.instance.GetCurrentEnvironment()?.m_name);
                Vector3 wind = EnvMan.instance.GetWindDir();
                _w.Wind(t, Mathf.Atan2(wind.x, wind.z) * Mathf.Rad2Deg, EnvMan.instance.GetWindIntensity());
                _w.Water(t, ZNet.instance.GetWrappedDayTimeSeconds());
            }
            if (t >= _nextStats)
            {
                _nextStats = t + 10f;
                var s = _w.Stats;
                Plugin.Log.LogInfo($"rec {t:0}s: {_tracked.Count} tracked, raw {s.Total / 1048576f:0.00} MB, file {_w.CompressedBytes / 1048576f:0.00} MB, sample {_sampleMs / Mathf.Max(1, _samples):0.00} ms avg");
            }
        }

        private static void Sample(float t, bool keyframe)
        {
            Vector3 center = _center;
            Vector2s pz = ZoneSystem.GetZone(center);
            int ring = Plugin.StaticZoneRing.Value;
            float radius = Plugin.DynamicRadius.Value;
            float r2 = radius > 0f ? radius * radius : float.MaxValue;
            float leave2 = radius > 0f ? (radius + 10f) * (radius + 10f) : float.MaxValue; // no flapping at the edge
            _seen.Clear();
            foreach (var kv in Instances(ZNetScene.instance))
            {
                ZDO z = kv.Key;
                ZNetView nv = kv.Value;
                if (z == null || nv == null) continue;
                ZDOID id = z.m_uid;
                bool dyn = IsDynamic(id, z, nv);
                Vector3 pos = z.GetPosition();
                Vector2s zone = ZoneSystem.GetZone(pos);
                // Moving objects already tracked stay until 10 m past the radius: dropping and re-adding one
                // at the edge rebuilt it in playback each time (corpses fell again, creatures popped).
                if (dyn ? (pos - center).sqrMagnitude > (_tracked.ContainsKey(id) ? leave2 : r2) : !InRing(zone, pz, ring)) continue;
                _seen.Add(id);
                if (!_tracked.TryGetValue(id, out Tracked tr))
                {
                    _tracked[id] = tr = new Tracked { Rev = z.DataRevision, Dynamic = dyn, Zone = zone };
                    Character boss = dyn ? nv.GetComponent<Character>() : null;
                    if (boss && boss.IsBoss() && _bossesSeen.Add(id)) Mark(MarkerKind.Boss, pos, $"Boss: {Localization.instance.Localize(boss.m_name)}");
                    WriteZdo(t, z, dyn ? ZdoFlags.Dynamic : ZdoFlags.None);
                    if (dyn && nv.GetComponent<ZSyncTransform>() && !nv.GetComponent<Character>() && !nv.GetComponent<Projectile>())
                    {
                        _movers[id] = new Mover { View = nv, Body = nv.GetComponent<Rigidbody>(), Time = -1f };
                    }
                }
                else if (tr.Rev != z.DataRevision || (dyn && keyframe))
                {
                    bool changed = tr.Rev != z.DataRevision;
                    tr.Rev = z.DataRevision;
                    tr.Zone = zone;
                    WriteZdo(t, z, dyn ? (changed ? ZdoFlags.Dynamic : ZdoFlags.Dynamic | ZdoFlags.Keyframe) : ZdoFlags.None);
                }
            }
            _gone.Clear();
            foreach (var kv in _tracked)
            {
                if (!_seen.Contains(kv.Key)) _gone.Add(kv.Key);
            }
            foreach (ZDOID id in _gone)
            {
                Tracked tr = _tracked[id];
                _tracked.Remove(id);
                _movers.Remove(id);
                // Moving things that leave the radius vanish in playback too. Scenery that drops out
                // of the zone ring stays as it was; only scenery destroyed inside the ring is removed.
                if (tr.Dynamic || InRing(tr.Zone, pz, ring)) _w.Remove(t, id);
            }
        }

        private static Vector3 _center;
        private static ZDOID _selfId;

        private static void RecordSelf(float t, Player p)
        {
            ZDO z = p.GetComponent<ZNetView>()?.GetZDO();
            if (z == null || z.m_uid == _selfId) return;
            _selfId = z.m_uid;
            _w.Self(t, _selfId);
        }

        // Food and status effects are kept on the owner only, like the inventory.
        private static string _hudKey;
        private static readonly List<StatusEffect> _seList = new List<StatusEffect>();
        private static readonly List<FoodSlot> _foodSlots = new List<FoodSlot>();
        private static readonly List<EffectSlot> _effectSlots = new List<EffectSlot>();
        private static readonly StringBuilder _hudText = new StringBuilder();

        private static void RecordHud(float t, Player p)
        {
            _seList.Clear();
            p.GetSEMan().GetHUDStatusEffects(_seList);
            _foodSlots.Clear();
            _effectSlots.Clear();
            _hudText.Clear();
            foreach (Player.Food f in p.GetFoods())
            {
                _foodSlots.Add(new FoodSlot { Prefab = f.m_name, Time = f.m_time });
                // What the HUD shows: the food and its whole seconds left.
                _hudText.Append(f.m_name).Append(Mathf.FloorToInt(f.m_time / Game.m_foodRate)).Append(';');
            }
            foreach (StatusEffect se in _seList)
            {
                string text = se.GetIconText() ?? "";
                _effectSlots.Add(new EffectSlot { Hash = se.NameHash(), Text = text });
                _hudText.Append(se.NameHash()).Append(text).Append(';');
            }
            string key = _hudText.ToString();
            if (key == _hudKey) return;
            _hudKey = key;
            _w.Hud(t, _foodSlots, _effectSlots);
        }

        private static readonly AccessTools.FieldRef<Minimap, System.Collections.BitArray> Explored = AccessTools.FieldRefAccess<Minimap, System.Collections.BitArray>("m_explored");
        private static readonly AccessTools.FieldRef<Minimap, System.Collections.BitArray> ExploredOthers = AccessTools.FieldRefAccess<Minimap, System.Collections.BitArray>("m_exploredOthers");

        private static void RecordMap()
        {
            Minimap map = Minimap.instance;
            if (!map || Explored(map) == null) return;
            byte[] Pack(System.Collections.BitArray bits)
            {
                var b = new byte[(bits.Length + 7) / 8];
                bits.CopyTo(b, 0);
                return b;
            }
            _w.Map(0f, Pack(Explored(map)), Pack(ExploredOthers(map)));
        }

        // A player's inventory isn't in the ZDO, so the hotbar is recorded on its own, whenever it changes.
        private static string _hotbarKey;
        private static readonly List<ItemDrop.ItemData> _bound = new List<ItemDrop.ItemData>();
        private static readonly List<HotbarSlot> _slots = new List<HotbarSlot>();
        private static readonly StringBuilder _hotbarText = new StringBuilder();

        private static void RecordHotbar(float t, Player p)
        {
            p.GetInventory().GetBoundItems(_bound);
            _slots.Clear();
            _hotbarText.Clear();
            foreach (ItemDrop.ItemData item in _bound)
            {
                if (!item.m_dropPrefab) continue;
                var s = new HotbarSlot
                {
                    Slot = item.m_gridPos.x, Prefab = item.m_dropPrefab.name, Stack = item.m_stack, Quality = item.m_quality,
                    Variant = item.m_variant, Durability = item.m_durability, Equipped = item.m_equipped,
                };
                _slots.Add(s);
                // Durability in whole percent: wear from every hit would otherwise write a record each sample.
                _hotbarText.Append(s.Slot).Append(s.Prefab).Append(',').Append(s.Stack).Append(',').Append(s.Quality).Append(',').Append(s.Variant)
                    .Append(',').Append(Mathf.RoundToInt(item.GetDurabilityPercentage() * 100f)).Append(s.Equipped ? '+' : '-');
            }
            string key = _hotbarText.ToString();
            if (key == _hotbarKey) return;
            _hotbarKey = key;
            _w.Hotbar(t, _slots);
        }

        private static bool InRing(Vector2s a, Vector2s b, int ring) => Math.Abs(a.x - b.x) <= ring && Math.Abs(a.y - b.y) <= ring;

        // Dynamic = lives in time. Non-persistent ZDOs (sound and visual effects, projectiles, ragdolls,
        // players) must too: as scenery they were shown from t=0 and every hit sound fired at load.
        private static bool IsDynamic(ZDOID id, ZDO z, ZNetView nv)
        {
            if (!_dynamic.TryGetValue(id, out bool dyn))
            {
                // Corpses and tombstones are persistent but appear at a death, so they aren't scenery.
                dyn = !z.Persistent || nv.GetComponent<ZSyncTransform>() || nv.GetComponent<ZSyncAnimation>() || nv.GetComponent<Character>()
                    || nv.GetComponent<Ragdoll>() || nv.GetComponent<TombStone>();
                _dynamic[id] = dyn;
            }
            return dyn;
        }

        private static void WriteZdo(float t, ZDO z, ZdoFlags flags)
        {
            _pkg.Clear();
            z.Serialize(_pkg);
            _w.Zdo(t, z.m_uid, flags, z.GetPosition(), _pkg.GetArray());
        }

        public static void FixedTick()
        {
            if (!Active || !Plugin.RecordHiFi.Value || Player.m_localPlayer == null) return;
            float t = Now;
            Player me = Player.m_localPlayer;
            Vector3 center = me.transform.position;
            float r2 = Plugin.HiFiRadius.Value * Plugin.HiFiRadius.Value;
            SampleMovers(t, true);
            foreach (Character c in Character.GetAllCharacters())
            {
                if (!c || (c.transform.position - center).sqrMagnitude > r2) continue;
                if (c == me && _seatedOn != null) continue; // written with the vehicle
                ZDO z = c.GetComponent<ZNetView>()?.GetZDO();
                if (z == null || !_tracked.ContainsKey(z.m_uid)) continue;
                _w.HiFi(t, z.m_uid, c.transform.position, c.transform.rotation);
            }
        }

        // High-rate lane for moving objects that aren't characters, whose ZDO records come only 20 times a
        // second. A body this machine simulates is exact at each physics step. Anything else moves once a
        // frame (a remote owner's object follows ZSyncTransform), so it's read as it was drawn.
        private class Mover
        {
            public ZNetView View;
            public Rigidbody Body;
            public Vector3 Pos;
            public Quaternion Rot;
            public float Time;
        }

        private const float MoverMinStep = 1f / 60f;
        private static readonly Dictionary<ZDOID, Mover> _movers = new Dictionary<ZDOID, Mover>();
        private static ZNetView _seatedOn;

        private static void SampleMovers(float t, bool physicsStep)
        {
            Player me = Player.m_localPlayer;
            if (me == null) return;
            // The game moves a seated player onto the seat in the physics step, so what it reads then is
            // already behind the vehicle. The seat's place on the vehicle, written with it, can't drift.
            Transform seat = me.IsAttached() ? AttachPoint(me) : null;
            ZNetView seatView = seat ? seat.GetComponentInParent<ZNetView>() : null;
            ZDO meZdo = me.GetComponent<ZNetView>()?.GetZDO();
            _seatedOn = null;
            foreach (var kv in _movers)
            {
                Mover m = kv.Value;
                if (!m.View) continue;
                if (m.View == seatView) _seatedOn = seatView;
                ZDO z = m.View.GetZDO();
                if (z == null || physicsStep != (m.Body && z.IsOwner()) || t - m.Time < MoverMinStep) continue;
                Transform tr = m.View.transform;
                Vector3 pos = physicsStep ? m.Body.position : tr.position;
                Quaternion rot = physicsStep ? m.Body.rotation : tr.rotation;
                if (m.Time >= 0f && (pos - m.Pos).sqrMagnitude < 1e-4f && Quaternion.Angle(rot, m.Rot) < 0.1f) continue;
                m.Pos = pos;
                m.Rot = rot;
                m.Time = t;
                _w.HiFi(t, kv.Key, pos, rot);
                if (m.View == seatView && meZdo != null && _tracked.ContainsKey(meZdo.m_uid))
                {
                    _w.HiFi(t, meZdo.m_uid, pos + rot * tr.InverseTransformPoint(seat.position), rot * (Quaternion.Inverse(tr.rotation) * seat.rotation));
                }
            }
        }

        // Exact pose lane: the recording player's final bone pose, read just before the game camera
        // renders (after the animator, IK and any mod that writes bones in LateUpdate, e.g. Eldenheim).
        // Other players only ever see the vanilla animator over the network; this keeps what you saw.
        private static Transform[] _bones;
        private static SkinnedMeshRenderer _boneSource;
        private static float _nextPose;
        private static int _poseFrame = -1;

        internal static void OnPreCull(Camera cam)
        {
            if (!Active || Time.frameCount == _poseFrame) return;
            if (!GameCamera.instance || cam != GameCamera.instance.GetComponent<Camera>()) return;
            _poseFrame = Time.frameCount;
            float t = Now;
            if (Plugin.RecordHiFi.Value) SampleMovers(t, false);
            if (!Plugin.RecordPose.Value || t < _nextPose) return;
            _nextPose = t + 1f / Mathf.Max(1f, Plugin.PoseRate.Value);
            Player p = Player.m_localPlayer;
            VisEquipment vis = p ? p.GetComponent<VisEquipment>() : null;
            SkinnedMeshRenderer body = vis ? vis.m_bodyModel : null;
            ZDO z = p ? p.GetComponent<ZNetView>()?.GetZDO() : null;
            if (body == null || z == null) return;
            if (body != _boneSource)
            {
                _boneSource = body;
                _bones = body.bones;
                var names = new string[_bones.Length];
                for (int i = 0; i < _bones.Length; i++) names[i] = _bones[i] ? _bones[i].name : "";
                _w.PoseBones(t, z.m_uid, names);
            }
            _w.Pose(t, z.m_uid, _bones);
            RecordParts(t, p);
        }

        // Vehicle parts lane: the local transforms of the vehicle you're seated in (gear legs, doors,
        // rudders). Mods often move these only on the driver's machine, so other players (and
        // the ZDO) never get them. Only changed parts are written.
        private static readonly AccessTools.FieldRef<Player, Transform> AttachPoint = AccessTools.FieldRefAccess<Player, Transform>("m_attachPoint");
        private static ZNetView _partsRoot;
        private static Transform[] _parts;
        private static Vector3[] _partPos;
        private static Quaternion[] _partRot;
        private static float[] _partT;
        private static readonly List<int> _changed = new List<int>();

        private static void RecordParts(float t, Player p)
        {
            Transform ap = p.IsAttached() ? AttachPoint(p) : null;
            ZNetView nv = ap ? ap.GetComponentInParent<ZNetView>() : null;
            ZDO z = nv ? nv.GetZDO() : null;
            if (z == null)
            {
                _partsRoot = null;
                return;
            }
            float step = 1f / Mathf.Max(1f, Plugin.PoseRate.Value);
            if (nv != _partsRoot)
            {
                _partsRoot = nv;
                _parts = nv.GetComponentsInChildren<Transform>(true);
                var paths = new string[_parts.Length];
                for (int i = 0; i < _parts.Length; i++) paths[i] = RelativePath(_parts[i], nv.transform);
                _w.PartsHeader(t, z.m_uid, paths);
                Ship ship = nv.GetComponent<Ship>();
                for (int i = 0; ship && i < _parts.Length; i++)
                {
                    if (SailPart(ship, _parts[i])) _parts[i] = null;
                }
                _partPos = new Vector3[_parts.Length];
                _partRot = new Quaternion[_parts.Length];
                _partT = new float[_parts.Length];
                for (int i = 0; i < _partT.Length; i++) _partT[i] = -1f;
            }
            // A part that starts moving after standing still first gets its resting value one step
            // earlier, or playback would interpolate across the whole still period.
            _changed.Clear();
            for (int i = 1; i < _parts.Length; i++) // 0 is the root, which ZSyncTransform already moves
            {
                Transform tr = _parts[i];
                if (!tr) continue;
                bool moved = _partT[i] < 0f || (tr.localPosition - _partPos[i]).sqrMagnitude > 1e-6f || Quaternion.Angle(tr.localRotation, _partRot[i]) > 0.05f;
                if (moved) _changed.Add(i);
            }
            if (_changed.Count == 0) return;
            var hold = _changed.FindAll(i => _partT[i] >= 0f && t - _partT[i] > 1.5f * step);
            if (hold.Count > 0) _w.Parts(t - step, z.m_uid, hold, _partPos, _partRot);
            foreach (int i in _changed)
            {
                _partPos[i] = _parts[i].localPosition;
                _partRot[i] = _parts[i].localRotation;
                _partT[i] = t;
            }
            _w.Parts(t, z.m_uid, _changed, _partPos, _partRot);
        }

        // A ship works out its sail from the recorded speed, and the sail's cloth simulation moves these
        // every frame. Writing recorded values over that made the sail flicker. The mast itself is fine.
        internal static bool SailPart(Ship ship, Transform tr)
        {
            if (tr == ship.m_sailBottomTransform) return true;
            return ship.m_mastObject && tr != ship.m_mastObject.transform && tr.IsChildOf(ship.m_mastObject.transform);
        }

        private static string RelativePath(Transform tr, Transform root)
        {
            if (tr == root) return "";
            string path = tr.name;
            for (Transform p = tr.parent; p != null && p != root; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        // Prefix on the single place every routed RPC lands on this machine.
        internal static void OnRoutedRpc(ZRoutedRpc.RoutedRPCData data)
        {
            if (!Active || data == null) return;
            if (!data.m_targetZDO.IsNone() && !_tracked.ContainsKey(data.m_targetZDO)) return;
            var pkg = new ZPackage();
            data.Serialize(pkg);
            _w.Rpc(Now, pkg.GetArray());
            _rpcCounts.TryGetValue(data.m_methodHash, out int n);
            _rpcCounts[data.m_methodHash] = n + 1;
        }

        public static void OnGUI()
        {
            if (!Active || Buffering || Exporter.HideUi) return;
            float t = Now;
            GUI.color = new Color(1f, 0.25f, 0.25f);
            GUI.Label(new Rect(12, 8, 400, 24), $"● REC {(int)t / 60:00}:{(int)t % 60:00}   {_w.CompressedBytes / 1048576f:0.0} MB   {_tracked.Count} objects");
            GUI.color = Color.white;
        }

        private static string Report(float duration, long fileBytes)
        {
            var s = _w.Stats;
            float min = Mathf.Max(duration / 60f, 1e-3f);
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"duration_s\": {duration:0.0},");
            sb.AppendLine($"  \"file_mb\": {fileBytes / 1048576f:0.000},");
            sb.AppendLine($"  \"file_mb_per_min\": {fileBytes / 1048576f / min:0.000},");
            sb.AppendLine($"  \"raw_mb\": {s.Total / 1048576f:0.000},");
            sb.AppendLine($"  \"raw_mb_static\": {s.StaticBytes / 1048576f:0.000}, \"static_records\": {s.StaticRecs},");
            sb.AppendLine($"  \"raw_mb_dynamic\": {s.DynamicBytes / 1048576f:0.000}, \"dynamic_records\": {s.DynamicRecs},");
            sb.AppendLine($"  \"raw_mb_keyframes\": {s.KeyframeBytes / 1048576f:0.000}, \"keyframe_records\": {s.KeyframeRecs},");
            sb.AppendLine($"  \"raw_mb_rpc\": {s.RpcBytes / 1048576f:0.000}, \"rpc_records\": {s.RpcRecs},");
            sb.AppendLine($"  \"raw_mb_hifi\": {s.HiFiBytes / 1048576f:0.000}, \"hifi_records\": {s.HiFiRecs},");
            sb.AppendLine($"  \"raw_mb_pose\": {s.PoseBytes / 1048576f:0.000}, \"pose_records\": {s.PoseRecs},");
            sb.AppendLine($"  \"remove_records\": {s.RemoveRecs}, \"camera_records\": {s.CamRecs}, \"env_records\": {s.EnvRecs},");
            sb.AppendLine($"  \"sample_ms_avg\": {_sampleMs / Mathf.Max(1, _samples):0.000}, \"sample_ms_max\": {_sampleMsMax:0.000},");
            sb.AppendLine("  \"rpcs\": {");
            var rows = _rpcCounts.OrderByDescending(kv => kv.Value).Select(kv => $"    \"{Rpcs.Name(kv.Key)}\": {kv.Value}").ToList();
            sb.AppendLine(string.Join(",\n", rows));
            sb.AppendLine("  }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }

    // Marker sources. OnDeath runs on the object's owner only, so in multiplayer deaths of creatures
    // another player owns are missed (shortcut: owner-only, upgrade by watching s_dead in sampled ZDOs).
    [HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
    internal static class MarkCharacterDeath
    {
        private static readonly AccessTools.FieldRef<Character, HitData> LastHit = AccessTools.FieldRefAccess<Character, HitData>("m_lastHit");

        private static void Prefix(Character __instance)
        {
            if (!Recorder.Active) return;
            string name = Localization.instance.Localize(__instance.m_name);
            Character by = LastHit(__instance)?.GetAttacker();
            Vector3 pos = __instance.transform.position;
            if (__instance.IsBoss()) Recorder.Mark(MarkerKind.Boss, pos, $"Boss defeated: {name}" + (by ? $" (by {by.GetHoverName()})" : ""));
            else if (by && by.IsPlayer()) Recorder.Mark(MarkerKind.Kill, pos, $"{by.GetHoverName()} killed {name}");
            else Recorder.Mark(MarkerKind.Death, pos, by ? $"{name} killed by {Localization.instance.Localize(by.m_name)}" : $"{name} died");
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
    internal static class MarkPlayerDeath
    {
        private static void Prefix(Player __instance)
        {
            if (Recorder.Active && __instance == Player.m_localPlayer) Recorder.Mark(MarkerKind.Death, __instance.transform.position, $"{__instance.GetPlayerName()} died");
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
    internal static class MarkTeleport
    {
        private static void Postfix(Player __instance, bool __result, Vector3 pos)
        {
            if (Recorder.Active && __result && __instance == Player.m_localPlayer) Recorder.Mark(MarkerKind.Portal, pos, $"{__instance.GetPlayerName()} teleported");
        }
    }

    [HarmonyPatch(typeof(ZRoutedRpc), "HandleRoutedRPC")]
    internal static class RoutedRpcRecordPatch
    {
        private static void Prefix(ZRoutedRpc.RoutedRPCData data) => Recorder.OnRoutedRpc(data);
    }
}
