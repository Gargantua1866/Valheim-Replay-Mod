using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace ReplayMod
{
    // File layout: a Deflate stream holding the header, then records in time order, then End.
    // Every ZDO record carries the object's full state (ZDO.Serialize plus position), so seeking is
    // "latest record at or before t" per object.
    internal enum RecType : byte { Zdo = 1, Remove = 2, Rpc = 3, Cam = 4, Env = 5, HiFi = 6, PoseBones = 7, Pose = 8, PartsHeader = 9, Parts = 10, Marker = 11, Vitals = 12, Wind = 13, Water = 14, Hotbar = 15, Hud = 16, Self = 17, Map = 18, End = 255 }

    // Timeline markers: things worth jumping to.
    internal enum MarkerKind : byte { Death, Kill, Boss, Portal, Skill }

    [Flags]
    internal enum ZdoFlags : byte { None = 0, Dynamic = 1, Keyframe = 2 }

    internal class ReplayHeader
    {
        public const string Magic = "MUNINN"; // the mod's old name; existing recordings start with it
        public const int FormatVersion = 6; // 2 added the pose lane, 3 vehicle parts, 4 markers, 5 vitals, 6 wind; older ones still read

        public int Version = FormatVersion;
        public string GameVersion = "";
        public string WorldName = "";
        public string SeedName = "";
        public int Seed;
        public int WorldGenVersion;
        public string PlayerName = "";
        public long PlayerUser;
        public uint PlayerId;
        public Vector3 StartPos;
        public string Date = "";

        public void Write(BinaryWriter w)
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(GameVersion);
            w.Write(WorldName);
            w.Write(SeedName);
            w.Write(Seed);
            w.Write(WorldGenVersion);
            w.Write(PlayerName);
            w.Write(PlayerUser);
            w.Write(PlayerId);
            w.WriteV(StartPos);
            w.Write(Date);
        }

        public static ReplayHeader Read(BinaryReader r)
        {
            if (r.ReadString() != Magic)
            {
                throw new InvalidDataException("not a Replay Mod recording");
            }
            var h = new ReplayHeader { Version = r.ReadInt32() };
            if (h.Version < 1 || h.Version > FormatVersion)
            {
                throw new InvalidDataException($"replay format {h.Version}, this build reads {FormatVersion}");
            }
            h.GameVersion = r.ReadString();
            h.WorldName = r.ReadString();
            h.SeedName = r.ReadString();
            h.Seed = r.ReadInt32();
            h.WorldGenVersion = r.ReadInt32();
            h.PlayerName = r.ReadString();
            h.PlayerUser = r.ReadInt64();
            h.PlayerId = r.ReadUInt32();
            h.StartPos = r.ReadV();
            h.Date = r.ReadString();
            return h;
        }
    }

    internal static class BinaryExt
    {
        public static void WriteV(this BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        public static void WriteQ(this BinaryWriter w, Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
        public static Vector3 ReadV(this BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        public static Quaternion ReadQ(this BinaryReader r) => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        public static void WriteId(this BinaryWriter w, ZDOID id) { w.Write(id.UserID); w.Write(id.ID); }
        public static RecId ReadId(this BinaryReader r) => new RecId(r.ReadInt64(), r.ReadUInt32());
    }

    // A recorded object id as raw numbers. ZDOID can't be held across a world start: it stores an index
    // into a static user table that ZDOMan clears (ZDOID.Reset), so a ZDOID built in the main menu
    // later points at a different user and objects collide (creatures showed up as trees).
    internal readonly struct RecId : IEquatable<RecId>
    {
        public readonly long User;
        public readonly uint Id;

        public RecId(long user, uint id)
        {
            User = user;
            Id = id;
        }

        // Only call while the replay world's ZDOMan exists.
        public ZDOID ToZdoId() => new ZDOID(User, Id);

        public bool Equals(RecId o) => o.User == User && o.Id == Id;
        public override bool Equals(object o) => o is RecId r && Equals(r);
        public override int GetHashCode() => User.GetHashCode() * 397 ^ (int)Id;
    }

    // Uncompressed byte counts per category, for the P0 size measurements.
    internal class RecordStats
    {
        public long StaticBytes, DynamicBytes, KeyframeBytes, RpcBytes, HiFiBytes, PoseBytes, OtherBytes;
        public int StaticRecs, DynamicRecs, KeyframeRecs, RemoveRecs, RpcRecs, HiFiRecs, PoseRecs, CamRecs, EnvRecs;
        public long Total => StaticBytes + DynamicBytes + KeyframeBytes + RpcBytes + HiFiBytes + PoseBytes + OtherBytes;
    }

    internal class ReplayWriter : IDisposable
    {
        private readonly FileStream _file;
        private readonly DeflateStream _deflate;
        private readonly BinaryWriter _w;
        private readonly MemoryStream _rec = new MemoryStream();
        private readonly BinaryWriter _rw;
        public readonly RecordStats Stats = new RecordStats();
        public readonly string Path;

        public ReplayWriter(string path, ReplayHeader header)
        {
            Path = path;
            _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            _deflate = new DeflateStream(_file, System.IO.Compression.CompressionLevel.Optimal);
            _w = new BinaryWriter(new BufferedStream(_deflate, 1 << 16));
            _rw = new BinaryWriter(_rec);
            header.Write(_w);
        }

        public long CompressedBytes => _file.Length;

        private BinaryWriter Begin(RecType type, float t)
        {
            _rec.SetLength(0);
            _rw.Write((byte)type);
            _rw.Write(t);
            return _rw;
        }

        private long Commit()
        {
            _rw.Flush();
            _w.Write(_rec.GetBuffer(), 0, (int)_rec.Length);
            return _rec.Length;
        }

        public void Zdo(float t, ZDOID id, ZdoFlags flags, Vector3 pos, byte[] data)
        {
            var w = Begin(RecType.Zdo, t);
            w.WriteId(id);
            w.Write((byte)flags);
            w.WriteV(pos);
            w.Write(data.Length);
            w.Write(data);
            long n = Commit();
            if ((flags & ZdoFlags.Keyframe) != 0) { Stats.KeyframeBytes += n; Stats.KeyframeRecs++; }
            else if ((flags & ZdoFlags.Dynamic) != 0) { Stats.DynamicBytes += n; Stats.DynamicRecs++; }
            else { Stats.StaticBytes += n; Stats.StaticRecs++; }
        }

        public void Remove(float t, ZDOID id)
        {
            Begin(RecType.Remove, t).WriteId(id);
            Stats.OtherBytes += Commit();
            Stats.RemoveRecs++;
        }

        public void Rpc(float t, byte[] data)
        {
            var w = Begin(RecType.Rpc, t);
            w.Write(data.Length);
            w.Write(data);
            Stats.RpcBytes += Commit();
            Stats.RpcRecs++;
        }

        public void Cam(float t, Vector3 pos, Quaternion rot, float fov)
        {
            var w = Begin(RecType.Cam, t);
            w.WriteV(pos);
            w.WriteQ(rot);
            w.Write(fov);
            Stats.OtherBytes += Commit();
            Stats.CamRecs++;
        }

        public void Env(float t, float dayFraction, string env)
        {
            var w = Begin(RecType.Env, t);
            w.Write(dayFraction);
            w.Write(env ?? "");
            Stats.OtherBytes += Commit();
            Stats.EnvRecs++;
        }

        public void Wind(float t, float angle, float intensity)
        {
            var w = Begin(RecType.Wind, t);
            w.Write(angle);
            w.Write(intensity);
            Stats.OtherBytes += Commit();
        }

        public void Water(float t, float waterTime)
        {
            var w = Begin(RecType.Water, t);
            w.Write(waterTime);
            Stats.OtherBytes += Commit();
        }

        // The recording player's whole hotbar, written when anything in it changes.
        public void Hotbar(float t, List<HotbarSlot> slots)
        {
            var w = Begin(RecType.Hotbar, t);
            w.Write(slots.Count);
            foreach (HotbarSlot s in slots)
            {
                w.Write((byte)s.Slot);
                w.Write(s.Prefab);
                w.Write(s.Stack);
                w.Write(s.Quality);
                w.Write(s.Variant);
                w.Write(s.Durability);
                w.Write(s.Equipped);
            }
            Stats.OtherBytes += Commit();
        }

        // The recording player's food and status effects, written when what the HUD shows changes.
        public void Hud(float t, List<FoodSlot> foods, List<EffectSlot> effects)
        {
            var w = Begin(RecType.Hud, t);
            w.Write(foods.Count);
            foreach (FoodSlot f in foods)
            {
                w.Write(f.Prefab);
                w.Write(f.Time);
            }
            w.Write(effects.Count);
            foreach (EffectSlot e in effects)
            {
                w.Write(e.Hash);
                w.Write(e.Text);
            }
            Stats.OtherBytes += Commit();
        }

        // The recording player's ZDO, each time it's a new one (respawning makes a new player).
        public void Self(float t, ZDOID id)
        {
            var w = Begin(RecType.Self, t);
            w.WriteId(id);
            Stats.OtherBytes += Commit();
        }

        // The minimap's explored pixels at the start, packed 8 to a byte.
        public void Map(float t, byte[] explored, byte[] others)
        {
            var w = Begin(RecType.Map, t);
            w.Write(explored.Length);
            w.Write(explored);
            w.Write(others.Length);
            w.Write(others);
            Stats.OtherBytes += Commit();
        }

        public void Marker(float t, MarkerKind kind, Vector3 pos, string text)
        {
            var w = Begin(RecType.Marker, t);
            w.Write((byte)kind);
            w.WriteV(pos);
            w.Write(text ?? "");
            Stats.OtherBytes += Commit();
        }

        public void Vitals(float t, float maxStamina, float maxEitr)
        {
            var w = Begin(RecType.Vitals, t);
            w.Write(maxStamina);
            w.Write(maxEitr);
            Stats.OtherBytes += Commit();
        }

        public void PoseBones(float t, ZDOID id, string[] names)
        {
            var w = Begin(RecType.PoseBones, t);
            w.WriteId(id);
            w.Write(names.Length);
            foreach (string n in names) w.Write(n);
            Stats.PoseBytes += Commit();
        }

        // Bone local rotations quantized to shorts, plus the hips' local position.
        public void Pose(float t, ZDOID id, Transform[] bones)
        {
            var w = Begin(RecType.Pose, t);
            w.WriteId(id);
            w.Write(bones.Length);
            Vector3 hips = Vector3.zero;
            foreach (Transform b in bones)
            {
                Quaternion q = b ? b.localRotation : Quaternion.identity;
                w.Write(Q16(q.x)); w.Write(Q16(q.y)); w.Write(Q16(q.z)); w.Write(Q16(q.w));
                if (b && b.name == "Hips") hips = b.localPosition;
            }
            w.WriteV(hips);
            Stats.PoseBytes += Commit();
            Stats.PoseRecs++;
        }

        public void PartsHeader(float t, ZDOID id, string[] paths)
        {
            var w = Begin(RecType.PartsHeader, t);
            w.WriteId(id);
            w.Write(paths.Length);
            foreach (string p in paths) w.Write(p);
            Stats.PoseBytes += Commit();
        }

        // Changed parts only: index, local position, local rotation (quantized).
        public void Parts(float t, ZDOID id, List<int> indices, Vector3[] pos, Quaternion[] rot)
        {
            var w = Begin(RecType.Parts, t);
            w.WriteId(id);
            w.Write(indices.Count);
            foreach (int i in indices)
            {
                w.Write((ushort)i);
                w.WriteV(pos[i]);
                Quaternion q = rot[i];
                w.Write(Q16(q.x)); w.Write(Q16(q.y)); w.Write(Q16(q.z)); w.Write(Q16(q.w));
            }
            Stats.PoseBytes += Commit();
        }

        private static short Q16(float v) => (short)Mathf.RoundToInt(Mathf.Clamp(v, -1f, 1f) * 32767f);

        public void HiFi(float t, ZDOID id, Vector3 pos, Quaternion rot)
        {
            var w = Begin(RecType.HiFi, t);
            w.WriteId(id);
            w.WriteV(pos);
            w.WriteQ(rot);
            Stats.HiFiBytes += Commit();
            Stats.HiFiRecs++;
        }

        public void Dispose()
        {
            _w.Write((byte)RecType.End);
            _w.Flush();
            _w.Dispose();
        }
    }

    internal class ReplayEvent
    {
        public RecType Type;
        public float T;
        public RecId Id;
        public ZdoFlags Flags;
        public Vector3 Pos;
        public Quaternion Rot;
        public float F;
        public string S;
        public byte[] Data;
    }

    internal struct HotbarSlot
    {
        public int Slot;
        public string Prefab;
        public int Stack, Quality, Variant;
        public float Durability;
        public bool Equipped;
    }

    internal struct FoodSlot
    {
        public string Prefab;
        public float Time;
    }

    internal struct EffectSlot
    {
        public int Hash;
        public string Text;
    }

    internal class HudSample
    {
        public float T;
        public FoodSlot[] Foods;
        public EffectSlot[] Effects;
    }

    internal class HotbarSample
    {
        public float T;
        public HotbarSlot[] Slots;
    }

    internal struct PartSample
    {
        public float T;
        public Vector3 Pos;
        public Quaternion Rot;
    }

    internal class PartsTrack
    {
        public string[] Paths;
        public List<PartSample>[] Samples; // per part index, in time order; null = never moved
    }

    internal class PoseSample
    {
        public float T;
        public string[] Names;
        public Quaternion[] Rot;
        public Vector3 Hips;
    }

    internal class ReplayData
    {
        public ReplayHeader Header;
        public string Path;
        public float Duration;
        public readonly List<ReplayEvent> Events = new List<ReplayEvent>();
        public readonly List<ReplayEvent> Cams = new List<ReplayEvent>();
        public readonly List<ReplayEvent> Envs = new List<ReplayEvent>();
        public readonly List<ReplayEvent> Winds = new List<ReplayEvent>(); // F = intensity, Pos.x = angle in degrees
        public readonly List<ReplayEvent> Waters = new List<ReplayEvent>(); // F = the game's wave clock (recordings from 0.0.27)
        public readonly List<HotbarSample> Hotbars = new List<HotbarSample>(); // recordings from 0.0.28
        public readonly List<HudSample> Huds = new List<HudSample>();          // the rest from 0.0.29
        public readonly List<ReplayEvent> Selves = new List<ReplayEvent>();    // Id = the recording player from then on
        public byte[] MapExplored, MapOthers;                                   // the first map record: the start
        public readonly List<ReplayEvent> Markers = new List<ReplayEvent>(); // F = MarkerKind, S = text
        public readonly List<ReplayEvent> Vitals = new List<ReplayEvent>(); // the recorder's maximums: F = stamina, Pos.x = eitr
        public readonly Dictionary<RecId, List<ReplayEvent>> HiFi = new Dictionary<RecId, List<ReplayEvent>>();
        public readonly Dictionary<RecId, List<PoseSample>> Poses = new Dictionary<RecId, List<PoseSample>>();
        public readonly Dictionary<RecId, PartsTrack> Parts = new Dictionary<RecId, PartsTrack>();

        public static ReplayData Load(string path)
        {
            var d = new ReplayData { Path = path };
            var boneNames = new Dictionary<RecId, string[]>();
            using (var r = new BinaryReader(new BufferedStream(new DeflateStream(File.OpenRead(path), CompressionMode.Decompress), 1 << 16)))
            {
                d.Header = ReplayHeader.Read(r);
                while (true)
                {
                    RecType type;
                    try { type = (RecType)r.ReadByte(); }
                    catch (EndOfStreamException) { break; } // recording cut short (crash): keep what we have
                    if (type == RecType.End) break;
                    var e = new ReplayEvent { Type = type, T = r.ReadSingle() };
                    switch (type)
                    {
                        case RecType.Zdo:
                            e.Id = r.ReadId();
                            e.Flags = (ZdoFlags)r.ReadByte();
                            e.Pos = r.ReadV();
                            e.Data = r.ReadBytes(r.ReadInt32());
                            d.Events.Add(e);
                            break;
                        case RecType.Remove:
                            e.Id = r.ReadId();
                            d.Events.Add(e);
                            break;
                        case RecType.Rpc:
                            e.Data = r.ReadBytes(r.ReadInt32());
                            d.Events.Add(e);
                            break;
                        case RecType.Cam:
                            e.Pos = r.ReadV();
                            e.Rot = r.ReadQ();
                            e.F = r.ReadSingle();
                            d.Cams.Add(e);
                            break;
                        case RecType.Env:
                            e.F = r.ReadSingle();
                            e.S = r.ReadString();
                            d.Envs.Add(e);
                            break;
                        case RecType.Marker:
                            e.F = r.ReadByte();
                            e.Pos = r.ReadV();
                            e.S = r.ReadString();
                            if ((MarkerKind)(int)e.F != MarkerKind.Skill) d.Markers.Add(e); // older recordings have them; no longer shown
                            break;
                        case RecType.Wind:
                            e.Pos = new Vector3(r.ReadSingle(), 0f, 0f);
                            e.F = r.ReadSingle();
                            d.Winds.Add(e);
                            break;
                        case RecType.Water:
                            e.F = r.ReadSingle();
                            d.Waters.Add(e);
                            break;
                        case RecType.Hotbar:
                        {
                            var slots = new HotbarSlot[r.ReadInt32()];
                            for (int i = 0; i < slots.Length; i++)
                            {
                                slots[i] = new HotbarSlot
                                {
                                    Slot = r.ReadByte(), Prefab = r.ReadString(), Stack = r.ReadInt32(), Quality = r.ReadInt32(),
                                    Variant = r.ReadInt32(), Durability = r.ReadSingle(), Equipped = r.ReadBoolean(),
                                };
                            }
                            d.Hotbars.Add(new HotbarSample { T = e.T, Slots = slots });
                            break;
                        }
                        case RecType.Hud:
                        {
                            var foods = new FoodSlot[r.ReadInt32()];
                            for (int i = 0; i < foods.Length; i++) foods[i] = new FoodSlot { Prefab = r.ReadString(), Time = r.ReadSingle() };
                            var effects = new EffectSlot[r.ReadInt32()];
                            for (int i = 0; i < effects.Length; i++) effects[i] = new EffectSlot { Hash = r.ReadInt32(), Text = r.ReadString() };
                            d.Huds.Add(new HudSample { T = e.T, Foods = foods, Effects = effects });
                            break;
                        }
                        case RecType.Self:
                            e.Id = r.ReadId();
                            d.Selves.Add(e);
                            break;
                        case RecType.Map:
                        {
                            byte[] explored = r.ReadBytes(r.ReadInt32()), others = r.ReadBytes(r.ReadInt32());
                            if (d.MapExplored == null)
                            {
                                d.MapExplored = explored;
                                d.MapOthers = others;
                            }
                            break;
                        }
                        case RecType.Vitals:
                            e.F = r.ReadSingle();
                            e.Pos = new Vector3(r.ReadSingle(), 0f, 0f);
                            d.Vitals.Add(e);
                            break;
                        case RecType.PoseBones:
                        {
                            e.Id = r.ReadId();
                            var names = new string[r.ReadInt32()];
                            for (int i = 0; i < names.Length; i++) names[i] = r.ReadString();
                            boneNames[e.Id] = names;
                            break;
                        }
                        case RecType.Pose:
                        {
                            e.Id = r.ReadId();
                            int n = r.ReadInt32();
                            var rot = new Quaternion[n];
                            for (int i = 0; i < n; i++)
                            {
                                rot[i] = Quaternion.Normalize(new Quaternion(r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f));
                            }
                            Vector3 hips = r.ReadV();
                            if (boneNames.TryGetValue(e.Id, out string[] bn) && bn.Length == n)
                            {
                                if (!d.Poses.TryGetValue(e.Id, out var poses)) d.Poses[e.Id] = poses = new List<PoseSample>();
                                poses.Add(new PoseSample { T = e.T, Names = bn, Rot = rot, Hips = hips });
                            }
                            break;
                        }
                        case RecType.PartsHeader:
                        {
                            e.Id = r.ReadId();
                            var paths = new string[r.ReadInt32()];
                            for (int i = 0; i < paths.Length; i++) paths[i] = r.ReadString();
                            // Getting back into the same vehicle repeats its header: keep the samples so far.
                            if (!d.Parts.TryGetValue(e.Id, out PartsTrack old) || old.Paths.Length != paths.Length)
                            {
                                d.Parts[e.Id] = new PartsTrack { Paths = paths, Samples = new List<PartSample>[paths.Length] };
                            }
                            break;
                        }
                        case RecType.Parts:
                        {
                            e.Id = r.ReadId();
                            int n = r.ReadInt32();
                            d.Parts.TryGetValue(e.Id, out PartsTrack track);
                            for (int k = 0; k < n; k++)
                            {
                                int i = r.ReadUInt16();
                                Vector3 pos = r.ReadV();
                                Quaternion rot = Quaternion.Normalize(new Quaternion(r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f));
                                if (track == null || i >= track.Samples.Length) continue;
                                (track.Samples[i] ?? (track.Samples[i] = new List<PartSample>())).Add(new PartSample { T = e.T, Pos = pos, Rot = rot });
                            }
                            break;
                        }
                        case RecType.HiFi:
                            e.Id = r.ReadId();
                            e.Pos = r.ReadV();
                            e.Rot = r.ReadQ();
                            if (!d.HiFi.TryGetValue(e.Id, out var list)) d.HiFi[e.Id] = list = new List<ReplayEvent>();
                            list.Add(e);
                            break;
                        default:
                            throw new InvalidDataException($"unknown record type {(byte)type}");
                    }
                    if (e.T > d.Duration) d.Duration = e.T;
                }
            }
            // Recordings before 0.0.19 that began as the character spawned start with a camera at the world's origin.
            if (d.Cams.Count > 1 && (d.Cams[0].Pos - d.Cams[1].Pos).sqrMagnitude > 500f * 500f) d.Cams.RemoveAt(0);
            // Update and FixedUpdate records interleave; a stable sort keeps same-time order.
            StableSort(d.Events);
            foreach (var list in d.HiFi.Values) StableSort(list);
            foreach (var list in d.Poses.Values)
            {
                list.Sort((a, b) => a.T.CompareTo(b.T));
                DropUnfinishedPose(list);
            }
            return d;
        }

        // Recordings before 0.0.20 that began as the character spawned open in the bind pose, which
        // shows as a jump far bigger than moving makes. What follows it holds from the start instead.
        internal static void DropUnfinishedPose(List<PoseSample> poses)
        {
            bool dropped = false;
            while (poses.Count > 2 && poses[0].T < 0.5f && LargestTurn(poses[0], poses[1]) > 20f)
            {
                poses.RemoveAt(0);
                dropped = true;
            }
            if (dropped) poses[0].T = 0f;
        }

        private static float LargestTurn(PoseSample a, PoseSample b)
        {
            float largest = 0f;
            for (int i = 0; i < a.Rot.Length && i < b.Rot.Length; i++) largest = Mathf.Max(largest, Quaternion.Angle(a.Rot[i], b.Rot[i]));
            return largest;
        }

        private static void StableSort(List<ReplayEvent> list)
        {
            var sorted = new List<ReplayEvent>(list);
            var keys = new int[sorted.Count];
            for (int i = 0; i < keys.Length; i++) keys[i] = i;
            Array.Sort(keys, (a, b) => { int c = sorted[a].T.CompareTo(sorted[b].T); return c != 0 ? c : a.CompareTo(b); });
            for (int i = 0; i < keys.Length; i++) list[i] = sorted[keys[i]];
        }
    }
}
