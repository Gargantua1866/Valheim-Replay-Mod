using HarmonyLib;
using System.Collections.Generic;
using UnityStandardAssets.ImageEffects;
using UnityEngine;

namespace ReplayMod
{
    // Six-degree-of-freedom replay camera. Runs on unscaled time, so it flies at full speed
    // while the replay is slowed down or frozen.
    internal static class FreeCam
    {
        public enum AxisLock { None, X, Y, Z }

        public static Vector3 Pos;
        public static float Yaw, Pitch, Roll, Fov = 65f;
        public static float MoveSpeed = 8f;
        public static float Smoothing = 0.3f; // 0 = rigid, 1 = long glide
        public static float AimSmoothing;     // the same for mouse look; 0 = direct
        private static Vector2 _aimRate;      // smoothed mouse movement, per second
        public static float SensYaw = 1f, SensPitch = 1f, RollSpeed = 45f, FovSpeed = 30f;
        public static bool InvertY;
        public static bool LockYaw, LockPitch, LockRoll, LockHeight;
        public static AxisLock MoveAxis = AxisLock.None;
        public static bool UseOriginal;

        // Attaching to a recorded object. Follow keeps the camera's offset from it in world space (the
        // view turns freely); Mount keeps offset and rotation in the object's own frame, as if bolted on.
        public enum AttachMode { None, Follow, Mount, Pov, Orbit }
        public static AttachMode Attach = AttachMode.None;
        public static string AttachName = "";
        private static RecId _target;
        private static Vector3 _offset;       // Follow: world offset. Mount: offset in the target's frame.
        private static float _localYaw, _localPitch, _localRoll; // Mount and POV: angles in the target's frame.
        private static Vector3 _anchor;       // Follow and Orbit: where the camera thinks the target is (FollowLag).
        public static float FollowLag;        // seconds; 0 = rigid
        public static float OrbitSpeed = 20f; // degrees per second
        public static float LookSmoothing = 0.25f; // seconds; aiming at a target follows it this softly
        private static Vector3 _lookPoint;
        private static bool _lookSettled;
        public static List<CameraPath.Key> Take; // samples of a hand-flown take in progress
        public static bool DofOn, DofAuto = true;
        public static float DofDistance = 10f, DofAperture = 0.5f;
        public const float DofMaxAperture = 0.95f; // the effect divides by 1 - aperture: at 1 and above it stops working
        public static bool FlashOn, FlashPreview;
        public static float FlashStrength = 2f, FlashRange = 25f;
        private static Light _flash;
        public static float IgnoredInputAt = -10f; // when flying inputs last arrived while the path or the original camera had the view
        private static float _nextTakeSample;
        private static bool _lookOn;
        private static RecId _lookTarget;
        public static string LookName => _lookOn ? _lookLabel : null;
        private static string _lookLabel;
        private static bool _hasOffset;

        private static Vector3 _vel;
        private static readonly AccessTools.FieldRef<GameCamera, Camera> CameraField = AccessTools.FieldRefAccess<GameCamera, Camera>("m_camera");

        public static Quaternion Rotation => Quaternion.Euler(Pitch, Yaw, 0f) * Quaternion.Euler(0f, 0f, Roll);

        public static void Init(Vector3 pos, Quaternion rot, float fov)
        {
            Pos = pos;
            Vector3 e = rot.eulerAngles;
            Yaw = e.y;
            Pitch = Mathf.DeltaAngle(0f, e.x);
            Roll = 0f;
            Fov = fov;
            _vel = Vector3.zero;
            _hasOffset = false;
        }

        public static void SetPose(Vector3 pos, float yaw, float pitch, float roll, float fov)
        {
            Pos = pos;
            Yaw = yaw;
            Pitch = pitch;
            Roll = roll;
            Fov = fov;
            _vel = Vector3.zero;
            _hasOffset = false; // re-measure the offset from the new pose
        }

        private static void SetRotation(Quaternion q)
        {
            Vector3 e = q.eulerAngles; // Euler(pitch, yaw, roll) is the same rotation as our Rotation
            Pitch = Mathf.DeltaAngle(0f, e.x);
            Yaw = e.y;
            Roll = Mathf.DeltaAngle(0f, e.z);
        }

        // Attaches to the recorded object under the screen centre, or detaches if already attached.
        public static string ToggleAttach(AttachMode mode, Camera cam)
        {
            if (Attach != AttachMode.None)
            {
                Attach = AttachMode.None;
                return "Camera detached";
            }
            ZNetView nv = AimedObject(cam);
            return nv ? AttachTo(nv, mode) : "Aim at a recorded object first (screen centre)";
        }

        public static string AttachTo(ZNetView nv, AttachMode mode)
        {
            ZDOID id = nv.GetZDO().m_uid;
            _target = new RecId(id.UserID, id.ID);
            AttachName = NameOf(nv);
            Attach = mode;
            _hasOffset = false;
            _lookSettled = false;
            if (mode == AttachMode.Pov)
            {
                // Start at the eyes, looking where the body faces.
                _offset = Vector3.zero;
                _localYaw = _localPitch = _localRoll = 0f;
                _hasOffset = true;
            }
            return $"{ModeName(mode)} {AttachName}";
        }

        public static string NameOf(ZNetView nv)
        {
            Character c = nv.GetComponent<Character>();
            return c ? c.GetHoverName() : Utils.GetPrefabName(nv.gameObject);
        }

        // The object the camera is attached to, or else the one at the screen centre.
        public static ZNetView TargetOrAimed(Camera cam) => Attach != AttachMode.None && TargetView() ? TargetView() : AimedObject(cam);

        public static string ModeName(AttachMode m) => m == AttachMode.Follow ? "Following" : m == AttachMode.Mount ? "Mounted on" : m == AttachMode.Orbit ? "Orbiting" : "Eyes of";

        // Keep the view on the aimed object (L), while moving or on a path. L again stops.
        public static string ToggleLookAt(Camera cam)
        {
            if (_lookOn)
            {
                _lookOn = false;
                return "Stopped looking at target";
            }
            ZNetView nv = AimedObject(cam);
            if (!nv) return "Aim at a recorded object first (screen centre)";
            ZDOID id = nv.GetZDO().m_uid;
            _lookTarget = new RecId(id.UserID, id.ID);
            _lookLabel = NameOf(nv);
            _lookOn = true;
            _lookSettled = false;
            return $"Looking at {_lookLabel}";
        }

        public static void Reset()
        {
            Attach = AttachMode.None;
            _lookOn = false;
            Take = null;
            Picture.Restore();
        }

        // Aim follows a target softly: recorded character motion is too jittery to aim at directly.
        private static void LookTowardsSmoothly(Vector3 point, float dt)
        {
            // A seek can move the target far away at once: don't sweep all the way there.
            bool snap = !_lookSettled || LookSmoothing <= 0f || (point - _lookPoint).sqrMagnitude > 2500f;
            _lookPoint = snap ? point : Vector3.Lerp(_lookPoint, point, 1f - Mathf.Exp(-dt / LookSmoothing));
            _lookSettled = true;
            LookTowards(_lookPoint);
        }

        // Depth of field through the game's own effect. Auto focus uses the look-at target, or what's
        // at the screen centre. In a replay this switch alone decides, whatever the game's graphics
        // setting says: leaving that on is what kept the blur on with the switch off. The next scene's
        // CameraEffects reads the graphics setting again, so nothing needs putting back.
        private static void ApplyDof(Camera cam, float dt)
        {
            CameraEffects fx = CameraEffects.instance;
            DepthOfField dof = fx ? fx.GetComponent<DepthOfField>() : null;
            if (!dof) return;
            fx.m_dofAutoFocus = false;
            dof.enabled = DofOn;
            if (!DofOn) return;
            if (DofAuto)
            {
                ZNetView look = _lookOn ? ViewOf(_lookTarget) : null;
                if (look) DofDistance = (CenterOf(look) - cam.transform.position).magnitude;
                else if (Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, fx.m_dofMaxDistance, fx.m_dofRayMask)) DofDistance = hit.distance;
                dof.focalLength = Mathf.Lerp(dof.focalLength, DofDistance, 1f - Mathf.Exp(-6f * dt));
            }
            else dof.focalLength = DofDistance;
            dof.aperture = Mathf.Min(DofAperture, DofMaxAperture);
        }

        internal static ZNetView ViewOf(RecId id)
        {
            ZDO z = ZDOMan.instance?.GetZDO(id.ToZdoId());
            return z != null ? ZNetScene.instance.FindInstance(z) : null;
        }

        private static Vector3 CenterOf(ZNetView nv)
        {
            Character c = nv.GetComponent<Character>();
            return c ? c.GetCenterPoint() : nv.transform.position;
        }

        private static void LookTowards(Vector3 point)
        {
            Vector3 d = point - Pos;
            if (d.sqrMagnitude < 1e-4f) return;
            Vector3 e = Quaternion.LookRotation(d).eulerAngles;
            Yaw = e.y;
            Pitch = Mathf.DeltaAngle(0f, e.x);
        }

        // Camera settings presets, kept in the config so they last between sessions.
        public static string PresetToText()
        {
            string F(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            return string.Join(" ", F(Fov), F(MoveSpeed), F(Smoothing), F(SensYaw), F(SensPitch), F(RollSpeed), InvertY ? "1" : "0", F(FollowLag), F(OrbitSpeed), F(LookSmoothing),
                F(Roll), DofOn ? "1" : "0", DofAuto ? "1" : "0", F(DofDistance), F(DofAperture), ((int)ReplayUI.GridMode).ToString(), F(AimSmoothing),
                F(Picture.Exposure), F(Picture.Temperature), F(Picture.Saturation), F(Picture.Contrast), F(Picture.Bloom), F(Picture.Shafts), F(Picture.Brightness), Picture.LensDirt ? "1" : "0");
        }

        public static bool PresetFromText(string text)
        {
            string[] f = text.Split(' ');
            // Presets from older versions are shorter: what they don't have stays as it is now, except
            // depth of field, which is off in them so that switching presets always gets out of it.
            float[] v = { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, LookSmoothing, Roll, 0f, DofAuto ? 1f : 0f, DofDistance, DofAperture, (float)ReplayUI.GridMode, AimSmoothing,
                Picture.Exposure, Picture.Temperature, Picture.Saturation, Picture.Contrast, Picture.Bloom, Picture.Shafts, Picture.Brightness, Picture.LensDirt ? 1f : 0f };
            if (f.Length < 9 || f.Length > v.Length) return false;
            for (int i = 0; i < f.Length; i++)
            {
                if (!float.TryParse(f[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v[i])) return false;
            }
            Fov = v[0]; MoveSpeed = v[1]; Smoothing = v[2]; SensYaw = v[3]; SensPitch = v[4]; RollSpeed = v[5]; InvertY = v[6] != 0f; FollowLag = v[7]; OrbitSpeed = v[8]; LookSmoothing = v[9];
            Roll = v[10]; DofOn = v[11] != 0f; DofAuto = v[12] != 0f; DofDistance = v[13]; DofAperture = v[14]; ReplayUI.GridMode = (ReplayUI.Grid)(int)v[15]; AimSmoothing = v[16];
            Picture.Exposure = v[17]; Picture.Temperature = v[18]; Picture.Saturation = v[19]; Picture.Contrast = v[20]; Picture.Bloom = v[21]; Picture.Shafts = v[22]; Picture.Brightness = v[23]; Picture.LensDirt = v[24] != 0f;
            return true;
        }

        public static ZNetView AimedObject(Camera cam)
        {
            if (!cam) return null;
            var ray = new Ray(cam.transform.position, cam.transform.forward);
            ZNetView projectile = AimedProjectile(ray);
            if (projectile) return projectile;
            RaycastHit[] hits = Physics.SphereCastAll(ray, 0.3f, 300f, ~0, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (RaycastHit h in hits)
            {
                ZNetView nv = h.collider.GetComponentInParent<ZNetView>();
                if (nv && nv.GetZDO() != null && nv.GetZDO().GetOwner() == ReplaySession.FakeOwner) return nv;
            }
            return null;
        }

        // Projectiles fly without colliders, so a ray can't hit them, and they're small and quick: the
        // one nearest the aim line counts, within half a metre or about 4 degrees.
        private static ZNetView AimedProjectile(Ray ray)
        {
            ZNetView best = null;
            float bestAngle = float.MaxValue;
            foreach (Projectile p in Object.FindObjectsByType<Projectile>(FindObjectsSortMode.None))
            {
                Vector3 to = p.transform.position - ray.origin;
                float along = Vector3.Dot(to, ray.direction);
                if (along <= 0f || along > 300f || Vector3.Cross(ray.direction, to).magnitude > Mathf.Max(0.5f, along * 0.07f)) continue;
                float angle = Vector3.Angle(ray.direction, to);
                if (angle >= bestAngle) continue;
                ZNetView nv = p.GetComponent<ZNetView>();
                if (!nv || nv.GetZDO() == null || nv.GetZDO().GetOwner() != ReplaySession.FakeOwner) continue;
                best = nv;
                bestAngle = angle;
            }
            return best;
        }

        private static ZNetView TargetView() => ViewOf(_target);

        // A light on the camera, lit for screenshots (and while lining one up).
        private static void ApplyFlash(Camera cam)
        {
            bool lit = FlashOn && (FlashPreview || Exporter.FlashNow) && !Exporter.Busy;
            if (!_flash)
            {
                if (!lit) return;
                var go = new GameObject("ReplayModFlash");
                go.transform.SetParent(cam.transform, false);
                _flash = go.AddComponent<Light>();
                _flash.type = LightType.Point;
                _flash.color = new Color(1f, 0.98f, 0.93f);
                _flash.shadows = LightShadows.None;
                _flash.renderMode = LightRenderMode.ForcePixel;
            }
            _flash.enabled = lit;
            _flash.intensity = FlashStrength;
            _flash.range = FlashRange;
        }

        // The frame the camera rides in. Mount: the object itself. POV: the eyes, turned with the body
        // but kept upright, so walking doesn't shake the view.
        private static void Frame(ZNetView nv, out Vector3 origin, out Quaternion rot)
        {
            Transform t = nv.transform;
            if (Attach == AttachMode.Pov)
            {
                Character c = nv.GetComponent<Character>();
                rot = Quaternion.Euler(0f, t.eulerAngles.y, 0f);
                origin = (c ? c.GetEyePoint() : t.position + Vector3.up * 1.7f) + rot * new Vector3(0f, 0.05f, 0.15f);
            }
            else
            {
                origin = t.position;
                rot = t.rotation;
            }
        }

        // Any of the flying inputs, which do nothing while on the path or the original camera.
        private static bool FlyingInput()
        {
            bool ctrl = ZInput.GetKey(KeyCode.LeftControl, false) || ZInput.GetKey(KeyCode.RightControl, false);
            if ((!ReplayUI.CursorFree && ZInput.GetMouseDelta() != Vector2.zero) || (!ReplayUI.MouseOverPanel && ZInput.GetMouseScrollWheel() != 0f)) return true;
            foreach (KeyCode k in new[] { KeyCode.W, KeyCode.A, KeyCode.S, KeyCode.D, KeyCode.E, KeyCode.Q, KeyCode.R, KeyCode.F, KeyCode.X })
            {
                if (ZInput.GetKey(k, false)) return true;
            }
            return !ctrl && (ZInput.GetKey(KeyCode.Z, false) || ZInput.GetKey(KeyCode.C, false));
        }

        private static void Steer(float dt)
        {
            if (Menu.IsVisible() || ReplayUI.Typing)
            {
                _vel = Vector3.zero;
                _aimRate = Vector2.zero;
                return;
            }
            Vector2 md = ReplayUI.CursorFree ? Vector2.zero : ZInput.GetMouseDelta();
            if (AimSmoothing > 0f && dt > 0f)
            {
                // Smooths the speed of the turn, so the camera still ends up as far as the mouse moved.
                _aimRate = Vector2.Lerp(_aimRate, md / dt, 1f - Mathf.Exp(-Mathf.Lerp(40f, 1.5f, AimSmoothing) * dt));
                md = _aimRate * dt;
            }
            else _aimRate = Vector2.zero;
            if (md != Vector2.zero)
            {
                // Same degrees per mouse count as the game's own look (Player.SetMouseLook), times our multipliers.
                // Past straight up or down the view is upside down: turn the other way, so right stays right on screen.
                if (!LockYaw) Yaw += md.x * SensYaw * PlayerController.m_mouseSens * (Mathf.Cos(Pitch * Mathf.Deg2Rad) < 0f ? -1f : 1f);
                if (!LockPitch) Pitch = Mathf.DeltaAngle(0f, Pitch - md.y * (InvertY ? -1f : 1f) * SensPitch * PlayerController.m_mouseSens);
            }
            if (!LockRoll)
            {
                // Ctrl+Z and Ctrl+C are undo and copy.
                bool ctrl = ZInput.GetKey(KeyCode.LeftControl, false) || ZInput.GetKey(KeyCode.RightControl, false);
                if (ZInput.GetKey(KeyCode.Z, false) && !ctrl) Roll += RollSpeed * dt;
                if (ZInput.GetKey(KeyCode.C, false) && !ctrl) Roll -= RollSpeed * dt;
            }
            if (ZInput.GetKeyDown(KeyCode.X, false))
            {
                Roll = 0f;
                // Also turns an inverted view back upright, looking the same way.
                if (Mathf.Cos(Pitch * Mathf.Deg2Rad) < 0f)
                {
                    Pitch = Mathf.DeltaAngle(0f, 180f - Pitch);
                    Yaw += 180f;
                }
            }

            if (ZInput.GetKey(KeyCode.R, false)) Fov = Mathf.Clamp(Fov - FovSpeed * dt, 5f, 140f);
            if (ZInput.GetKey(KeyCode.F, false)) Fov = Mathf.Clamp(Fov + FovSpeed * dt, 5f, 140f);

            float wheel = ZInput.GetMouseScrollWheel();
            if (wheel != 0f && !ReplayUI.MouseOverPanel)
            {
                if (ZInput.GetKey(KeyCode.LeftAlt, false)) Fov = Mathf.Clamp(Fov - Mathf.Sign(wheel) * 2f, 5f, 140f);
                else MoveSpeed = Mathf.Clamp(MoveSpeed * (wheel > 0f ? 1.2f : 1f / 1.2f), 0.2f, 200f);
            }

            Vector3 input = Vector3.zero;
            if (ZInput.GetKey(KeyCode.W, false)) input.z += 1f;
            if (ZInput.GetKey(KeyCode.S, false)) input.z -= 1f;
            if (ZInput.GetKey(KeyCode.D, false)) input.x += 1f;
            if (ZInput.GetKey(KeyCode.A, false)) input.x -= 1f;
            if (ZInput.GetKey(KeyCode.E, false)) input.y += 1f;
            if (ZInput.GetKey(KeyCode.Q, false)) input.y -= 1f;
            float mult = ZInput.GetKey(KeyCode.LeftShift, false) ? 4f : ZInput.GetKey(KeyCode.LeftControl, false) ? 0.25f : 1f;

            Quaternion look = Quaternion.Euler(Pitch, Yaw, 0f);
            Vector3 fwd = look * Vector3.forward, right = look * Vector3.right;
            if (LockHeight)
            {
                fwd.y = 0f;
                right.y = 0f;
                fwd.Normalize();
                right.Normalize();
            }
            Vector3 wish = fwd * input.z + right * input.x + Vector3.up * input.y;
            switch (MoveAxis)
            {
                case AxisLock.X: wish = Vector3.Project(wish, Vector3.right); break;
                case AxisLock.Y: wish = Vector3.Project(wish, Vector3.up); break;
                case AxisLock.Z: wish = Vector3.Project(wish, Vector3.forward); break;
            }
            if (wish.sqrMagnitude > 1f) wish.Normalize();
            wish *= MoveSpeed * mult;
            float response = Mathf.Lerp(40f, 1.5f, Smoothing);
            _vel = Vector3.Lerp(_vel, wish, 1f - Mathf.Exp(-response * dt));
            Pos += _vel * dt;
        }

        // Postfix on GameCamera.LateUpdate (last), so it overrides vanilla and other camera mods.
        public static void Apply(GameCamera gc)
        {
            if (!ReplaySession.Active || !ReplaySession.Ready) return;
            float dt = Time.unscaledDeltaTime;
            Vector3 pos;
            Quaternion rot;
            float fov;
            if (CameraPath.Follow && CameraPath.Evaluate(ReplaySession.T, out pos, out float yaw, out float pitch, out float roll, out fov))
            {
                // Keep the freecam on the path, so turning Follow off continues from this exact pose.
                SetPose(pos, yaw, pitch, roll, fov);
                rot = Rotation;
                if (!Menu.IsVisible() && !ReplayUI.Typing && FlyingInput()) IgnoredInputAt = Time.unscaledTime;
            }
            else if (UseOriginal && ReplaySession.SampleCamera(ReplaySession.T, out pos, out rot, out fov))
            {
                // Hand over smoothly when switching back to the freecam.
                Init(pos, rot, fov);
                if (!Menu.IsVisible() && !ReplayUI.Typing && FlyingInput()) IgnoredInputAt = Time.unscaledTime;
            }
            else
            {
                // Attached. Follow: keep a world offset from the target, look freely. Mount and POV: steer
                // inside the target's own frame (forward, up and mouse look are the object's), then turn
                // that into world space. Pos and angles end in world space for keys and bookmarks.
                // If the target isn't there at this time (seeked before it appeared), fly freely.
                ZNetView target = Attach != AttachMode.None ? TargetView() : null;
                if (!target) Steer(dt);
                else if (Attach == AttachMode.Follow || Attach == AttachMode.Orbit)
                {
                    Vector3 at = target.transform.position;
                    // A seek can move the target far away at once: don't glide all the way there.
                    float lag = Attach == AttachMode.Orbit ? Mathf.Max(FollowLag, LookSmoothing) : FollowLag;
                    bool snap = !_hasOffset || lag <= 0f || (at - _anchor).sqrMagnitude > 2500f;
                    _anchor = snap ? at : Vector3.Lerp(_anchor, at, 1f - Mathf.Exp(-dt / lag));
                    if (_hasOffset)
                    {
                        if (Attach == AttachMode.Orbit) _offset = Quaternion.Euler(0f, OrbitSpeed * dt, 0f) * _offset;
                        Pos = _anchor + _offset;
                    }
                    Steer(dt);
                    _offset = Pos - _anchor;
                    _hasOffset = true;
                    if (Attach == AttachMode.Orbit) LookTowardsSmoothly(CenterOf(target), dt);
                }
                else
                {
                    Frame(target, out Vector3 origin, out Quaternion frame);
                    if (!_hasOffset)
                    {
                        Quaternion inv = Quaternion.Inverse(frame);
                        _offset = inv * (Pos - origin);
                        SetRotation(inv * Rotation);
                        _localYaw = Yaw;
                        _localPitch = Pitch;
                        _localRoll = Roll;
                        _hasOffset = true;
                    }
                    // The angles are kept as they are, not rebuilt from a rotation, so an inverted view stays steerable.
                    Pos = _offset;
                    Yaw = _localYaw;
                    Pitch = _localPitch;
                    Roll = _localRoll;
                    Steer(dt);
                    _offset = Pos;
                    _localYaw = Yaw;
                    _localPitch = Pitch;
                    _localRoll = Roll;
                    Pos = origin + frame * _offset;
                    SetRotation(frame * Rotation);
                }
                pos = Pos;
                rot = Rotation;
                fov = Fov;
            }
            if (_lookOn && !UseOriginal)
            {
                ZNetView look = ViewOf(_lookTarget);
                if (look)
                {
                    LookTowardsSmoothly(CenterOf(look), dt);
                    rot = Rotation;
                }
            }
            gc.transform.SetPositionAndRotation(pos, rot);
            // A take samples at a steady rate in real time, so slow motion gives it more detail, not less.
            // Each sample keeps its replay time and speed.
            if (Take != null && !ReplaySession.Paused && Time.unscaledTime >= _nextTakeSample && (Take.Count == 0 || ReplaySession.T > Take[Take.Count - 1].t))
            {
                _nextTakeSample = Time.unscaledTime + 1f / 30f;
                CameraPath.Key sample = CameraPath.KeyFromWorld(ReplaySession.T, pos, Yaw, Pitch, Roll, fov);
                sample.speed = ReplaySession.Speed / CameraPath.Rate;
                Take.Add(sample);
            }
            Camera cam = CameraField(gc);
            if (cam)
            {
                ApplyDof(cam, dt);
                cam.fieldOfView = fov;
                cam.nearClipPlane = 0.05f;
                Picture.Apply(cam);
                ApplyFlash(cam);
                CameraPath.SyncOverlay(cam);
            }
            if (gc.m_skyCamera) gc.m_skyCamera.fieldOfView = fov;

            // The hidden viewer follows the camera, so zones load wherever we fly and audio is heard from here.
            Player p = Player.m_localPlayer;
            if (p)
            {
                p.transform.position = pos;
                Rigidbody body = ReplaySession.Body(p);
                if (body) body.position = pos;
                // The game removes objects whose ZDO is outside the area around this position, and
                // without the viewer no replay control works. A far jump in one frame did that.
                p.GetComponent<ZNetView>()?.GetZDO()?.SetPosition(pos);
            }

            if (ReplayUI.CursorFree)
            {
                ZCursor.LockState = CursorLockMode.None;
                ZCursor.Show();
            }
        }
    }
}
