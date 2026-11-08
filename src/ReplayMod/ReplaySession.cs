using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ReplayMod
{
    // A replay runs in an offline, non-saving world built from the recorded seed. Recorded ZDOs are
    // injected with a fake owner, so every object runs Valheim's own remote-client code (interpolation,
    // animation from synced parameters, effects from RPCs) and nothing simulates on its own.
    internal static class ReplaySession
    {
        public const long FakeOwner = 0x4D554E494E4EL; // "MUNINN"
        public const string WorldName = "ReplayMod";

        public static bool Active { get; private set; }
        public static bool Ready { get; private set; }
        public static ReplayData Data { get; private set; }
        public static float T { get; private set; }
        public static float Speed = 1f;
        // While following the camera path its speed track sets the replay speed.
        public static bool PathSpeed => CameraPath.Follow && CameraPath.Keys.Count >= 2;
        public static float PlaySpeed => PathSpeed ? CameraPath.SpeedAt(T) : Speed;
        public static bool Paused;
        public static bool UseHiFi = true;
        public static bool FollowRecordedTime = true;
        public static float TimeOverride = 0.5f;
        public static bool FollowRecordedWeather = true;
        public static string WeatherOverride = "";
        public static bool WindOverride;
        public static float WindAngle, WindStrength = 0.5f;
        public static bool HidePlayers, HideHeld, HideProjectiles;
        private static (bool, bool, bool) _hideApplied;
        private static bool _thumbPending;
        private static float _envShownTime = -1f;
        private static string _envShownName;
        public static string LastError = "";
        public static int RpcsApplied, RpcErrors;

        private static SaveSystemSessionFlags _prevFlags;
        private static bool _baseApplied;
        private static int _cursor;
        private static HashSet<int> _whitelist;
        private static readonly HashSet<RecId> _alive = new HashSet<RecId>();
        private static readonly HashSet<RecId> _static = new HashSet<RecId>();
        private static readonly HashSet<RecId> _late = new HashSet<RecId>(); // scenery that first appeared after its zone came into range
        private const float LateAfter = 3f; // seconds: a zone's objects all arrive at about the same time
        private static readonly Dictionary<RecId, List<ReplayEvent>> _byId = new Dictionary<RecId, List<ReplayEvent>>();
        private static readonly HashSet<Vector2s> _zones = new HashSet<Vector2s>();
        // Objects a mod RPC was replayed on. Mods keep state (vehicle damage) in their components, not
        // the ZDO, so seeking back rebuilds these objects from scratch.
        private static readonly HashSet<RecId> _modTouched = new HashSet<RecId>();
        private static float _nextHide;
        private static float _savedLodBias;

        private static readonly FieldInfo SessionFlags = AccessTools.Field(typeof(SaveSystem), "s_sessionFlags");
        private static readonly FieldInfo StartingWorld = AccessTools.Field(typeof(FejdStartup), "m_startingWorld");
        private static readonly MethodInfo TransitionToMainScene = AccessTools.Method(typeof(FejdStartup), "TransitionToMainScene");
        private static readonly MethodInfo CreateNewZDO = AccessTools.Method(typeof(ZDOMan), "CreateNewZDO", new[] { typeof(ZDOID), typeof(Vector3), typeof(int) });
        private static readonly MethodInfo HandleDestroyedZDO = AccessTools.Method(typeof(ZDOMan), "HandleDestroyedZDO");
        private static readonly MethodInfo HandleRoutedRPC = AccessTools.Method(typeof(ZRoutedRpc), "HandleRoutedRPC");
        internal static readonly AccessTools.FieldRef<ZoneSystem, HashSet<Vector2s>> GeneratedZones =
            AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_generatedZones");
        internal static readonly AccessTools.FieldRef<Character, Rigidbody> Body = AccessTools.FieldRefAccess<Character, Rigidbody>("m_body");
        internal static readonly AccessTools.FieldRef<Character, CapsuleCollider> Collider = AccessTools.FieldRefAccess<Character, CapsuleCollider>("m_collider");

        public static float Duration => Data?.Duration ?? 0f;

        // Called from the main menu once a character is selected.
        public static void Start(string path)
        {
            LastError = "";
            try
            {
                Data = ReplayData.Load(path);
            }
            catch (Exception e)
            {
                LastError = $"Couldn't open replay: {e.Message}";
                Plugin.Log.LogError($"{LastError}\n{e}");
                return;
            }
            FejdStartup fs = FejdStartup.instance;
            if (fs == null) return;

            Index();
            ReplayHud.Reset();
            KeptAreas.Clear();
            CameraPath.Load(path);
            ReplayUI.NewReplay();
            _whitelist = Rpcs.ParseWhitelist(Rpcs.DefaultWhitelist + "," + Plugin.ExtraRpcs.Value);
            var world = new World(WorldName, Data.Header.SeedName)
            {
                m_seed = Data.Header.Seed,
                m_worldGenVersion = Data.Header.WorldGenVersion,
                // The game reuses a world's cached minimap only for the current world version, and a new
                // World has none, so it drew the minimap again (5-6 s) for every replay of the same seed.
                m_worldVersion = global::Version.World.CheatCleanSlate,
            };
            _prevFlags = (SaveSystemSessionFlags)SessionFlags.GetValue(null);
            SaveSystem.SetSessionFlags(SaveSystemSessionFlags.DontSaveCharacter | SaveSystemSessionFlags.DontSaveWorld);
            Active = true;
            Ready = false;
            _baseApplied = false;
            T = 0f;
            Speed = 1f;
            Paused = false;
            RpcsApplied = RpcErrors = 0;
            _thumbPending = true;
            Plugin.Log.LogInfo($"starting replay {path}: {Data.Events.Count} events, {Data.Duration:0.0} s, world '{Data.Header.WorldName}' seed '{Data.Header.SeedName}', {_zones.Count} recorded zones");
            ZNet.SetServer(server: true, openServer: false, publicServer: false, WorldName, "", world);
            ZNet.ResetServerHost();
            StartingWorld.SetValue(fs, true);
            TransitionToMainScene.Invoke(fs, null);
        }

        // Back at the main menu: undo everything.
        public static void End()
        {
            if (!Active) return;
            Plugin.Log.LogInfo("replay ended");
            SessionFlags.SetValue(null, _prevFlags);
            Active = false;
            Ready = false;
            Loading = false;
            KeptAreas.Clear();
            Data = null;
            _alive.Clear();
            _static.Clear();
            _late.Clear();
            _byId.Clear();
            _zones.Clear();
            _modTouched.Clear();
            _turretShots.Clear();
            _projectiles.Clear();
            _catapults.Clear();
            _basket.Clear();
            Hidden.Clear();
            HidePlayers = HideHeld = HideProjectiles = WindOverride = false;
            _hideApplied = default;
            FreeCam.Reset();
            _poseBones.Clear();
            _partMaps.Clear();
            _pitch.Clear();
            _sources.Clear();
            Time.timeScale = 1f;
            AudioListener.pause = false;
            if (_savedLodBias > 0f) QualitySettings.lodBias = _savedLodBias;
            _savedLodBias = 0f;
        }

        private static void Index()
        {
            _byId.Clear();
            _static.Clear();
            _late.Clear();
            _zones.Clear();
            // shortcut: a piece built in a zone with nothing else in it shows from the start; upgrade by recording when zones come into range.
            var zoneSeen = new Dictionary<Vector2s, float>();
            _catapults.Clear();
            foreach (ReplayEvent e in Data.Events)
            {
                if (e.Type == RecType.Rpc) IndexCatapultRpc(e);
                if (e.Type != RecType.Zdo && e.Type != RecType.Remove) continue;
                if (!_byId.TryGetValue(e.Id, out var list)) _byId[e.Id] = list = new List<ReplayEvent>();
                list.Add(e);
                // Scenery = recorded as static and persistent. The persistent bit (0x100) is in the
                // serialized flags; version-1 recordings filed effects as static, this re-files them.
                if (e.Type == RecType.Zdo && (e.Flags & ZdoFlags.Dynamic) == 0 && e.Data.Length >= 2 && (BitConverter.ToUInt16(e.Data, 0) & 0x100) != 0)
                {
                    _static.Add(e.Id);
                    Vector2s zone = ZoneSystem.GetZone(e.Pos);
                    _zones.Add(zone);
                    if (!zoneSeen.TryGetValue(zone, out float seen)) zoneSeen[zone] = seen = e.T;
                    if (list.Count == 1 && e.T > seen + LateAfter) _late.Add(e.Id);
                }
            }
        }

        // Replaces Game.FindSpawnPoint: put the hidden viewer at the recording's start once the area is ready.
        internal static bool FindSpawnPoint(out Vector3 point)
        {
            point = Data.Header.StartPos;
            if (!_baseApplied)
            {
                // Mark recorded zones as generated before they load, so the seed doesn't add trees on top of the recorded ones.
                HashSet<Vector2s> gen = GeneratedZones(ZoneSystem.instance);
                foreach (Vector2s z in _zones) gen.Add(z);
                // Far terrain is a low-detail mesh from the seed that never shows terraforming, so load
                // full terrain further out than usual (patched into GetSyncedSimulationDistance too).
                ZNet.instance.ApplySimulationDistance(ZNet.instance.GetSyncedSimulationDistance());
                _static.RemoveWhere(LivesInTime);
                IndexProjectiles();
                Seek(0f);
                _baseApplied = true;
            }
            ZNet.instance.SetReferencePosition(point);
            if (!ZNetScene.instance.IsAreaReady(point)) return false;
            if (ZoneSystem.instance.GetGroundHeight(point, out float h) && point.y < h) point.y = h;
            point.y += 0.25f;
            return true;
        }

        // Scenery shows from the start of the replay, so that what you passed later is there when you
        // look at it from afar. Not what was made during the recording (built, planted, grown): it
        // appears when it did. Terrain edits stay: taking one away doesn't put the ground back.
        // Recordings before 0.0.10 also filed corpses and tombstones as scenery, which only the loaded
        // prefabs can tell apart.
        private static bool LivesInTime(RecId id)
        {
            ReplayEvent first = _byId[id].Find(e => e.Type == RecType.Zdo);
            GameObject prefab = first != null ? ZNetScene.instance.GetPrefab(BitConverter.ToInt32(first.Data, 2)) : null;
            if (prefab && prefab.GetComponent<TerrainComp>()) return false;
            return _late.Contains(id) || (prefab && (prefab.GetComponent<Ragdoll>() || prefab.GetComponent<TombStone>()));
        }

        internal static void OnLocalPlayerSpawned(Player p)
        {
            SetupGhost(p);
            if (Hud.instance) Hud.instance.m_userHidden = true;
            Vector3 pos = Data.Header.StartPos + Vector3.up * 2f;
            Quaternion rot = p.transform.rotation;
            float fov = 65f;
            if (Data.Cams.Count > 0)
            {
                pos = Data.Cams[0].Pos;
                rot = Data.Cams[0].Rot;
                fov = Data.Cams[0].F;
            }
            FreeCam.Init(pos, rot, fov);
            Paused = true; // start paused: look around first
            // Draw small things (creatures) further out.
            if (Plugin.ReplayLodBias.Value > 0f && _savedLodBias <= 0f)
            {
                _savedLodBias = QualitySettings.lodBias;
                QualitySettings.lodBias = Mathf.Max(_savedLodBias, Plugin.ReplayLodBias.Value);
            }
            _settleFrames = SettleFrames;
            Ready = true;
            Plugin.Log.LogInfo($"replay ready: {_alive.Count} objects at t=0");
        }

        private static void SetupGhost(Player p)
        {
            p.SetGodMode(true);
            p.SetGhostMode(true);
            Rigidbody body = Body(p);
            if (body)
            {
                body.isKinematic = true;
                body.useGravity = false;
            }
            CapsuleCollider col = Collider(p);
            if (col) col.enabled = false;
            HideGhost(p);
        }

        private static void HideGhost(Player p)
        {
            foreach (Renderer r in p.GetComponentsInChildren<Renderer>(true)) r.forceRenderingOff = true;
        }

        public static void Update()
        {
            if (!Active || !Ready) return;
            Player p = Player.m_localPlayer;
            if (p == null || ZDOMan.instance == null) return;
            KeepClothHook();
            if (Time.unscaledTime >= _nextHide)
            {
                _nextHide = Time.unscaledTime + 1f; // equipment gets new renderers
                HideGhost(p);
            }
            ReplayUI.HandleKeys();
            if (_seekAfterJump)
            {
                _seekAfterJump = false;
                Seek(T);
            }
            if (!Paused && !Game.IsPaused() && !(Loading && Exporter.Busy && !Exporter.RecordingSound))
            {
                T += Time.deltaTime;
                if (T >= Duration)
                {
                    T = Duration;
                    Paused = true;
                }
            }
            Advance(T);
            CameraPath.UpdateVisuals();
            AudioListener.pause = Paused || Game.IsPaused();
            ApplyEnvironment();
            // Paused, the sky doesn't step by itself: show changes from camera keys at once.
            EnvMan envNow = EnvMan.instance;
            if (Paused && envNow && (envNow.m_debugTime != _envShownTime || envNow.m_debugEnv != _envShownName))
            {
                _envShownTime = envNow.m_debugTime;
                _envShownName = envNow.m_debugEnv;
                EnvironmentChanged();
            }
            ApplyVitals(p);
            ReplayHud.Update(p);
            UpdateLoading(p.transform.position);
            KeepZones();
            ApplyHidden();
            if (Paused)
            {
                // Objects keep being built while paused (see BuildWhilePaused), so settle every frame.
                Settle();
                if (_settleFrames > 0)
                {
                    _settleFrames--;
                    EnvironmentChanged(); // the sky needs a few frames after load or a seek while paused
                    if (_settleFrames == 0 && _thumbPending)
                    {
                        _thumbPending = false;
                        Plugin.Instance.StartCoroutine(Exporter.Thumbnail(Data.Path));
                    }
                }
            }
        }

        // While paused, physics and animators don't step (timeScale 0), so after a seek nothing would
        // move to its new state and new characters would stand in bind pose. For a few frames after a
        // seek, put each object at its recorded transform and evaluate animators once at zero time.
        private const int SettleFrames = 30;
        private static readonly AccessTools.FieldRef<BaseAI, bool> AlertedField = AccessTools.FieldRefAccess<BaseAI, bool>("m_alerted");
        private static readonly AccessTools.FieldRef<Catapult, float> ArmTime = AccessTools.FieldRefAccess<Catapult, float>("m_armAnimTime");
        private static readonly AccessTools.FieldRef<Catapult, Vector3> ArmRest = AccessTools.FieldRefAccess<Catapult, Vector3>("m_armRotation");
        private static readonly AccessTools.FieldRef<Catapult, bool> LegsMoving = AccessTools.FieldRefAccess<Catapult, bool>("m_movingLegs");
        private static readonly AccessTools.FieldRef<Catapult, Vector3[]> LegRest = AccessTools.FieldRefAccess<Catapult, Vector3[]>("m_legRotations");
        private static readonly AccessTools.FieldRef<Catapult, AnimationCurve> LegCurveUp = AccessTools.FieldRefAccess<Catapult, AnimationCurve>("m_legAnimationCurveUp");
        private static readonly AccessTools.FieldRef<Catapult, GameObject> BasketVisual = AccessTools.FieldRefAccess<Catapult, GameObject>("m_visualItem");
        private static readonly AccessTools.FieldRef<Catapult, ItemDrop.ItemData> BasketItem = AccessTools.FieldRefAccess<Catapult, ItemDrop.ItemData>("m_loadedItem");
        private static readonly MethodInfo LoadBasket = AccessTools.Method(typeof(Catapult), "RPC_SetLoadedVisual");
        private static readonly MethodInfo TrapUpdateState = AccessTools.Method(typeof(Trap), "UpdateState", Type.EmptyTypes);
        private static readonly Dictionary<Turret, (float value, float at)> _turretShots = new Dictionary<Turret, (float, float)>();

        internal static bool TurretCooling(Turret turret, ZDO z)
        {
            float shot = z.GetFloat(ZDOVars.s_lastAttack);
            // Already there when first seen: an old shot.
            if (!_turretShots.TryGetValue(turret, out var known)) known = _turretShots[turret] = (shot, float.NegativeInfinity);
            else if (known.value != shot) known = _turretShots[turret] = (shot, T);
            return T >= known.at && T - known.at < turret.m_attackCooldown;
        }

        // A catapult's swing, legs and basket exist only as RPCs, which a seek or a frame step skips, and
        // its animation only runs in the physics step, which stops while paused. So they're worked out
        // from the timeline every frame. The RPCs still play for their sounds and effects; this stops
        // the game's copy of a swing that a seek has left behind.
        private static void SettleCatapult(Catapult catapult)
        {
            ArmTime(catapult) = 0f;
            LegsMoving(catapult) = false;
        }

        internal class CatapultEvents
        {
            public readonly List<float> Shots = new List<float>();
            public readonly List<(float T, string Item)> Loads = new List<(float, string)>();
            public readonly List<(float T, bool Locked)> Legs = new List<(float, bool)>();
        }

        private static readonly Dictionary<RecId, CatapultEvents> _catapults = new Dictionary<RecId, CatapultEvents>();
        private static readonly Dictionary<Catapult, string> _basket = new Dictionary<Catapult, string>();
        private static readonly int ShootHash = "RPC_Shoot".GetStableHashCode(), LoadHash = "RPC_SetLoadedVisual".GetStableHashCode(), LegHash = "RPC_OnLegUse".GetStableHashCode();
        private const float DynamicsStep = 0.02f, SpringSettle = 3f; // the physics step the game animates at; how long a spring takes to rest

        private static void IndexCatapultRpc(ReplayEvent e)
        {
            // Three longs and a ZDOID come before the method's hash.
            if (e.Data.Length < 40) return;
            int hash = BitConverter.ToInt32(e.Data, 36);
            if (hash != ShootHash && hash != LoadHash && hash != LegHash) return;
            var data = new ZRoutedRpc.RoutedRPCData();
            data.Deserialize(new ZPackage(e.Data));
            var id = new RecId(data.m_targetZDO.UserID, data.m_targetZDO.ID);
            if (!_catapults.TryGetValue(id, out CatapultEvents events)) _catapults[id] = events = new CatapultEvents();
            if (hash == ShootHash) events.Shots.Add(e.T);
            else if (hash == LoadHash) events.Loads.Add((e.T, data.m_parameters.ReadString()));
            else events.Legs.Add((e.T, data.m_parameters.ReadBool()));
        }

        // What's in the basket at time t: the last thing loaded, until the shot after it lets go.
        internal static string BasketAt(CatapultEvents events, float t, float releaseAfter)
        {
            int load = events.Loads.FindLastIndex(l => l.T <= t);
            if (load < 0) return null;
            float loadedAt = events.Loads[load].T;
            foreach (float shot in events.Shots)
            {
                if (shot >= loadedAt && t >= shot + releaseAfter) return null;
            }
            return events.Loads[load].Item;
        }

        // The game's spring, run from the start of the move to now, so it comes out the same at any
        // playback speed or after any jump.
        private static float Sprung(Dynamics.DynamicsParameters settings, float start, float elapsed, Func<float, float> target)
        {
            var spring = new Dynamics.FloatDynamics(settings, start);
            int steps = (int)(elapsed / DynamicsStep);
            float before = start, now = start;
            for (int i = 0; i <= steps; i++)
            {
                before = now;
                now = spring.Update(DynamicsStep, target(i * DynamicsStep));
            }
            return Mathf.Lerp(before, now, elapsed / DynamicsStep - steps);
        }

        internal static void ApplyCatapults()
        {
            if (!Active || !Ready) return;
            foreach (var kv in _catapults)
            {
                ZNetView nv = FreeCam.ViewOf(kv.Key);
                Catapult c = nv ? nv.GetComponent<Catapult>() : null;
                if (!c || LegRest(c) == null) continue; // not there at this time, or its Start hasn't run
                CatapultEvents events = kv.Value;

                float armTime = c.m_armAnimationTime, atRest = c.m_armAnimation.Evaluate(0f), swing = atRest;
                int shot = events.Shots.FindLastIndex(s => s <= T);
                if (shot >= 0 && T - events.Shots[shot] < armTime + SpringSettle)
                {
                    swing = Sprung(c.m_armDynamicsSettings, atRest, T - events.Shots[shot], x => c.m_armAnimation.Evaluate(x < armTime ? x / armTime : 0f));
                }
                Vector3 arm = ArmRest(c);
                c.m_arm.transform.localEulerAngles = new Vector3(arm.x + swing * c.m_armAnimationDegrees, arm.y, arm.z);

                int leg = events.Legs.FindLastIndex(l => l.T <= T);
                bool locked = leg >= 0 ? events.Legs[leg].Locked : nv.GetZDO().GetBool(ZDOVars.s_locked);
                AnimationCurve curve = locked ? c.m_legAnimationCurve : LegCurveUp(c), previous = locked ? LegCurveUp(c) : c.m_legAnimationCurve;
                float legTime = c.m_legAnimationTime, legs = curve.Evaluate(1f);
                if (leg >= 0 && T - events.Legs[leg].T < legTime + SpringSettle)
                {
                    legs = Sprung(c.m_legDynamicsSettings, previous.Evaluate(1f), T - events.Legs[leg].T, x => curve.Evaluate(Mathf.Min(x / legTime, 1f)));
                }
                for (int i = 0; i < c.m_legs.Count; i++)
                {
                    Vector3 angles = LegRest(c)[i];
                    angles.z += legs * c.m_legAnimationDegrees;
                    c.m_legs[i].transform.localEulerAngles = angles;
                }

                string item = BasketAt(events, T, c.m_releaseAnimationTime * armTime);
                _basket.TryGetValue(c, out string shown);
                if (item == shown) continue;
                _basket[c] = item;
                if (BasketVisual(c)) UnityEngine.Object.Destroy(BasketVisual(c));
                BasketVisual(c) = null;
                BasketItem(c) = null;
                if (item != null) LoadBasket.Invoke(c, new object[] { 0L, item });
            }
        }

        // A projectile's own smoothing keeps it a good way behind where it was recorded, more the faster
        // it flies, and it landed well after its explosion. Its records are 20 a second, so it follows those.
        private static readonly Dictionary<RecId, List<ReplayEvent>> _projectiles = new Dictionary<RecId, List<ReplayEvent>>();

        private static void IndexProjectiles()
        {
            _projectiles.Clear();
            var isProjectile = new Dictionary<int, bool>();
            foreach (var kv in _byId)
            {
                ReplayEvent first = kv.Value.Find(e => e.Type == RecType.Zdo);
                if (first == null || first.Data.Length < 6) continue;
                int hash = BitConverter.ToInt32(first.Data, 2);
                if (!isProjectile.TryGetValue(hash, out bool flies))
                {
                    GameObject prefab = ZNetScene.instance.GetPrefab(hash);
                    isProjectile[hash] = flies = prefab && prefab.GetComponent<Projectile>();
                }
                if (flies) _projectiles[kv.Key] = kv.Value.FindAll(e => e.Type == RecType.Zdo);
            }
        }

        internal static void ApplyProjectiles()
        {
            if (!Active || !Ready) return;
            foreach (var kv in _projectiles)
            {
                List<ReplayEvent> s = kv.Value;
                if (T < s[0].T) continue;
                ZNetView nv = FreeCam.ViewOf(kv.Key);
                if (!nv) continue;
                int next = LowerBound(s, T);
                ReplayEvent a = s[next - 1], b = s[Mathf.Min(next, s.Count - 1)];
                nv.transform.position = Vector3.Lerp(a.Pos, b.Pos, b.T > a.T ? Mathf.Clamp01((T - a.T) / (b.T - a.T)) : 0f);
            }
        }

        public static void SelfCheck()
        {
            var events = new CatapultEvents();
            events.Loads.Add((1f, "a"));
            events.Shots.Add(2f);
            events.Loads.Add((5f, "b"));
            bool ok = BasketAt(events, 0.5f, 0.5f) == null && BasketAt(events, 1f, 0.5f) == "a" && BasketAt(events, 2.4f, 0.5f) == "a"
                && BasketAt(events, 2.5f, 0.5f) == null && BasketAt(events, 6f, 0.5f) == "b";
            if (!ok) Plugin.Log.LogError("catapult basket self-check FAILED");
            // Steady speed sampled unevenly stays on the line; a start from rest and the clock's daily wrap hold.
            Vector3 onLine = CurveThrough(Vector3.zero, 0f, Vector3.right * 3f, 0.1f, Vector3.right * 4.5f, 0.15f, Vector3.right * 9f, 0.3f, 0.5f);
            Vector3 fromRest = CurveThrough(Vector3.zero, 1f, Vector3.zero, 1f, Vector3.right, 2f, Vector3.right, 2f, 0.5f);
            var clock = new List<ReplayEvent> { new ReplayEvent { T = 1f, F = 86399.5f }, new ReplayEvent { T = 2f, F = 0.5f }, new ReplayEvent { T = 3f, F = 1.5f } };
            if ((onLine - Vector3.right * 3.75f).magnitude > 1e-3f || (fromRest - Vector3.right * 0.5f).magnitude > 1e-3f
                || !Mathf.Approximately(WaterTimeAt(clock, 2.5f), 1f) || !Mathf.Approximately(WaterTimeAt(clock, 4f), 2.5f) || !Mathf.Approximately(WaterTimeAt(clock, 0f), 86398.5f))
            {
                Plugin.Log.LogError("motion curve or wave clock self-check FAILED");
            }
        }
        private static int _seekFrame = -100;
        public static bool SnapBars => Active && Ready && (Paused || Time.frameCount - _seekFrame < 5);
        private static int _settleFrames;
        private static readonly MethodInfo SyncParameters = AccessTools.Method(typeof(ZSyncAnimation), "SyncParameters");
        private static readonly AccessTools.FieldRef<ZSyncAnimation, Animator> SyncAnimator = AccessTools.FieldRefAccess<ZSyncAnimation, Animator>("m_animator");

        private static void Settle()
        {
            foreach (RecId id in _alive)
            {
                if (_static.Contains(id)) continue;
                ZDO z = ZDOMan.instance.GetZDO(id.ToZdoId());
                ZNetView nv = z != null ? ZNetScene.instance.FindInstance(z) : null;
                if (!nv) continue;
                nv.transform.SetPositionAndRotation(z.GetPosition(), z.GetRotation());
                // Read from the ZDO only in the physics step. A boss's bar shows while it's alerted.
                BaseAI ai = nv.GetComponent<BaseAI>();
                if (ai) AlertedField(ai) = z.GetBool(ZDOVars.s_alert);
                if (_settleFrames > 0 && nv.TryGetComponent(out Catapult catapult)) SettleCatapult(catapult);
                ZSyncAnimation za = nv.GetComponent<ZSyncAnimation>();
                if (!za) continue;
                try
                {
                    SyncParameters.Invoke(za, new object[] { 0f, true });
                    Animator an = SyncAnimator(za);
                    if (an && an.isActiveAndEnabled) an.Update(0f);
                }
                catch (Exception) { /* half-built object; next frame */ }
            }
        }

        // Called by the menu when the time or weather override changes, so it shows at once even paused.
        public static void EnvironmentChanged()
        {
            EnvMan env = EnvMan.instance;
            if (!env) return;
            ApplyEnvironment();
            env.ForceInstantEnvironmentSwitch();
            EnvFixedUpdate.Invoke(env, null);
        }

        private static readonly MethodInfo EnvFixedUpdate = AccessTools.Method(typeof(EnvMan), "FixedUpdate");

        // Paused, particles don't step, so a flame left in the world stays where its object was before
        // the seek and jumps on unpausing. Once objects have their new pose, looping world-space effects
        // on moving objects restart from there. Two frames later, so a drag along the timeline runs this less.
        private static int _particlesIn;
        private static readonly List<ParticleSystem> _particles = new List<ParticleSystem>();

        internal static void ApplyParticles()
        {
            if (!Active || !Ready || _particlesIn <= 0 || --_particlesIn > 0) return;
            // Traps read their state when built and then wait for a message, which a seek skips.
            foreach (Trap trap in UnityEngine.Object.FindObjectsByType<Trap>(FindObjectsSortMode.None)) TrapUpdateState.Invoke(trap, null);
            if (!Paused) return;
            foreach (RecId id in _alive)
            {
                if (_static.Contains(id)) continue;
                ZDO z = ZDOMan.instance.GetZDO(id.ToZdoId());
                ZNetView nv = z != null ? ZNetScene.instance.FindInstance(z) : null;
                if (!nv) continue;
                nv.GetComponentsInChildren(_particles);
                foreach (ParticleSystem ps in _particles)
                {
                    ParticleSystem.MainModule main = ps.main;
                    if (!main.loop || !ps.isPlaying || main.simulationSpace == ParticleSystemSimulationSpace.Local) continue;
                    ps.Simulate(1f, false, true);
                    ps.Play(false); // Simulate leaves it paused
                }
            }
        }

        // The HUD reads the local player, which here is the hidden viewer: give it the recording
        // player's numbers. Food, hotbar and status icons aren't recorded, so they don't follow.
        private static readonly AccessTools.FieldRef<Player, float> Stamina = AccessTools.FieldRefAccess<Player, float>("m_stamina");
        private static readonly AccessTools.FieldRef<Player, float> MaxStamina = AccessTools.FieldRefAccess<Player, float>("m_maxStamina");
        private static readonly AccessTools.FieldRef<Player, float> Eitr = AccessTools.FieldRefAccess<Player, float>("m_eitr");
        private static readonly AccessTools.FieldRef<Player, float> MaxEitr = AccessTools.FieldRefAccess<Player, float>("m_maxEitr");

        // After the viewer jumps far (a teleport in the recording, a bookmark, a followed target), the
        // replay pauses behind the loading screen the way a portal waits, while zones and objects build:
        // the viewer counts as teleporting (the game then builds 100 objects a frame, not 10) and a zone
        // is built every frame. Before, the destination took half a minute to fill in, in slow motion longer.
        public static bool Loading { get; private set; }
        public static float LoadingSeconds => Time.unscaledTime - _loadingSince;
        private static float _loadingSince;
        private static bool _seekAfterJump;
        private const float FarJump = 300f;

        // Recorded samples this far apart are a teleport, not motion: they don't blend into each other.
        // Blending the camera across the gap a teleport leaves made it cross the world in steps, each
        // one far enough to load again.
        internal static bool Teleported(Vector3 a, Vector3 b) => (b - a).sqrMagnitude > 100f * 100f;

        // The terrain of the areas the viewer last jumped away from stays loaded, so skipping back across a
        // teleport only has to bring back the objects (100 a frame). Keeping their objects too, as 0.0.32 did,
        // left two areas' worth in the scene (35,000 objects, 10 fps).
        public static readonly List<Vector3> KeptAreas = new List<Vector3>();
        private const int MaxKeptAreas = 2;
        private static readonly MethodInfo PokeZone = AccessTools.Method(typeof(ZoneSystem), "PokeLocalZone");
        private static float _nextPoke;

        private static void KeepZones()
        {
            ZoneSystem zs = ZoneSystem.instance;
            if (KeptAreas.Count == 0 || !zs || PokeZone == null || Time.unscaledTime < _nextPoke) return;
            _nextPoke = Time.unscaledTime + 1f; // zones go 4 s after nothing pokes them
            int r = ZNet.instance.GetSyncedSimulationDistance().NearSimulationDistance;
            foreach (Vector3 kept in KeptAreas)
            {
                Vector2s center = ZoneSystem.GetZone(kept);
                for (int y = center.y - r; y <= center.y + r; y++)
                {
                    for (int x = center.x - r; x <= center.x + r; x++)
                    {
                        var zone = new Vector2s(x, y);
                        // Only zones still there: poking a missing one would build it.
                        if (zs.IsZoneLoaded(zone) && zs.ZonesWithinRadius(center, zone, r)) PokeZone.Invoke(zs, new object[] { zone });
                    }
                }
            }
        }

        internal static void ViewerMoved(Vector3 from, Vector3 to)
        {
            if (!Ready || (to - from).sqrMagnitude < FarJump * FarJump) return;
            KeptAreas.RemoveAll(k => (k - to).sqrMagnitude < FarJump * FarJump);
            KeptAreas.Add(from);
            if (KeptAreas.Count > MaxKeptAreas) KeptAreas.RemoveAt(0);
            if (Loading || ZNetScene.instance.IsAreaReady(to)) return; // nothing to wait for
            Plugin.Log.LogInfo($"viewer jumped {(to - from).magnitude:0} m: loading");
            // A load that started while playing left the frame rate in single digits for good; one that
            // started paused didn't (cause not found). So the replay pauses, except in an export, where
            // time only holds (pausing would stop the export).
            if (!Exporter.Busy) Paused = true;
            // Then the scene is applied again as if the timeline had been clicked here while paused, which
            // never left the frame rate low; arriving by playing through the teleport did.
            _seekAfterJump = true;
            Loading = true;
            _loadingSince = Time.unscaledTime;
        }

        private static void UpdateLoading(Vector3 at)
        {
            if (!Loading) return;
            bool ready = ZoneSystem.instance && ZoneSystem.instance.IsActiveAreaLoaded() && ZNetScene.instance.IsAreaReady(at);
            // A portal gives up after 15 s too.
            if (!ready && Time.unscaledTime - _loadingSince < 15f) return;
            Loading = false;
            // A portal switches the weather at once on arrival; without it an export showed the old
            // area's weather fading out slowly.
            EnvironmentChanged();
            Plugin.Log.LogInfo($"loaded in {Time.unscaledTime - _loadingSince:0.0} s{(ready ? "" : " (gave up waiting)")}");
        }

        // The recording player at the replay's time: respawning made a new player object.
        public static RecId RecordedId()
        {
            int i = LowerBound(Data.Selves, T) - 1;
            return i >= 0 ? Data.Selves[i].Id : new RecId(Data.Header.PlayerUser, Data.Header.PlayerId);
        }

        public static Player RecordedPlayer()
        {
            ZDO z = ZDOMan.instance.GetZDO(RecordedId().ToZdoId());
            ZNetView nv = z != null ? ZNetScene.instance.FindInstance(z) : null;
            return nv ? nv.GetComponent<Player>() : null;
        }

        private static void ApplyVitals(Player ghost)
        {
            ZDO rec = ZDOMan.instance.GetZDO(RecordedId().ToZdoId());
            ZDO own = ghost.GetComponent<ZNetView>()?.GetZDO();
            if (own == null) return;
            if (rec == null)
            {
                own.Set(ZDOVars.s_health, 0.01f); // dead, waiting to respawn
                return;
            }
            float maxHealth = rec.GetFloat(ZDOVars.s_maxHealth, own.GetFloat(ZDOVars.s_maxHealth, 25f));
            own.Set(ZDOVars.s_maxHealth, maxHealth);
            own.Set(ZDOVars.s_health, Mathf.Max(rec.GetFloat(ZDOVars.s_health, maxHealth), 0.01f)); // the ghost itself must not count as dead
            if (Data.Vitals.Count > 0)
            {
                ReplayEvent v = Data.Vitals[Mathf.Clamp(LowerBound(Data.Vitals, T) - 1, 0, Data.Vitals.Count - 1)];
                MaxStamina(ghost) = v.F;
                MaxEitr(ghost) = v.Pos.x;
            }
            Stamina(ghost) = rec.GetFloat(ZDOVars.s_stamina, MaxStamina(ghost));
            Eitr(ghost) = rec.GetFloat(ZDOVars.s_eitr, MaxEitr(ghost));
            // Older recordings have no maximums: at least keep the bar from overflowing.
            MaxStamina(ghost) = Mathf.Max(MaxStamina(ghost), Stamina(ghost));
            MaxEitr(ghost) = Mathf.Max(MaxEitr(ghost), Eitr(ghost));
        }

        // Objects hidden from the shot (U). Rechecked each frame: a seek rebuilds objects and equipment changes renderers.
        public static readonly HashSet<RecId> Hidden = new HashSet<RecId>();

        private static void ApplyHidden()
        {
            ApplyHiddenKinds();
            foreach (RecId id in Hidden) SetRendered(id, false);
        }

        private static readonly AccessTools.FieldRef<VisEquipment, GameObject> LeftItem = AccessTools.FieldRefAccess<VisEquipment, GameObject>("m_leftItemInstance");
        private static readonly AccessTools.FieldRef<VisEquipment, GameObject> RightItem = AccessTools.FieldRefAccess<VisEquipment, GameObject>("m_rightItemInstance");

        private static void Render(GameObject go, bool on)
        {
            if (!go) return;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true)) r.forceRenderingOff = !on;
        }

        // Whole kinds of things hidden from the shot. Also runs once after a switch goes off, to bring them back.
        private static void ApplyHiddenKinds()
        {
            var now = (HidePlayers, HideHeld, HideProjectiles);
            bool changed = now != _hideApplied;
            if (!changed && now == default) return;
            _hideApplied = now;
            if (changed || HidePlayers || HideHeld)
            {
                foreach (Player pl in Player.GetAllPlayers())
                {
                    if (pl == Player.m_localPlayer) continue;
                    Render(pl.gameObject, !HidePlayers);
                    VisEquipment vis = pl.GetComponent<VisEquipment>();
                    if (!vis || HidePlayers || !HideHeld) continue;
                    Render(LeftItem(vis), false);
                    Render(RightItem(vis), false);
                }
            }
            if (changed || HideProjectiles)
            {
                foreach (Projectile p in UnityEngine.Object.FindObjectsByType<Projectile>(FindObjectsSortMode.None)) Render(p.gameObject, !HideProjectiles);
            }
        }

        // Where a recorded object was at time t (its first record before that), between its recorded samples.
        public static bool RecordedPose(RecId id, float t, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            if (!_byId.TryGetValue(id, out var list)) return false;
            ReplayEvent a = null, b = null;
            foreach (ReplayEvent e in list)
            {
                if (e.Type != RecType.Zdo) continue;
                if (e.T <= t || a == null) a = e;
                else
                {
                    b = e;
                    break;
                }
            }
            if (a == null) return false;
            float u = b != null && b.T > a.T ? Mathf.Clamp01((t - a.T) / (b.T - a.T)) : 0f;
            if (b != null && Teleported(a.Pos, b.Pos)) u = t >= b.T ? 1f : 0f;
            pos = b != null ? Vector3.Lerp(a.Pos, b.Pos, u) : a.Pos;
            rot = b != null ? Quaternion.Slerp(RecordedRotation(a), RecordedRotation(b), u) : RecordedRotation(a);
            return true;
        }

        // ZDO.Serialize: flags, prefab, then the rotation as Euler angles if the flag says it's there.
        private static Quaternion RecordedRotation(ReplayEvent e)
        {
            if (e.Data.Length < 18 || (BitConverter.ToUInt16(e.Data, 0) & 0x1000) == 0) return Quaternion.identity;
            return Quaternion.Euler(BitConverter.ToSingle(e.Data, 6), BitConverter.ToSingle(e.Data, 10), BitConverter.ToSingle(e.Data, 14));
        }

        private static void SetRendered(RecId id, bool on)
        {
            ZDO z = ZDOMan.instance.GetZDO(id.ToZdoId());
            ZNetView nv = z != null ? ZNetScene.instance.FindInstance(z) : null;
            if (!nv) return;
            foreach (Renderer r in nv.GetComponentsInChildren<Renderer>(true)) r.forceRenderingOff = !on;
        }

        public static void ShowAllHidden() => RestoreHidden(new HashSet<RecId>());

        // For undo and redo.
        public static void RestoreHidden(HashSet<RecId> hidden)
        {
            foreach (RecId id in Hidden)
            {
                if (!hidden.Contains(id)) SetRendered(id, true);
            }
            Hidden.Clear();
            Hidden.UnionWith(hidden);
        }

        private static void Advance(float t)
        {
            var events = Data.Events;
            while (_cursor < events.Count && events[_cursor].T <= t)
            {
                ReplayEvent e = events[_cursor++];
                switch (e.Type)
                {
                    case RecType.Zdo:
                        ApplyZdo(e);
                        CreateNow(e);
                        break;
                    case RecType.Remove: Destroy(e.Id); break;
                    case RecType.Rpc: ApplyRpc(e); break;
                }
            }
        }

        // Rebuilds the scene at time t: each object takes its latest record at or before t.
        // Scenery first recorded later is shown from the start (see LivesInTime).
        public static void Seek(float t)
        {
            t = Mathf.Clamp(t, 0f, Duration);
            if (t < T)
            {
                foreach (RecId id in _modTouched) Destroy(id);
                _modTouched.Clear();
            }
            T = t;
            foreach (var kv in _byId)
            {
                List<ReplayEvent> list = kv.Value;
                ReplayEvent last = null;
                for (int i = 0; i < list.Count && list[i].T <= t; i++) last = list[i];
                if (last == null && _static.Contains(kv.Key)) last = list[0];
                if (last != null && last.Type == RecType.Zdo) ApplyZdo(last);
                else if (_alive.Contains(kv.Key)) Destroy(kv.Key);
            }
            _cursor = LowerBound(Data.Events, t);
            _settleFrames = SettleFrames;
            _particlesIn = 2;
            _seekFrame = Time.frameCount;
            if (Paused && Ready) EnvironmentChanged();
        }

        private static int LowerBound(List<ReplayEvent> list, float t)
        {
            int lo = 0, hi = list.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (list[mid].T <= t) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private static void ApplyZdo(ReplayEvent e)
        {
            ZDOMan man = ZDOMan.instance;
            ZDOID id = e.Id.ToZdoId();
            ZDO z = man.GetZDO(id);
            if (z == null) z = (ZDO)CreateNewZDO.Invoke(man, new object[] { id, e.Pos, 0 });
            // Deserialize only adds values, so going back in time kept later ones (a boss's lost health).
            // Each record holds the whole state. This also clears the owner, which is set next.
            ZDOExtraData.Release(z, id);
            z.SetOwnerInternal(FakeOwner);
            z.InternalSetPosition(e.Pos);
            z.Deserialize(new ZPackage(e.Data));
            // ZSyncTransform restarts its interpolation and TerrainComp rebuilds only when the revision moves.
            z.DataRevision++;
            _alive.Add(e.Id);
        }

        // The game makes new objects on a 30 Hz timer, a few at a time and nearest first, and not at all
        // while the area around the viewer loads. An explosion has to be there on its own frame.
        private static readonly MethodInfo CreateObject = AccessTools.Method(typeof(ZNetScene), "CreateObject");

        private static void CreateNow(ReplayEvent e)
        {
            // Effects and projectiles: moving, and not saved with the world (the persistent bit is 0x100).
            if ((e.Flags & ZdoFlags.Dynamic) == 0 || (BitConverter.ToUInt16(e.Data, 0) & 0x100) != 0) return;
            ZDO z = ZDOMan.instance.GetZDO(e.Id.ToZdoId());
            if (z == null || z.Created || !ZNetScene.InActiveArea(e.Pos, ZNet.instance.GetReferencePosition())) return;
            CreateObject.Invoke(ZNetScene.instance, new object[] { z });
        }

        private static void Destroy(RecId id)
        {
            if (!_alive.Remove(id)) return;
            HandleDestroyedZDO.Invoke(ZDOMan.instance, new object[] { id.ToZdoId() });
        }

        private static void ApplyRpc(ReplayEvent e)
        {
            var data = new ZRoutedRpc.RoutedRPCData();
            data.Deserialize(new ZPackage(e.Data));
            // Vanilla RPCs only from the visual-only list. Mod RPCs replay when they were broadcast to
            // everybody at a recorded object (weapons, launches, vehicle damage): what every player sees.
            // RPCs sent to one player are skipped: a mod's answer to a seat request would seat the viewer.
            bool modRpc = Plugin.ReplayModRpcs.Value && !data.m_targetZDO.IsNone() && data.m_targetPeerID == ZRoutedRpc.Everybody
                && !Rpcs.IsVanilla(data.m_methodHash);
            if (!modRpc && !_whitelist.Contains(data.m_methodHash)) return;
            data.m_parameters.SetPos(0);
            try
            {
                HandleRoutedRPC.Invoke(ZRoutedRpc.instance, new object[] { data });
                RpcsApplied++;
                if (modRpc) _modTouched.Add(new RecId(data.m_targetZDO.UserID, data.m_targetZDO.ID));
            }
            catch (Exception ex)
            {
                if (RpcErrors++ < 20) Plugin.Log.LogWarning($"replayed {Rpcs.Name(data.m_methodHash)} threw: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        private static void ApplyEnvironment()
        {
            EnvMan env = EnvMan.instance;
            if (!env) return;
            env.m_debugTimeOfDay = true;
            ReplayEvent rec = SampleEnv(T);
            env.m_debugTime = CameraPath.DayAt(T, out float keyDay) ? keyDay : FollowRecordedTime && rec != null ? rec.F : TimeOverride;
            env.m_debugEnv = CameraPath.WeatherAt(T) ?? (FollowRecordedWeather ? rec?.S ?? "" : WeatherOverride);
            if (WindOverride) env.SetDebugWind(WindAngle, WindStrength);
            else if (Data.Winds.Count > 0) ApplyRecordedWind(env);
            else env.ResetDebugWind();
        }

        private static readonly AccessTools.FieldRef<EnvMan, Vector4> WindFrom = AccessTools.FieldRefAccess<EnvMan, Vector4>("m_windDir1");
        private static readonly AccessTools.FieldRef<EnvMan, Vector4> WindTo = AccessTools.FieldRefAccess<EnvMan, Vector4>("m_windDir2");
        private static readonly AccessTools.FieldRef<EnvMan, Vector4> WindNow = AccessTools.FieldRefAccess<EnvMan, Vector4>("m_wind");
        private static readonly AccessTools.FieldRef<EnvMan, float> WindTimer = AccessTools.FieldRefAccess<EnvMan, float>("m_windTransitionTimer");

        // Set directly: the game's own five-second change between targets would lag, and be wrong after a seek.
        private static void ApplyRecordedWind(EnvMan env)
        {
            var winds = Data.Winds;
            int next = LowerBound(winds, T);
            ReplayEvent a = winds[Mathf.Clamp(next - 1, 0, winds.Count - 1)], b = winds[Mathf.Min(next, winds.Count - 1)];
            float u = b.T > a.T ? Mathf.Clamp01((T - a.T) / (b.T - a.T)) : 0f;
            float angle = Mathf.LerpAngle(a.Pos.x, b.Pos.x, u), intensity = Mathf.Lerp(a.F, b.F, u);
            env.SetDebugWind(angle, intensity);
            float rad = angle * Mathf.Deg2Rad;
            var wind = new Vector4(Mathf.Sin(rad), 0f, Mathf.Cos(rad), Mathf.Clamp(intensity, 0.05f, 1f));
            WindFrom(env) = WindTo(env) = WindNow(env) = wind;
            WindTimer(env) = -1f;
        }

        public static ReplayEvent SampleEnv(float t)
        {
            var envs = Data.Envs;
            if (envs.Count == 0) return null;
            int i = Mathf.Clamp(LowerBound(envs, t) - 1, 0, envs.Count - 1);
            return envs[i];
        }

        public static bool SampleCamera(float t, out Vector3 pos, out Quaternion rot, out float fov)
        {
            pos = default;
            rot = Quaternion.identity;
            fov = 65f;
            var cams = Data?.Cams;
            if (cams == null || cams.Count == 0) return false;
            int i = LowerBound(cams, t);
            ReplayEvent b = cams[Mathf.Clamp(i, 0, cams.Count - 1)];
            ReplayEvent a = cams[Mathf.Clamp(i - 1, 0, cams.Count - 1)];
            float u = b.T > a.T ? Mathf.Clamp01((t - a.T) / (b.T - a.T)) : 0f;
            if (Teleported(a.Pos, b.Pos)) u = t >= b.T ? 1f : 0f;
            pos = Vector3.Lerp(a.Pos, b.Pos, u);
            rot = Quaternion.Slerp(a.Rot, b.Rot, u);
            fov = Mathf.Lerp(a.F, b.F, u);
            return true;
        }

        // The game's wave clock at the replay's time, so waves stand where they did under a recorded ship.
        // Waves are computed from the world's clock, and the replay world's clock is another one.
        internal static bool RecordedWaterTime(out float time)
        {
            time = 0f;
            if (!Active || !Ready || Data.Waters.Count == 0) return false;
            time = WaterTimeAt(Data.Waters, T);
            return true;
        }

        internal static float WaterTimeAt(List<ReplayEvent> waters, float t)
        {
            int next = LowerBound(waters, t);
            ReplayEvent a = waters[Mathf.Clamp(next - 1, 0, waters.Count - 1)], b = waters[Mathf.Min(next, waters.Count - 1)];
            // At the daily wrap the two aren't comparable, and past either end there's only one.
            return b.T > a.T && b.F >= a.F ? Mathf.Lerp(a.F, b.F, Mathf.Clamp01((t - a.T) / (b.T - a.T))) : a.F + t - a.T;
        }

        // The recording player's hotbar at the replay's time, as an inventory the hotbar can draw.
        // Empty for recordings made before 0.0.28.
        private static Inventory _hotbar;
        private static HotbarSample _hotbarShown;

        internal static Inventory HotbarAt()
        {
            if (_hotbar == null) _hotbar = new Inventory("ReplayHotbar", null, 8, 1);
            List<HotbarSample> list = Data.Hotbars;
            int lo = 0, hi = list.Count;
            while (lo < hi) { int mid = (lo + hi) / 2; if (list[mid].T <= T) lo = mid + 1; else hi = mid; }
            HotbarSample s = lo > 0 ? list[lo - 1] : null;
            if (s == _hotbarShown && _hotbarShown != null) return _hotbar;
            _hotbarShown = s;
            List<ItemDrop.ItemData> items = _hotbar.GetAllItems();
            items.Clear(); // the list itself, so no inventory-changed callbacks run
            foreach (HotbarSlot slot in s?.Slots ?? new HotbarSlot[0])
            {
                GameObject prefab = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(slot.Prefab) : null;
                ItemDrop drop = prefab ? prefab.GetComponent<ItemDrop>() : null;
                if (!drop) continue; // an item from a mod that isn't installed now
                ItemDrop.ItemData item = drop.m_itemData.Clone();
                item.m_dropPrefab = prefab;
                item.m_stack = slot.Stack;
                item.m_quality = slot.Quality;
                item.m_variant = slot.Variant;
                item.m_durability = slot.Durability;
                item.m_equipped = slot.Equipped;
                item.m_gridPos = new Vector2i(slot.Slot, 0);
                items.Add(item);
            }
            return _hotbar;
        }

        private static bool _clothHookFailed;

        private static void KeepClothHook()
        {
            if (_clothHookFailed) return;
            try { ReplayPatches.BeforeCloth.Keep(); }
            catch (Exception e)
            {
                // The LateUpdate pass still poses the body; capes just follow the animator's pose.
                _clothHookFailed = true;
                Plugin.Log.LogWarning($"cloth hook failed: {e.Message}");
            }
        }

        // High-rate lane: drive characters and other moving objects along a curve through their recorded
        // positions, after ZSyncTransform has moved them (postfix on MonoUpdaters.LateUpdate), and before
        // the cloth simulates (BeforeCloth).
        internal static void ApplyHiFi()
        {
            if (!Active || !Ready || !UseHiFi || Data.HiFi.Count == 0) return;
            float t = T;
            foreach (var kv in Data.HiFi)
            {
                List<ReplayEvent> s = kv.Value;
                if (s.Count < 2 || t < s[0].T || t > s[s.Count - 1].T) continue;
                ZDO z = ZDOMan.instance.GetZDO(kv.Key.ToZdoId());
                if (z == null) continue;
                ZNetView nv = ZNetScene.instance.FindInstance(z);
                if (!nv) continue;
                int i = Mathf.Clamp(LowerBound(s, t), 1, s.Count - 1);
                ReplayEvent p1 = s[i - 1], p2 = s[i];
                if (p2.T - p1.T > 0.25f || Teleported(p1.Pos, p2.Pos)) continue; // a gap or a teleport: ZSyncTransform places it
                ReplayEvent p0 = s[Mathf.Max(i - 2, 0)], p3 = s[Mathf.Min(i + 1, s.Count - 1)];
                // A neighbor across a gap or a teleport says nothing about the speed here.
                if (p1.T - p0.T > 0.25f || Teleported(p0.Pos, p1.Pos)) p0 = p1;
                if (p3.T - p2.T > 0.25f || Teleported(p2.Pos, p3.Pos)) p3 = p2;
                float u = Mathf.Clamp01((t - p1.T) / Mathf.Max(p2.T - p1.T, 1e-4f));
                Vector3 pos = CurveThrough(p0.Pos, p0.T, p1.Pos, p1.T, p2.Pos, p2.T, p3.Pos, p3.T, u);
                nv.transform.SetPositionAndRotation(pos, Quaternion.Slerp(p1.Rot, p2.Rot, u));
            }
        }

        // Exact pose lane: put the recorded final bone pose on the recording player's puppet after its
        // animator (which only knows the vanilla state). Called from LateUpdate: skinning runs right after
        // it, so onPreCull was too late — the body skinned with the vanilla pose while the rigid helmet
        // followed ours.
        public static bool UsePose = true;
        private static readonly Dictionary<RecId, (SkinnedMeshRenderer body, string[] names, Transform[] map)> _poseBones =
            new Dictionary<RecId, (SkinnedMeshRenderer, string[], Transform[])>();

        internal static void ApplyPose()
        {
            if (!Active || !Ready || !UsePose || Data.Poses.Count == 0) return;
            float t = T;
            foreach (var kv in Data.Poses)
            {
                List<PoseSample> s = kv.Value;
                if (s.Count == 0 || t < s[0].T || t > s[s.Count - 1].T) continue;
                int i = Mathf.Clamp(LowerBoundPose(s, t), 1, Mathf.Max(s.Count - 1, 1));
                PoseSample a = s[Mathf.Max(i - 1, 0)], b = s[Mathf.Min(i, s.Count - 1)];
                if (b.T - a.T > 0.5f || a.Names != b.Names) b = a; // gap or new skeleton: hold
                float u = b.T > a.T ? Mathf.Clamp01((t - a.T) / (b.T - a.T)) : 0f;

                ZDO z = ZDOMan.instance.GetZDO(kv.Key.ToZdoId());
                ZNetView nv = z != null ? ZNetScene.instance.FindInstance(z) : null;
                SkinnedMeshRenderer body = nv ? nv.GetComponent<VisEquipment>()?.m_bodyModel : null;
                if (!body) continue;
                if (!_poseBones.TryGetValue(kv.Key, out var m) || m.body != body || m.names != a.Names)
                {
                    var byName = new Dictionary<string, Transform>();
                    foreach (Transform bone in body.bones)
                    {
                        if (bone && !byName.ContainsKey(bone.name)) byName[bone.name] = bone;
                    }
                    var map = new Transform[a.Names.Length];
                    for (int k = 0; k < map.Length; k++) byName.TryGetValue(a.Names[k], out map[k]);
                    _poseBones[kv.Key] = m = (body, a.Names, map);
                }
                for (int k = 0; k < m.map.Length; k++)
                {
                    Transform bone = m.map[k];
                    if (!bone) continue;
                    bone.localRotation = Quaternion.Slerp(a.Rot[k], b.Rot[k], u);
                    if (bone.name == "Hips") bone.localPosition = Vector3.Lerp(a.Hips, b.Hips, u);
                }
            }
        }

        // Vehicle parts lane: put recorded local transforms back on the vehicle's parts (after its own
        // scripts animated them from synced values).
        private static readonly Dictionary<RecId, (ZNetView nv, Transform[] map)> _partMaps = new Dictionary<RecId, (ZNetView, Transform[])>();

        internal static void ApplyParts()
        {
            if (!Active || !Ready || !UsePose || Data.Parts.Count == 0) return;
            float t = T;
            foreach (var kv in Data.Parts)
            {
                ZDO z = ZDOMan.instance.GetZDO(kv.Key.ToZdoId());
                ZNetView nv = z != null ? ZNetScene.instance.FindInstance(z) : null;
                if (!nv) continue;
                PartsTrack track = kv.Value;
                if (!_partMaps.TryGetValue(kv.Key, out var m) || m.nv != nv)
                {
                    var byPath = new Dictionary<string, Transform>();
                    foreach (Transform tr in nv.GetComponentsInChildren<Transform>(true))
                    {
                        string path = RelativePath(tr, nv.transform);
                        if (!byPath.ContainsKey(path)) byPath[path] = tr;
                    }
                    var map = new Transform[track.Paths.Length];
                    Ship ship = nv.GetComponent<Ship>();
                    for (int i = 0; i < map.Length; i++)
                    {
                        // Recordings before 0.0.18 have the sail's parts.
                        if (byPath.TryGetValue(track.Paths[i], out map[i]) && ship && Recorder.SailPart(ship, map[i])) map[i] = null;
                    }
                    _partMaps[kv.Key] = m = (nv, map);
                }
                for (int i = 1; i < m.map.Length; i++)
                {
                    List<PartSample> s = track.Samples[i];
                    Transform tr = m.map[i];
                    if (s == null || !tr || t < s[0].T) continue;
                    int lo = 0, hi = s.Count;
                    while (lo < hi) { int mid = (lo + hi) / 2; if (s[mid].T <= t) lo = mid + 1; else hi = mid; }
                    PartSample a = s[Mathf.Max(lo - 1, 0)], b = s[Mathf.Min(lo, s.Count - 1)];
                    float u = b.T > a.T ? Mathf.Clamp01((t - a.T) / (b.T - a.T)) : 0f;
                    tr.localPosition = Vector3.Lerp(a.Pos, b.Pos, u);
                    tr.localRotation = Quaternion.Slerp(a.Rot, b.Rot, u);
                }
            }
        }

        // Every sound follows replay speed. Games and mods set pitch themselves (ZSFX every frame,
        // engine loops from rpm), so keep the pitch they last set and write base * speed on top.
        private static readonly Dictionary<AudioSource, (float basePitch, float written)> _pitch = new Dictionary<AudioSource, (float, float)>();
        private static readonly HashSet<AudioSource> _sources = new HashSet<AudioSource>();
        private static readonly List<AudioSource> _scan = new List<AudioSource>();

        // A sound that starts playing gets the replay pitch at once. Before, new sounds played at full
        // pitch until the next scan (up to half a second), which made slow motion stutter.
        internal static void OnSoundStarted(AudioSource src)
        {
            if (!Active || !Ready || !src) return;
            _sources.Add(src);
            PitchOne(src, SoundSpeed());
        }

        // Below MinSoundPitch, sounds would turn into a low rumble: keep them a little faster than the picture.
        private static float SoundSpeed() => Mathf.Max(PlaySpeed, Mathf.Min(Plugin.MinSoundPitch.Value, 1f));

        private static void PitchOne(AudioSource src, float speed)
        {
            if (src.ignoreListenerPause) return; // our own UI beep
            string group = src.outputAudioMixerGroup ? src.outputAudioMixerGroup.name : "";
            if (group == "Music" || group == "GUI") return;
            if (!_pitch.TryGetValue(src, out var p) || !Mathf.Approximately(src.pitch, p.written)) p.basePitch = src.pitch;
            p.written = p.basePitch * speed;
            src.pitch = p.written;
            _pitch[src] = p;
        }
        private static float _nextAudioScan;
        private static readonly List<AudioSource> _deadSources = new List<AudioSource>();

        internal static void ApplyPitch()
        {
            if (!Active || !Ready) return;
            if (Time.unscaledTime >= _nextAudioScan)
            {
                _nextAudioScan = Time.unscaledTime + 0.5f;
                _sources.Clear();
                foreach (AudioSource s in UnityEngine.Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None)) _sources.Add(s);
                _deadSources.Clear();
                foreach (AudioSource s in _pitch.Keys) if (!s) _deadSources.Add(s);
                foreach (AudioSource s in _deadSources) _pitch.Remove(s);
            }
            float speed = SoundSpeed();
            _scan.Clear();
            _scan.AddRange(_sources);
            foreach (AudioSource src in _scan)
            {
                if (src) PitchOne(src, speed);
            }
        }

        private static string RelativePath(Transform tr, Transform root)
        {
            if (tr == root) return "";
            string path = tr.name;
            for (Transform p = tr.parent; p != null && p != root; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        private static int LowerBoundPose(List<PoseSample> list, float t)
        {
            int lo = 0, hi = list.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (list[mid].T <= t) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        // Hermite with Catmull-Rom tangents worked out from the real times: samples taken once a frame aren't evenly spaced.
        internal static Vector3 CurveThrough(Vector3 p0, float t0, Vector3 p1, float t1, Vector3 p2, float t2, Vector3 p3, float t3, float u)
        {
            float dt = t2 - t1;
            Vector3 m1 = (t2 > t0 ? (p2 - p0) / (t2 - t0) : Vector3.zero) * dt, m2 = (t3 > t1 ? (p3 - p1) / (t3 - t1) : Vector3.zero) * dt;
            float u2 = u * u, u3 = u2 * u;
            return (2f * u3 - 3f * u2 + 1f) * p1 + (u3 - 2f * u2 + u) * m1 + (-2f * u3 + 3f * u2) * p2 + (u3 - u2) * m2;
        }

        public static int AliveCount => _alive.Count;
    }
}
