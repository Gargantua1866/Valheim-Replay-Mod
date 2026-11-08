using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ReplayMod
{
    // Cuts a time range out of one or more recordings into a new replay file: the state of the world at
    // the start of the range becomes records at time 0, and later records are copied as they are.
    // Several sources are one timeline (the replay buffer's rolling files). Uses no Unity objects, so it
    // can run on a worker thread and a save during play doesn't hitch the game.
    internal static class Clipper
    {
        private static volatile string _message;
        private static int _running;

        public static bool Busy => Volatile.Read(ref _running) > 0;

        // The finished clip's message, once, on the main thread.
        public static string TakeMessage()
        {
            string m = _message;
            if (m != null) _message = null;
            return m;
        }

        // cameraText: the new replay's camera keys, or null. deleteSources: they're the replay buffer's files.
        public static void Start(string[] sources, float from, float to, string dest, string cameraText, bool deleteSources)
        {
            Interlocked.Increment(ref _running);
            string name = Path.GetFileNameWithoutExtension(dest);
            Task.Run(() =>
            {
                try
                {
                    float duration = Write(sources, from, to, dest);
                    File.WriteAllText(Path.ChangeExtension(dest, ".json"), "{\n  \"duration_s\": " + duration.ToString("0.0", CultureInfo.InvariantCulture) + "\n}\n");
                    if (cameraText != null) File.WriteAllText(Path.ChangeExtension(dest, ".camera.txt"), cameraText);
                    _message = $"Clip saved: {name}";
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"clip {dest} failed: {e}");
                    _message = $"Clip failed: {e.Message}";
                }
                finally
                {
                    if (deleteSources) foreach (string s in sources) TryDelete(s);
                    Interlocked.Decrement(ref _running);
                }
            });
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Plugin.Log.LogWarning($"couldn't delete {path}: {e.Message}"); }
        }

        // A free name: "<stem>_clip1.replay", "<stem>_clip2.replay"...
        public static string FreeName(string stem)
        {
            for (int i = 1; ; i++)
            {
                string path = Path.Combine(Plugin.ReplayDir, $"{stem}_clip{i}.replay");
                if (!File.Exists(path)) return path;
            }
        }

        // Writes the clip and returns its length in seconds.
        internal static float Write(IList<string> sources, float from, float to, string dest)
        {
            var zdos = new Dictionary<RecId, byte[]>();            // latest object state before the range
            var bones = new Dictionary<RecId, byte[]>();           // latest pose skeleton
            var partsHeaders = new Dictionary<RecId, byte[]>();
            var partsRest = new Dictionary<RecId, Dictionary<ushort, byte[]>>(); // where each moving part stands
            var movingAlive = new HashSet<RecId>();                // moving objects that exist right now
            byte[] env = null, wind = null, vitals = null, hotbar = null;
            float? waterOffset = null;                             // wave clock minus recording time
            ReplayHeader header = null;
            Vector3? camPos = null;
            BinaryWriter w = null;
            float offset = 0f, end = 0f, last = 0f;

            void Put(RecType type, float t, byte[] body)
            {
                w.Write((byte)type);
                w.Write(t);
                w.Write(body);
            }

            void Open()
            {
                if (camPos.HasValue) header.StartPos = camPos.Value;
                w = new BinaryWriter(new BufferedStream(new DeflateStream(new FileStream(dest, FileMode.CreateNew, FileAccess.Write), System.IO.Compression.CompressionLevel.Optimal), 1 << 16));
                header.Write(w);
                foreach (byte[] b in zdos.Values) Put(RecType.Zdo, 0f, b);
                foreach (byte[] b in bones.Values) Put(RecType.PoseBones, 0f, b);
                foreach (byte[] b in partsHeaders.Values) Put(RecType.PartsHeader, 0f, b);
                foreach (var kv in partsRest)
                {
                    var ms = new MemoryStream();
                    var bw = new BinaryWriter(ms);
                    bw.Write(kv.Key.User);
                    bw.Write(kv.Key.Id);
                    bw.Write(kv.Value.Count);
                    foreach (byte[] entry in kv.Value.Values) bw.Write(entry);
                    Put(RecType.Parts, 0f, ms.ToArray());
                }
                if (env != null) Put(RecType.Env, 0f, env);
                if (wind != null) Put(RecType.Wind, 0f, wind);
                if (vitals != null) Put(RecType.Vitals, 0f, vitals);
                if (hotbar != null) Put(RecType.Hotbar, 0f, hotbar);
                if (waterOffset.HasValue) Put(RecType.Water, 0f, BitConverter.GetBytes(waterOffset.Value + from));
            }

            void Handle(RecType type, float t, byte[] body)
            {
                if (type == RecType.Zdo || type == RecType.Remove)
                {
                    RecId id = IdOf(body);
                    if (type == RecType.Zdo && (body[12] & (byte)ZdoFlags.Dynamic) != 0) movingAlive.Add(id);
                    else movingAlive.Remove(id);
                }
                if (t > to) return;
                if (t >= from && w == null) Open();
                if (w != null)
                {
                    Put(type, Mathf.Max(t - from, 0f), body);
                    end = Mathf.Max(end, t - from);
                    return;
                }
                switch (type) // before the range: remember only what later moments depend on
                {
                    case RecType.Zdo: zdos[IdOf(body)] = body; break;
                    case RecType.Remove: zdos.Remove(IdOf(body)); break;
                    case RecType.PoseBones: bones[IdOf(body)] = body; break;
                    case RecType.PartsHeader: partsHeaders[IdOf(body)] = body; break;
                    case RecType.Env: env = body; break;
                    case RecType.Wind: wind = body; break;
                    case RecType.Vitals: vitals = body; break;
                    case RecType.Hotbar: hotbar = body; break;
                    case RecType.Water: waterOffset = BitConverter.ToSingle(body, 0) - t; break;
                    case RecType.Cam: camPos = new Vector3(BitConverter.ToSingle(body, 0), BitConverter.ToSingle(body, 4), BitConverter.ToSingle(body, 8)); break;
                    case RecType.Parts:
                        RecId vehicle = IdOf(body);
                        if (!partsRest.TryGetValue(vehicle, out var rest)) partsRest[vehicle] = rest = new Dictionary<ushort, byte[]>();
                        for (int at = 16; at + PartEntry <= body.Length; at += PartEntry)
                        {
                            var entry = new byte[PartEntry];
                            Array.Copy(body, at, entry, 0, PartEntry);
                            rest[BitConverter.ToUInt16(entry, 0)] = entry;
                        }
                        break;
                }
            }

            foreach (string source in sources)
            {
                // The next file records everything loaded in its first sample. A moving object not in
                // that sample left the world between the files, so it's removed where they join.
                HashSet<RecId> leftBehind = last > 0f || w != null ? new HashSet<RecId>(movingAlive) : null;
                float? firstSample = null;
                using (var r = new BinaryReader(new BufferedStream(new DeflateStream(File.OpenRead(source), CompressionMode.Decompress), 1 << 16)))
                {
                    ReplayHeader h = ReplayHeader.Read(r);
                    if (header == null) header = h;
                    while (true)
                    {
                        RecType type;
                        float t;
                        try
                        {
                            type = (RecType)r.ReadByte();
                            if (type == RecType.End) break;
                            t = r.ReadSingle();
                        }
                        catch (EndOfStreamException) { break; } // recording cut short: keep what there is
                        byte[] body;
                        try { body = ReadBody(r, type); }
                        catch (EndOfStreamException) { break; }
                        t += offset;
                        last = Mathf.Max(last, t);
                        if (leftBehind != null)
                        {
                            if (firstSample.HasValue && t > firstSample.Value + 1e-3f)
                            {
                                foreach (RecId id in leftBehind) Handle(RecType.Remove, firstSample.Value, IdBytes(id));
                                leftBehind = null;
                            }
                            else if (type == RecType.Zdo)
                            {
                                firstSample = firstSample ?? t;
                                leftBehind.Remove(IdOf(body));
                            }
                        }
                        Handle(type, t, body);
                    }
                }
                offset = last;
            }
            if (w == null) throw new InvalidDataException("nothing recorded in that range");
            w.Write((byte)RecType.End);
            w.Flush();
            w.Dispose();
            return end;
        }

        private const int PartEntry = 2 + 12 + 8; // index, position, quantized rotation

        private static RecId IdOf(byte[] body) => new RecId(BitConverter.ToInt64(body, 0), BitConverter.ToUInt32(body, 8));

        private static byte[] IdBytes(RecId id)
        {
            var b = new byte[12];
            BitConverter.GetBytes(id.User).CopyTo(b, 0);
            BitConverter.GetBytes(id.Id).CopyTo(b, 8);
            return b;
        }

        // One record's bytes after its type and time, for every type ReplayData.Load reads.
        private static byte[] ReadBody(BinaryReader r, RecType type)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            void Copy(int n)
            {
                byte[] b = r.ReadBytes(n);
                if (b.Length < n) throw new EndOfStreamException();
                w.Write(b);
            }
            int Int()
            {
                int v = r.ReadInt32();
                w.Write(v);
                return v;
            }
            void Text() => w.Write(r.ReadString());
            switch (type)
            {
                case RecType.Zdo: Copy(12 + 1 + 12); Copy(Int()); break;
                case RecType.Remove: Copy(12); break;
                case RecType.Rpc: Copy(Int()); break;
                case RecType.Cam: Copy(12 + 16 + 4); break;
                case RecType.Env: Copy(4); Text(); break;
                case RecType.Marker: Copy(1 + 12); Text(); break;
                case RecType.Vitals:
                case RecType.Wind: Copy(8); break;
                case RecType.Water: Copy(4); break;
                case RecType.Hotbar:
                    for (int n = Int(); n > 0; n--)
                    {
                        Copy(1);
                        Text();
                        Copy(4 + 4 + 4 + 4 + 1);
                    }
                    break;
                case RecType.HiFi: Copy(12 + 12 + 16); break;
                case RecType.PoseBones:
                case RecType.PartsHeader:
                    Copy(12);
                    for (int n = Int(); n > 0; n--) Text();
                    break;
                case RecType.Pose: Copy(12); Copy(Int() * 8 + 12); break;
                case RecType.Parts: Copy(12); Copy(Int() * PartEntry); break;
                default: throw new InvalidDataException($"unknown record type {(byte)type}");
            }
            w.Flush();
            return ms.ToArray();
        }

        // Builds two small recordings in the temp folder and checks the cut and the join.
        public static void SelfCheck()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ReplayModClipCheck" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                string a = Path.Combine(dir, "a.replay"), b = Path.Combine(dir, "b.replay"), clip = Path.Combine(dir, "clip.replay");
                void Source(string path, Action<BinaryWriter> records)
                {
                    using (var w = new BinaryWriter(new DeflateStream(File.Create(path), System.IO.Compression.CompressionLevel.Fastest)))
                    {
                        new ReplayHeader { WorldName = "check" }.Write(w);
                        records(w);
                        w.Write((byte)RecType.End);
                    }
                }
                void Zdo(BinaryWriter w, float t, uint id, bool moving)
                {
                    w.Write((byte)RecType.Zdo); w.Write(t); w.Write(1L); w.Write(id);
                    w.Write((byte)(moving ? ZdoFlags.Dynamic : ZdoFlags.None));
                    w.WriteV(new Vector3(id, t, 0f));
                    w.Write(2); w.Write((ushort)0);
                }
                void Remove(BinaryWriter w, float t, uint id)
                {
                    w.Write((byte)RecType.Remove); w.Write(t); w.Write(1L); w.Write(id);
                }
                Source(a, w =>
                {
                    Zdo(w, 0f, 1, true);   // gone before the range
                    Zdo(w, 0f, 2, true);   // state carries into the range
                    Zdo(w, 0f, 3, true);   // alive at the join, not in the next file
                    Remove(w, 2f, 1);
                    Zdo(w, 3f, 2, true);
                    w.Write((byte)RecType.Env); w.Write(3f); w.Write(0.5f); w.Write("Clear"); w.Write((byte)RecType.Wind); w.Write(3f); w.Write(10f); w.Write(0.5f);
                    w.Write((byte)RecType.Water); w.Write(3f); w.Write(103f);
                    w.Write((byte)RecType.Hotbar); w.Write(3f); w.Write(1); w.Write((byte)2); w.Write("Club"); w.Write(1); w.Write(1); w.Write(0); w.Write(50f); w.Write(true);
                    Zdo(w, 6f, 2, true);
                });
                Source(b, w =>
                {
                    Zdo(w, 0f, 2, true);   // the next file's first sample
                    Zdo(w, 1f, 2, true);
                    Zdo(w, 9f, 2, true);   // past the end of the range
                });
                Write(new[] { a, b }, 4f, 10f, clip);
                ReplayData d = ReplayData.Load(clip);
                bool ok = !d.Events.Exists(e => e.Id.Id == 1)
                    && d.Events.Exists(e => e.Id.Id == 2 && e.Type == RecType.Zdo && e.T == 0f && e.Pos.y == 3f)
                    && d.Events.Exists(e => e.Id.Id == 2 && e.Type == RecType.Zdo && e.T == 2f)
                    && d.Events.Exists(e => e.Id.Id == 3 && e.Type == RecType.Remove && e.T == 2f)
                    && d.Envs.Count == 1 && d.Envs[0].T == 0f && d.Winds.Count == 1 && d.Winds[0].T == 0f && d.Winds[0].Pos.x == 10f && d.Duration <= 6f
                    && d.Waters.Count == 1 && d.Waters[0].T == 0f && d.Waters[0].F == 104f
                    && d.Hotbars.Count == 1 && d.Hotbars[0].T == 0f && d.Hotbars[0].Slots.Length == 1 && d.Hotbars[0].Slots[0].Prefab == "Club"
                    && d.Hotbars[0].Slots[0].Slot == 2 && d.Hotbars[0].Slots[0].Durability == 50f && d.Hotbars[0].Slots[0].Equipped;
                if (!ok) Plugin.Log.LogError("Clipper self-check FAILED: a cut recording doesn't match its source");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Clipper self-check FAILED: {e}");
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch (IOException) { /* temp folder: Windows clears it eventually */ }
            }
        }
    }
}
